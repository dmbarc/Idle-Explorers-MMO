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

            // Reuse the attack animation for the swing of a pickaxe or an axe;
            // dedicated gathering clips arrive with the real art pass.
            anim.SetBool("2_Attack", true);
            currentNodeTarget.TickGather(Time.deltaTime);
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
            if (attackTimer >= attackSpeed)
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
            alive = false;
            anim.SetBool("1_Move", false);
            anim.SetBool("4_Death", true);
        }
        else
        {
            anim.SetBool("1_Move", false);
            anim.SetBool("3_Damaged", true);
        }
    }

    public void RegenHealth()
    {
        if (currentHealthPoints >= maxHealthPoints) return;
        currentHealthPoints = System.Math.Min(maxHealthPoints, currentHealthPoints + healthRegenAmount);
        GameEvents.OnPlayerHealthChanged?.Invoke(currentHealthPoints, maxHealthPoints);
    }

    public bool IsAlive() => alive;
    public void SetAutoAttack(bool autoAttackVal) { autoAttack = autoAttackVal; }
}
