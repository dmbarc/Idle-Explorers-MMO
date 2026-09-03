using TMPro;
using UnityEngine;
using UnityEngine.AI;

public class MonsterController : MonoBehaviour, ICombatTarget
{
    /// <summary>What a player would call it. See ICombatTarget.</summary>
    public string TargetName => _data != null ? _data.DisplayName : name;

    private Transform    player;
    private SpriteFacing _facing;
    private PlayerController playerController;  // cached — avoids GetComponent every frame
    public NavMeshAgent agent;
    public Animator anim;

    public double attackDamage = 5d;
    public float aggroDistance = 5f;
    public float attackDistance = 2f;
    public float attackSpeed = 2f;
    public double maxHealthPoints = 50;
    public double healthRegenAmount = 5;
    public float healthRegenSpeed = 5f;
    public float corpseDespawnTime = 10f;

    // Assigned from monster_data.json at spawn — see Initialize()
    private MonsterData _data;
    public MonsterData Data => _data;

    public GameObject dropPrefab;

    /// <summary>
    /// The floating bar above this monster.
    ///
    /// Replaces three serialised fields — healthUI, healthNumber and healthSlider —
    /// that MonsterPrefabSetup deliberately NULLS on every monster it builds, because
    /// they point into the donor rig. That is why the goblin and the bramblekin, the
    /// only two monsters in the two playable maps, had no health bar at all: the
    /// wiring is removed on purpose and nothing put it back. This one attaches itself.
    /// </summary>
    private WorldStatusBar _healthBar;
    private NamePlate      _namePlate;

    private const float UI_SHOW_TIME = 3f;

    private MonsterSpawner spawner;
    private float regenTimer = 0f;
    private bool alive = true;
    private double currentHealthPoints = 50;
    private float attackTimer = 0f;
    private Vector3 wanderPoint;
    private float wanderTimer = 0f;


    void Start()
    {
        agent = GetComponent<NavMeshAgent>();

        // Flat artwork turns edge-on the moment the agent rotates to face travel, and
        // the health bar is a WORLD-SPACE canvas with the same problem. Attach also
        // billboards, and both halves are idempotent, so a prefab the builder already
        // covered pays nothing here — this is the net for _default and skeleton,
        // which the builder never rebuilds.
        _facing = SpriteFacing.Attach(gameObject);


        GameObject playerObj = GameObject.Find("PlayerCharacter");
        if (playerObj != null)
        {
            player = playerObj.transform;
            playerController = playerObj.GetComponent<PlayerController>();
        }
        else
        {
            Debug.LogError("PlayerCharacter not found!");
        }

        if (NavMesh.SamplePosition(transform.position, out NavMeshHit hit, 5f, NavMesh.AllAreas))
            agent.Warp(hit.position);

        PickNewWanderPoint();

        // Above the head rather than a fixed offset: a bramblekin and a boss are not
        // the same height, and a bar floating inside the taller one is worse than none.
        float head = SpumRig.MeasureCharacterHeight(transform);
        _healthBar = WorldStatusBar.Attach(gameObject,
                                           // ══ CLEAR OF THE SPRITE ════════════════════════════
                                           //
                                           // 0.35 put the bar inside the head. A measured
                                           // height is the ART height and a SPUM head is
                                           // mostly hair -- high enough to clear it, low
                                           // enough to stay under the name.
                                           heightAbove: (head > 0.1f ? head : 2f) + 1.05f,
                                           fill: UIManager.Theme.hpFill);

        if (_healthBar != null)
        {
            _healthBar.HideAfterSeconds = UI_SHOW_TIME;
            _healthBar.SetVisible(false);
        }

        // Above the bar, using the same measured head height, so a bramblekin and the
        // King both get a plate that clears their own artwork rather than a constant
        // that suits neither.
        _namePlate = NamePlate.Attach(gameObject,
                                      _data != null ? _data.DisplayName : name,
                                      NamePlate.Role.Hostile,
                                      heightAbove: (head > 0.1f ? head : 2f) + NamePlate.ClearOfHead);
    }

