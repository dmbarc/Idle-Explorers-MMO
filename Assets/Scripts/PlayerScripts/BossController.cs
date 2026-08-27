using System.Collections.Generic;
using IdleExplorers.Rules;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// The Goblin King, and any boss after him.
///
/// ══ WHY NOT A SUBCLASS OF MonsterController ═══════════════════════════════════
///
/// MonsterController was not written for inheritance. It owns its own wander, aggro,
/// attack timer and death, all in Update, all private — subclassing it would mean
/// either overriding nothing useful or making half of it virtual on the chance a
/// boss might want it. A boss shares almost none of that behaviour: it does not
/// wander, does not respawn, and its attacks are a scheduled timeline rather than a
/// swing on a timer.
///
/// What it DOES share is taking damage and dying, and that is a small enough surface
/// to state twice honestly.
///
/// ══ THE TIMELINE IS THE POINT ═════════════════════════════════════════════════
///
/// Attacks are scheduled ahead: chosen, telegraphed, and resolved when the telegraph
/// completes. Nothing here decides damage — that is the server's, and this drives
/// the picture. What makes the fight feel right over the internet is that every
/// telegraph is drawn locally from a schedule, so the wind-up never waits on a
/// packet.
///
/// TODO(Phase 4): take the schedule from the server's engage response, generated
/// deterministically from a seed, so client and server agree on every attack without
/// a round trip per swing. The shape below is already that shape; it just rolls its
/// own seed today.
/// </summary>
public class BossController : MonoBehaviour
{
    public MonsterData Data { get; private set; }

    public double MaxHealth     { get; private set; }
    public double CurrentHealth { get; private set; }

    public bool IsAlive => CurrentHealth > 0d && _state != State.Dead;

    /// <summary>0-1, for the health bar at the top of the screen.</summary>
    public float HealthFraction => MaxHealth > 0d ? (float)(CurrentHealth / MaxHealth) : 0f;

    /// <summary>Which phase it is in. Drives the pips on the bar.</summary>
    public BossPhase Phase { get; private set; }

    /// <summary>Seconds until it gives up and wipes the arena.</summary>
    public float EnrageRemaining => Mathf.Max(0f, _enrageAt - Time.time);

    private enum State { Waiting, Fighting, Dead }

    private State        _state = State.Waiting;
    private NavMeshAgent _agent;
    private Animator     _anim;
    private Transform    _player;
    private SpriteFacing _facing;
    private WorldStatusBar _bar;

    private float _enrageAt;
    private float _nextAttackAt;
    private float _nextWaveAt;

    /// <summary>When each ability may next be used, by id.</summary>
    private readonly Dictionary<string, float> _cooldowns = new();

    /// <summary>Attacks that have been telegraphed and are waiting to land.</summary>
    private readonly List<Pending> _pending = new();

    private struct Pending
    {
        public BossAbility Ability;
        public AreaShape   Shape;
        public float       ResolvesAt;
        public int         PulsesLeft;
    }

    // ── Setup ─────────────────────────────────────────────────────────────────

    public void Initialize(MonsterData data)
    {
        Data = data;

        MaxHealth     = data?.maxHp ?? 1d;
        CurrentHealth = MaxHealth;

        Phase     = data?.PhaseAt(1d);
        _enrageAt = Time.time + Mathf.Max(30f, data?.enrageSeconds ?? 300f);
    }

    private void Start()
    {
        _agent  = GetComponent<NavMeshAgent>();
        _anim   = GetComponentInChildren<Animator>();
        _facing = SpriteFacing.Attach(gameObject);

        if (Data == null)
        {
            // Placed in a scene without being initialised: read the map's own boss
            // rather than standing there inert, which is what a designer dropping one
            // in would expect.
            string monsterId = GameManager.Zone?.CurrentMap?.defaultMonsterId;
            Initialize(GameManager.Content?.GetMonster(monsterId));
        }

        // Its own bar is the floating one every monster has; the big one at the top
        // of the screen is separate and owned by the HUD.
        float head = SpumRig.MeasureCharacterHeight(transform);
        _bar = WorldStatusBar.Attach(gameObject, (head > 0.1f ? head : 3f) + 0.5f,
                                     UIManager.Theme.hpFill);

        if (_bar != null) _bar.AutoHide = false;

        var player = GameObject.Find("PlayerCharacter");
        if (player != null) _player = player.transform;

        GameEvents.FireBossEngaged(Data?.DisplayName ?? "Boss", (float)MaxHealth);
    }

    // ── The fight ─────────────────────────────────────────────────────────────

