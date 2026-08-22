using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Manages the active character's inventory.
/// All quantities stored as long (up to 9.2Qa).
/// Phase 1: local. Phase 8: server SyncList.
/// </summary>
public class InventoryManager : MonoBehaviour
{
    public const int MaxSlots = 30;

    public List<InventoryEntry> Items => CharacterManager.Current?.inventory;

    public void AddItem(string itemId, long quantity)
    {
        var inv = Items;
        if (inv == null) return;

        // Find existing stack
        foreach (var entry in inv)
        {
            if (entry.itemId == itemId)
            {
                entry.quantity += quantity;
                GameEvents.FireInventoryChanged();
                return;
            }
        }

        // New stack
        if (inv.Count < MaxSlots)
        {
            inv.Add(new InventoryEntry { itemId = itemId, quantity = quantity });
            GameEvents.FireInventoryChanged();
        }
        else
        {
            Debug.LogWarning($"[InventoryManager] Inventory full — could not add {quantity}x {itemId}");
        }
    }

    public bool CanAddItem(string itemId)
    {
        var inv = Items;
        if (inv == null) return false;
        foreach (var e in inv) if (e.itemId == itemId) return true;
        return inv.Count < MaxSlots;
    }

    public void SwapOrStackSlots(int fromIndex, int toIndex)
    {
        var inv = Items;
        if (inv == null) return;
        if (fromIndex < 0 || toIndex < 0 || fromIndex >= inv.Count || toIndex >= inv.Count) return;

        var a = inv[fromIndex];
        var b = inv[toIndex];

        // Stack if same item
        if (b != null && a.itemId == b.itemId)
        {
            b.quantity += a.quantity;
            inv.RemoveAt(fromIndex);
        }
        else
        {
            inv[fromIndex] = b;
            inv[toIndex]   = a;
        }

        GameEvents.FireInventoryChanged();
    }
}
