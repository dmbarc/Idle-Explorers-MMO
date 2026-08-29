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
/// The schedule comes from the server's engage response, generated deterministically
/// from a seed, so client and server agree on every attack without a round trip per
/// swing. This file draws it; BossFight is the conversation that fetched it.
///
/// ══ WHAT THIS FILE IS NOT ALLOWED TO DECIDE ═══════════════════════════════════
///
/// The King's health, whether he died, whether the player won, and what dropped. All
/// four live on the server. What is here is the arithmetic of the PICTURE: where he
/// stands, which telegraph is on the ground, and a predicted health bar that the next
/// report replaces.
/// </summary>
public class BossController : MonoBehaviour
{
    public MonsterData Data { get; private set; }

    /// <summary>
    /// The server conversation. Every number that matters comes through it.
    ///
    /// Null only before Start, and when there is no server at all -- in which case
    /// the King stands inert rather than fighting a battle nobody is scoring.
    /// </summary>
    public BossFight Fight { get; private set; }

    public double MaxHealth     => Fight?.BossMaxHealth ?? 0d;
    public double CurrentHealth => Fight?.BossHealth    ?? 0d;

    public bool IsAlive => CurrentHealth > 0d && _state != State.Dead;

    /// <summary>0-1, for the health bar at the top of the screen.</summary>
    public float HealthFraction => Fight?.HealthFraction ?? 0f;

    /// <summary>Which phase it is in. Drives the pips on the bar.</summary>
    public BossPhase Phase { get; private set; }

    /// <summary>
    /// Seconds until it gives up and wipes the arena.
    ///
    /// The SERVER's clock, folded forward on every report. A local countdown would
    /// drift, and worse, a suspended tab would freeze it -- so a laptop lid closing
    /// mid-fight would pause the enrage timer, which is a free win.
    /// </summary>
    public float EnrageRemaining => Fight?.EnrageRemaining ?? 0f;

    private enum State { Waiting, Fighting, Dead }

    private State        _state = State.Waiting;
    private NavMeshAgent _agent;
    private Animator     _anim;
    private Transform    _player;
    private SpriteFacing _facing;
    private WorldStatusBar _bar;

    private float _nextWaveAt;

    /// <summary>
    /// How far into the server's schedule for this phase the King has got.
    ///
    /// An index rather than a set of local cooldowns, because the schedule is a
    /// LIST now -- the server generated it from the seed and the client walks it.
    /// Local cooldowns were a second implementation of the same decision, and the
    /// two would disagree the first time anybody retuned an ability.
    /// </summary>
    private int   _nextCast;
    private float _phaseStartedAt;

    /// <summary>Attacks that have been telegraphed and are waiting to land.</summary>
    private readonly List<Pending> _pending = new();

    private struct Pending
    {
        public IdleExplorers.Rules.BossCast Cast;
        public AreaShape Shape;
        public float     ResolvesAt;
        public int       PulsesLeft;
    }

    // ── Setup ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Which boss this is. Health and the enrage clock are NOT set here -- they come
    /// from the server at engage, and a locally-invented starting health would be a
    /// bar that jumps the moment the real one arrives.
    /// </summary>
    public void Initialize(MonsterData data)
    {
        Data  = data;
        Phase = data?.PhaseAt(1d);
    }

