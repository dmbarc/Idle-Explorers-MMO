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
    }

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

        currentHealthPoints = maxHealthPoints;
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
        if (autoAttack && currentNodeTarget == null &&
            (currentTarget == null && currentItemTarget == null || Time.frameCount % 15 == 0))
            FindBestAutoTarget();

        // Mouse.current is null on touch-only devices — this project targets mobile,
        // where an unguarded read throws every frame.
        if (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame)
            HandleMouseClick();

        HandleAbilityKeys();

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

    private void FindBestAutoTarget()
    {
        MonsterController bestMonster = GetClosestReachableMonster();
        DropPickup bestItem = GetClosestReachableItem();

        if (bestMonster != null && bestItem != null)
        {
            float monsterDist = Vector3.Distance(transform.position, bestMonster.transform.position);
            float itemDist = Vector3.Distance(transform.position, bestItem.transform.position);
            if (monsterDist < itemDist) SwitchToMonster(bestMonster);
            else SwitchToItem(bestItem);
        }
        else if (bestMonster != null) SwitchToMonster(bestMonster);
        else if (bestItem != null) SwitchToItem(bestItem);
    }

    private MonsterController GetClosestReachableMonster()
    {
        MonsterController best = null;
        float bestDist = float.MaxValue;
        foreach (var m in Object.FindObjectsByType<MonsterController>(FindObjectsInactive.Exclude))
        {
            if (!m.IsAlive()) continue;
            float dist = Vector3.Distance(transform.position, m.transform.position);
            if (dist < bestDist && dist < 40f && agent.CalculatePath(m.transform.position, new NavMeshPath()))
            {
                bestDist = dist; best = m;
            }
        }
        return best;
    }

    private DropPickup GetClosestReachableItem()
    {
        DropPickup best = null;
        float bestDist = float.MaxValue;
        var inv = GameManager.Inventory;
        foreach (var item in Object.FindObjectsByType<DropPickup>(FindObjectsInactive.Exclude))
        {
            if (string.IsNullOrEmpty(item.itemId)) continue;
            if (inv != null && !inv.CanAddItem(item.itemId)) continue;
            float dist = Vector3.Distance(transform.position, item.transform.position);
            if (dist < bestDist && dist < 25f && agent.CalculatePath(item.transform.position, new NavMeshPath()))
            {
                bestDist = dist; best = item;
            }
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
            agent.ResetPath();
            agent.SetDestination(newTarget.transform.position);
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

    private void AttackLogic()
    {
        if (currentTarget == null || !currentTarget.IsAlive() ||
            !agent.CalculatePath(currentTarget.transform.position, new NavMeshPath()) ||
            Vector3.Distance(transform.position, currentTarget.transform.position) > 40f)
        {
            currentTarget = null;
            agent.isStopped = false;
            return;
        }

        agent.stoppingDistance = 0f;

        // Snap the destination onto the NavMesh before pathing to it. Monsters that
        // wander onto un-navigable terrain (the mountain) previously left the agent
        // pathing at an unreachable point forever, freezing auto-mode.
        Vector3 targetPos = currentTarget.transform.position;
        if (NavMesh.SamplePosition(targetPos, out NavMeshHit navHit, 3f, NavMesh.AllAreas))
            targetPos = navHit.position;
        agent.SetDestination(targetPos);

        float dist = Vector3.Distance(transform.position, currentTarget.transform.position);
        if (dist <= attackDistance)
        {
            agent.isStopped = true;
            attackTimer += Time.deltaTime;
            if (attackTimer >= EffectiveAttackSpeed)
            {
                currentTarget.TakeDamage(attackDamage);
                anim.SetBool("1_Move", false);
                anim.SetBool("2_Attack", true);
                attackTimer = 0f;
            }
        }
        else
        {
            agent.isStopped = false;
        }
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

        currentHealthPoints = System.Math.Min(maxHealthPoints, currentHealthPoints + amount);
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
    public void UseAbility(int slot)
    {
        var ability = GetAbility(slot);
        if (ability == null) return;

        if (!ability.IsActivatable)
        {
            GameEvents.FireToast($"{ability.name} is passive — always active.");
            return;
        }

        if (GetAbilityCooldownRemaining(slot) > 0f)
        {
            GameEvents.FireToast($"{ability.name}: {GetAbilityCooldownRemaining(slot):0.0}s");
            return;
        }

        if (!ApplyAbilityEffect(ability)) return;

        _abilityReadyAt[slot] = Time.time + ability.cooldownSeconds;
        GameEvents.FireToast($"✦ {ability.name}");
        anim.SetBool("2_Attack", true);
    }

    /// <summary>Returns false when the ability could not be used (e.g. no target).</summary>
    private bool ApplyAbilityEffect(AbilityData ability)
    {
        switch (ability.effect)
        {
            case "damage":
                if (currentTarget == null || !currentTarget.IsAlive())
                {
                    GameEvents.FireToast("No target.");
                    return false;
                }
                currentTarget.TakeDamage(attackDamage * ability.power);
                return true;

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
                if (hits == 0) { GameEvents.FireToast("Nothing in range."); return false; }
                return true;
            }

            case "heal":
            {
                if (currentHealthPoints >= maxHealthPoints)
                {
                    GameEvents.FireToast("Already at full health.");
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

            default:
                GameEvents.FireToast($"{ability.name} does nothing yet.");
                return false;
        }
    }

    /// <summary>Attack interval after any active haste buff.</summary>
    private float EffectiveAttackSpeed =>
        Time.time < _hasteUntil ? attackSpeed / _hasteMultiplier : attackSpeed;
}
