using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using IdleExplorers.Rules;

public class PlayerController : MonoBehaviour
{
    private NavMeshAgent agent;

    // What the player has been told to go and do. A point on the ground gets an
    // arrow above it; a monster or a station is lit up in place. See TargetMarker.cs
    // for why one green cylinder could not honestly answer all three.
    private GroundArrow     _groundArrow;
    private TargetHighlight _highlight;

    public Animator anim;

    public double maxHealthPoints = 100;
    public double healthRegenAmount = 1;
    public float healthRegenSpeed = 1f;
    /// <summary>
    /// Reach with nothing equipped. The weapon decides the real figure — see
    /// EffectiveAttackRange — and this is what a bare-handed character falls back to.
    /// </summary>
    public float attackDistance = WeaponProfile.UnarmedRange;

    /// <summary>Swing interval from the stat block, before the weapon has its say.</summary>
    public float attackSpeed = 2f;
    public bool autoAttack = false;
    public double dropMultiplier = 1;

    public float movementSpeed = 5f;

    private bool alive = true;
    private double currentHealthPoints = 100;
    private float regenTimer = 0f;
    private float attackTimer = 0f;

    // ── Mana and stamina ──────────────────────────────────────────────────────
    //
    // baseMp existed in class_data.json from the beginning and nothing ever read it —
    // the HUD's MP bar sat permanently full. Both pools now have a job: abilities
    // declare which they cost, so a caster is limited by mana and a fighter by
    // stamina, and the two are distinguishable rather than decorative.

    private float maxMana;
    private float currentMana;
    private float maxStamina;
    private float currentStamina;

    public float CurrentMana    => currentMana;
    public float MaxMana        => maxMana;
    public float CurrentStamina => currentStamina;
    public float MaxStamina     => maxStamina;

    private MonsterController currentTarget;
    private SpriteFacing      _facing;
    private DropPickup currentItemTarget;
    private SkillNodeController currentNodeTarget;

    public TMP_Text healthNumber;
    public RectTransform healthSlider;

    void Start()
    {
        agent = GetComponent<NavMeshAgent>();
        agent.stoppingDistance = 0.1f;

        // The mirror lives on the art subtree, not here — see SpriteFacing. Attach is
        // idempotent, so a prefab the builder already covered pays nothing.
        _facing = SpriteFacing.Attach(gameObject);

        // Speed is not set here. ApplyClassStats below assigns it from the stat block,
        // and doing it twice would mean the Inspector value briefly won.

        _spawnPoint    = transform.position;
        _spawnPointSet = true;

        ApplyClassStats();

        // Cosmetics are drawn by their own component so the rig lookup stays out of
        // the controller; adding it here means every spawned player gets it.
        // Base appearance first, equipment second. Both are safe in either order —
        // CharacterBaseAppearance invalidates the equipment layer cache after writing
        // the body — but this way the equipment layer captures the right body's feet
        // on its first pass instead of capturing the prefab's and correcting itself.
        if (GetComponent<CharacterBaseAppearance>() == null)
            gameObject.AddComponent<CharacterBaseAppearance>();

        if (GetComponent<CharacterAppearance>() == null)
            gameObject.AddComponent<CharacterAppearance>();
    }

    void OnEnable()  => GameEvents.OnEquipmentChanged += OnEquipmentChanged;
    void OnDisable() => GameEvents.OnEquipmentChanged -= OnEquipmentChanged;

    /// <summary>
    /// Pulls base combat stats from the active character's class in class_data.json.
    /// The Inspector values are only a fallback for testing the scene directly
    /// without going through character select.
    /// </summary>
    private void ApplyClassStats()
    {
        RefreshFromStats(preserveVitals: false);

        if (CharacterManager.Current == null)
            Debug.LogWarning("[PlayerController] No active character — running on the baseline stat block.");
    }

    // ── Stats ─────────────────────────────────────────────────────────────────
    //
    // Four separate methods used to layer class, gear and talents onto each other by
    // hand, each carefully written to avoid compounding into itself — including a
    // captured "base regen" whose only job was to stop one of them doing so. All of
    // that is now StatsManager's problem: it sums every source from scratch and this
    // reads the answer.

    /// <summary>
    /// The character's live stats. Never null, so nothing downstream needs a guard —
    /// a character with no class still has the shared baseline.
    /// </summary>
    private StatBlock Stats => GameManager.Stats?.Current ?? _fallbackStats;

    /// <summary>
    /// Used only when the scene is played directly with no GameManager, which happens
    /// while iterating on a map. Numbers are deliberately dull; this is not balance.
    /// </summary>
    private static readonly StatBlock _fallbackStats = new StatBlock
    {
        health = 100, mana = 50, stamina = 50,
        healthRegen = 1f, manaRegen = 1f, staminaRegen = 2f,
        minHit = 4, maxHit = 8, critChance = 0.05f, critMultiplier = 0.5f,
        attackSpeed = 2f,
        moveSpeed = 5f,
    };

    /// <summary>
    /// Re-reads the stat block into the pools.
    ///
    /// <paramref name="preserveVitals"/> keeps the character as hurt as they were,
    /// proportionally. Without it, swapping a ring mid-fight would be a free heal.
    /// </summary>
    private void RefreshFromStats(bool preserveVitals)
    {
        var stats = Stats;

        double healthFraction  = maxHealthPoints > 0 ? currentHealthPoints / maxHealthPoints : 1d;
        float  manaFraction    = maxMana    > 0f ? currentMana    / maxMana    : 1f;
        float  staminaFraction = maxStamina > 0f ? currentStamina / maxStamina : 1f;

        maxHealthPoints   = stats.EffectiveHealth;
        maxMana           = Mathf.Max(0f, stats.mana);
        maxStamina        = Mathf.Max(0f, stats.stamina);
        healthRegenAmount = stats.healthRegen;
        attackSpeed       = stats.EffectiveAttackSpeed;

        // MonsterController reads this off the player when it rolls its loot table.
        dropMultiplier = 1d + stats.dropRateMultiplier;

        // How fast the character walks was an Inspector field read once in Start and
        // never again, so no class, item or talent could ever affect it. It comes from
        // the stat block now, which means it recomputes whenever anything that feeds
        // the block changes. Floored well above zero: a stack of negative modifiers
        // must never produce a character who cannot leave the spawn point.
        movementSpeed = Mathf.Max(1f, stats.moveSpeed);
        if (agent != null) agent.speed = movementSpeed;

        if (preserveVitals)
        {
            currentHealthPoints = System.Math.Max(1d, maxHealthPoints * healthFraction);
            currentMana         = maxMana    * manaFraction;
            currentStamina      = maxStamina * staminaFraction;
        }
        else
        {
            currentHealthPoints = maxHealthPoints;
            currentMana         = maxMana;
            currentStamina      = maxStamina;
        }

        GameEvents.OnPlayerHealthChanged?.Invoke(currentHealthPoints, maxHealthPoints);
        GameEvents.OnPlayerResourcesChanged?.Invoke(currentMana, maxMana, currentStamina, maxStamina);
    }

    /// <summary>Recomputes from the stat block whenever anything feeding it changes.</summary>
    private void OnEquipmentChanged() => RefreshFromStats(preserveVitals: true);

    void Update()
    {
        UpdateHealthUI();

        // Die() already put the rig into its death state and left it there. Setting
        // it again every frame would re-fire the trigger and restart the clip, which
        // is the opposite of holding the final frame.
        if (!alive) return;

        // Auto-targeting must not yank the player away from a node they chose to
        // gather at — gathering is an explicit commitment, combat is the default.
        //
        // Previously this ran on `Time.frameCount % 15 == 0`, which — because && binds
        // tighter than || — re-picked a target four times a second even mid-pursuit.
        // Every re-pick called ResetPath, so two monsters at similar distance made the
        // agent restart its path forever and the player barely moved.
        if (autoAttack && currentNodeTarget == null && Time.time >= _nextTargetScanAt)
        {
            _nextTargetScanAt = Time.time + TargetScanInterval;
            FindBestAutoTarget();
        }

        // Mouse.current is null on touch-only devices — this project targets mobile,
        // where an unguarded read throws every frame.
        if (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame)
            HandleMouseClick();

        HandleAbilityKeys();
        if (autoAttack) AutoCastAbilities();

        // Standing still mid-fight should not mean facing wherever the last step
        // happened to leave us. Movement still wins when there is any.
        if (_facing != null) _facing.LookTarget = currentTarget != null ? currentTarget.transform : null;

        if (currentTarget != null) AttackLogic();
        else if (currentItemTarget != null) PickupLogic();
        else if (currentNodeTarget != null) GatherLogic();

        SpumAnim.SetMoving(anim, agent.velocity.magnitude > 0.1f);

        CleanupMarker();
        HandleRegen();
    }