    void Update()
    {
        // The bar owns its own showing and hiding, including the delayed chip that
        // makes a small hit visible. All this has to do is tell it the number.
        if (_healthBar != null)
            _healthBar.Set(currentHealthPoints, maxHealthPoints,
                           NumberFormatter.Format((long)currentHealthPoints));

        if (alive)
        {
            UpdateDebuffs();

            // Smoke Bomb: the player is briefly not worth noticing. Wandering rather
            // than standing still, so a stealthed player sees them lose interest.
            bool aggroSuppressed = Time.time < _aggroBlockedUntil;

            if (player != null && playerController != null && !aggroSuppressed &&
                (agent.isOnNavMesh || !agent.pathPending))
            {
                float dist = Vector3.Distance(transform.position, player.position);

                // ══ FACE THE PLAYER FOR AS LONG AS THEY MATTER ════════════════════
                //
                // Set once, before the range branches, rather than inside the chase.
                // It used to be assigned only while CLOSING -- so a monster ran at you
                // facing you, arrived, and then kept whatever facing it happened to
                // have while it stood there hitting you. Two sprites side by side,
                // both looking somewhere else, for the entire fight.
                //
                // Cleared when they stop being a target, so a monster that has lost
                // interest goes back to facing the way it wanders.
                if (_facing != null)
                    _facing.LookTarget = dist <= aggroDistance && playerController.IsAlive()
                                       ? player
                                       : null;

                if (dist <= aggroDistance && dist > attackDistance && playerController.IsAlive())
                {
                    // Only re-path when the player has actually moved. SetDestination
                    // recalculates the whole path, and calling it every frame for every
                    // monster in aggro range was most of the cost of a busy camp.

                    if ((player.position - _lastChaseDestination).sqrMagnitude > 1f)
                    {
                        _lastChaseDestination = player.position;
                        agent.SetDestination(player.position);
                    }

                    SpumAnim.SetMoving(anim, true);
                    SpumAnim.CancelAttack(anim);
                }
                else if (dist <= attackDistance && playerController.IsAlive())
                {
                    attackTimer += Time.deltaTime;
                    if (attackTimer >= attackSpeed)
                    {
                        playerController.TakeDamage(attackDamage);
                        SpumAnim.PlayAttack(anim);
                        attackTimer = 0f;
                    }
                }
                else
                {
                    wanderTimer -= Time.deltaTime;

                    // pathPending must be checked first: remainingDistance reads 0
                    // while a path is still being computed, so without this the
                    // "arrived" test is true every frame and a new destination is
                    // requested every frame — a full path calculation per monster
                    // per frame, for monsters that are merely standing about.
                    bool arrived = !agent.pathPending && agent.remainingDistance < 1f;
                    if (wanderTimer <= 0f || arrived)
                        PickNewWanderPoint();

                    SpumAnim.SetMoving(anim, agent.velocity.sqrMagnitude > 0.01f);
                    SpumAnim.CancelAttack(anim);
                }
            }

            regenTimer += Time.deltaTime;
            if (regenTimer >= healthRegenSpeed)
            {
                RegenHealth();
                regenTimer = 0f;
            }
        }
        else
        {
            regenTimer += Time.deltaTime;
            if (regenTimer >= corpseDespawnTime)
                Destroy(gameObject);
        }
    }

    public void SetSpawner(MonsterSpawner spawnerRef)
    {
        spawner = spawnerRef;
    }

    /// <summary>
    /// Applies a monster definition from monster_data.json. Called by MonsterSpawner
    /// immediately after Instantiate, before Start runs, so the stats below replace
    /// the prefab's inspector defaults rather than racing them.
    /// </summary>
    public void Initialize(MonsterData data)
    {
        if (data == null)
        {
            Debug.LogWarning($"[MonsterController] {name} initialized with null MonsterData — keeping prefab defaults.");
            return;
        }

        _data               = data;
        maxHealthPoints     = data.maxHp;
        currentHealthPoints = data.maxHp;
        attackSpeed         = data.attackSpeedSeconds;
        // Damage is rolled once per monster so individuals vary within the band
        attackDamage        = Random.Range(data.attackDamageMin, data.attackDamageMax + 1);

        name = data.DisplayName;

        // Initialize can run AFTER Start for a spawned monster, so the plate is built
        // with the placeholder name and corrected here. Without this every goblin is
        // labelled with its prefab name.
        if (_namePlate != null) _namePlate.SetText(data.DisplayName);
    }

