using System.Collections.Generic;
using UnityEngine;

public class PlayerInventory : MonoBehaviour
{
    public List<InventoryItem> items = new List<InventoryItem>();
    public int maxSlots = 30;
    public int coins = 0;

    /// <summary>Fired whenever the inventory contents change.</summary>
    public event System.Action OnInventoryChanged;

    [System.Serializable]
    public class InventoryItem
    {
        public ItemDrop item;
        public int quantity = 0;

        public void Set(ItemDrop newItem, int newQty)
        {
            item = newItem;
            quantity = (newItem != null) ? Mathf.Clamp(newQty, 1, newItem.maxStack) : 0;
        }
        public bool IsEmpty() => item == null;
    }

    private void Awake() { InitializeInventory(); }

    private void InitializeInventory()
    {
        if (items == null) items = new List<InventoryItem>(maxSlots);
        while (items.Count < maxSlots) items.Add(new InventoryItem());
        if (items.Count > maxSlots) items.RemoveRange(maxSlots, items.Count - maxSlots);
    }

    public bool AddItem(ItemDrop item, int quantity = 1)
    {
        if (item == null || quantity <= 0) return false;
        int remaining = quantity;

        // Fill existing partial stacks first
        for (int i = 0; i < maxSlots && remaining > 0; i++)
            if (items[i].item == item && items[i].quantity < item.maxStack)
            {
                int canAdd = Mathf.Min(remaining, item.maxStack - items[i].quantity);
                items[i].quantity += canAdd;
                remaining -= canAdd;
            }

        // Then fill empty slots
        for (int i = 0; i < maxSlots && remaining > 0; i++)
            if (items[i].IsEmpty())
            {
                int addThis = Mathf.Min(remaining, item.maxStack);
                items[i].Set(item, addThis);
                remaining -= addThis;
            }

        bool success = remaining <= 0;
        OnInventoryChanged?.Invoke();
        return success;
    }

    public void SwapOrStackItems(int fromIndex, int toIndex)
    {
        if (fromIndex < 0 || fromIndex >= maxSlots ||
            toIndex < 0 || toIndex >= maxSlots ||
            fromIndex == toIndex) return;

        var from = items[fromIndex];
        var to = items[toIndex];
        if (from.IsEmpty()) return;

        if (to.IsEmpty() || from.item != to.item)
        {
            // Move to empty slot or swap different items
            var temp = items[fromIndex];
            items[fromIndex] = items[toIndex];
            items[toIndex] = temp;
        }
        else
        {
            // Stack same item type
            int canAdd = to.item.maxStack - to.quantity;
            if (canAdd > 0)
            {
                int amount = Mathf.Min(canAdd, from.quantity);
                to.quantity += amount;
                from.quantity -= amount;
                if (from.quantity <= 0) from.Set(null, 0);
            }
        }

        OnInventoryChanged?.Invoke();
    }

    public bool HasEmptySlot()
    {
        for (int i = 0; i < maxSlots; i++)
            if (items[i].IsEmpty()) return true;
        return false;
    }

    public bool CanAddItem(ItemDrop item)
    {
        if (item == null) return false;
        for (int i = 0; i < maxSlots; i++)
            if (items[i].item == item && items[i].quantity < item.maxStack) return true;
        return HasEmptySlot();
    }
}