    private void Update()
    {
        if (_state == State.Dead) return;

        ResolvePending();

        if (_player == null) return;

        float distance = Vector3.Distance(transform.position, _player.position);

        if (_state == State.Waiting)
        {
            float aggro = Data?.aggroRange > 0f ? Data.aggroRange : 15f;
            if (distance > aggro) return;

            _state = State.Fighting;
        }

        if (_facing != null) _facing.LookTarget = _player;

        if (Time.time >= _enrageAt) { Enrage(); return; }

        UpdatePhase();
        Chase(distance);
        MaybeAttack(distance);
        MaybeSpawnAdds();

        if (_bar != null)
            _bar.Set(CurrentHealth, MaxHealth, NumberFormatter.Format((long)CurrentHealth));
    }

    /// <summary>
    /// Moves to the next phase when health crosses a threshold.
    ///
    /// Announced rather than silent: a phase change that only shows up as the boss
    /// hitting harder reads as the player getting worse.
    /// </summary>
    private void UpdatePhase()
    {
        BossPhase next = Data?.PhaseAt(HealthFraction);
        if (next == null || ReferenceEquals(next, Phase)) return;

        Phase = next;

        GameEvents.FireBossPhaseChanged(Phase.name, HealthFraction);
        GameEvents.FireToast($"✦ {Data.DisplayName}: {Phase.name}", ChatTone.Warning);

        // A fresh wave timer, so a phase that spawns adds does so promptly rather
        // than whenever the previous phase's clock happened to be.
        _nextWaveAt = Time.time + 2f;
    }

    private void Chase(float distance)
    {
        if (_agent == null || !_agent.isActiveAndEnabled) return;

        float reach = Data?.attackRange > 0f ? Data.attackRange : 3f;

        // Stops at its reach rather than walking into the player, so a melee boss
        // does not shove them across the arena while swinging.
        if (distance > reach)
        {
            _agent.isStopped = false;
            _agent.SetDestination(_player.position);
        }
        else
        {
            _agent.isStopped = true;
        }
    }

    private void MaybeAttack(float distance)
    {
        if (Time.time < _nextAttackAt) return;

        BossAbility ability = ChooseAbility(distance);
        if (ability == null) return;

        AreaShape shape = ShapeFor(ability);

        TelegraphDecal.Show(shape, ability.telegraphSeconds);

        _pending.Add(new Pending
        {
            Ability    = ability,
            Shape      = shape,
            ResolvesAt = Time.time + ability.telegraphSeconds,
            PulsesLeft = Mathf.Max(1, ability.pulses),
        });

        _cooldowns[ability.id] = Time.time + ability.cooldownSeconds;

        // Haste shortens the gap between attacks, not the telegraphs. A phase that
        // shortened wind-ups would make the fight harder to READ rather than harder
        // to survive, which is the wrong kind of difficulty.
        float haste = Phase?.hasteMultiplier > 0f ? Phase.hasteMultiplier : 1f;
        _nextAttackAt = Time.time + (Data?.attackSpeedSeconds ?? 2f) / haste;

        SpumAnim.PlayAttack(_anim);
    }

    /// <summary>
    /// The first ability off cooldown and in range.
    ///
    /// In authored order rather than at random, so a phase's list reads as a priority
    /// and a designer can put the dangerous one first. Random selection makes two
    /// runs of the same fight feel unrelated, which is the opposite of learnable.
    /// </summary>
    private BossAbility ChooseAbility(float distance)
    {
        if (Phase?.abilities == null) return null;

        foreach (var ability in Phase.abilities)
        {
            if (ability == null) continue;
            if (_cooldowns.TryGetValue(ability.id, out float ready) && Time.time < ready) continue;
            if (distance > ability.range + 1f) continue;

            return ability;
        }

        return null;
    }

    private AreaShape ShapeFor(BossAbility ability)
    {
        var origin = new Ground(transform.position.x, transform.position.z);

        Vector3 toPlayer = _player.position - transform.position;
        var facing = new Ground(toPlayer.x, toPlayer.z);

        return ability.shape switch
        {
            "cone"   => AreaShape.Cone(origin, facing, ability.range, ability.arcDegrees),
            "line"   => AreaShape.Line(origin, facing, ability.range, ability.halfWidth),
            "ring"   => AreaShape.Ring(origin, ability.innerRadius, ability.range),

            // A circle lands ON the player rather than on the boss -- it is a thrown
            // stone, and one centred on the thrower would never be dodgeable.
            "circle" => AreaShape.Circle(new Ground(_player.position.x, _player.position.z), ability.range),

            _        => AreaShape.Single(new Ground(_player.position.x, _player.position.z)),
        };
    }