    /// <summary>
    /// How much incoming damage this monster shrugs off.
    ///
    /// Scaled from its level rather than authored per monster, so a new entry in
    /// monster_data.json cannot accidentally ship with none. The curve in
    /// StatBlock.DamageThrough means this never reaches immunity.
    /// </summary>
    public float Armor => _data != null ? _data.level * 6f : 0f;

    // ── Belonging to the map rather than to this client ───────────────────────

    /// <summary>
    /// This monster's row in the shared population, or empty when it is a local one.
    ///
    /// ══ WHY BOTH KINDS EXIST ══════════════════════════════════════════════════
    ///
    /// Offline and in the editor there is no server to own a population, and the old
    /// MonsterSpawner runs unchanged so the game is playable with no network. Online
    /// the server owns it and this is set. The difference is one field, checked in
    /// the two places below where it actually changes behaviour.
    /// </summary>
    public string ServerId { get; private set; } = "";

    /// <summary>True when the server decides this monster's health, not us.</summary>
    public bool IsShared => !string.IsNullOrEmpty(ServerId);

    /// <summary>Its full health, for a caller reconciling a server report.</summary>
    public double MaxHealth => maxHealthPoints;

    /// <summary>Ties this object to a row in the map's population.</summary>
    public void BindToServer(string serverId) => ServerId = serverId ?? "";

    /// <summary>
    /// Takes the server's word for how much of this monster is left.
    ///
    /// ══ WHY IT DOES NOT REDRAW A DAMAGE NUMBER ════════════════════════════════
    ///
    /// Because the number was already drawn, by the swing that predicted it. Drawing
    /// one here as well would mean every hit appears twice — once when it lands and
    /// once when the server confirms it two seconds later — and somebody ELSE's hit
    /// would pop a number over a monster this player never touched.
    ///
    /// The health moves silently and the bar follows. That is the whole difference
    /// between predicting and being told.
    /// </summary>
    public void AdoptServerState(double health, double maxHealth, double secondsDead)
    {
        if (maxHealth > 0d) maxHealthPoints = maxHealth;

        currentHealthPoints = System.Math.Max(0d, System.Math.Min(maxHealthPoints, health));

        if (currentHealthPoints > 0d)
        {
            // Back on its feet. A revived row keeps its object rather than being
            // rebuilt, so the walk animation does not restart on every poll.
            if (!alive) Revive();
            return;
        }

        if (alive) FallOver(ourKill: false);
    }

    /// <summary>
    /// Stands a shared monster back up after the server respawned its row.
    ///
    /// The object is reused rather than replaced because the alternative — destroy
    /// and rebuild — restarts every animation and throws away the health bar's
    /// tweening twice a second for a monster nobody was looking at.
    /// </summary>
    private void Revive()
    {
        alive      = true;
        regenTimer = 0f;

        if (agent != null) agent.enabled = true;

        SpumAnim.Revive(anim);

        if (_healthBar != null) _healthBar.SetVisible(false);
    }

    public void TakeDamage(double damageAmount) => TakeDamage(damageAmount, false);

    public void TakeDamage(double damageAmount, bool wasCrit)
    {
        ShowHealthUI();
        DamageNumber.Spawn(transform.position, damageAmount,
                           wasCrit ? DamageNumber.PlayerCrit : DamageNumber.PlayerDealt,
                           prefix: "", big: wasCrit);

        // ══ A SHARED MONSTER IS TOLD ABOUT, NOT DECIDED ═══════════════════════
        //
        // The hit is still applied locally, immediately, so the bar moves under the
        // swing that caused it rather than two seconds later. The server's answer
        // arrives on the next poll and overwrites this -- and in the ordinary case
        // the two agree, because both are computed from the same shared rules.
        //
        // Reported so everybody ELSE sees it. Without this a shared population would
        // be a population everybody watches one person fail to hurt.
        if (IsShared) MonsterSync.ReportDamage(ServerId, damageAmount);

        currentHealthPoints = System.Math.Max(0d, currentHealthPoints - damageAmount);

        if (currentHealthPoints <= 0) FallOver(ourKill: true);
        else                          SpumAnim.PlayHurt(anim);
    }

