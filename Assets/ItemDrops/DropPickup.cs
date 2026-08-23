using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// A lootable item lying in the world.
///
/// Carries a string itemId rather than an ItemDrop ScriptableObject reference:
/// item definitions now live in item_data.json and are resolved through
/// ContentManager, so drops need no per-item assets and remote content updates
/// require no client rebuild.
///
/// Drops do not use physics. They used to: a dynamic Rigidbody with a box collider
/// and a Billboard on the same object, which is a direct contradiction — Billboard
/// writes transform.rotation every LateUpdate while the physics engine is also
/// solving for it, so every drop spent its life being depenetrated a fraction of a
/// unit at a time. That is what "items slipping and sliding all over" was. The toss
/// is now a scripted arc onto a point sampled from the ground, after which the item
/// never moves again.
/// </summary>
public class DropPickup : MonoBehaviour
{
    public SpriteRenderer spriteRenderer;

    [Tooltip("Item id from item_data.json — set at runtime by MonsterController.")]
    public string itemId;
    public long   quantity = 1;

    [Header("Toss")]
    [Tooltip("Seconds the drop takes to arc from where it spawned to where it lands.")]
    public float tossDuration = 0.45f;
    [Tooltip("Peak height of the arc above the straight line to the landing point.")]
    public float tossArcHeight = 0.8f;

    /// <summary>How high above the ground the item rests, so it is not half-buried.</summary>
    private const float RestHeight = 0.45f;

    private Vector3 _from, _to;
    private float   _tossElapsed;
    private bool    _tossing;
    private float   _bobPhase;

    private void Awake()
    {
        // Belt and braces: the prefab ships kinematic, but a drop that somehow gets a
        // simulated body is the exact bug this class exists to prevent.
        var body = GetComponent<Rigidbody>();
        if (body != null)
        {
            body.isKinematic = true;
            body.useGravity  = false;
        }

        _bobPhase = Random.Range(0f, Mathf.PI * 2f);   // so a pile does not bob in lockstep
    }

    public void Setup(string newItemId, long qty)
    {
        itemId   = newItemId;
        quantity = System.Math.Max(1L, qty);

        if (spriteRenderer != null)
        {
            var icon = GameManager.Content?.GetItemIcon(itemId);
            if (icon != null) spriteRenderer.sprite = icon;
        }

        UpdateVisualSize();
        BeginToss();
    }

    /// <summary>
    /// Scales the SPRITE, never the root.
    ///
    /// The root carries the pickup trigger, so scaling it changed how close you had to
    /// walk to collect something — a single item had a smaller pickup radius than a
    /// stack of a thousand. Keeping the root at 1 makes pickup range constant.
    /// </summary>
    private void UpdateVisualSize()
    {
        if (spriteRenderer == null) return;

        // Bigger piles read as bigger stacks. Log-scaled because quantities run to
        // the billions — a linear scale would make anything past ~30 identical.
        float magnitude = Mathf.Log10(Mathf.Max(1f, quantity)) / 6f;   // 1 → 0, 1M → 1
        float scale     = 0.75f + Mathf.Clamp01(magnitude) * 0.55f;

        spriteRenderer.transform.localScale = Vector3.one * scale;
    }

    // ── The toss ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Picks a resting place on the ground near where the drop spawned and starts the
    /// arc toward it. Sampling the NavMesh as well as the terrain matters: an item
    /// that lands somewhere the player cannot stand is loot they can never collect.
    /// </summary>
    private void BeginToss()
    {
        _from = transform.position;
        _to   = ResolveLanding(_from);

        _tossElapsed = 0f;
        _tossing     = tossDuration > 0f && (_to - _from).sqrMagnitude > 0.0001f;

        if (!_tossing) transform.position = _to;
    }

    private static Vector3 ResolveLanding(Vector3 near)
    {
        Vector3 ground = near;

        // Terrain first — it is what the item visually rests on.
        var origin = new Vector3(near.x, near.y + 30f, near.z);
        if (Physics.Raycast(origin, Vector3.down, out RaycastHit hit, 200f,
                            ~0, QueryTriggerInteraction.Ignore))
            ground = hit.point;

        // Then nudge onto walkable ground if there is any close by. A small radius:
        // pulling a drop several units to reach the NavMesh would look like the item
        // teleporting away from the corpse it came off.
        if (NavMesh.SamplePosition(ground, out NavMeshHit navHit, 2.5f, NavMesh.AllAreas))
            ground = new Vector3(navHit.position.x, ground.y, navHit.position.z);

        return ground + Vector3.up * RestHeight;
    }

    private void Update()
    {
        if (_tossing)
        {
            _tossElapsed += Time.deltaTime;
            float t = Mathf.Clamp01(_tossElapsed / tossDuration);

            // Straight line plus a sine hop, which reads as a toss without needing
            // gravity, a collider, or anything that can push another drop around.
            Vector3 position = Vector3.Lerp(_from, _to, t);
            position.y += Mathf.Sin(t * Mathf.PI) * tossArcHeight;
            transform.position = position;

            if (t >= 1f) _tossing = false;
            return;
        }

        // A gentle idle bob, so loot on the ground catches the eye. Applied to the
        // sprite's local position, leaving the root — and therefore the pickup
        // trigger — exactly where it landed.
        if (spriteRenderer != null)
        {
            var local = spriteRenderer.transform.localPosition;
            local.y = Mathf.Sin(Time.time * 2f + _bobPhase) * 0.08f;
            spriteRenderer.transform.localPosition = local;
        }
    }

    private void OnTriggerEnter(Collider other)
    {
        if (!other.CompareTag("Player")) return;
        if (string.IsNullOrEmpty(itemId)) return;

        var inventory = GameManager.Inventory;
        if (inventory == null) return;

        if (!inventory.CanAddItem(itemId))
        {
            // Inventory full — leave it on the ground rather than deleting loot.
            return;
        }

        inventory.AddItem(itemId, quantity);
        GameEvents.FireItemPickedUp(itemId, quantity);

        var item = GameManager.Content?.GetItem(itemId);
        GameEvents.FireToast($"+{NumberFormatter.Format(quantity)} {item?.DisplayName ?? itemId}");
        GameManager.Audio?.PlayPickup();

        Destroy(gameObject);
    }
}
