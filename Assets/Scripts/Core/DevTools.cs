using UnityEngine;

/// <summary>
/// Switches for things that exist to make the game testable, not playable.
///
/// Everything gated here must be inert when Enabled is false — no leftover items,
/// no altered rates — so shipping is a single flag flip rather than a hunt for
/// debug affordances.
/// </summary>
public static class DevTools
{
    /// <summary>
    /// Master switch. Development builds and the Editor only; a release build cannot
    /// turn this on by accident because the symbol is not defined there.
    /// </summary>
    public static bool Enabled
    {
        get
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            return true;
#else
            return false;
#endif
        }
    }

    /// <summary>
    /// Items topped up on every character selection.
    ///
    /// Topped up rather than granted once, because the whole point is to test the
    /// same thing repeatedly — running out mid-test would mean editing the save by
    /// hand to carry on.
    /// </summary>
    private static readonly (string ItemId, long Stock)[] TestItems =
    {
        // Fast-forwards the current activity by an hour, for testing offline windows.
        ("mystic_gem", 5),

        // Changes the character's class. Fewer, because two is enough to go somewhere
        // and come back, and a stack of twenty would crowd out the inventory that
        // every other test needs room in.
        ("shifting_sigil", 2),

        // Wears down a random worn piece on use. Durability takes an hour of being hit
        // to observe otherwise, so every state past it — the break, the stats dropping
        // off, a set falling under its threshold, the repair bill — was effectively
        // untestable. Ten, because the interesting run is hammering one set to pieces.
        ("proving_hammer", 10),

        // Puts every skill back to level 1, for re-testing level gates.
        ("resetting_draught", 2),

        // Reopens the appearance editor. Two, because the interesting test is
        // changing race twice with boots on, to check the feet follow the body.
        ("mirror_of_faces", 2),

        // A complete Weak Tin Man set. Set bonuses and durability are only testable
        // with six matching pieces on at once, and reaching that by smithing means
        // mining ore first — a long way to go before you can look at the feature.
        //
        // Tin only: copper and bronze are gated behind smithing 10 and 20, and handing
        // those out would make the level gates untestable, which is what the reset
        // draught above exists to exercise.
        ("tin_helmet",    1),
        ("tin_shoulders", 1),
        ("tin_platebody", 1),
        ("tin_gloves",    1),
        ("tin_boots",     1),
        ("tin_legguards", 1),
    };

    /// <summary>
    /// Ensures the character is carrying the test items. Called on character select,
    /// so consuming one never strands a test session.
    /// </summary>
    public static void EnsureTestItems()
    {
        if (!Enabled) return;

        var inventory = GameManager.Inventory;
        if (inventory == null || CharacterManager.Current == null) return;

        foreach (var (itemId, stock) in TestItems)
        {
            // Worn copies count. Without this, equipping a test helmet drops the
            // carried quantity to zero and the next character selection hands out
            // another one — six armour slots would fill the bag with duplicates.
            long held = inventory.GetQuantity(itemId) + EquippedCount(itemId);
            if (held >= stock) continue;

            if (!inventory.CanAddItem(itemId))
            {
                Debug.Log($"[DevTools] No inventory room for {itemId}.");
                continue;
            }

            inventory.AddItem(itemId, stock - held);
            Debug.Log($"[DevTools] Topped up {itemId} to {stock}.");
        }
    }

    /// <summary>How many of an item the character is wearing.</summary>
    private static long EquippedCount(string itemId)
    {
        var equipment = GameManager.Equipment;
        if (equipment == null) return 0;

        long worn = 0;
        foreach (var slot in EquipmentSlots.All)
            if (equipment.GetEquipped(slot.SlotId) == itemId) worn++;

        return worn;
    }
}
