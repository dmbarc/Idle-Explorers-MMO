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
            // Only top up when below the target, so this never fights the player's own
            // stack or silently refills something they deliberately dropped everything
            // of mid-session — it runs once per character selection, not per frame.
            long held = inventory.GetQuantity(itemId);
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
}
