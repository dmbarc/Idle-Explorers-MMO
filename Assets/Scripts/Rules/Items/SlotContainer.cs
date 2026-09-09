using System;
using System.Collections.Generic;

// ═══════════════════════════════════════════════════════════════════════════════
//  Slot-based storage: the inventory, the bank, and the merge board.
//
//  ══ WHY THIS IS IN THE SHARED RULES TREE ══════════════════════════════════════
//
//  "How much of this will fit?" is a question the SERVER has to answer, on every
//  settlement, because a gathering run that outpaces the bag has to stop at the bag
//  rather than at the rate. And it is a question the CLIENT has to answer, for every
//  tooltip and every drag.
//
//  Answering it twice means two definitions of a full bag. The failure mode is not
//  subtle: the client shows twenty-nine ore going in and the server stores twenty-
//  eight, and the difference reappears as an item vanishing on the next read.
//
//  So the stacking arithmetic lives here and both hosts run it. What stays outside is
//  everything that needs a bag to exist -- persistence, events, the UI.
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>One stack: an item id and how many of it.</summary>
[Serializable]
public class InventoryEntry
{
    public string   itemId;
    public long     quantity;
}

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
    /// <summary>
    /// Most of one item a single slot may hold. Beyond this it spills into another
    /// slot rather than growing without limit.
    ///
    /// One quadrillion, for two reasons. It displays as a clean "1Qa" through
    /// NumberFormatter, and it leaves enormous headroom: the largest container is 120
    /// slots, so even a completely full bank totals 1.2e17 against a long's 9.22e18
    /// ceiling. Summing every stack therefore cannot overflow, which is what makes
    /// GetQuantity safe to write as a plain loop.
    /// </summary>
    public const long MaxStack = 1_000_000_000_000_000L;

    /// <summary>
    /// Addition that saturates at long.MaxValue instead of wrapping.
    ///
    /// Signed overflow in C# is silent in an unchecked context — it wraps to a large
    /// negative, and a negative quantity reads as an empty slot, so an inventory that
    /// overflowed would appear to have LOST everything rather than to have too much.
    /// Every quantity sum goes through here so that outcome is impossible regardless
    /// of what the caps are later changed to.
    /// </summary>
    public static long SafeAdd(long a, long b)
    {
        if (a > 0 && b > long.MaxValue - a) return long.MaxValue;
        if (a < 0 && b < long.MinValue - a) return long.MinValue;
        return a + b;
    }

    public static bool IsEmpty(InventoryEntry e) =>
        e == null || string.IsNullOrEmpty(e.itemId) || e.quantity <= 0;

    /// <summary>Room left in one slot before it hits the stack cap.</summary>
    private static long Headroom(InventoryEntry e) =>
        IsEmpty(e) ? MaxStack : System.Math.Max(0L, MaxStack - e.quantity);

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

    /// <summary>
    /// Total room for an item: the headroom left in its partial stacks, plus a full
    /// stack for every empty slot. Saturating, so a large container cannot overflow
    /// the sum it is measuring.
    /// </summary>
    public static long FreeCapacityFor(List<InventoryEntry> slots, string itemId)
    {
        if (slots == null || string.IsNullOrEmpty(itemId)) return 0;

        long capacity = 0;
        foreach (var entry in slots)
        {
            if (IsEmpty(entry))              capacity = SafeAdd(capacity, MaxStack);
            else if (entry.itemId == itemId) capacity = SafeAdd(capacity, Headroom(entry));
        }
        return capacity;
    }

    /// <summary>
    /// Adds a quantity, topping up existing stacks to the cap before opening new ones.
    ///
    /// All-or-nothing: when the whole amount will not fit, NOTHING is added and false
    /// is returned. Callers such as EquipmentManager are moving an item rather than
    /// creating one, and a partial add would leave the remainder nowhere — which is
    /// to say, destroyed.
    /// </summary>
    public static bool AddItem(List<InventoryEntry> slots, string itemId, long quantity)
    {
        if (slots == null || string.IsNullOrEmpty(itemId) || quantity <= 0) return false;
        if (FreeCapacityFor(slots, itemId) < quantity) return false;

        long added = AddUpTo(slots, itemId, quantity);

        if (added < quantity)
        {
            // FreeCapacityFor said it would fit, so this cannot happen — and if it
            // ever does, the two are disagreeing and that is worth knowing about.
            IdleExplorers.Rules.RulesLog.Warn($"[SlotContainer] Capacity check passed but only {added} of " +
                           $"{quantity}x '{itemId}' fit. This is a bug in SlotContainer.");
        }

        return added > 0;
    }

    /// <summary>
    /// Adds as much as will fit and returns how much that was.
    ///
    /// Separate from AddItem because the two callers want opposite things. Moving an
    /// item needs all-or-nothing, or the remainder is destroyed. Granting offline
    /// rewards into a bag that may be full wants whatever fits, and needs to know the
    /// real number so the summary reports what the player actually received rather
    /// than what was theoretically earned.
    /// </summary>
    public static long AddUpTo(List<InventoryEntry> slots, string itemId, long quantity)
    {
        if (slots == null || string.IsNullOrEmpty(itemId) || quantity <= 0) return 0;

        long remaining = quantity;

        // Fill partial stacks of this item first, so the container stays compact and a
        // second stack only appears once the first is genuinely full.
        foreach (var entry in slots)
        {
            if (remaining <= 0) break;
            if (IsEmpty(entry) || entry.itemId != itemId) continue;

            long room = Headroom(entry);
            if (room <= 0) continue;

            long moving = System.Math.Min(room, remaining);
            entry.quantity += moving;
            remaining      -= moving;
        }

        // Then spill the rest into empty slots, a capped stack at a time.
        foreach (var entry in slots)
        {
            if (remaining <= 0) break;
            if (!IsEmpty(entry)) continue;

            long moving = System.Math.Min(MaxStack, remaining);
            entry.itemId   = itemId;
            entry.quantity = moving;
            remaining     -= moving;
        }

        return quantity - remaining;
    }

    /// <summary>Whether a given quantity would fit. Defaults to a single unit.</summary>
    public static bool CanAddItem(List<InventoryEntry> slots, string itemId, long quantity = 1)
    {
        if (slots == null || string.IsNullOrEmpty(itemId) || quantity <= 0) return false;
        return FreeCapacityFor(slots, itemId) >= quantity;
    }

    /// <summary>
    /// How much of an item the container holds, ACROSS EVERY STACK.
    ///
    /// This used to return the first matching stack and stop. That was correct only
    /// while a second stack was impossible; now that a full stack spills into a new
    /// slot, reading one stack would under-report what the player owns — and the
    /// crafting supply check reads exactly this, so a full bank of ore would report
    /// as one stack's worth and the station would refuse to work.
    /// </summary>
    public static long GetQuantity(List<InventoryEntry> slots, string itemId)
    {
        if (slots == null || string.IsNullOrEmpty(itemId)) return 0;

        long total = 0;
        foreach (var e in slots)
            if (!IsEmpty(e) && e.itemId == itemId) total = SafeAdd(total, e.quantity);
        return total;
    }

    /// <summary>
    /// Removes a quantity, draining across as many stacks as it takes.
    ///
    /// All-or-nothing, and it checks the total BEFORE touching anything — spending
    /// half a cost and then discovering the rest is missing would charge the player
    /// for a craft they never receive.
    /// </summary>
    public static bool RemoveItem(List<InventoryEntry> slots, string itemId, long quantity)
    {
        if (slots == null || string.IsNullOrEmpty(itemId) || quantity <= 0) return false;
        if (GetQuantity(slots, itemId) < quantity) return false;

        long remaining = quantity;

        // Drain the smallest stacks first so the container tends toward fewer, fuller
        // stacks rather than a scattering of nearly-empty ones.
        foreach (var e in SmallestFirst(slots, itemId))
        {
            if (remaining <= 0) break;

            long taken = System.Math.Min(e.quantity, remaining);
            e.quantity -= taken;
            remaining  -= taken;

            if (e.quantity <= 0) Clear(e);
        }

        return remaining <= 0;
    }

    /// <summary>The container's stacks of one item, smallest first.</summary>
    private static List<InventoryEntry> SmallestFirst(List<InventoryEntry> slots, string itemId)
    {
        var matches = new List<InventoryEntry>();
        foreach (var e in slots)
            if (!IsEmpty(e) && e.itemId == itemId) matches.Add(e);

        matches.Sort((a, b) => a.quantity.CompareTo(b.quantity));
        return matches;
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
            // Merge only as far as the cap allows and leave the remainder behind.
            // Pouring one stack into another without a limit is how a slot ends up
            // holding more than a slot can hold.
            long moving = System.Math.Min(Headroom(to), from.quantity);

            to.quantity   += moving;
            from.quantity -= moving;

            if (from.quantity <= 0) Clear(from);
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
            // Capped, like the within-container merge. A full destination stack takes
            // nothing and the drag is a no-op rather than a silent overflow.
            long moving = System.Math.Min(Headroom(dst), src.quantity);
            if (moving <= 0) return false;

            dst.quantity += moving;
            src.quantity -= moving;

            if (src.quantity <= 0) Clear(src);
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

        // Clamp to what the destination can actually take, rather than refusing the
        // whole move. Depositing a stack into a nearly-full bank should move what
        // fits, not nothing at all — and AddItem is all-or-nothing, so the amount has
        // to be trimmed here rather than there.
        moving = System.Math.Min(moving, FreeCapacityFor(to, src.itemId));
        if (moving <= 0) return false;

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
