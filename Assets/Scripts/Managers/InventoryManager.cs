using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The active character's inventory: 30 fixed slots.
///
/// The slot mechanics live in SlotContainer, shared with the account bank — the two
/// are the same model at different capacities, and keeping two copies is how they
/// would drift apart. This class binds that logic to the active character and owns
/// the coin wallet and change events.
///
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

    /// <summary>The raw slot list, always MaxSlots long. Index == on-screen slot.</summary>
    public List<InventoryEntry> Items
    {
        get
        {
            var inv = CharacterManager.Current?.inventory;
            if (inv == null) return null;
            SlotContainer.Normalize(inv, MaxSlots);
            return inv;
        }
    }

    /// <summary>Number of occupied slots.</summary>
    public int UsedSlots => SlotContainer.UsedSlots(Items);

    public long Coins => CharacterManager.Current?.coins ?? 0;

    /// <summary>Kept as a passthrough so existing call sites do not all have to move.</summary>
    public static bool IsEmpty(InventoryEntry e) => SlotContainer.IsEmpty(e);

    // ── Currency ──────────────────────────────────────────────────────────────

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

    // ── Items ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Adds to the inventory. Returns false when there was no room and NOTHING was
    /// added — callers that are moving an item rather than creating one must check,
    /// or the item ceases to exist.
    /// </summary>
    public bool AddItem(string itemId, long quantity)
    {
        if (itemId == CoinsItemId) { AddCoins(quantity); return true; }
        if (string.IsNullOrEmpty(itemId) || quantity <= 0) return false;

        var inv = Items;
        if (inv == null) return false;

        if (!SlotContainer.AddItem(inv, itemId, quantity))
        {
            Debug.LogWarning($"[InventoryManager] Inventory full — could not add {quantity}x {itemId}");
            return false;
        }

        GameEvents.FireInventoryChanged();
        return true;
    }

    public bool CanAddItem(string itemId)
    {
        if (itemId == CoinsItemId) return true;   // wallet, not a slot
        return SlotContainer.CanAddItem(Items, itemId);
    }

    public long GetQuantity(string itemId)
    {
        if (itemId == CoinsItemId) return Coins;
        return SlotContainer.GetQuantity(Items, itemId);
    }

    public bool RemoveItem(string itemId, long quantity)
    {
        if (itemId == CoinsItemId) return TrySpendCoins(quantity);

        if (!SlotContainer.RemoveItem(Items, itemId, quantity)) return false;

        GameEvents.FireInventoryChanged();
        return true;
    }

    /// <summary>Empties one slot outright — used when an item is dropped or consumed whole.</summary>
    public void ClearSlot(int slotIndex)
    {
        var inv = Items;
        if (inv == null || slotIndex < 0 || slotIndex >= inv.Count) return;

        SlotContainer.Clear(inv[slotIndex]);
        GameEvents.FireInventoryChanged();
    }

    /// <summary>Removes a quantity from one specific slot rather than searching by id.</summary>
    public bool RemoveFromSlot(int slotIndex, long quantity)
    {
        var inv = Items;
        if (inv == null || slotIndex < 0 || slotIndex >= inv.Count || quantity <= 0) return false;

        var entry = inv[slotIndex];
        if (SlotContainer.IsEmpty(entry) || entry.quantity < quantity) return false;

        entry.quantity -= quantity;
        if (entry.quantity <= 0) SlotContainer.Clear(entry);

        GameEvents.FireInventoryChanged();
        return true;
    }

    // ── Slot manipulation (drag and drop) ─────────────────────────────────────

    /// <summary>
    /// Moves the item in fromIndex onto toIndex: stacks if both hold the same item,
    /// otherwise swaps. Moving onto an empty slot is a plain move.
    /// </summary>
    public void SwapOrStackSlots(int fromIndex, int toIndex)
    {
        SlotContainer.SwapOrStackSlots(Items, fromIndex, toIndex);
        GameEvents.FireInventoryChanged();
    }
}