    /// <summary>
    /// Gives the King a body if the scene did not.
    ///
    /// ══ WHY HE WAS INVISIBLE ══════════════════════════════════════════════════
    ///
    /// GoblinThroneSetup places `new GameObject("GoblinKing")` and adds a
    /// BossController to it. That is all. No rig, no renderer, no animator — an empty
    /// transform with a script on it.
    ///
    /// It was not obvious because the fight WORKED: the health bar is drawn by the HUD
    /// from the server's numbers, the telegraphs are ground decals, and the damage was
    /// real. The arena had a boss fight and no boss.
    ///
    /// ══ WHY AT RUNTIME AND NOT IN THE RECIPE ══════════════════════════════════
    ///
    /// The recipe is the right long-term home and it now builds a goblin_king prefab.
    /// But the arena scene already exists with the empty object in it, and rebuilding
    /// a generated scene to fix a missing sprite is a large diff and a NavMesh rebake
    /// for something this file can do in eight lines on arrival.
    ///
    /// Idempotent: a King that already has art keeps it, so the day the scene is
    /// regenerated with a proper prefab this quietly stops doing anything.
    /// </summary>
    private void EnsureBody()
    {
        if (GetComponentInChildren<SpriteRenderer>() != null) return;

        string id = Data?.id;

        var prefab = string.IsNullOrEmpty(id) ? null : Resources.Load<GameObject>("Monsters/" + id)
                                             ?? Resources.Load<GameObject>("Monsters/_default");

        if (prefab == null)
        {
            Debug.LogError($"[Boss] No rig for '{id}' and no fallback in Resources/Monsters. " +
                           "The King will be invisible. Run 'Idle Explorers → Setup Everything'.");
            return;
        }

        var body = Instantiate(prefab, transform.position, transform.rotation, transform);

        body.name = "Body";
        body.transform.localPosition = Vector3.zero;

        // The prefab is a complete monster -- its own controller, agent and collider.
        // Only the ART is wanted here; this object already owns the behaviour, and a
        // second MonsterController would wander the King around his own arena.
        foreach (var monster in body.GetComponentsInChildren<MonsterController>(true)) Destroy(monster);
        foreach (var agent   in body.GetComponentsInChildren<NavMeshAgent>(true))     Destroy(agent);
        foreach (var facing  in body.GetComponentsInChildren<SpriteFacing>(true))     Destroy(facing);
        foreach (var plate   in body.GetComponentsInChildren<NamePlate>(true))        Destroy(plate.gameObject);
        foreach (var bar     in body.GetComponentsInChildren<WorldStatusBar>(true))   Destroy(bar.gameObject);

        // Colliders too: the King's own is what a click has to land on, and a spare
        // set from the donor would sit in front of it.
        foreach (var hit in body.GetComponentsInChildren<Collider>(true)) Destroy(hit);

        // Something to click on and something for the camera ray to stop at.
        if (GetComponent<Collider>() == null)
        {
            float height = Mathf.Max(1f, SpumRig.MeasureCharacterHeight(transform));

            var capsule = gameObject.AddComponent<CapsuleCollider>();

            capsule.radius = 1.1f;
            capsule.height = height;
            capsule.center = Vector3.up * height * 0.5f;
        }
    }

    private void Start()
    {
        // ══ THERE IS EXACTLY ONE KING ═════════════════════════════════════════
        //
        // Three things already try to guarantee this: the scene holds a single
        // BossController, MonsterSpawner refuses to spawn anything with isBoss, and
        // the server's population does the same.
        //
        // This is the last one, and it is the only one that cannot be got round by a
        // route nobody has thought of — a scene loaded twice, an editor rebuild that
        // duplicates the object, a future spawner. Whatever produced the second King,
        // it does not survive its own Start.
        //
        // The FIRST one wins. The newcomer destroys itself rather than the incumbent,
        // because the incumbent may already be in a fight the server knows about.
        foreach (var other in FindObjectsByType<BossController>(FindObjectsInactive.Exclude))
        {
            if (other == this || other == null) continue;

            Debug.LogWarning($"[Boss] A second {name} appeared and was removed. " +
                             "Something is spawning bosses -- check MonsterSpawner and " +
                             "the map's population.");

            Destroy(gameObject);
            return;
        }

        if (Data == null)
        {
            // Placed in a scene without being initialised: read the map's own boss
            // rather than standing there inert, which is what a designer dropping one
            // in would expect.
            string monsterId = GameManager.Zone?.CurrentMap?.defaultMonsterId;
            Initialize(GameManager.Content?.GetMonster(monsterId));
        }

        EnsureBody();

        _agent  = GetComponent<NavMeshAgent>();
        _anim   = GetComponentInChildren<Animator>();
        _facing = SpriteFacing.Attach(gameObject);

        // Its own bar is the floating one every monster has; the big one at the top
        // of the screen is separate and owned by the HUD.
        float head = SpumRig.MeasureCharacterHeight(transform);
        // ══ THE KING HAS NO BAR OVER HIS HEAD ══════════════════════════════
        //
        // He had one, exactly like a goblin: a small strip and a floating number stuck
        // to a sprite the player is trying to read telegraphs off.
        //
        // BossHealthBar draws the real one across the top of the screen, with the name,
        // the phase and the enrage clock -- all of which are what a boss fight is
        // actually about. It existed the whole time and nothing had ever created it;
        // GameHUD does now.

        if (_bar != null) _bar.AutoHide = false;

        var player = GameObject.Find("PlayerCharacter");
        if (player != null) _player = player.transform;

        Begin();
    }

