using TMPro;
using UnityEngine;
using UnityEngine.AI;

public class MonsterController : MonoBehaviour
{
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

    // Assigned from monster_data.json at spawn — see Initialize()
    private MonsterData _data;
    public MonsterData Data => _data;

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
        // Health UI — guarded, because monsters are now spawned from a Resources
        // prefab that may not have these wired for every future monster type.
        if (healthNumber != null)
            healthNumber.text = NumberFormatter.Format((long)currentHealthPoints);
        if (healthSlider != null && maxHealthPoints > 0)
            healthSlider.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal,
                (float)(currentHealthPoints / maxHealthPoints * 190));

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
    }

    public void TakeDamage(double damageAmount)
    {
        ShowHealthUI();

        currentHealthPoints = System.Math.Max(0d, currentHealthPoints - damageAmount);

        if (currentHealthPoints <= 0)
        {
            alive = false;
            DropLoot();
            AwardKillRewards();

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

        foreach (var entry in _data.lootTable)
        {
            if (entry == null || string.IsNullOrEmpty(entry.itemId)) continue;
            if (Random.value > entry.DropChance) continue;

            long rolled = RandomRangeLong(entry.minQty, entry.maxQty);
            long qty    = (long)System.Math.Max(1d, rolled * multiplier);

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
        GameManager.Skills?.AddSkillXP("combat", _data.xpReward);
        GameManager.Character?.AddXP(_data.xpReward / 4);   // character XP = 1/4 of combat XP
        GameManager.Audio?.PlayDeath();
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