    /// <summary>
    /// Dies, in every sense the client owns.
    ///
    /// ══ WHY THIS IS A METHOD NOW ══════════════════════════════════════════════
    ///
    /// A shared monster can die without this client having landed the last blow —
    /// somebody else did, and the news arrives on a poll. So the death presentation
    /// has to be reachable from two directions, and the alternative was AdoptServerState
    /// growing a second copy of it that would drift.
    ///
    /// The loot and the kill event still fire on both paths, and both are LOCAL: the
    /// drops are theatre and the kill count is a mirror. What a kill actually pays
    /// comes from settlement, which integrates this player's own time — so a goblin
    /// somebody else killed pays this player nothing, which is correct.
    /// </summary>
    private void FallOver(bool ourKill)
    {
        alive = false;

        // ══ ONLY OUR OWN KILLS DROP AND COUNT ═════════════════════════════════
        //
        // A shared monster can fall because somebody ELSE landed the last blow, and
        // the news arrives on a poll. Spawning loot for it would put items on the
        // ground that this player never earned, and counting it would make the boss
        // portal's label climb from other people's fighting.
        //
        // Neither is a duplication exploit -- the drops are theatre and the label is
        // a mirror, and what a kill PAYS comes from settlement, which integrates this
        // player's own time. Both would simply be lying about whose kill it was.
        if (ourKill)
        {
            DropLoot();
            AwardKillRewards();
        }

        SpumAnim.PlayDeath(anim);

        // Disable agent so the corpse doesn't slide
        if (agent != null) agent.enabled = false;
        regenTimer = 0f;

        if (spawner != null) spawner.MonsterDied();
        if (_healthBar != null) _healthBar.SetVisible(true);

        // The corpse holds its final frame because SpumAnim.PlayDeath sets the
        // isDeath bool, which is the only condition on the transition out of the
        // DEATH state. A coroutine used to try to do this by freezing the animator
        // once the clip finished, and could not: it waited for a state called
        // "4_Death", which is the name of the PARAMETER — the state is "DEATH".
        // It never matched, so it never froze anything and simply polled every
        // frame until the corpse despawned.
    }

    public void RegenHealth()
    {
        if (currentHealthPoints >= maxHealthPoints) return;
        ShowHealthUI();
        currentHealthPoints = System.Math.Min(maxHealthPoints, currentHealthPoints + healthRegenAmount);
    }

    /// <summary>
    /// Reveals the bar. Set drives the rest, including hiding it again once the
    /// monster has been back at full health for a while.
    /// </summary>
    private void ShowHealthUI() => _healthBar?.SetVisible(true);

    public bool IsAlive() => alive;

    // ── Debuffs ───────────────────────────────────────────────────────────────

    private float _slowUntil;
    private float _aggroBlockedUntil;
    private float _baseSpeed = -1f;

    /// <summary>
    /// Frost Nova. multiplier is a fraction of normal movement speed, so 0.4 means
    /// 40% as fast. The base speed is captured on first use rather than in Start,
    /// because Initialize may not have run yet when a monster is spawned.
    /// </summary>
    public void ApplySlow(float multiplier, float duration)
    {
        if (agent == null || !alive) return;

        if (_baseSpeed < 0f) _baseSpeed = agent.speed;

        agent.speed = _baseSpeed * Mathf.Clamp(multiplier, 0.05f, 1f);
        _slowUntil  = Mathf.Max(_slowUntil, Time.time + duration);
    }

    /// <summary>Smoke Bomb. Stops this monster noticing the player for a while.</summary>
    public void DropAggro(float duration)
    {
        if (!alive) return;

        _aggroBlockedUntil = Mathf.Max(_aggroBlockedUntil, Time.time + duration);
        SpumAnim.CancelAttack(anim);
        PickNewWanderPoint();
    }

    /// <summary>Restores anything whose timer has run out.</summary>
    private void UpdateDebuffs()
    {
        if (_slowUntil > 0f && Time.time >= _slowUntil)
        {
            if (agent != null && _baseSpeed >= 0f) agent.speed = _baseSpeed;
            _slowUntil = 0f;
        }
    }