    /// <summary>
    /// Asks the server to start the fight, and stands still if it says no.
    ///
    /// ══ WHY THE KING WAITS FOR AN ANSWER ══════════════════════════════════════
    ///
    /// Because the alternative is a King who starts swinging on a client that the
    /// server does not believe is fighting anything -- an arena where telegraphs land,
    /// health drops, and none of it counts. A player would rightly call that a stolen
    /// twenty minutes.
    ///
    /// So: no encounter, no fight. The reason is already on screen, because
    /// BossFight.EngageAsync raised it.
    /// </summary>
    private async void Begin()
    {
        Fight = gameObject.AddComponent<BossFight>();

        Fight.PhaseChanged += EnterPhase;

        // ══ THE SERVER'S VERDICT, NOT THE CLIENT'S ════════════════════════════
        //
        // Die() and Enrage() are reactions to an answer, never decisions. The client
        // has a predicted health bar that reaches zero a fraction of a second early;
        // that prediction is not evidence, and a King who fell because a client
        // thought so would be a King anybody could fell.
        Fight.Finished += Ended;

        var started = await Fight.EngageAsync(Data?.id ?? "goblin_king");

        if (started == null)
        {
            _state = State.Dead;   // inert, not dying: nothing happens and nothing counts

            if (_bar != null) _bar.gameObject.SetActive(false);
            return;
        }

        _state          = State.Waiting;
        _phaseStartedAt = Time.time;
        _nextCast       = 0;

        GameEvents.FireBossEngaged(Data?.DisplayName ?? "Boss", (float)MaxHealth);
    }

    // ── The fight ─────────────────────────────────────────────────────────────

    private void Update()
    {
        if (_state == State.Dead) return;

        ResolvePending();

        // ══ NOTHING HAPPENS BEFORE THE SERVER SAYS SO ═════════════════════════
        //
        // Fight exists from the first frame of Begin(), but its encounter does not
        // arrive until the round trip completes. In that gap EnrageRemaining is zero
        // -- there is no timer yet -- and an unguarded check below read that as "the
        // King gave up" and enraged the fight before it had started.
        if (Fight == null || !Fight.IsLive) return;

        if (_player == null) return;

        float distance = Vector3.Distance(transform.position, _player.position);

        if (_state == State.Waiting)
        {
            float aggro = Data?.aggroRange > 0f ? Data.aggroRange : 15f;
            if (distance > aggro) return;

            _state = State.Fighting;
        }

        if (_facing != null) _facing.LookTarget = _player;

        // The server's clock, not a local countdown. A suspended tab freezes
        // Time.time, and an enrage timer that pauses when a laptop lid closes is a
        // free win.
        if (Fight.EnrageRemaining <= 0f) { Enrage(); return; }

        Chase(distance);
        RunSchedule();
        MaybeSpawnAdds();

        if (_bar != null)
            _bar.Set(CurrentHealth, MaxHealth, NumberFormatter.Format((long)CurrentHealth));
    }

