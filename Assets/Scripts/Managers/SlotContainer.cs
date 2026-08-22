using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Slot-list mechanics shared by the character inventory and the account bank.
///
/// Both are the same model at different capacities and scopes: a FIXED-length list
/// with empty slots represented explicitly rather than being absent. A compact list
/// cannot express "item in slot 7, slots 0-6 empty", which is why dragging onto an
/// empty cell once had no possible representation and silently did nothing.
///
/// These bodies were lifted out of InventoryManager unchanged — the logic is
/// playtested, this only makes it reusable so the bank does not grow a second,
/// subtly different copy.
///
/// All quantities are long (up to 9.2Qa).
/// </summary>
public static class SlotContainer
{
    public static bool IsEmpty(InventoryEntry e) =>
        e == null || string.IsNullOrEmpty(e.itemId) || e.quantity <= 0;

    /// <summary>
    /// Pads or trims a list to exactly <paramref name="capacity"/>. Runs on access so
    /// saves written under an older model are upgraded transparently.
    /// </summary>
    public static void Normalize(List<InventoryEntry> slots, int capacity)
    {
        if (slots == null) return;

        for (int i = 0; i < slots.Count; i++)
            slots[i] ??= new InventoryEntry();

        while (slots.Count < capacity) slots.Add(new InventoryEntry());
        if (slots.Count > capacity) slots.RemoveRange(capacity, slots.Count - capacity);
    }

    public static int UsedSlots(List<InventoryEntry> slots)
    {
        if (slots == null) return 0;

        int used = 0;
        foreach (var e in slots) if (!IsEmpty(e)) used++;
        return used;
    }

    // ── Contents ──────────────────────────────────────────────────────────────

    /// <summary>Tops up an existing stack, else takes the first free slot. False when full.</summary>
    public static bool AddItem(List<InventoryEntry> slots, string itemId, long quantity)
    {
        if (slots == null || string.IsNullOrEmpty(itemId) || quantity <= 0) return false;

        foreach (var entry in slots)
        {
            if (!IsEmpty(entry) && entry.itemId == itemId)
            {
                entry.quantity += quantity;
                return true;
            }
        }

        for (int i = 0; i < slots.Count; i++)
        {
            if (!IsEmpty(slots[i])) continue;

            slots[i].itemId   = itemId;
            slots[i].quantity = quantity;
            return true;
        }

        return false;
    }

    public static bool CanAddItem(List<InventoryEntry> slots, string itemId)
    {
        if (slots == null) return false;

        foreach (var e in slots)
        {
            if (IsEmpty(e)) return true;             // free slot
            if (e.itemId == itemId) return true;     // existing stack
        }
        return false;
    }

    public static long GetQuantity(List<InventoryEntry> slots, string itemId)
    {
        if (slots == null) return 0;

        foreach (var e in slots)
            if (!IsEmpty(e) && e.itemId == itemId) return e.quantity;
        return 0;
    }

    public static bool RemoveItem(List<InventoryEntry> slots, string itemId, long quantity)
    {
        if (slots == null || quantity <= 0) return false;

        foreach (var e in slots)
        {
            if (IsEmpty(e) || e.itemId != itemId) continue;
            if (e.quantity < quantity) return false;

            e.quantity -= quantity;
            if (e.quantity <= 0) Clear(e);
            return true;
        }
        return false;
    }

    // ── Slot manipulation ─────────────────────────────────────────────────────

    /// <summary>
    /// Moves the item in fromIndex onto toIndex within one container: stacks when both
    /// hold the same item, otherwise swaps. Moving onto an empty slot is a plain move.
    /// </summary>
    public static void SwapOrStackSlots(List<InventoryEntry> slots, int fromIndex, int toIndex)
    {
        if (slots == null) return;
        if (fromIndex < 0 || toIndex < 0 || fromIndex >= slots.Count || toIndex >= slots.Count) return;
        if (fromIndex == toIndex) return;

        var from = slots[fromIndex];
        var to   = slots[toIndex];

        if (IsEmpty(from)) return;   // nothing to move

        if (!IsEmpty(to) && from.itemId == to.itemId)
        {
            to.quantity += from.quantity;
            Clear(from);
            return;
        }

        // Swap contents in place. The entries themselves stay put, so no other slot
        // index shifts underneath the UI.
        string itemId   = to.itemId;
        long   quantity = to.quantity;

        to.itemId     = from.itemId;
        to.quantity   = from.quantity;
        from.itemId   = itemId;
        from.quantity = quantity;
    }

    /// <summary>
    /// Moves one slot's contents between two different containers.
    ///
    /// Stacks when the destination already holds the same item, swaps when it holds a
    /// different one, and plain-moves into an empty slot. Returns false when nothing
    /// happened, so callers can avoid firing change events for a no-op.
    /// </summary>
    public static bool Transfer(List<InventoryEntry> from, int fromIndex,
                                List<InventoryEntry> to,   int toIndex)
    {
        if (from == null || to == null) return false;
        if (fromIndex < 0 || fromIndex >= from.Count) return false;
        if (toIndex   < 0 || toIndex   >= to.Count)   return false;

        var src = from[fromIndex];
        var dst = to[toIndex];

        if (IsEmpty(src)) return false;

        if (!IsEmpty(dst) && dst.itemId == src.itemId)
        {
            dst.quantity += src.quantity;
            Clear(src);
            return true;
        }

        // A swap across containers is legal — the destination item simply ends up in
        // the slot the dragged item vacated.
        string itemId   = dst.itemId;
        long   quantity = dst.quantity;

        dst.itemId   = src.itemId;
        dst.quantity = src.quantity;
        src.itemId   = itemId;
        src.quantity = quantity;
        return true;
    }

    /// <summary>Moves a specific quantity, splitting the source stack if needed.</summary>
    public static bool TransferQuantity(List<InventoryEntry> from, int fromIndex,
                                        List<InventoryEntry> to, long quantity)
    {
        if (from == null || to == null || quantity <= 0) return false;
        if (fromIndex < 0 || fromIndex >= from.Count) return false;

        var src = from[fromIndex];
        if (IsEmpty(src)) return false;

        long moving = System.Math.Min(quantity, src.quantity);
        if (!CanAddItem(to, src.itemId)) return false;

        if (!AddItem(to, src.itemId, moving)) return false;

        src.quantity -= moving;
        if (src.quantity <= 0) Clear(src);
        return true;
    }

    public static void Clear(InventoryEntry e)
    {
        if (e == null) return;
        e.itemId   = null;
        e.quantity = 0;
    }
}
