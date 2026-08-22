using UnityEngine;

/// <summary>
/// A lootable item lying in the world.
///
/// Carries a string itemId rather than an ItemDrop ScriptableObject reference:
/// item definitions now live in item_data.json and are resolved through
/// ContentManager, so drops need no per-item assets and remote content updates
/// require no client rebuild.
/// </summary>
public class DropPickup : MonoBehaviour
{
    public SpriteRenderer spriteRenderer;

    [Tooltip("Item id from item_data.json — set at runtime by MonsterController.")]
    public string itemId;
    public long   quantity = 1;

    private void Start()
    {
        // Prevent the player's physics body from pushing the item around.
        // This prefab has two colliders (a Sphere trigger + a Box physics collider),
        // so we ignore ALL of them against the player.
        GameObject playerObj = GameObject.FindWithTag("Player");
        if (playerObj != null)
        {
            Collider playerCol = playerObj.GetComponent<Collider>();
            if (playerCol != null)
            {
                foreach (Collider col in GetComponents<Collider>())
                    Physics.IgnoreCollision(playerCol, col);
            }
        }
    }

    public void Setup(string newItemId, long qty)
    {
        itemId   = newItemId;
        quantity = System.Math.Max(1L, qty);

        if (spriteRenderer != null)
            spriteRenderer.sprite = GameManager.Content?.GetItemIcon(itemId);

        UpdateVisualSize();
    }

    private void UpdateVisualSize()
    {
        // Bigger piles read as bigger stacks. Log-scaled because quantities run to
        // the billions — a linear scale would make anything past ~30 identical.
        float magnitude = Mathf.Log10(Mathf.Max(1f, quantity)) / 6f;   // 1 → 0, 1M → 1
        float scale     = 0.1f + Mathf.Clamp01(magnitude) * 0.2f;
        transform.localScale = Vector3.one * scale;
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