    /// <summary>
    /// Announced rather than silent: a phase change that only shows up as the boss
    /// hitting harder reads as the player getting worse.
    /// </summary>
    /// <summary>
    /// Enters a phase the SERVER has confirmed.
    ///
    /// ══ WHY NOT FROM THE HEALTH BAR ═══════════════════════════════════════════
    ///
    /// Because the health bar is a prediction between reports, and a phase driven off
    /// a prediction flickers: the client crosses 66%, announces "Roused", the next
    /// report puts the King back at 67%, and the announcement happens again. Phase is
    /// a thing that happens once, so it comes from the party that knows.
    ///
    /// It also resets the schedule cursor, because the server's timeline for a phase
    /// is measured from the moment that phase begins.
    /// </summary>
    private void EnterPhase(int index)
    {
        BossPhase next = Data?.phases != null && index >= 0 && index < Data.phases.Length
            ? Data.phases[index]
            : null;

        if (next == null || ReferenceEquals(next, Phase)) return;

        Phase           = next;
        _phaseStartedAt = Time.time;
        _nextCast       = 0;

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

    /// <summary>
    /// Walks the server's schedule for this phase.
    ///
    /// ══ WHY A LIST AND NOT A COOLDOWN LOOP ════════════════════════════════════
    ///
    /// The whole schedule arrived at engage, generated deterministically from a seed
    /// the server holds. Walking it means the client and the server agree about every
    /// attack without a single round trip -- so a 200 ms connection does not shorten a
    /// 1.2 second telegraph, and a dropped packet does not delete a mechanic.
    ///
    /// The previous version rolled its own cooldowns here. That was a second
    /// implementation of the same decision, and the two would have started disagreeing
    /// the first time anybody retuned an ability.
    /// </summary>
    private void RunSchedule()
    {
        if (Fight == null) return;

        var casts = Fight.CastsFor(Fight.PhaseIndex);
        if (casts.Length == 0) return;

        float intoPhase = Time.time - _phaseStartedAt;

        // A while, not an if: a frame hitch or a tab coming back from the background
        // can leave several casts due at once, and skipping them would silently drop
        // attacks the server believes happened.
        while (_nextCast < casts.Length && casts[_nextCast].atSeconds <= intoPhase)
        {
            Cast(casts[_nextCast]);
            _nextCast++;
        }
    }

    /// <summary>
    /// Telegraphs one scheduled attack.
    ///
    /// The shape comes off the CAST rather than out of the client's content copy, so a
    /// player whose content is stale still sees the right marker. "Your download was
    /// out of date" is not a boss mechanic.
    /// </summary>
    private void Cast(IdleExplorers.Rules.BossCast cast)
    {
        AreaShape shape = ShapeFor(cast);

        TelegraphDecal.Show(shape, cast.telegraphSeconds);

        _pending.Add(new Pending
        {
            Cast       = cast,
            Shape      = shape,
            ResolvesAt = Time.time + cast.telegraphSeconds,
            PulsesLeft = Mathf.Max(1, cast.pulses),
        });

        SpumAnim.PlayAttack(_anim);
    }

    private AreaShape ShapeFor(IdleExplorers.Rules.BossCast ability)
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
                pending.ResolvesAt = Time.time + Mathf.Max(0.1f, pending.Cast.secondsBetweenPulses);

                // Each pulse gets its own marker, since a three-pulse quake the player
                // cannot see the second and third of is one pulse and two ambushes.
                TelegraphDecal.Show(pending.Shape, pending.Cast.secondsBetweenPulses);

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

        // From the CAST, so a stale content copy cannot quietly change what an attack
        // does. Player health is presentation -- the fail condition is the enrage
        // timer -- so this is about the fight being consistent, not about it counting.
        double damage = ((Data?.attackDamageMin ?? 1) + (Data?.attackDamageMax ?? 2)) * 0.5d
                      * Mathf.Max(0.1f, pending.Cast.damageMultiplier);

        controller.TakeDamage(damage);
        AbilityVFX.Play("impact", _player.position);

        if (pending.Cast.knockback > 0f) Knock(controller, pending.Cast.knockback);
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

    /// <summary>
    /// "The player hit me."
    ///
    /// ══ WHY THE AMOUNT IS IGNORED ═════════════════════════════════════════════
    ///
    /// Because it is a number the client made up, and the whole architecture exists
    /// so that no number a client made up ever reaches a health bar anybody is scored
    /// on. The attack loop still calls this, exactly as it does for every ordinary
    /// monster -- the King simply answers differently: he reports the SWING and lets
    /// the server say what it was worth.
    ///
    /// The parameter stays so PlayerController needs no special case. A boss that had
    /// to be attacked through a different code path would be a boss that stopped
    /// working the next time somebody touched the attack loop.
    ///
    /// The damage number on screen is the prediction, which is honest: it is what the
    /// client BELIEVES happened, drawn from the same shared rules the server runs, and
    /// corrected within half a second if the two disagree.
    /// </summary>
    public void TakeDamage(double amount) => TakeDamage(amount, false);

    public void TakeDamage(double amount, bool wasCrit)
    {
        if (!IsAlive || Fight == null) return;

        double before = Fight.BossHealth;

        Fight.Swung();

        double predicted = System.Math.Max(0d, before - Fight.BossHealth);

        DamageNumber.Spawn(transform.position, predicted,
                           wasCrit ? DamageNumber.PlayerCrit : DamageNumber.PlayerDealt,
                           prefix: "", big: wasCrit);

        GameEvents.FireBossHealthChanged(HealthFraction);

        // Predicted to zero. NOT a death -- the client does not get to decide that, and
        // BossFight will ask. Die() runs when the answer comes back.
        if (Fight.BossHealth <= 0d) Fight.Finish();
    }

    /// <summary>
    /// The server said how it went.
    ///
    /// One handler for both outcomes, because from here they are the same event: the
    /// fight is over and somebody else decided. What differs is only which animation
    /// plays.
    /// </summary>
    private void Ended(IdleExplorers.Backend.EncounterResult result)
    {
        if (_state == State.Dead) return;

        if (result != null && result.won) Die();
        else                              Enrage();
    }

    private void Die()
    {
        _state = State.Dead;

        _pending.Clear();

        if (_agent != null) _agent.enabled = false;
        SpumAnim.PlayDeath(_anim);

        GameEvents.FireBossDefeated(Data?.id ?? "");
        GameEvents.FireMonsterKilled(Data?.id ?? "");

        // Loot is the SERVER's. Nothing is dropped here: a client that spawned the
        // King's drops would be a client that decides what the King drops, which is
        // the whole thing this architecture removes. BossFight has already claimed
        // what the server granted, and the items arrive through the normal pickup
        // event as though they had been walked over.
        GameEvents.FireToast($"✦ {Data?.DisplayName} falls.", ChatTone.Good);

        // ══ AND THE ROOM STOPS BEING A ONE-WAY DOOR ══════════════════════════
        //
        // Opened here rather than by a listener somewhere else, because this is the
        // only place in the project that knows a boss has actually been beaten -- and
        // a component subscribed to OnBossDefeated would have to exist in the arena
        // scene, which is regenerated from a recipe and would lose it.
        //
        // Beside the corpse rather than at the entrance: that is where the player is
        // standing, and an exit they have to go looking for is an exit that reads as
        // the fight not having finished.
        ExitPortal.Open(transform.position + ExitOffset,
                        GameManager.StartingMapId,
                        "Leave the Throne");
    }

    /// <summary>
    /// Where the exit opens relative to the corpse. Far enough that it does not stand
    /// inside the King's own sprite, close enough to be plainly his.
    /// </summary>
    private static readonly Vector3 ExitOffset = new(3.5f, 0f, -3.5f);

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
        if (_state == State.Dead) return;

        _state = State.Dead;

        // Asked, in case the timer got here before a report did. Finish() is a no-op
        // once it has run, so arriving from both directions costs nothing.
        Fight?.Finish();

        GameEvents.FireBossEnraged();
        GameEvents.FireToast($"✦ {Data?.DisplayName} tires of you.", ChatTone.Bad);

        var controller = _player != null ? _player.GetComponent<PlayerController>() : null;
        if (controller != null) controller.TakeDamage(double.MaxValue / 4d);
    }
}