    void PickNewWanderPoint()
    {
        wanderPoint   = transform.position + Random.insideUnitSphere * 8f;
        wanderPoint.y = transform.position.y;

        // Snap onto walkable ground. An off-mesh destination makes SetDestination
        // produce a partial path that never completes, so the monster stops and is
        // immediately judged to have "arrived" — picking another bad point forever.
        if (NavMesh.SamplePosition(wanderPoint, out NavMeshHit hit, 8f, NavMesh.AllAreas))
            wanderPoint = hit.position;

        if (agent != null && agent.isOnNavMesh) agent.SetDestination(wanderPoint);
        wanderTimer = Random.Range(3f, 6f);
    }

    /// <summary>Last position we asked the agent to path to while chasing.</summary>
    private Vector3 _lastChaseDestination = new Vector3(float.MinValue, 0f, 0f);

    /// <summary>
    /// Rolls this monster's loot table from monster_data.json and spawns one
    /// DropPickup per successful entry. Each entry rolls independently against its
    /// own DropChance (weight out of 100), so bones at 100 always drop and a
    /// magic staff at 5 is genuinely rare.
    /// </summary>
    private void DropLoot()
    {
        if (dropPrefab == null || _data?.lootTable == null) return;

        // Drop multiplier is a player stat — it scales quantity, not drop chance,
        // so rare items stay rare no matter how high it goes.
        double multiplier = (playerController != null) ? playerController.dropMultiplier : 1d;
        Vector3 basePos   = transform.position + Vector3.up * 0.6f;

        // Insight raises the CHANCE of a drop, drop quantity raises how much falls.
        // Keeping them separate is the whole reason both stats exist: one makes rare
        // things less rare, the other makes common things more plentiful.
        float insight = GameManager.Stats?.Current.insight ?? 0f;

        foreach (var entry in _data.lootTable)
        {
            if (entry == null || string.IsNullOrEmpty(entry.itemId)) continue;
            if (Random.value > entry.DropChance * (1f + insight)) continue;

            long rolled = RandomRangeLong(entry.minQty, entry.maxQty);
            long qty    = (long)System.Math.Max(1d, rolled * multiplier);

            // Coins go straight to the wallet — they are a currency, so there is
            // nothing to walk over and nothing to carry.
            if (entry.itemId == InventoryManager.CoinsItemId)
            {
                GameManager.Inventory?.AddCoins(qty);
                GameManager.Audio?.Play(Sfx.Coins);
                continue;
            }

            // One pickup per entry regardless of size — quantities reach the
            // billions and a pickup per stack would spawn thousands of objects.
            SpawnDrop(entry.itemId, qty, basePos);
        }
    }

    /// <summary>Inclusive random in [min, max] for long quantities.</summary>
    private static long RandomRangeLong(long min, long max)
    {
        if (max <= min) return min;
        double t = Random.value;
        return min + (long)(t * (max - min + 1));
    }

    /// <summary>Grants combat XP and fires the kill event for quest/slayer tracking.</summary>
    private void AwardKillRewards()
    {
        if (_data == null) return;

        GameEvents.FireMonsterKilled(_data.id);

        // Character XP is derived inside AddSkillXP now, for every skill rather than
        // combat alone — granting it here as well would pay combat twice.
        GameManager.Skills?.AddSkillXP("combat", _data.xpReward);
        GameManager.Audio?.PlayDeath();

        // "onKill" is one of the documented equipment triggers and was the only one
        // nothing ever fired, so a ring that promised something on a kill did nothing.
        ItemEffectResolver.Fire("onKill", _data.id);
        SetBonusResolver.OnKill();
    }

    private void SpawnDrop(string itemId, long qty, Vector3 basePos)
    {
        Vector3 offset = Random.insideUnitSphere * 1.8f;
        offset.y = Mathf.Abs(offset.y) + 0.8f;

        GameObject dropObj = Instantiate(dropPrefab, basePos + offset, Quaternion.identity);
        DropPickup pickup = dropObj.GetComponent<DropPickup>();
        if (pickup != null)
            pickup.Setup(itemId, qty);
    }
}
