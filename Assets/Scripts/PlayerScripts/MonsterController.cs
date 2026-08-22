using TMPro;
using UnityEngine;
using UnityEngine.AI;

public class MonsterController : MonoBehaviour
{
    [System.Serializable]
    public class DropEntry
    {
        public ItemDrop item;
        public float baseChance = 0.1f;
        public int minAmount = 1;
        public int maxAmount = 1;
    }

    private Transform player;
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

    public DropEntry[] dropTable;
    public GameObject dropPrefab;

    public GameObject healthUI;
    private float hideUITimer = 0f;
    private const float UI_SHOW_TIME = 3f;

    private MonsterSpawner spawner;
    private float regenTimer = 0f;
    private bool alive = true;
    private double currentHealthPoints = 50;
    private float attackTimer = 0f;
    private Vector3 wanderPoint;
    private float wanderTimer = 0f;

    public TMP_Text healthNumber;
    public RectTransform healthSlider;

    void Start()
    {
        agent = GetComponent<NavMeshAgent>();

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

        if (healthUI != null) healthUI.SetActive(false);
    }

    void Update()
    {
        // Health UI
        healthNumber.text = currentHealthPoints.ToString();
        healthSlider.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, (float)(currentHealthPoints / maxHealthPoints * 190));

        if (healthUI != null && hideUITimer > 0f)
        {
            hideUITimer -= Time.deltaTime;
            if (hideUITimer <= 0f)
                healthUI.SetActive(false);
        }

        if (alive)
        {
            if (player != null && playerController != null && (agent.isOnNavMesh || !agent.pathPending))
            {
                float dist = Vector3.Distance(transform.position, player.position);

                if (dist <= aggroDistance && dist > attackDistance && playerController.IsAlive())
                {
                    agent.SetDestination(player.position);
                    anim.SetBool("1_Move", true);
                    anim.SetBool("2_Attack", false);
                }
                else if (dist <= attackDistance && playerController.IsAlive())
                {
                    attackTimer += Time.deltaTime;
                    if (attackTimer >= attackSpeed)
                    {
                        playerController.TakeDamage(attackDamage);
                        anim.SetBool("1_Move", false);
                        anim.SetBool("2_Attack", true);
                        attackTimer = 0f;
                    }
                }
                else
                {
                    wanderTimer -= Time.deltaTime;
                    if (wanderTimer <= 0f || agent.remainingDistance < 1f)
                        PickNewWanderPoint();

                    anim.SetBool("1_Move", true);
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

    public void TakeDamage(double damageAmount)
    {
        ShowHealthUI();

        currentHealthPoints = System.Math.Max(0d, currentHealthPoints - damageAmount);

        if (currentHealthPoints <= 0)
        {
            alive = false;
            DropLoot();

            anim.SetBool("1_Move", false);
            anim.SetBool("2_Attack", false);
            anim.SetBool("3_Damaged", false);
            anim.SetBool("4_Death", true);

            // Disable agent so the corpse doesn't slide
            agent.enabled = false;
            regenTimer = 0f;

            if (spawner != null) spawner.MonsterDied();
            if (healthUI != null) healthUI.SetActive(true);

            // Freeze the animator after the death clip finishes so it can't
            // transition back to idle before the corpse despawns
            StartCoroutine(FreezeAnimatorAfterDeath());
        }
        else
        {
            anim.SetBool("1_Move", false);
            anim.SetBool("3_Damaged", true);
        }
    }

    private System.Collections.IEnumerator FreezeAnimatorAfterDeath()
    {
        // Wait one frame so the death state transition has a chance to begin
        yield return null;

        // Poll frame-by-frame until the death animation has fully played through once
        while (anim != null)
        {
            AnimatorStateInfo currentDeathStateInfo = anim.GetCurrentAnimatorStateInfo(0);
            bool isPlayingDeathAnimation = currentDeathStateInfo.IsName("4_Death");
            bool deathAnimationHasFinished = currentDeathStateInfo.normalizedTime >= 1f;

            if (isPlayingDeathAnimation && deathAnimationHasFinished)
                break;

            yield return null;
        }

        // Zero the playback speed so the corpse holds on the final death frame
        // until it despawns, rather than transitioning back to idle or looping.
        if (anim != null)
            anim.speed = 0f;
    }

    public void RegenHealth()
    {
        if (currentHealthPoints >= maxHealthPoints) return;
        ShowHealthUI();
        currentHealthPoints = System.Math.Min(maxHealthPoints, currentHealthPoints + healthRegenAmount);
    }

    private void ShowHealthUI()
    {
        if (healthUI != null)
        {
            healthUI.SetActive(true);
            hideUITimer = UI_SHOW_TIME;
        }
    }

    public bool IsAlive() => alive;

    void PickNewWanderPoint()
    {
        wanderPoint = transform.position + Random.insideUnitSphere * 8f;
        wanderPoint.y = transform.position.y;
        agent.SetDestination(wanderPoint);
        wanderTimer = Random.Range(3f, 6f);
    }

    private void DropLoot()
    {
        if (dropPrefab == null || dropTable == null || dropTable.Length == 0) return;

        // Read drop multiplier from the player's controller stat
        float multiplier = (playerController != null) ? (float)playerController.dropMultiplier : 1f;

        foreach (var entry in dropTable)
        {
            if (entry.item == null) continue;

            // Roll qty in [minAmount, maxAmount], then scale by multiplier
            int rolledQty = Random.Range(entry.minAmount, entry.maxAmount + 1);
            float finalChance = entry.baseChance * multiplier;
            if (finalChance <= 0) continue;

            // Guaranteed drops = floor(chance), plus probabilistic extra
            int guaranteed = Mathf.FloorToInt(finalChance);
            float extraChance = finalChance - guaranteed;
            int dropCount = guaranteed + (Random.value <= extraChance ? 1 : 0);
            if (dropCount <= 0) continue;

            // Total quantity = rolled amount × number of successful drops
            int totalQty = rolledQty * dropCount;

            // Spawn the minimum number of DropPickup objects (one per maxStack batch)
            Vector3 basePos = transform.position + Vector3.up * 0.6f;
            int fullStacks = totalQty / entry.item.maxStack;
            int remainder = totalQty % entry.item.maxStack;

            for (int i = 0; i < fullStacks; i++)
                SpawnDrop(entry.item, entry.item.maxStack, basePos);

            if (remainder > 0)
                SpawnDrop(entry.item, remainder, basePos);
        }
    }

    private void SpawnDrop(ItemDrop item, int qty, Vector3 basePos)
    {
        Vector3 offset = Random.insideUnitSphere * 1.8f;
        offset.y = Mathf.Abs(offset.y) + 0.8f;

        GameObject dropObj = Instantiate(dropPrefab, basePos + offset, Quaternion.identity);
        DropPickup pickup = dropObj.GetComponent<DropPickup>();
        if (pickup != null)
            pickup.Setup(item, qty);
    }
}
