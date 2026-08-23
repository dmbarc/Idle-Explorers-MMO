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

    /// <summary>
    /// Condition this piece of gear should come back at, for armour that was thrown
    /// rather than looted. -1 means "not gear, or unknown" and is the normal case.
    ///
    /// Only the set bonus that hurls armour at an enemy sets this, and only the one
    /// that scoops it back up reads it — a drop collected the ordinary way goes into
    /// the inventory, where the durability ledger already knows its condition.
    /// </summary>
    [System.NonSerialized] public int RecoveredDurability = -1;

    [Header("Toss")]
    [Tooltip("Seconds the drop takes to arc from where it spawned to where it lands.")]
    public float tossDuration = 0.45f;
    [Tooltip("Peak height of the arc above the straight line to the landing point.")]
    public float tossArcHeight = 0.8f;

    [Header("Lifetime")]
    [Tooltip("Seconds before uncollected loot vanishes. Zero means it never does.")]
    public float despawnSeconds = 180f;

    [Tooltip("Seconds of fading before it goes, so loot does not blink out of existence.")]
    public float fadeSeconds = 8f;

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

    /// <summary>
    /// Where a drop comes to rest.
    ///
    /// ══ WHY LOOT WAS FLOATING IN MID-AIR ══════════════════════════════════════
    ///
    /// This used to raycast straight down from 30 units up against EVERY layer. A
    /// drop spawns inside the monster that produced it, whose collider outlives it by
    /// corpseDespawnTime — so the first thing the ray hit on the way down was the top
    /// of the corpse's own capsule, about two units above the floor. That became the
    /// "ground". Worse, the NavMesh sample that followed took only x and z from the
    /// hit and KEPT that bogus y, so snapping to walkable ground could not correct it.
    /// The item hung at chest height, and auto-mode walked to it and stood underneath
    /// it forever because the pickup trigger never reached the player.
    ///
    /// The NavMesh leads now. It is baked from the walkable floor, so a point on it is
    /// on the ground by construction and — unlike a raycast — is guaranteed to be
    /// somewhere the player can actually stand. The raycast is only a fallback for
    /// spots with no NavMesh nearby, and it now skips characters and other drops.
    /// </summary>
    private static Vector3 ResolveLanding(Vector3 near)
    {
        // Generous radius: the toss starts up to ~2.5 units above the corpse's feet,
        // and SamplePosition measures in 3D. Too small a radius here is exactly the
        // mistake that once made the monster spawner stop finding anywhere to spawn.
        if (NavMesh.SamplePosition(near, out NavMeshHit navHit, 8f, NavMesh.AllAreas))
            return navHit.position + Vector3.up * RestHeight;

        return RaycastToGround(near) + Vector3.up * RestHeight;
    }

    /// <summary>
    /// Lowest solid surface under a point, ignoring anything that is not scenery.
    ///
    /// RaycastAll rather than Raycast: the nearest hit is usually the corpse the loot
    /// fell out of. Taking the LOWEST qualifying hit also survives a drop that spawns
    /// underneath an overhang.
    /// </summary>
    private static Vector3 RaycastToGround(Vector3 near)
    {
        var origin = new Vector3(near.x, near.y + 30f, near.z);
        var hits   = Physics.RaycastAll(origin, Vector3.down, 200f, ~0, QueryTriggerInteraction.Ignore);

        bool  found = false;
        float bestY = float.MaxValue;

        foreach (var hit in hits)
        {
            // Skip the things that are standing on the ground rather than being it.
            if (hit.collider.GetComponentInParent<MonsterController>() != null) continue;
            if (hit.collider.GetComponentInParent<PlayerController>()  != null) continue;
            if (hit.collider.GetComponentInParent<DropPickup>()        != null) continue;

            if (hit.point.y >= bestY) continue;
            bestY = hit.point.y;
            found = true;
        }

        return found ? new Vector3(near.x, bestY, near.z) : near;
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

        TickLifetime();
    }

    /// <summary>
    /// Fades out and removes loot nobody collected.
    ///
    /// Without this, drops are immortal: an overnight AFK session with auto-pickup
    /// unable to keep up — or a full inventory, which makes the player walk past
    /// everything — leaves thousands of objects, each with a collider and an Update,
    /// and the frame rate goes with them. Fading rather than vanishing so a player
    /// walking toward something can see it is about to go.
    /// </summary>
    private void TickLifetime()
    {
        if (despawnSeconds <= 0f) return;

        _age += Time.deltaTime;
        if (_age < despawnSeconds - fadeSeconds) return;

        if (_age >= despawnSeconds)
        {
            Destroy(gameObject);
            return;
        }

        if (spriteRenderer == null) return;

        float remaining = despawnSeconds - _age;
        var colour = spriteRenderer.color;
        colour.a = Mathf.Clamp01(remaining / Mathf.Max(0.01f, fadeSeconds));
        spriteRenderer.color = colour;
    }

    private float _age;

    /// <summary>
    /// Drags the drop back down onto real ground.
    ///
    /// Called when something has walked to this item and failed to collect it, which
    /// almost always means it is out of reach of the trigger. Cheap, idempotent, and
    /// it rescues loot already lying in a running scene from before the landing bug
    /// was fixed — those drops never move again on their own.
    /// </summary>
    public void Reground()
    {
        if (_tossing) return;

        Vector3 grounded = ResolveLanding(transform.position);
        if ((grounded - transform.position).sqrMagnitude < 0.0004f) return;

        transform.position = grounded;
    }

    private void OnTriggerEnter(Collider other)
    {
        if (other == null || !other.CompareTag("Player")) return;
        TryCollect();
    }

    private bool _warnedFull;

    /// <summary>
    /// Collects what fits. Returns true once nothing is left and the drop has been
    /// destroyed.
    ///
    /// Public because the trigger cannot be trusted on its own. OnTriggerEnter fires
    /// exactly once, so a player who arrives with a full bag never gets a second
    /// chance, and OnTriggerStay is unreliable here — this body is kinematic, and a
    /// sleeping kinematic body against a stationary character does not reliably
    /// generate Stay callbacks. PlayerController calls this directly when it has
    /// walked to a drop, which does not depend on the physics engine's opinion.
    /// </summary>
    public bool TryCollect()
    {
        if (string.IsNullOrEmpty(itemId)) return false;

        var inventory = GameManager.Inventory;
        if (inventory == null) return false;

        // Take what fits and leave the rest on the ground. The check has to be against
        // the actual quantity, not one unit: a bag with room for ten of something is
        // not a bag with room for a stack of a thousand, and destroying the pickup
        // after a failed add would delete the difference.
        long taken = inventory.AddUpTo(itemId, quantity);
        if (taken <= 0)
        {
            // Said once per drop, not once per attempt — the player controller retries
            // this while standing on a pile it has no room for.
            if (!_warnedFull)
            {
                _warnedFull = true;
                GameEvents.FireToast("Inventory full — some was left behind.");
            }
            return false;
        }

        GameEvents.FireItemPickedUp(itemId, taken);

        var item = GameManager.Content?.GetItem(itemId);
        GameEvents.FireToast($"+{NumberFormatter.Format(taken)} {item?.DisplayName ?? itemId}");
        GameManager.Audio?.PlayPickup();

        quantity -= taken;
        if (quantity > 0)
        {
            // Partially collected: shrink the pile and leave it there.
            UpdateVisualSize();
            return false;
        }

        Destroy(gameObject);
        return true;
    }
}
