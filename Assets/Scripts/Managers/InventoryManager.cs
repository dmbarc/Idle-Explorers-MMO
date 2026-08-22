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

    /// <summary>
    /// Coins are a currency, not an item. They never occupy an inventory slot and
    /// never drop as a physical pickup — they land straight in the wallet like XP.
    /// item_data.json still defines them so tooltips and the AFK summary can name
    /// and describe them.
    /// </summary>
    public const string CoinsItemId = "coins";

    public List<InventoryEntry> Items => CharacterManager.Current?.inventory;

    public long Coins => CharacterManager.Current?.coins ?? 0;

    public void AddCoins(long amount)
    {
        var ch = CharacterManager.Current;
        if (ch == null || amount == 0) return;
        ch.coins = System.Math.Max(0, ch.coins + amount);
        GameEvents.OnCoinsChanged?.Invoke(ch.coins);
    }

    public bool TrySpendCoins(long amount)
    {
        var ch = CharacterManager.Current;
        if (ch == null || amount < 0 || ch.coins < amount) return false;
        ch.coins -= amount;
        GameEvents.OnCoinsChanged?.Invoke(ch.coins);
        return true;
    }

    public void AddItem(string itemId, long quantity)
    {
        if (itemId == CoinsItemId) { AddCoins(quantity); return; }

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
        if (itemId == CoinsItemId) return true;   // wallet, not a slot

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
        // Dropping a stack onto itself would otherwise double its quantity and then
        // delete the entry.
        if (fromIndex == toIndex) return;

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