    /// <summary>
    /// Applies attacks whose telegraph has finished.
    ///
    /// The hit test uses the SAME AreaShape the decal was drawn from, so a player
    /// outside the marker is never hit and one inside it always is.
    /// </summary>
    private void ResolvePending()
    {
        for (int i = _pending.Count - 1; i >= 0; i--)
        {
            Pending pending = _pending[i];
            if (Time.time < pending.ResolvesAt) continue;

            Land(pending);

            if (--pending.PulsesLeft > 0)
            {
                pending.ResolvesAt = Time.time + Mathf.Max(0.1f, pending.Ability.secondsBetweenPulses);

                // Each pulse gets its own marker, since a three-pulse quake the player
                // cannot see the second and third of is one pulse and two ambushes.
                TelegraphDecal.Show(pending.Shape, pending.Ability.secondsBetweenPulses);

                _pending[i] = pending;
                continue;
            }

            _pending.RemoveAt(i);
        }
    }

    private void Land(Pending pending)
    {
        if (_player == null) return;

        var at = new Ground(_player.position.x, _player.position.z);
        if (!pending.Shape.Contains(at)) return;

        var controller = _player.GetComponent<PlayerController>();
        if (controller == null) return;

        double damage = ((Data?.attackDamageMin ?? 1) + (Data?.attackDamageMax ?? 2)) * 0.5d
                      * Mathf.Max(0.1f, pending.Ability.damageMultiplier);

        controller.TakeDamage(damage);
        AbilityVFX.Play("impact", _player.position);

        if (pending.Ability.knockback > 0f) Knock(controller, pending.Ability.knockback);
    }

    private void Knock(PlayerController player, float distance)
    {
        Vector3 away = (player.transform.position - transform.position).normalized * distance;
        Vector3 to   = player.transform.position + away;

        // Onto the NavMesh, or a knockback puts the player through a wall and the
        // agent spends the rest of the fight trying to path home.
        if (!NavMesh.SamplePosition(to, out NavMeshHit hit, distance, NavMesh.AllAreas)) return;

        var agent = player.GetComponent<NavMeshAgent>();
        if (agent != null && agent.isActiveAndEnabled) agent.Warp(hit.position);
    }

    private void MaybeSpawnAdds()
    {
        if (Phase == null || Phase.addsPerWave <= 0) return;
        if (Time.time < _nextWaveAt) return;

        _nextWaveAt = Time.time + Mathf.Max(5f, Phase.secondsBetweenWaves);

        // TODO(Phase 4): spawn through MonsterSpawner so adds share its pooling and
        // its cap. Announced meanwhile, because a wave the player is not told about
        // is a wave they discover by dying to it.
        GameEvents.FireToast($"✦ {Data.DisplayName} calls for help.", ChatTone.Warning);
    }

    // ── Damage and death ──────────────────────────────────────────────────────

    public void TakeDamage(double amount) => TakeDamage(amount, false);

    public void TakeDamage(double amount, bool wasCrit)
    {
        if (!IsAlive) return;

        // Armour is stated rather than scaled from level: a boss is not balanced by
        // its level, and the level-scaled figure every ordinary monster uses would
        // make this one either trivial or immune depending on where it landed.
        double through = StatBlock.DamageThrough(Data?.armor ?? 0f);
        double dealt   = System.Math.Max(1d, amount * through);

        CurrentHealth = System.Math.Max(0d, CurrentHealth - dealt);

        DamageNumber.Spawn(transform.position, dealt,
                           wasCrit ? DamageNumber.PlayerCrit : DamageNumber.PlayerDealt);

        GameEvents.FireBossHealthChanged(HealthFraction);

        if (CurrentHealth <= 0d) Die();
    }

    private void Die()
    {
        _state = State.Dead;

        foreach (var pending in _pending) { }
        _pending.Clear();

        if (_agent != null) _agent.enabled = false;
        SpumAnim.PlayDeath(_anim);

        GameEvents.FireBossDefeated(Data?.id ?? "");
        GameEvents.FireMonsterKilled(Data?.id ?? "");

        // Loot is the SERVER's. Nothing is dropped here: a client that spawned the
        // King's drops would be a client that decides what the King drops, which is
        // the whole thing this architecture removes. The kill is reported; the
        // rewards arrive from the next settlement.
        GameEvents.FireToast($"✦ {Data?.DisplayName} falls.", ChatTone.Good);
    }

    /// <summary>
    /// The fail condition: a clock, not player death.
    ///
    /// The server cannot verify that anybody dodged anything -- it does not know
    /// where they were standing. So dodging matters for survival FEEL, and the fight
    /// is won or lost on damage against a timer, which cannot be faked because the
    /// damage comes from a frozen stat snapshot and the clock is the database's.
    /// </summary>
    private void Enrage()
    {
        _state = State.Dead;

        GameEvents.FireBossEnraged();
        GameEvents.FireToast($"✦ {Data?.DisplayName} tires of you.", ChatTone.Bad);

        var controller = _player != null ? _player.GetComponent<PlayerController>() : null;
        if (controller != null) controller.TakeDamage(double.MaxValue / 4d);
    }
}
