using UnityEngine;

public class DropPickup : MonoBehaviour
{
    public SpriteRenderer spriteRenderer;
    public ItemDrop item;
    public int quantity = 1;

    private Vector3 spawnPosition;

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

    public void Setup(ItemDrop newItem, int qty)
    {
        item = newItem;
        spriteRenderer.sprite = newItem.icon;
        quantity = Mathf.Min(qty, newItem.maxStack);
        spawnPosition = transform.position;
        UpdateVisualSize();
    }

    private void UpdateVisualSize()
    {
        float scale = 0.1f + (quantity - 1) * 0.01f;
        transform.localScale = Vector3.one * Mathf.Min(scale, 0.3f);
    }

    private void OnTriggerEnter(Collider other)
    {
        if (!other.CompareTag("Player")) return;

        PlayerInventory inv = other.GetComponent<PlayerInventory>();
        if (inv == null) return;

        if (inv.AddItem(item, quantity))
            Destroy(gameObject);
        // Else: inventory full — item stays on the ground
    }
}
