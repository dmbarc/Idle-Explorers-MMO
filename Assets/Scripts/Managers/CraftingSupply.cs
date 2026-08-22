using System.Collections.Generic;

/// <summary>
/// Where a crafting station looks for its inputs: the character's own inventory
/// first, then the account bank.
///
/// Inventory first is deliberate. Material you are carrying is material you chose to
/// bring, so spending it before dipping into shared storage matches what a player
/// expects — and it means a character can work from their own stock without another
/// character's banked goods quietly disappearing.
/// </summary>
public static class CraftingSupply
{
    /// <summary>Total of an item across the character inventory and the account bank.</summary>
    public static long Available(string itemId)
    {
        if (string.IsNullOrEmpty(itemId)) return 0;

        long carried = GameManager.Inventory?.GetQuantity(itemId) ?? 0;
        long banked  = GameManager.Bank?.GetQuantity(itemId) ?? 0;
        return carried + banked;
    }

    /// <summary>How much of it is in the bank rather than carried — shown in the recipe list.</summary>
    public static long AvailableInBank(string itemId) =>
        GameManager.Bank?.GetQuantity(itemId) ?? 0;

    /// <summary>
    /// Spends a quantity, drawing from the inventory before the bank. Returns false
    /// and changes nothing when the combined total is short.
    /// </summary>
    public static bool Consume(string itemId, long quantity)
    {
        if (string.IsNullOrEmpty(itemId) || quantity <= 0) return false;
        if (Available(itemId) < quantity) return false;

        long carried   = GameManager.Inventory?.GetQuantity(itemId) ?? 0;
        long fromInv   = System.Math.Min(carried, quantity);
        long fromBank  = quantity - fromInv;

        if (fromInv  > 0) GameManager.Inventory?.RemoveItem(itemId, fromInv);
        if (fromBank > 0) GameManager.Bank?.RemoveItem(itemId, fromBank);
        return true;
    }

    /// <summary>
    /// How many times a recipe can run given current stock, across both stores.
    /// long.MaxValue when the recipe consumes nothing.
    /// </summary>
    public static long MaxCrafts(CraftRecipe recipe, out string limitingItemId)
    {
        limitingItemId = null;
        if (recipe?.inputs == null || recipe.inputs.Length == 0) return long.MaxValue;

        long best = long.MaxValue;

        foreach (var input in recipe.inputs)
        {
            if (input == null || input.quantity <= 0) continue;

            long possible = Available(input.itemId) / input.quantity;
            if (possible < best)
            {
                best           = possible;
                limitingItemId = input.itemId;
            }
        }

        return best == long.MaxValue ? long.MaxValue : best;
    }

    /// <summary>Per-input stock, for the recipe list's have/need readout.</summary>
    public static List<(CraftIngredient input, long have)> Stock(CraftRecipe recipe)
    {
        var rows = new List<(CraftIngredient, long)>();
        if (recipe?.inputs == null) return rows;

        foreach (var input in recipe.inputs)
        {
            if (input == null) continue;
            rows.Add((input, Available(input.itemId)));
        }
        return rows;
    }
}
