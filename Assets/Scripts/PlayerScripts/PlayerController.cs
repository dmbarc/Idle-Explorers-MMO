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

    public TMP_Text healthNumber;
    public RectTransform healthSlider;

    private PlayerInventory inventory;

    void Start()
    {
        agent = GetComponent<NavMeshAgent>();
        inventory = GetComponent<PlayerInventory>();
        agent.speed = movementSpeed;
        agent.stoppingDistance = 0.1f;
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

        if (autoAttack && (currentTarget == null && currentItemTarget == null || Time.frameCount % 15 == 0))
            FindBestAutoTarget();

        if (Mouse.current.leftButton.wasPressedThisFrame)
            HandleMouseClick();

        if (currentTarget != null) AttackLogic();
        else if (currentItemTarget != null) PickupLogic();

        anim.SetBool("1_Move", agent.velocity.magnitude > 0.1f);

        CleanupMarker();
        HandleRegen();
    }

    private void UpdateHealthUI()
    {
        healthNumber.text = currentHealthPoints.ToString();
        healthSlider.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, (float)(currentHealthPoints / maxHealthPoints * 190));
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
        foreach (var item in Object.FindObjectsByType<DropPickup>(FindObjectsInactive.Exclude))
        {
            if (item.item == null || !inventory.CanAddItem(item.item)) continue;
            float dist = Vector3.Distance(transform.position, item.transform.position);
            if (dist < bestDist && dist < 25f && agent.CalculatePath(item.transform.position, new NavMeshPath()))
            {
                bestDist = dist; best = item;
            }
        }
        return best;
    }

    private void SwitchToMonster(MonsterController newTarget)
    {
        currentItemTarget = null;
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
        agent.SetDestination(currentTarget.transform.position);
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
        if (currentItemTarget == null || currentItemTarget.item == null || !inventory.CanAddItem(currentItemTarget.item))
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
        if (EventSystem.current.IsPointerOverGameObject(Mouse.current.deviceId)) return;
        Ray ray = Camera.main.ScreenPointToRay(Mouse.current.position.ReadValue());
        if (Physics.Raycast(ray, out RaycastHit hit))
        {
            MonsterController monster = hit.collider.GetComponentInParent<MonsterController>();
            if (monster != null)
            {
                SwitchToMonster(monster);
                if (currentMarker) Destroy(currentMarker);
                currentMarker = Instantiate(destinationMarker, monster.transform.position + Vector3.up * 0.1f, Quaternion.identity);
                currentMarker.transform.SetParent(monster.transform);
            }
            else
            {
                currentTarget = null;
                currentItemTarget = null;
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
        if (currentHealthPoints < maxHealthPoints)
            currentHealthPoints = System.Math.Min(maxHealthPoints, currentHealthPoints + healthRegenAmount);
    }

    public bool IsAlive() => alive;
    public void SetAutoAttack(bool autoAttackVal) { autoAttack = autoAttackVal; }
}
