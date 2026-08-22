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
    /// The AFK fast-forward item. Topped up rather than granted once, because the
    /// whole point is to test long offline windows repeatedly — running out mid-test
    /// would mean editing the save by hand to carry on.
    /// </summary>
    public const string MysticGemItemId = "mystic_gem";
    public const long   MysticGemStock  = 5;

    /// <summary>
    /// Ensures the character is carrying test gems. Called on character select, so
    /// consuming one never strands a test session.
    /// </summary>
    public static void EnsureTestItems()
    {
        if (!Enabled) return;

        var inventory = GameManager.Inventory;
        if (inventory == null || CharacterManager.Current == null) return;

        // Only top up when below the target, so this never fights the player's own
        // stack or silently refills something they deliberately dropped everything of
        // mid-session — it runs once per character selection, not per frame.
        long held = inventory.GetQuantity(MysticGemItemId);
        if (held >= MysticGemStock) return;

        if (!inventory.CanAddItem(MysticGemItemId))
        {
            Debug.Log("[DevTools] No inventory room for Mystic Gems.");
            return;
        }

        inventory.AddItem(MysticGemItemId, MysticGemStock - held);
        Debug.Log($"[DevTools] Topped up {MysticGemItemId} to {MysticGemStock}.");
    }
}
