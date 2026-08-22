using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Manages the active character's inventory.
///
/// The backing list is a FIXED 30 entries, with empty slots represented
/// explicitly rather than being absent. A compact list cannot express "item in
/// slot 7, slots 0-6 empty", so dragging an item onto an empty cell had no
/// possible representation and silently did nothing.
///
/// All quantities are long (up to 9.2Qa).
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
            Normalize(inv);
            return inv;
        }
    }

    /// <summary>Number of occupied slots.</summary>
    public int UsedSlots
    {
        get
        {
            var inv = Items;
            if (inv == null) return 0;

            int used = 0;
            foreach (var e in inv) if (!IsEmpty(e)) used++;
            return used;
        }
    }

    public long Coins => CharacterManager.Current?.coins ?? 0;

    public static bool IsEmpty(InventoryEntry e) =>
        e == null || string.IsNullOrEmpty(e.itemId) || e.quantity <= 0;

    /// <summary>
    /// Pads or trims a character's inventory to exactly MaxSlots. Runs on access so
    /// saves written under the old compact model are upgraded transparently.
    /// </summary>
    private static void Normalize(List<InventoryEntry> inv)
    {
        for (int i = 0; i < inv.Count; i++)
            inv[i] ??= new InventoryEntry();

        while (inv.Count < MaxSlots) inv.Add(new InventoryEntry());
        if (inv.Count > MaxSlots) inv.RemoveRange(MaxSlots, inv.Count - MaxSlots);
    }

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

    public void AddItem(string itemId, long quantity)
    {
        if (itemId == CoinsItemId) { AddCoins(quantity); return; }
        if (string.IsNullOrEmpty(itemId) || quantity <= 0) return;

        var inv = Items;
        if (inv == null) return;

        // Top up an existing stack first
        foreach (var entry in inv)
        {
            if (!IsEmpty(entry) && entry.itemId == itemId)
            {
                entry.quantity += quantity;
                GameEvents.FireInventoryChanged();
                return;
            }
        }

        // Otherwise take the first free slot
        for (int i = 0; i < inv.Count; i++)
        {
            if (!IsEmpty(inv[i])) continue;

            inv[i].itemId   = itemId;
            inv[i].quantity = quantity;
            GameEvents.FireInventoryChanged();
            return;
        }

        Debug.LogWarning($"[InventoryManager] Inventory full — could not add {quantity}x {itemId}");
    }

    public bool CanAddItem(string itemId)
    {
        if (itemId == CoinsItemId) return true;   // wallet, not a slot

        var inv = Items;
        if (inv == null) return false;

        foreach (var e in inv)
        {
            if (IsEmpty(e)) return true;                     // free slot
            if (e.itemId == itemId) return true;             // existing stack
        }
        return false;
    }

    public long GetQuantity(string itemId)
    {
        var inv = Items;
        if (inv == null) return 0;

        foreach (var e in inv)
            if (!IsEmpty(e) && e.itemId == itemId) return e.quantity;
        return 0;
    }

    public bool RemoveItem(string itemId, long quantity)
    {
        var inv = Items;
        if (inv == null || quantity <= 0) return false;

        foreach (var e in inv)
        {
            if (IsEmpty(e) || e.itemId != itemId) continue;
            if (e.quantity < quantity) return false;

            e.quantity -= quantity;
            if (e.quantity <= 0) Clear(e);

            GameEvents.FireInventoryChanged();
            return true;
        }
        return false;
    }

    // ── Slot manipulation (drag and drop) ─────────────────────────────────────

    /// <summary>
    /// Moves the item in fromIndex onto toIndex: stacks if both hold the same item,
    /// otherwise swaps. Moving onto an empty slot is a plain move.
    /// </summary>
    public void SwapOrStackSlots(int fromIndex, int toIndex)
    {
        var inv = Items;
        if (inv == null) return;
        if (fromIndex < 0 || toIndex < 0 || fromIndex >= inv.Count || toIndex >= inv.Count) return;
        if (fromIndex == toIndex) return;

        var from = inv[fromIndex];
        var to   = inv[toIndex];

        if (IsEmpty(from)) return;   // nothing to move

        if (!IsEmpty(to) && from.itemId == to.itemId)
        {
            // Merge stacks
            to.quantity += from.quantity;
            Clear(from);
        }
        else
        {
            // Swap contents in place. The entries themselves stay put so no other
            // slot index shifts underneath the UI.
            string itemId   = to.itemId;
            long   quantity = to.quantity;

            to.itemId     = from.itemId;
            to.quantity   = from.quantity;
            from.itemId   = itemId;
            from.quantity = quantity;
        }

        GameEvents.FireInventoryChanged();
    }

    private static void Clear(InventoryEntry e)
    {
        e.itemId   = null;
        e.quantity = 0;
    }
}