    private void UpdateHealthUI()
    {
        // These two are optional now — GameHUD draws the real HP bar and is driven
        // by OnPlayerHealthChanged. They remain for the legacy in-scene world bar.
        if (healthNumber != null)
            healthNumber.text = NumberFormatter.Format((long)currentHealthPoints);
        if (healthSlider != null && maxHealthPoints > 0)
            healthSlider.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal,
                (float)(currentHealthPoints / maxHealthPoints * 190));
    }

    // ── Auto-targeting ────────────────────────────────────────────────────────
    //
    // The rule is simple and deliberate: if there is a living monster anywhere in
    // the scene, auto-mode goes to it, however far away it is. Everything below
    // exists to make that hold up against the two things that used to break it —
    // a monster standing just off the NavMesh, and two candidates swapping places.

    private const float TargetScanInterval = 0.4f;

    /// <summary>How much closer a rival must be before we abandon the current target.</summary>
    private const float SwitchHysteresis = 0.75f;

    /// <summary>How long a monster is skipped after we fail to make progress toward it.</summary>
    private const float UnreachableCooldown = 8f;

    private float _nextTargetScanAt;

    /// <summary>
    /// Reused across every path query. CalculatePath fills a caller-supplied path, and
    /// allocating one per monster per scan was pure garbage — this runs against every
    /// monster in the scene several times a second.
    /// </summary>
    private NavMeshPath _pathScratch;

    /// <summary>
    /// Monsters we could not actually reach, and when to consider them again. Without
    /// this, one monster stranded on unwalkable ground holds auto-mode hostage forever
    /// — which is exactly what "always go to the enemy" would otherwise guarantee.
    /// </summary>
    private readonly Dictionary<MonsterController, float> _skipUntil = new();

    private void FindBestAutoTarget()
    {
        MonsterController bestMonster = GetBestMonster();
        DropPickup        bestItem    = GetClosestReachableItem();

        // Monsters win ties: loot is not going anywhere, and the item is usually
        // lying where the last monster died anyway.
        if (bestMonster != null && bestItem != null)
        {
            float monsterDist = Vector3.Distance(transform.position, bestMonster.transform.position);
            float itemDist    = Vector3.Distance(transform.position, bestItem.transform.position);
            if (monsterDist <= itemDist) SwitchToMonster(bestMonster);
            else                         SwitchToItem(bestItem);
        }
        else if (bestMonster != null) SwitchToMonster(bestMonster);
        else if (bestItem    != null) SwitchToItem(bestItem);
    }

    /// <summary>
    /// The monster auto-mode should be fighting, with NO distance limit.
    ///
    /// Candidates are ranked in tiers — a fully reachable monster beats a partially
    /// reachable one, which beats one we cannot path to at all — and by distance
    /// within a tier. The bottom tier matters: a monster whose position happens to sit
    /// off the NavMesh is still worth walking toward, and the old code discarded it
    /// silently, which is the single biggest reason auto-mode "found no target" in a
    /// scene visibly full of them.
    /// </summary>
    private MonsterController GetBestMonster()
    {
        MonsterController best     = null;
        int               bestTier = -1;
        float             bestDist = float.MaxValue;

        _pathScratch ??= new NavMeshPath();

        foreach (var m in Object.FindObjectsByType<MonsterController>(FindObjectsInactive.Exclude))
        {
            if (m == null || !m.IsAlive()) continue;
            if (_skipUntil.TryGetValue(m, out float until) && Time.time < until) continue;

            float dist = Vector3.Distance(transform.position, m.transform.position);
            int   tier = ReachabilityTier(m.transform.position);

            if (tier > bestTier || (tier == bestTier && dist < bestDist))
            {
                bestTier = tier; bestDist = dist; best = m;
            }
        }

        if (best == null) return null;

        // Stay on the current target unless the newcomer is meaningfully closer.
        // Re-pathing costs a ResetPath, and flip-flopping between two similar
        // candidates is indistinguishable from being stuck.
        if (currentTarget != null && currentTarget.IsAlive() && currentTarget != best &&
            !_skipUntil.ContainsKey(currentTarget))
        {
            float currentDist = Vector3.Distance(transform.position, currentTarget.transform.position);
            if (bestDist > currentDist * SwitchHysteresis) return currentTarget;
        }

        return best;
    }

    /// <summary>
    /// 2 = a complete path exists, 1 = only a partial one, 0 = none at all.
    /// The position is snapped onto the NavMesh first: CalculatePath fails outright
    /// for a destination that is not on the mesh, and a monster standing on a rock or
    /// mid-step off a ledge is off it often enough to matter.
    /// </summary>
    private int ReachabilityTier(Vector3 worldPosition)
    {
        if (agent == null || !agent.isOnNavMesh) return 0;

        _pathScratch ??= new NavMeshPath();

        Vector3 destination = worldPosition;
        if (NavMesh.SamplePosition(worldPosition, out NavMeshHit hit, 6f, NavMesh.AllAreas))
            destination = hit.position;

        if (!agent.CalculatePath(destination, _pathScratch)) return 0;

        return _pathScratch.status switch
        {
            NavMeshPathStatus.PathComplete => 2,
            NavMeshPathStatus.PathPartial  => 1,
            _                              => 0,
        };
    }

    /// <summary>
    /// Marks a monster as not-worth-chasing for a while. Called when pursuit stops
    /// making progress, so auto-mode moves on to something it can actually reach and
    /// tries this one again later rather than writing it off permanently.
    /// </summary>
    private void SkipMonster(MonsterController monster)
    {
        if (monster == null) return;
        _skipUntil[monster] = Time.time + UnreachableCooldown;

        // Drop entries for monsters that have since been destroyed, so a long session
        // does not accumulate one dictionary slot per corpse.
        if (_skipUntil.Count > 32)
        {
            var stale = new List<MonsterController>();
            foreach (var kvp in _skipUntil)
                if (kvp.Key == null || Time.time > kvp.Value) stale.Add(kvp.Key);
            foreach (var key in stale) _skipUntil.Remove(key);
        }
    }

    /// <summary>
    /// Loot still has a range limit, unlike monsters. Walking the length of the map
    /// for one bone while a fight is happening next to you is not what auto-mode is
    /// for — and unlike a monster, an item you ignore is still there later.
    /// </summary>
    private DropPickup GetClosestReachableItem()
    {
        DropPickup best = null;
        float bestDist = float.MaxValue;
        var inv = GameManager.Inventory;

        _pathScratch ??= new NavMeshPath();

        foreach (var item in Object.FindObjectsByType<DropPickup>(FindObjectsInactive.Exclude))
        {
            if (item == null || string.IsNullOrEmpty(item.itemId)) continue;
            if (inv != null && !inv.CanAddItem(item.itemId)) continue;
            if (_skipItemUntil.TryGetValue(item, out float until) && Time.time < until) continue;

            float dist = Vector3.Distance(transform.position, item.transform.position);
            if (dist >= bestDist || dist >= 40f) continue;
            if (ReachabilityTier(item.transform.position) == 0) continue;

            bestDist = dist; best = item;
        }
        return best;
    }

    private void SwitchToNode(SkillNodeController node)
    {
        if (node == null || !node.CanGather()) return;

        HighlightTarget(node.gameObject);

        currentTarget     = null;
        currentItemTarget = null;

        if (currentNodeTarget != node)
        {
            // BeginGathering will set this node's own activity in a moment.
            ClearNodeTarget(revertActivity: false);
            currentNodeTarget = node;
            agent.stoppingDistance = Mathf.Max(0.1f, node.interactionRange * 0.6f);
            agent.ResetPath();
            agent.SetDestination(node.transform.position);
        }
    }

    /// <summary>
    /// Stops gathering at the current node, if any, and stops CLAIMING to.
    ///
    /// The activity snapshot is what the character card, the AFK summary and offline
    /// accrual all read. Nothing used to clear it when the player walked away from a
    /// node, so killing a goblin after a stint at the tin rock left the game reporting
    /// "Mining — Tin Ore" indefinitely: the readout was wrong, and logging out there
    /// would have accrued mining from a rock nobody was standing at.
    ///
    /// Reverting to the map's default combat activity is the honest answer, because
    /// combat is what auto-mode does the moment it is not gathering.
    /// </summary>
    /// <param name="revertActivity">
    /// False when the caller is about to set an activity of its own — switching from
    /// one rock to another would otherwise announce "Now: Combat" and then "Now:
    /// Mining" back to back, and reset the AFK clock twice on the way.
    /// </param>
    private void ClearNodeTarget(bool revertActivity = true)
    {
        bool wasGathering = currentNodeTarget != null;

        // Only when it is THIS node that is lit. SwitchToNode lights the new node
        // before clearing the old one, so an unconditional clear here would put out
        // the light it had just switched on.
        if (currentNodeTarget != null && _highlight != null &&
            _highlight.gameObject == currentNodeTarget.gameObject)
            ClearHighlight();

        if (currentNodeTarget != null) currentNodeTarget.StopGathering();
        currentNodeTarget = null;

        if (wasGathering && revertActivity)
            GameManager.Activity?.SetDefaultCombatActivity(GameManager.Zone?.CurrentMapId);
    }

    /// <summary>Seconds between gathering swings. Roughly a working rhythm.</summary>
    private const float GatherSwingInterval = 1.1f;

    private float _nextGatherSwingAt;

    /// <summary>
    /// Reuses the attack animation for the swing of a pickaxe or an axe — dedicated
    /// gathering clips arrive with the real art pass.
    ///
    /// On a cadence rather than every frame. 2_Attack is a TRIGGER, so setting it
    /// sixty times a second re-fires the clip from its first frame the instant it
    /// finishes, which reads as a twitch rather than as work.
    /// </summary>
    private void SwingAtNode()
    {
        if (Time.time < _nextGatherSwingAt) return;

        _nextGatherSwingAt = Time.time + GatherSwingInterval;
        SpumAnim.PlayAttack(anim);
    }

    private void GatherLogic()
    {
        if (currentNodeTarget == null)
        {
            agent.isStopped = false;
            return;
        }

        float dist = Vector3.Distance(transform.position, currentNodeTarget.transform.position);
        if (dist <= currentNodeTarget.interactionRange)
        {
            agent.isStopped = true;
            SpumAnim.SetMoving(anim, false);

            if (!currentNodeTarget.IsGathering)
                currentNodeTarget.BeginGathering();

            // A bank chest is furniture; you stand at it. Everything else — an ore
            // seam, a tree, a fishing spot, an anvil, a campfire — is worked at, and
            // the character used to stand motionless at all of them because every
            // station counted as furniture. You could hear the mining and see nothing.
            if (currentNodeTarget.IsPassiveStation)
            {
                SpumAnim.CancelAttack(anim);
            }
            else
            {
                SwingAtNode();
                currentNodeTarget.TickGather(Time.deltaTime);
            }
        }
        else
        {
            agent.isStopped = false;
            agent.SetDestination(currentNodeTarget.transform.position);
        }
    }

    private void SwitchToMonster(MonsterController newTarget)
    {
        currentItemTarget = null;
        ClearNodeTarget();

        HighlightTarget(newTarget.gameObject);
        if (currentTarget != newTarget)
        {
            currentTarget = newTarget;
            agent.stoppingDistance = 0f;
            agent.isStopped = false;      // we may have been standing still attacking
            agent.ResetPath();
            agent.SetDestination(newTarget.transform.position);

            // Fresh pursuit gets a fresh clock, or the previous chase's stall carries
            // over and the new target is abandoned the moment it is picked.
            _closestApproach = float.MaxValue;
            _lastProgressAt  = Time.time;
        }
    }

    private void SwitchToItem(DropPickup newItem)
    {
        currentTarget = null;
        ClearNodeTarget();

        // A dropped item already glows for itself, and the goblin that dropped it is
        // no longer what the character is walking to.
        ClearHighlight();
        if (currentItemTarget != newItem)
        {
            currentItemTarget = newItem;
            // Walk right up to the item so the trigger sphere fires reliably.
            agent.stoppingDistance = 0.1f;
            agent.ResetPath();
            agent.SetDestination(newItem.transform.position);
        }
    }

    // ── Pursuit progress ──────────────────────────────────────────────────────
    //
    // Chasing a target with no distance limit needs a way out, or one unreachable
    // monster stalls auto-mode indefinitely. Rather than capping range — which is
    // what stopped auto-mode seeing distant enemies at all — pursuit is abandoned
    // only when it demonstrably stops working.

    private const float StuckTimeout = 6f;

    private float _closestApproach = float.MaxValue;
    private float _lastProgressAt;

    private void AttackLogic()
    {
        if (currentTarget == null || !currentTarget.IsAlive())
        {
            ClearCombatTarget();
            return;
        }

        agent.stoppingDistance = 0f;

        // Snap the destination onto the NavMesh before pathing to it. Monsters that
        // wander onto un-navigable terrain (the mountain) previously left the agent
        // pathing at an unreachable point forever, freezing auto-mode.
        Vector3 targetPos = currentTarget.transform.position;
        if (NavMesh.SamplePosition(targetPos, out NavMeshHit navHit, 6f, NavMesh.AllAreas))
            targetPos = navHit.position;
        agent.SetDestination(targetPos);

        float dist = Vector3.Distance(transform.position, currentTarget.transform.position);
        if (dist <= EffectiveAttackRange)
        {
            _lastProgressAt = Time.time;      // in range counts as progress
            agent.isStopped = true;
            attackTimer += Time.deltaTime;
            if (attackTimer >= EffectiveAttackSpeed)
            {
                var struck = currentTarget;
                double dealt = StrikeMonster(struck);

                GameManager.Audio?.PlayHit();
                SpumAnim.PlayAttack(anim);
                attackTimer = 0f;

                // Talent lifesteal applies to ordinary swings. Ability lifesteal is
                // separate and lives on AbilityData.lifestealFraction — Soul Drain
                // should still be Soul Drain on a character who has taken none.
                float lifesteal = TalentManager.Bonus(TalentManager.LifestealPercent);
                if (lifesteal > 0f) Heal(dealt * lifesteal);

                ItemEffectResolver.Fire("onHit", null);
                SetBonusResolver.OnDamageDealt(this, struck, dealt);
            }
            return;
        }

        agent.isStopped = false;

        // Closing the gap at all resets the clock, so a long walk across the map is
        // fine and only genuine deadlock trips this.
        if (dist < _closestApproach - 0.5f)
        {
            _closestApproach = dist;
            _lastProgressAt  = Time.time;
        }
        else if (Time.time - _lastProgressAt > StuckTimeout)
        {
            Debug.Log($"[PlayerController] Cannot reach {currentTarget.name} " +
                      $"({dist:0.#} units away) — trying something else.");
            SkipMonster(currentTarget);
            ClearCombatTarget();
            _nextTargetScanAt = 0f;   // re-pick immediately rather than idling
        }
    }

    private void ClearCombatTarget()
    {
        // Whatever was lit was this target — the node case clears itself in
        // ClearNodeTarget, and nothing else lights anything.
        ClearHighlight();

        currentTarget    = null;
        _closestApproach = float.MaxValue;
        if (agent != null && agent.isOnNavMesh) agent.isStopped = false;
    }

    /// <summary>Drops we walked to and could not collect, and when to try again.</summary>
    private readonly Dictionary<DropPickup, float> _skipItemUntil = new();

    /// <summary>How long we stand next to a drop before deciding it is out of reach.</summary>
    private const float PickupPatience = 2.5f;

    /// <summary>
    /// Horizontal distance at which we consider ourselves standing on a drop.
    /// Matches the pickup trigger's radius on ItemDrops/GenericDrop, so walking into
    /// range and the collider firing agree with each other.
    /// </summary>
    private const float PickupReach = 1.2f;

    private float _itemArrivedAt;

    private void PickupLogic()
    {
        // currentItemTarget == null is true for both C# null and destroyed Unity objects
        var inv = GameManager.Inventory;
        if (currentItemTarget == null ||
            string.IsNullOrEmpty(currentItemTarget.itemId) ||
            (inv != null && !inv.CanAddItem(currentItemTarget.itemId)))
        {
            ClearItemTarget();
            return;
        }

        Vector3 itemPos = currentItemTarget.transform.position;

        // Horizontal distance, because a drop stranded above the ground is directly
        // overhead — the 3D distance would say we had not arrived and we would keep
        // walking into it forever, which is exactly what floating loot looked like.
        Vector3 flat = itemPos - transform.position;
        flat.y = 0f;
        float dist = flat.magnitude;

        if (dist > 60f)
        {
            ClearItemTarget();
            return;
        }

        // Standing on it and still holding it means the pickup trigger is not reaching
        // us. Pull the drop down onto real ground once, then give up on it for a while
        // so one bad pickup cannot hold auto-mode hostage — the same treatment an
        // unreachable monster gets.
        if (dist <= PickupReach)
        {
            agent.isStopped = true;

            // Ask the drop directly rather than waiting for a trigger callback. The
            // pickup body is kinematic and the player is standing still, which is
            // exactly the case where Unity stops raising trigger events — so relying
            // on physics here is how a character ends up parked on top of its loot.
            if (currentItemTarget.TryCollect())
            {
                ClearItemTarget();
                _nextTargetScanAt = 0f;
                return;
            }

            if (_itemArrivedAt <= 0f) _itemArrivedAt = Time.time;

            if (Time.time - _itemArrivedAt > PickupPatience)
            {
                Debug.Log($"[PlayerController] Could not collect {currentItemTarget.itemId} " +
                          "after standing on it — re-grounding the drop and moving on.");

                currentItemTarget.Reground();
                _skipItemUntil[currentItemTarget] = Time.time + UnreachableCooldown;
                ClearItemTarget();
                _nextTargetScanAt = 0f;
            }
            return;
        }

        _itemArrivedAt = 0f;

        // Keep walking toward the item. The trigger collider on DropPickup fires
        // when we overlap it, which destroys the item and clears currentItemTarget on
        // the next PickupLogic call.
        agent.isStopped = false;
        agent.SetDestination(itemPos);
    }

    private void ClearItemTarget()
    {
        currentItemTarget = null;
        _itemArrivedAt    = 0f;
        if (agent != null && agent.isOnNavMesh) agent.isStopped = false;

        // Same housekeeping the monster skip list gets — one entry per collected drop
        // would otherwise accumulate for the whole session.
        if (_skipItemUntil.Count > 32)
        {
            var stale = new List<DropPickup>();
            foreach (var kvp in _skipItemUntil)
                if (kvp.Key == null || Time.time > kvp.Value) stale.Add(kvp.Key);
            foreach (var key in stale) _skipItemUntil.Remove(key);
        }
    }

    /// <summary>
    /// How far off a target a click may land and still count as aimed at it.
    ///
    /// A goblin is a flat sprite about half a metre wide on a capsule a little wider
    /// than that, seen from a camera thirty units up at fifty degrees. Demanding a hit
    /// on the capsule means demanding pixel accuracy on a moving target, which is what
    /// "extremely difficult to select an enemy" was. This is only consulted when the
    /// precise ray hit nothing worth targeting, so it can never steal a click from
    /// something the player DID hit.
    /// </summary>
    private const float ClickAssistRadius = 0.6f;

    private void HandleMouseClick()
    {
        if (Mouse.current == null || Camera.main == null) return;
        if (EventSystem.current != null &&
            EventSystem.current.IsPointerOverGameObject(Mouse.current.deviceId)) return;

        Ray ray = Camera.main.ScreenPointToRay(Mouse.current.position.ReadValue());

        // The precise ray decides where the ground is, and gets first refusal on what
        // was clicked. Nothing below can override a direct hit.
        if (!Physics.Raycast(ray, out RaycastHit hit)) return;

        var monster = hit.collider.GetComponentInParent<MonsterController>();
        var node    = hit.collider.GetComponentInParent<SkillNodeController>();

        // Only when the ray found bare ground. A near miss on a goblin is a click on
        // the goblin; a click on the goblin's rock is a click on the rock.
        if (monster == null && node == null) FindNearMiss(ray, out monster, out node);

        // The marker is not chosen here. SwitchToMonster and SwitchToNode raise it,
        // so auto-mode picking its own target lights that target up too — which is
        // the whole point of a highlight rather than a marker dropped on click.
        if (monster != null)
        {
            SwitchToMonster(monster);
        }
        else if (node != null)
        {
            SwitchToNode(node);
        }
        else
        {
            currentTarget     = null;
            currentItemTarget = null;
            ClearNodeTarget();

            agent.ResetPath();
            agent.isStopped        = false;
            agent.stoppingDistance = 0.1f;
            agent.SetDestination(hit.point);

            ShowGroundArrow(hit.point);
        }
    }

    /// <summary>
    /// Sweeps a small sphere along the click ray and returns the nearest monster or
    /// node it brushes past.
    ///
    /// SphereCastAll rather than SphereCast, because the first thing a fattened ray
    /// touches on the way to a goblin is usually the ground it is standing on.
    /// </summary>
    private void FindNearMiss(Ray ray, out MonsterController monster, out SkillNodeController node)
    {
        monster = null;
        node    = null;

        var hits = Physics.SphereCastAll(ray, ClickAssistRadius, Camera.main.farClipPlane);
        float best = float.MaxValue;

        foreach (var candidate in hits)
        {
            var m = candidate.collider.GetComponentInParent<MonsterController>();
            var n = candidate.collider.GetComponentInParent<SkillNodeController>();
            if (m == null && n == null) continue;

            // A dead monster is scenery until it despawns, and selecting a corpse
            // leaves the player walking to something they cannot fight.
            if (m != null && !m.IsAlive()) continue;

            if (candidate.distance >= best) continue;

            best    = candidate.distance;
            monster = m;
            node    = m != null ? null : n;
        }
    }

    // ── Showing what is targeted ──────────────────────────────────────────────

    /// <summary>
    /// Points an arrow at a spot on the ground, and puts out whatever was lit.
    ///
    /// The two are mutually exclusive on purpose: an arrow over open ground while a
    /// goblin still glowed would show the player two destinations, only one of which
    /// the character was walking to.
    /// </summary>
    private void ShowGroundArrow(Vector3 point)
    {
        ClearHighlight();

        if (_groundArrow == null) _groundArrow = GroundArrow.ShowAt(point);
        else                      _groundArrow.MoveTo(point);
    }

    /// <summary>
    /// Lights up a monster or a station, and takes the ground arrow down.
    ///
    /// Re-lighting what is already lit is a no-op rather than a rebuild, because
    /// AttackLogic calls through here every time it re-acquires the same target and a
    /// highlight that restarted its pulse each frame would sit at one brightness.
    /// </summary>
    private void HighlightTarget(GameObject target)
    {
        if (target == null) return;

        ClearGroundArrow();

        if (_highlight != null && _highlight.gameObject == target) return;

        ClearHighlight();
        _highlight = TargetHighlight.Apply(target);
    }

    private void ClearGroundArrow()
    {
        if (_groundArrow != null) Destroy(_groundArrow.gameObject);
        _groundArrow = null;
    }

    private void ClearHighlight()
    {
        // Remove puts the original colours back. A dead monster has already restored
        // itself in OnDisable and destroyed the component with the object, so the
        // null check here is the normal path rather than the exceptional one.
        if (_highlight != null) _highlight.Remove();
        _highlight = null;
    }

    /// <summary>
    /// Takes the arrow down on arrival. The highlight deliberately stays: the point
    /// of lighting a goblin up is to know which one is being fought, and that outlives
    /// the walk toward it.
    /// </summary>
    private void CleanupMarker()
    {
        if (_groundArrow != null && agent.isOnNavMesh && !agent.pathPending &&
            agent.remainingDistance <= agent.stoppingDistance + 0.05f)
        {
            ClearGroundArrow();
        }
    }

    private void HandleRegen()
    {
        regenTimer += Time.deltaTime;
        if (regenTimer >= healthRegenSpeed)
        {
            RegenHealth();
            regenTimer = 0f;
        }

        RegenResources(Time.deltaTime);
    }

    /// <summary>
    /// Mana and stamina tick continuously rather than on the health timer, because
    /// they gate abilities: a one-second granularity would make an ability that is
    /// almost affordable feel like it was refusing at random.
    /// </summary>
    private void RegenResources(float deltaTime)
    {
        if (maxMana <= 0f && maxStamina <= 0f) return;

        var stats = Stats;

        float mana    = Mathf.Min(maxMana,    currentMana    + stats.manaRegen    * deltaTime);
        float stamina = Mathf.Min(maxStamina, currentStamina + stats.staminaRegen * deltaTime);

        // Only announce on a visible change — this runs every frame, and the HUD bars
        // do not need a rebuild for a hundredth of a point.
        bool changed = Mathf.Abs(mana - currentMana) > 0.01f ||
                       Mathf.Abs(stamina - currentStamina) > 0.01f;

        currentMana    = mana;
        currentStamina = stamina;

        if (changed)
            GameEvents.OnPlayerResourcesChanged?.Invoke(currentMana, maxMana, currentStamina, maxStamina);
    }

    /// <summary>Whether the pool covers an ability, without spending anything.</summary>
    private bool CanAffordCost(AbilityData ability, bool announce)
    {
        if (ability == null || ability.cost <= 0f) return true;

        float have = ability.costType switch
        {
            "mana"    => currentMana,
            "stamina" => currentStamina,
            _         => float.MaxValue,
        };

        if (have >= ability.cost) return true;

        if (announce)
            GameEvents.FireToast($"Not enough {ability.costType} for {ability.name}.", ChatTone.Bad);
        return false;
    }

    /// <summary>
    /// Spends an ability's cost. Returns false — and spends nothing — when the pool is
    /// short, which is what stops a caster chain-casting their most expensive spell.
    /// </summary>
    private bool TrySpendCost(AbilityData ability, bool announce)
    {
        if (ability == null || ability.cost <= 0f) return true;

        switch (ability.costType)
        {
            case "mana":
                if (currentMana < ability.cost)
                {
                    if (announce) GameEvents.FireToast($"Not enough mana for {ability.name}.", ChatTone.Bad);
                    return false;
                }
                currentMana -= ability.cost;
                break;

            case "stamina":
                if (currentStamina < ability.cost)
                {
                    if (announce) GameEvents.FireToast($"Not enough stamina for {ability.name}.", ChatTone.Bad);
                    return false;
                }
                currentStamina -= ability.cost;
                break;

            default:
                return true;   // "none", or an unrecognised pool: free
        }

        GameEvents.OnPlayerResourcesChanged?.Invoke(currentMana, maxMana, currentStamina, maxStamina);
        return true;
    }

    /// <summary>
    /// Chance that a single hit wears down a piece of armour.
    ///
    /// Not every hit, because at one attack every couple of seconds a full-durability
    /// helmet would break inside a few minutes of ordinary play. At roughly a third,
    /// a 120-point piece survives several hours of continuous fighting — which is the
    /// timescale an idle game is played on.
    /// </summary>
    private const float DurabilityLossChance = 0.34f;

    public void TakeDamage(double damageAmount)
    {
        // Armour applies here rather than at the attacker, so every source of damage
        // to the player is reduced by it — a monster's swing, a trap, anything added
        // later. A floor of 1 keeps armour from ever making the player untouchable.
        damageAmount = System.Math.Max(1d, damageAmount * StatBlock.DamageThrough(Stats.EffectiveArmor));

        currentHealthPoints = System.Math.Max(0d, currentHealthPoints - damageAmount);
        DamageNumber.Spawn(transform.position, damageAmount, DamageNumber.PlayerTook, "-");
        GameManager.Audio?.Play(Sfx.PlayerHurt);
        GameEvents.OnPlayerDamageTaken?.Invoke(damageAmount);
        GameEvents.OnPlayerHealthChanged?.Invoke(currentHealthPoints, maxHealthPoints);

        // Armour wears where it is hit. Rolled before the set bonuses so a set that
        // manipulates durability sees the state this hit actually left behind.
        if (Random.value < DurabilityLossChance)
            GameManager.Equipment?.DamageRandom(SetBonusResolver.DurabilityLossFor(1));

        SetBonusResolver.OnDamageTaken(this, damageAmount);

        if (currentHealthPoints <= 0)
        {
            Die();
        }
        else
        {
            SpumAnim.PlayHurt(anim);
        }
    }

    // ── Death and respawn ─────────────────────────────────────────────────────

    /// <summary>Where the character reappears on respawn — where they entered the map.</summary>
    private Vector3 _spawnPoint;
    private bool    _spawnPointSet;

    private void Die()
    {
        if (!alive) return;

        alive = false;
        SpumAnim.PlayDeath(anim);

        currentTarget     = null;
        currentItemTarget = null;
        ClearNodeTarget();

        // Dying is not a target change, so nothing else would take these down.
        ClearHighlight();
        ClearGroundArrow();

        if (agent != null && agent.isOnNavMesh)
        {
            agent.ResetPath();
            agent.isStopped = true;
        }

        GameManager.Audio?.Play(Sfx.PlayerDeath);
        GameEvents.OnPlayerDied?.Invoke();
    }

    /// <summary>
    /// Puts the character back on their feet at the spawn point with full health.
    /// Called by DeathScreen — there is no death penalty yet beyond the walk back.
    /// </summary>
    public void Respawn()
    {
        alive               = true;
        currentHealthPoints = maxHealthPoints;
        attackTimer         = 0f;
        regenTimer          = 0f;

        SpumAnim.Revive(anim);

        if (agent != null)
        {
            // Warp rather than SetDestination: a plain move would leave the agent
            // pathing away from where the corpse fell. If Start never recorded a
            // spawn point, revive in place rather than teleporting to the origin.
            if (_spawnPointSet &&
                NavMesh.SamplePosition(_spawnPoint, out NavMeshHit hit, 10f, NavMesh.AllAreas))
                agent.Warp(hit.position);

            agent.isStopped = false;
            agent.ResetPath();
        }

        GameEvents.OnPlayerHealthChanged?.Invoke(currentHealthPoints, maxHealthPoints);
        GameEvents.FireToast("You are back on your feet.", ChatTone.Good);
    }

    public void RegenHealth()
    {
        if (currentHealthPoints >= maxHealthPoints) return;
        currentHealthPoints = System.Math.Min(maxHealthPoints, currentHealthPoints + healthRegenAmount);
        GameEvents.OnPlayerHealthChanged?.Invoke(currentHealthPoints, maxHealthPoints);
    }

    /// <summary>
    /// Restores a flat amount. Returns false at full health so a consumable is not
    /// eaten for nothing.
    /// </summary>
    public bool Heal(double amount)
    {
        if (!alive || amount <= 0) return false;
        if (currentHealthPoints >= maxHealthPoints) return false;

        // Report what was actually restored, not what was offered — healing for 25 at
        // 3 HP from full should not claim 25.
        double before = currentHealthPoints;
        currentHealthPoints = System.Math.Min(maxHealthPoints, currentHealthPoints + amount);

        DamageNumber.Spawn(transform.position, currentHealthPoints - before, DamageNumber.Healed, "+");
        GameEvents.OnPlayerHealthChanged?.Invoke(currentHealthPoints, maxHealthPoints);
        return true;
    }

    public bool IsAlive() => alive;

    /// <summary>
    /// Turns auto-mode on or off and announces it.
    ///
    /// The event exists because the HUD is cached and rebuilt across characters and
    /// screens, so a button that only recoloured itself on click showed the wrong
    /// state the moment anything else redrew it. Anything that wants to display
    /// auto-mode subscribes and asks; nothing has to guess.
    /// </summary>
    public void SetAutoAttack(bool autoAttackVal)
    {
        if (autoAttack == autoAttackVal) return;

        autoAttack = autoAttackVal;

        if (!autoAttack)
        {
            // Leaving auto-mode should not leave the character jogging toward whatever
            // it had picked. Gathering is a deliberate choice and is left running.
            ClearCombatTarget();
            ClearItemTarget();
        }

        GameEvents.OnAutoModeChanged?.Invoke(autoAttack);
    }

    /// <summary>Whether auto-mode is currently running. Read by the HUD indicator.</summary>
    public bool AutoModeEnabled => autoAttack;

    /// <summary>Number row 1-5 fires the matching action bar slot.</summary>
    private void HandleAbilityKeys()
    {
        var kb = Keyboard.current;
        if (kb == null) return;   // null on touch-only devices

        // Typing "1 goblin left" must not cast the ability in slot one. The chat
        // field raises this flag while it holds focus.
        if (UIManager.TextInputFocused) return;

        if (kb.digit1Key.wasPressedThisFrame) UseAbility(0);
        if (kb.digit2Key.wasPressedThisFrame) UseAbility(1);
        if (kb.digit3Key.wasPressedThisFrame) UseAbility(2);
        if (kb.digit4Key.wasPressedThisFrame) UseAbility(3);
        if (kb.digit5Key.wasPressedThisFrame) UseAbility(4);
    }

    // ── Abilities (action bar slots 1-5) ──────────────────────────────────────

    /// <summary>
    /// Cooldowns, keyed by ABILITY ID rather than bar slot.
    ///
    /// This used to be a float[5] indexed by slot, which was fine only while the bar
    /// was fixed. The moment abilities became draggable that would have meant moving
    /// Fireball to another slot handed it that slot's cooldown — spend it in slot 1,
    /// drag it to slot 3, and it is ready again.
    /// </summary>
    private readonly Dictionary<string, float> _abilityReadyAt = new();

    private float _hasteUntil;
    private float _hasteMultiplier = 1f;

    /// <summary>Remaining cooldown in seconds, 0 when ready. Drives the HUD overlay.</summary>
    public float GetAbilityCooldownRemaining(int slot) =>
        GetAbilityCooldownRemaining(GetAbility(slot));

    public float GetAbilityCooldownRemaining(AbilityData ability)
    {
        if (ability == null || string.IsNullOrEmpty(ability.id)) return 0f;
        if (!_abilityReadyAt.TryGetValue(ability.id, out float readyAt)) return 0f;

        return Mathf.Max(0f, readyAt - Time.time);
    }

    /// <summary>
    /// An ability's cooldown after talent reduction — what the HUD sweep must divide
    /// by. Dividing by the raw cooldownSeconds would make the sweep start part-filled
    /// and never quite reach the top for anyone who took a cooldown talent.
    /// </summary>
    public float GetAbilityCooldownLength(AbilityData ability)
    {
        if (ability == null) return 0f;

        float reduction = TalentManager.ReductionMultiplier(TalentManager.CooldownPercent);

        // An ability-scoped cooldown talent applies on top of the character-wide one.
        reduction *= Mathf.Max(0.25f,
            1f - TalentManager.AbilityBonus(CharacterManager.Current, ability.id,
                                            TalentManager.CooldownPercent));

        return ability.cooldownSeconds * reduction;
    }

    /// <summary>
    /// What is in an action bar slot.
    ///
    /// THE single place a slot becomes an ability. It used to be resolved as
    /// ClassData.abilities[slot] here AND independently in GameHUD, so the two could
    /// disagree and slot position was the ability's only identity. Now the character's
    /// hotbar decides, and both callers come through here.
    /// </summary>
    public AbilityData GetAbility(int slot)
    {
        var character = CharacterManager.Current;
        if (character == null || slot < 0 || slot >= CharacterData.HotbarSlots) return null;

        string abilityId = character.Hotbar()[slot];
        if (string.IsNullOrEmpty(abilityId)) return null;

        // Resolved against every specced class, so a Paladin's bar can hold Cleave and
        // Fireball at the same time.
        AbilityData fromTalents = TalentManager.FindAbilityFor(character, abilityId);
        if (fromTalents != null) return fromTalents;

        // Then whatever is WORN. The Goblin Destroyer grants Whirling Throw, and that
        // ability belongs to the weapon rather than to a spec -- putting it in a class
        // tree would let anyone with that class use it without ever finding the sword.
        return GrantedByEquipment(abilityId);
    }

    /// <summary>
    /// An ability an equipped item grants, or null.
    ///
    /// Checked on every read rather than cached on equip, because the answer changes
    /// the instant the weapon comes off -- and a cached one would leave a button on
    /// the bar that fires an ability the character no longer has any claim to.
    ///
    /// A broken piece grants nothing, matching every other passive: durability is the
    /// payoff for maintaining gear, and an ability that survived at zero would make
    /// the best item in the game the one you never have to repair.
    /// </summary>
    private AbilityData GrantedByEquipment(string abilityId)
    {
        var equipment = GameManager.Equipment;
        var content   = GameManager.Content;

        if (equipment == null || content == null) return null;

        foreach (var item in equipment.EquippedItems())
        {
            if (item?.effects == null) continue;

            foreach (var effect in item.effects)
            {
                if (effect == null) continue;
                if (effect.action != "grantAbility") continue;
                if (effect.param != abilityId) continue;

                return content.Catalogue.GetAbility(abilityId);
            }
        }

        return null;
    }

    /// <summary>
    /// Puts an ability into a slot, REPLACING whatever was there.
    ///
    /// This is the talent-tree route onto the bar, and it is deliberately not a swap.
    /// It used to move the target slot's ability into wherever the dragged one already
    /// sat, so dropping a known ability onto slot 3 quietly rearranged slot 1 as well
    /// — the abilities "shifted around" for reasons nothing on screen explained.
    /// Dragging one bar slot onto another still swaps; see SwapAbilitySlots. That
    /// distinction is the whole reason AbilityDrag carries where it came from.
    ///
    /// The one thing it will not do is leave the same ability on the bar twice: two
    /// copies share a cooldown, so the second looks broken. If the ability was already
    /// somewhere else, that slot is EMPTIED rather than filled with the displaced one
    /// — an empty slot is a state the player can see and understand, where a silent
    /// reshuffle is not.
    /// </summary>
    public bool AssignAbility(int slot, string abilityId)
    {
        var character = CharacterManager.Current;
        if (character == null || slot < 0 || slot >= CharacterData.HotbarSlots) return false;

        if (!string.IsNullOrEmpty(abilityId) && !TalentManager.HasAbility(character, abilityId))
        {
            GameEvents.FireToast("You have not learned that yet.", ChatTone.Bad);
            return false;
        }

        var hotbar = character.Hotbar();

        int existing = hotbar.IndexOf(abilityId);
        if (!string.IsNullOrEmpty(abilityId) && existing >= 0 && existing != slot)
            hotbar[existing] = "";

        hotbar[slot] = abilityId ?? "";

        GameManager.Save?.Save();
        GameEvents.OnHotbarChanged?.Invoke();
        return true;
    }

    /// <summary>Swaps two bar slots, for dragging one ability onto another.</summary>
    public void SwapAbilitySlots(int from, int to)
    {
        var character = CharacterManager.Current;
        if (character == null || from == to) return;
        if (from < 0 || to < 0 || from >= CharacterData.HotbarSlots || to >= CharacterData.HotbarSlots) return;

        var hotbar = character.Hotbar();
        (hotbar[from], hotbar[to]) = (hotbar[to], hotbar[from]);

        GameManager.Save?.Save();
        GameEvents.OnHotbarChanged?.Invoke();
    }

    /// <summary>Fires the ability in the given action bar slot (0-4).</summary>
    public void UseAbility(int slot) => UseAbility(slot, announce: true);

    /// <summary>
    /// Fires an ability. <paramref name="announce"/> is false for auto-cast, which
    /// tries every slot several times a second — the toasts a manual press earns
    /// ("No target", "3.4s") would be a solid wall of text on the same code path.
    /// </summary>
    public bool UseAbility(int slot, bool announce)
    {
        // Update() early-returns before HandleAbilityKeys when dead, so the keyboard
        // path was already safe — but the HUD buttons call straight in here, and a
        // corpse casting Fireball is not a feature.
        if (!alive) return false;

        var ability = GetAbility(slot);
        if (ability == null) return false;

        if (!ability.IsActivatable)
        {
            if (announce) GameEvents.FireToast($"{ability.name} is passive — always active.", ChatTone.Bad);
            return false;
        }

        if (GetAbilityCooldownRemaining(slot) > 0f)
        {
            if (announce) GameEvents.FireToast($"{ability.name}: {GetAbilityCooldownRemaining(slot):0.0}s", ChatTone.Bad);
            return false;
        }

        // Cost is checked BEFORE the effect but spent only if the effect took hold —
        // otherwise a heal refused at full health would still empty the mana bar.
        if (!CanAffordCost(ability, announce)) return false;

        if (!ApplyAbilityEffect(ability, announce)) return false;

        TrySpendCost(ability, announce: false);

        // Against the ability, not the slot — and through the same helper the HUD's
        // sweep divides by, so the two cannot disagree about how long it lasts.
        _abilityReadyAt[ability.id] = Time.time + GetAbilityCooldownLength(ability);

        if (announce) GameEvents.FireToast($"✦ {ability.name}");
        SpumAnim.PlayAttack(anim);

        // The ability's own visual, then any worn proc that triggers on casting.
        GameManager.Audio?.Play(Sfx.AbilityCast);
        AbilityVFX.Play(AbilityVfxId(ability), AbilityOrigin(ability));
        ItemEffectResolver.Fire("onAbilityUse", ability.id);
        return true;
    }

    // ── Auto-cast ─────────────────────────────────────────────────────────────

    /// <summary>Minimum gap between auto-cast abilities, so they do not all fire at once.</summary>
    private const float AutoCastInterval = 1.25f;

    private float _nextAutoCastAt;

    /// <summary>
    /// Spends abilities while auto-mode is fighting.
    ///
    /// Two effect types are deliberately excluded. "blink" teleports toward the mouse
    /// cursor, which in auto-mode is wherever the player happened to leave it, and
    /// "stealth" clears the current target by design — both would fight the auto-mode
    /// they were cast from. Everything else is fair game, cheapest first: the ability
    /// itself refuses when it would do nothing (heal at full HP, aoe with nothing in
    /// range), so no separate list of preconditions is needed here.
    /// </summary>
    private void AutoCastAbilities()
    {
        if (!alive || currentTarget == null || !currentTarget.IsAlive()) return;
        if (Time.time < _nextAutoCastAt) return;

        // Only once we are actually engaged — casting Cleave while still jogging
        // across the map wastes the cooldown on nothing.
        if (Vector3.Distance(transform.position, currentTarget.transform.position) > EffectiveAttackRange * 1.5f)
            return;

        // Walks the bar in order, so slot position is now a player-facing auto-cast
        // PRIORITY rather than just a keybind — arranging the bar decides what an
        // unattended character reaches for first.
        for (int slot = 0; slot < CharacterData.HotbarSlots; slot++)
        {
            var ability = GetAbility(slot);
            if (ability == null || !ability.IsActivatable) continue;
            if (ability.effect == "blink" || ability.effect == "stealth") continue;
            if (GetAbilityCooldownRemaining(slot) > 0f) continue;

            if (UseAbility(slot, announce: false))
            {
                _nextAutoCastAt = Time.time + AutoCastInterval;
                return;
            }
        }
    }

    /// <summary>
    /// Which effect prefab an ability plays. A per-ability vfxAddress wins; otherwise
    /// the effect type picks a shared default, so every ability has SOME visual
    /// without needing bespoke art for all twenty-five.
    /// </summary>
    private static string AbilityVfxId(AbilityData ability)
    {
        if (!string.IsNullOrEmpty(ability.vfxAddress)) return ability.vfxAddress;

        return ability.effect switch
        {
            "damage"  => "impact",
            "aoe"     => "aoe_burst",
            "heal"    => "heal",
            "haste"   => "haste_aura",
            "slow"    => "frost",
            "stealth" => "smoke",
            "blink"   => "blink",
            "summon"  => "aoe_burst",
            _         => null,
        };
    }

    /// <summary>Single-target effects play on the victim; everything else on the caster.</summary>
    private Vector3 AbilityOrigin(AbilityData ability)
    {
        bool onTarget = ability.effect == "damage" && currentTarget != null;
        return onTarget ? currentTarget.transform.position : transform.position;
    }

    /// <summary>Returns false when the ability could not be used (e.g. no target).</summary>
    private bool ApplyAbilityEffect(AbilityData ability, bool announce)
    {
        // Auto-cast walks all five slots several times a second, so the refusals a
        // deliberate keypress deserves an explanation for would be a wall of toasts.
        void Explain(string reason) { if (announce) GameEvents.FireToast(reason); }

        // Talent ability power scales what an ability produces — damage dealt, health
        // restored, turret output. Deliberately NOT applied to "haste", whose power is
        // an attack-rate multiplier rather than an amount: scaling 1.5x up to 1.8x
        // reads as the same +20% and is worth several times as much.
        float power = ability.power * TalentManager.Multiplier(TalentManager.AbilityPowerPercent);

        // Ranks poured into this specific ability's own talent node. This is what
        // makes upgrading one ability in the tree different from a general power stat.
        power *= TalentManager.AbilityPowerMultiplier(CharacterManager.Current, ability.id);

        switch (ability.effect)
        {
            case "damage":
            {
                if (currentTarget == null || !currentTarget.IsAlive())
                {
                    Explain("No target.");
                    return false;
                }

                // Rapid Shot says "three quick shots" and used to land exactly one.
                int    strikes = Mathf.Max(1, ability.hits);
                double dealt   = 0d;

                for (int i = 0; i < strikes && currentTarget != null && currentTarget.IsAlive(); i++)
                {
                    double blow = AttackDamage * power;
                    currentTarget.TakeDamage(blow);
                    dealt += blow;
                }

                // Soul Drain: "what it loses, you gain".
                if (ability.lifestealFraction > 0f)
                    Heal(dealt * ability.lifestealFraction);

                return true;
            }

            case "aoe":
            {
                int hits = 0;
                foreach (var m in Object.FindObjectsByType<MonsterController>(FindObjectsInactive.Exclude))
                {
                    if (!m.IsAlive()) continue;
                    if (Vector3.Distance(transform.position, m.transform.position) > ability.aoeRadius) continue;
                    m.TakeDamage(AttackDamage * power);
                    hits++;
                }
                if (hits == 0) { Explain("Nothing in range."); return false; }
                return true;
            }

            case "heal":
            {
                if (currentHealthPoints >= maxHealthPoints)
                {
                    Explain("Already at full health.");
                    return false;
                }
                double amount = maxHealthPoints * power;
                currentHealthPoints = System.Math.Min(maxHealthPoints, currentHealthPoints + amount);
                GameEvents.OnPlayerHealthChanged?.Invoke(currentHealthPoints, maxHealthPoints);
                return true;
            }

            case "haste":
                _hasteMultiplier = Mathf.Max(1f, ability.power);
                _hasteUntil      = Time.time + ability.durationSeconds;
                return true;

            // ── Effects added so the descriptions stop lying ──────────────────
            //
            // Frost Nova, Smoke Bomb, Blink and Deploy Turret all shipped as plain
            // "aoe"/"heal" and did none of what their text claimed.

            case "slow":
            {
                int slowed = 0;
                foreach (var m in InRange(ability.aoeRadius))
                {
                    m.TakeDamage(AttackDamage * power);
                    m.ApplySlow(0.4f, ability.durationSeconds);
                    slowed++;
                }
                if (slowed == 0) { Explain("Nothing in range."); return false; }
                return true;
            }

            case "stealth":
            {
                int dropped = 0;
                foreach (var m in InRange(ability.aoeRadius))
                {
                    m.DropAggro(ability.durationSeconds);
                    dropped++;
                }
                if (dropped == 0) { Explain("Nothing is watching you."); return false; }

                // Being forgotten mid-fight should also end your own pursuit,
                // otherwise you immediately walk back into what just lost you.
                currentTarget = null;
                return true;
            }

            case "blink":
            {
                if (!TryBlink(ability.aoeRadius))
                {
                    Explain("Nowhere to blink to.");
                    return false;
                }
                if (power > 0f) Heal(maxHealthPoints * power);
                return true;
            }

            case "summon":
            {
                TurretController.Deploy(transform.position, AttackDamage * power,
                                         ability.aoeRadius, ability.durationSeconds);
                return true;
            }

            case "bladestorm":
            {
                // Thrown at the current target, or at the character's feet with
                // nothing selected -- a whirlwind that refuses to fire because
                // nothing is targeted is a whirlwind nobody uses defensively.
                Vector3 at = currentTarget != null
                    ? currentTarget.transform.position
                    : transform.position;

                // Damage per PULSE, so the ability's power reads as "per tick" rather
                // than as a total that has to be divided by a duration nobody sees.
                Bladestorm.Throw(transform, at, AttackDamage * power,
                                 ability.aoeRadius, ability.durationSeconds);

                return true;
            }

            default:
                Explain($"{ability.name} does nothing yet.");
                return false;
        }
    }

    /// <summary>Living monsters within a radius of the player.</summary>
    private List<MonsterController> InRange(float radius)
    {
        var found = new List<MonsterController>();

        foreach (var m in Object.FindObjectsByType<MonsterController>(FindObjectsInactive.Exclude))
        {
            if (m == null || !m.IsAlive()) continue;
            if (Vector3.Distance(transform.position, m.transform.position) > radius) continue;
            found.Add(m);
        }
        return found;
    }

    /// <summary>
    /// Teleports toward the cursor, or straight ahead when there is no pointer.
    /// Snapped to the NavMesh so a blink can never strand the agent off-mesh, which
    /// would leave the player unable to move at all.
    /// </summary>
    private bool TryBlink(float distance)
    {
        Vector3 direction = transform.forward;

        var mouse  = Mouse.current;
        var camera = Camera.main;
        if (mouse != null && camera != null &&
            Physics.Raycast(camera.ScreenPointToRay(mouse.position.ReadValue()), out RaycastHit hit, 200f))
        {
            Vector3 toCursor = hit.point - transform.position;
            toCursor.y = 0f;
            if (toCursor.sqrMagnitude > 0.01f) direction = toCursor.normalized;
        }

        Vector3 desired = transform.position + direction * distance;

        if (!NavMesh.SamplePosition(desired, out NavMeshHit navHit, distance, NavMesh.AllAreas))
            return false;

        agent.Warp(navHit.position);
        return true;
    }

    /// <summary>
    /// Rolls one attack and applies it, returning what actually landed.
    ///
    /// The whole combat model in one place. Damage used to be a single number rolled
    /// ONCE when the character spawned and then never again, so every swing for the
    /// rest of the session hit for exactly the same amount. Now each swing rolls
    /// between the character's minimum and maximum hit, can crit, and is reduced by
    /// the target's armour.
    /// </summary>
    private double StrikeMonster(MonsterController target)
    {
        if (target == null) return 0d;

        var mainHand = MainHand;
        var profile  = Stats.Resolve();

        // The weapon widens the band rather than replacing it, so an unarmed
        // character is weak instead of harmless — and so a swing still crits at the
        // character's own rate rather than the weapon having to restate it.
        var weapon = WeaponProfile.WeaponDamage(mainHand);
        profile.Min += weapon.Min;
        profile.Max += weapon.Max;

        int shots = WeaponProfile.ProjectilesPerShot(mainHand);
        if (shots > 0) return FireVolley(target, profile, shots);

        double dealt = profile.Roll(UnityRandomSource.Instance, out bool wasCrit);
        dealt *= StatBlock.DamageThrough(target.Armor);
        dealt  = System.Math.Max(1d, dealt);

        target.TakeDamage(dealt, wasCrit);
        return dealt;
    }

    /// <summary>
    /// Fires a ranged shot, or several.
    ///
    /// ══ THE DAMAGE IS DECIDED HERE, NOT ON ARRIVAL ════════════════════════════
    ///
    /// Every arrow is rolled and reduced by armour before it leaves the bow, and the
    /// projectile only carries the number to the target so it lands with the impact
    /// rather than the twang. If arrival decided the hit, the client would be
    /// deciding it too, by moving the arrow — which is the whole class of thing the
    /// server-authoritative work exists to remove.
    ///
    /// Each arrow rolls SEPARATELY. Three arrows sharing one roll is one big arrow
    /// drawn three times, and it would crit as one: the Trisong Bow is meant to feel
    /// like a spread, which means each one can be the good one.
    ///
    /// The return value is what was fired, for the caller's lifesteal and logging.
    /// Nothing has actually landed yet.
    /// </summary>
    private double FireVolley(MonsterController target, DamageProfile profile, int shots)
    {
        double armour = StatBlock.DamageThrough(target.Armor);
        double total  = 0d;

        // Centred: one arrow flies straight, three fan to -1, 0, +1.
        float middle = (shots - 1) * 0.5f;

        for (int i = 0; i < shots; i++)
        {
            double dealt = profile.Roll(UnityRandomSource.Instance, out bool wasCrit) * armour;
            dealt = System.Math.Max(1d, dealt);

            Projectile.Launch(transform.position + Vector3.up * 0.9f,
                              target, dealt, wasCrit, spread: (i - middle) * 0.55f);

            total += dealt;
        }

        return total;
    }

    /// <summary>
    /// A representative swing, for effects that scale off the character's damage
    /// rather than rolling their own — set bonuses and abilities.
    ///
    /// The midpoint of the band rather than a fresh roll, so an ability that reads
    /// "5x your damage" is not itself a gamble on top of the gamble it already is.
    /// </summary>
    public double AttackDamage
    {
        get
        {
            var profile = Stats.Resolve();
            var weapon  = WeaponProfile.WeaponDamage(MainHand);

            return (profile.Min + weapon.Min + profile.Max + weapon.Max) * 0.5d;
        }
    }

    /// <summary>Whatever is in the right hand, or null for bare knuckles.</summary>
    private ItemData MainHand => GameManager.Equipment?.GetEquippedItem("mainhand");

    /// <summary>
    /// How close to get before swinging.
    ///
    /// This was a hard-coded 2f, which is why a bow and a fist had identical reach.
    /// Resolved through the shared rules so the server validates against the same
    /// number the client walks to.
    /// </summary>
    public float EffectiveAttackRange
    {
        get
        {
            var weapon = MainHand;
            return weapon != null ? WeaponProfile.AttackRange(weapon) : attackDistance;
        }
    }

    /// <summary>Attack interval from the weapon, then the stat block, then any haste.</summary>
    private float EffectiveAttackSpeed
    {
        get
        {
            float seconds = WeaponProfile.AttackSeconds(MainHand, attackSpeed);
            return Time.time < _hasteUntil ? seconds / _hasteMultiplier : seconds;
        }
    }
}
