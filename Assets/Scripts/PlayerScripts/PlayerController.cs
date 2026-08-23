using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

public class PlayerController : MonoBehaviour
{
    private NavMeshAgent agent;
    public GameObject destinationMarker;
    private GameObject currentMarker;

    public Animator anim;

    public double maxHealthPoints = 100;
    public double healthRegenAmount = 1;
    public float healthRegenSpeed = 1f;
    public float attackDistance = 2f;
    public float attackSpeed = 2f;
    public double attackDamage = 10d;
    public bool autoAttack = false;
    public double dropMultiplier = 1;

    public float movementSpeed = 5f;

    private bool alive = true;
    private double currentHealthPoints = 100;
    private float regenTimer = 0f;
    private float attackTimer = 0f;

    private MonsterController currentTarget;
    private DropPickup currentItemTarget;
    private SkillNodeController currentNodeTarget;

    public TMP_Text healthNumber;
    public RectTransform healthSlider;

    void Start()
    {
        agent = GetComponent<NavMeshAgent>();
        agent.speed = movementSpeed;
        agent.stoppingDistance = 0.1f;

        _spawnPoint    = transform.position;
        _spawnPointSet = true;

        ApplyClassStats();

        // Cosmetics are drawn by their own component so the rig lookup stays out of
        // the controller; adding it here means every spawned player gets it.
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
        var character = CharacterManager.Current;
        if (character == null)
        {
            Debug.LogWarning("[PlayerController] No active character — using Inspector stat defaults.");
            currentHealthPoints = maxHealthPoints;
            GameEvents.OnPlayerHealthChanged?.Invoke(currentHealthPoints, maxHealthPoints);
            return;
        }

        var cls = GameManager.Content?.GetClass(character.classId);
        if (cls != null)
        {
            maxHealthPoints = cls.baseHp;
            attackSpeed     = cls.attackSpeedSeconds;
            attackDamage    = Random.Range(cls.baseAttackMin, cls.baseAttackMax + 1);
        }
        else
        {
            Debug.LogWarning($"[PlayerController] Unknown classId '{character.classId}' — using Inspector defaults.");
        }

        ApplyEquipmentBonuses();

        currentHealthPoints = maxHealthPoints;
        GameEvents.OnPlayerHealthChanged?.Invoke(currentHealthPoints, maxHealthPoints);
    }

    /// <summary>
    /// Layers worn gear on top of the class baseline. Re-runs whenever equipment
    /// changes, so the class stats are recomputed from scratch each time rather than
    /// the bonuses compounding on themselves.
    /// </summary>
    private void ApplyEquipmentBonuses()
    {
        var equipment = GameManager.Equipment;
        if (equipment == null) return;

        maxHealthPoints += equipment.AggregateStat("maxHp");
        attackDamage    += equipment.AggregateStat("attackDamage");

        // Attack speed is an interval, so a bonus makes it SMALLER. Clamped so gear
        // can never drive it to zero and produce an infinite attack rate.
        attackSpeed = Mathf.Max(0.2f, attackSpeed - equipment.AggregateStat("attackSpeed"));
    }

    /// <summary>Recomputes stats from the class baseline plus current gear.</summary>
    private void OnEquipmentChanged()
    {
        double healthFraction = maxHealthPoints > 0 ? currentHealthPoints / maxHealthPoints : 1d;

        ApplyClassStats();

        // Preserve how hurt the player was rather than refilling them — otherwise
        // swapping a ring mid-fight is a free heal.
        currentHealthPoints = System.Math.Max(1d, maxHealthPoints * healthFraction);
        GameEvents.OnPlayerHealthChanged?.Invoke(currentHealthPoints, maxHealthPoints);
    }

    void Update()
    {
        UpdateHealthUI();

        if (!alive)
        {
            anim.SetBool("1_Move", false);
            anim.SetBool("4_Death", true);
            return;
        }

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

        if (currentTarget != null) AttackLogic();
        else if (currentItemTarget != null) PickupLogic();
        else if (currentNodeTarget != null) GatherLogic();

        anim.SetBool("1_Move", agent.velocity.magnitude > 0.1f);

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

        currentTarget     = null;
        currentItemTarget = null;

        if (currentNodeTarget != node)
        {
            ClearNodeTarget();
            currentNodeTarget = node;
            agent.stoppingDistance = Mathf.Max(0.1f, node.interactionRange * 0.6f);
            agent.ResetPath();
            agent.SetDestination(node.transform.position);
        }
    }

    /// <summary>Stops gathering at the current node, if any.</summary>
    private void ClearNodeTarget()
    {
        if (currentNodeTarget != null) currentNodeTarget.StopGathering();
        currentNodeTarget = null;
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
            anim.SetBool("1_Move", false);

            if (!currentNodeTarget.IsGathering)
                currentNodeTarget.BeginGathering();

            if (currentNodeTarget.IsStation && !currentNodeTarget.IsCrafting)
            {
                // A bank chest, or a crafting station with no recipe chosen yet.
                // Standing at it should not look like swinging a pickaxe at it.
                anim.SetBool("2_Attack", false);
            }
            else
            {
                // Reuse the attack animation for the swing of a pickaxe or an axe;
                // dedicated gathering clips arrive with the real art pass.
                anim.SetBool("2_Attack", true);
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
        if (dist <= attackDistance)
        {
            _lastProgressAt = Time.time;      // in range counts as progress
            agent.isStopped = true;
            attackTimer += Time.deltaTime;
            if (attackTimer >= EffectiveAttackSpeed)
            {
                currentTarget.TakeDamage(attackDamage);
                anim.SetBool("1_Move", false);
                anim.SetBool("2_Attack", true);
                attackTimer = 0f;
                ItemEffectResolver.Fire("onHit", null);
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
        currentTarget    = null;
        _closestApproach = float.MaxValue;
        if (agent != null && agent.isOnNavMesh) agent.isStopped = false;
    }

    private void PickupLogic()
    {
        // currentItemTarget == null is true for both C# null and destroyed Unity objects
        var inv = GameManager.Inventory;
        if (currentItemTarget == null ||
            string.IsNullOrEmpty(currentItemTarget.itemId) ||
            (inv != null && !inv.CanAddItem(currentItemTarget.itemId)))
        {
            currentItemTarget = null;
            agent.isStopped = false;
            return;
        }

        float dist = Vector3.Distance(transform.position, currentItemTarget.transform.position);
        if (dist > 60f)
        {
            currentItemTarget = null;
            agent.isStopped = false;
            return;
        }

        // Keep walking toward the item. The trigger collider on DropPickup fires
        // OnTriggerEnter when we overlap it, which destroys the item and clears
        // currentItemTarget on the next PickupLogic call.
        agent.isStopped = false;
        agent.SetDestination(currentItemTarget.transform.position);
    }

    private void HandleMouseClick()
    {
        if (Mouse.current == null || Camera.main == null) return;
        if (EventSystem.current != null &&
            EventSystem.current.IsPointerOverGameObject(Mouse.current.deviceId)) return;

        Ray ray = Camera.main.ScreenPointToRay(Mouse.current.position.ReadValue());
        if (Physics.Raycast(ray, out RaycastHit hit))
        {
            MonsterController monster = hit.collider.GetComponentInParent<MonsterController>();
            SkillNodeController node  = hit.collider.GetComponentInParent<SkillNodeController>();

            if (monster != null)
            {
                SwitchToMonster(monster);
                if (currentMarker) Destroy(currentMarker);
                currentMarker = Instantiate(destinationMarker, monster.transform.position + Vector3.up * 0.1f, Quaternion.identity);
                currentMarker.transform.SetParent(monster.transform);
            }
            else if (node != null)
            {
                SwitchToNode(node);
                if (currentMarker) Destroy(currentMarker);
                currentMarker = Instantiate(destinationMarker, node.transform.position + Vector3.up * 0.1f, Quaternion.identity);
                currentMarker.transform.SetParent(node.transform);
            }
            else
            {
                currentTarget = null;
                currentItemTarget = null;
                ClearNodeTarget();
                agent.ResetPath();
                agent.isStopped = false;
                agent.stoppingDistance = 0.1f;
                agent.SetDestination(hit.point);
                if (currentMarker) Destroy(currentMarker);
                currentMarker = Instantiate(destinationMarker, hit.point + Vector3.up * 0.1f, Quaternion.identity);
            }
        }
    }

    private void CleanupMarker()
    {
        if (currentMarker && agent.isOnNavMesh && !agent.pathPending &&
            agent.remainingDistance <= agent.stoppingDistance + 0.05f)
        {
            Destroy(currentMarker);
            currentMarker = null;
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
    }

    public void TakeDamage(double damageAmount)
    {
        currentHealthPoints = System.Math.Max(0d, currentHealthPoints - damageAmount);
        DamageNumber.Spawn(transform.position, damageAmount, DamageNumber.PlayerTook, "-");
        GameEvents.OnPlayerDamageTaken?.Invoke(damageAmount);
        GameEvents.OnPlayerHealthChanged?.Invoke(currentHealthPoints, maxHealthPoints);

        if (currentHealthPoints <= 0)
        {
            Die();
        }
        else
        {
            anim.SetBool("1_Move", false);
            anim.SetBool("3_Damaged", true);
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
        anim.SetBool("1_Move", false);
        anim.SetBool("4_Death", true);

        currentTarget     = null;
        currentItemTarget = null;
        ClearNodeTarget();

        if (agent != null && agent.isOnNavMesh)
        {
            agent.ResetPath();
            agent.isStopped = true;
        }

        GameManager.Audio?.PlayDeath();
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

        anim.SetBool("4_Death", false);
        anim.SetBool("3_Damaged", false);
        anim.SetBool("2_Attack", false);
        anim.SetBool("1_Move", false);

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
        GameEvents.FireToast("You are back on your feet.");
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
    public void SetAutoAttack(bool autoAttackVal) { autoAttack = autoAttackVal; }

    /// <summary>Number row 1-5 fires the matching action bar slot.</summary>
    private void HandleAbilityKeys()
    {
        var kb = Keyboard.current;
        if (kb == null) return;   // null on touch-only devices

        if (kb.digit1Key.wasPressedThisFrame) UseAbility(0);
        if (kb.digit2Key.wasPressedThisFrame) UseAbility(1);
        if (kb.digit3Key.wasPressedThisFrame) UseAbility(2);
        if (kb.digit4Key.wasPressedThisFrame) UseAbility(3);
        if (kb.digit5Key.wasPressedThisFrame) UseAbility(4);
    }

    // ── Abilities (action bar slots 1-5) ──────────────────────────────────────

    private readonly float[] _abilityReadyAt = new float[5];
    private float _hasteUntil;
    private float _hasteMultiplier = 1f;

    /// <summary>Remaining cooldown in seconds, 0 when ready. Drives the HUD overlay.</summary>
    public float GetAbilityCooldownRemaining(int slot)
    {
        if (slot < 0 || slot >= _abilityReadyAt.Length) return 0f;
        return Mathf.Max(0f, _abilityReadyAt[slot] - Time.time);
    }

    public AbilityData GetAbility(int slot)
    {
        var cls = GameManager.Content?.GetClass(CharacterManager.Current?.classId);
        if (cls?.abilities == null || slot < 0 || slot >= cls.abilities.Length) return null;
        return cls.abilities[slot];
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
            if (announce) GameEvents.FireToast($"{ability.name} is passive — always active.");
            return false;
        }

        if (GetAbilityCooldownRemaining(slot) > 0f)
        {
            if (announce) GameEvents.FireToast($"{ability.name}: {GetAbilityCooldownRemaining(slot):0.0}s");
            return false;
        }

        if (!ApplyAbilityEffect(ability, announce)) return false;

        _abilityReadyAt[slot] = Time.time + ability.cooldownSeconds;
        if (announce) GameEvents.FireToast($"✦ {ability.name}");
        anim.SetBool("2_Attack", true);

        // The ability's own visual, then any worn proc that triggers on casting.
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
        if (Vector3.Distance(transform.position, currentTarget.transform.position) > attackDistance * 1.5f)
            return;

        for (int slot = 0; slot < _abilityReadyAt.Length; slot++)
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
                    double blow = attackDamage * ability.power;
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
                    m.TakeDamage(attackDamage * ability.power);
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
                double amount = maxHealthPoints * ability.power;
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
                    m.TakeDamage(attackDamage * ability.power);
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
                if (ability.power > 0f) Heal(maxHealthPoints * ability.power);
                return true;
            }

            case "summon":
            {
                TurretController.Deploy(transform.position, attackDamage * ability.power,
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

    /// <summary>Attack interval after any active haste buff.</summary>
    private float EffectiveAttackSpeed =>
        Time.time < _hasteUntil ? attackSpeed / _hasteMultiplier : attackSpeed;
}
