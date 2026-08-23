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
    /// How long an activity's materials will last, in crafts and in seconds.
    ///
    /// Crafting is the only activity that can run dry, and this is the number that
    /// decides whether logging out — or spending a gem — is worth anything. Shared
    /// between the menu readout and the gem confirmation so the two cannot disagree
    /// about how long the supplies hold out.
    /// </summary>
    /// <returns>False when the activity is not crafting, so there is nothing to run out of.</returns>
    public static bool ProjectSupply(SkillActivityData activity, out long crafts,
                                      out long seconds, out string limitingItemId)
    {
        crafts = 0; seconds = 0; limitingItemId = null;

        if (activity == null || string.IsNullOrEmpty(activity.recipeId)) return false;

        var recipe = GameManager.Content?.GetRecipe(activity.recipeId);
        if (recipe == null) return false;

        crafts = MaxCrafts(recipe, out limitingItemId);

        float perHour = ActivityManager.ActionsPerHour(
                            ActivityManager.TalentAdjustedSeconds(activity.secondsPerAction, crafting: true),
                            activity.activeRateMulti)
                        * ActivityManager.EffectiveAfkRate(activity);

        seconds = perHour > 0f ? (long)(crafts / perHour * 3600f) : 0;
        return true;
    }

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
        if (recipe == null) return 0;

        // A crafting recipe that consumes nothing is a data error, not a free lunch.
        // This used to return long.MaxValue, which is an unbounded-production bug one
        // bad JSON edit away from firing — and producing from nothing is precisely the
        // failure this whole system exists to remove.
        if (recipe.inputs == null || recipe.inputs.Length == 0)
        {
            UnityEngine.Debug.LogWarning($"[CraftingSupply] Recipe '{recipe.id}' has no inputs — " +
                                          "refusing to craft. Check recipe_data.json.");
            return 0;
        }

        long best = long.MaxValue;

        foreach (var input in recipe.inputs)
        {
            if (input == null || string.IsNullOrEmpty(input.itemId) || input.quantity <= 0)
            {
                UnityEngine.Debug.LogWarning($"[CraftingSupply] Recipe '{recipe.id}' has a malformed " +
                                              "input entry — refusing to craft.");
                return 0;
            }

            long possible = Available(input.itemId) / input.quantity;
            if (possible < best)
            {
                best           = possible;
                limitingItemId = input.itemId;
            }
        }

        return best;
    }

    /// <summary>
    /// Consumes the inputs for a number of crafts, all or nothing.
    ///
    /// Verifying every input before spending any is what stops a multi-input recipe
    /// eating the copper and then discovering there is no tin — which would destroy
    /// material and produce nothing.
    /// </summary>
    public static bool ConsumeFor(CraftRecipe recipe, long crafts)
    {
        if (recipe?.inputs == null || crafts <= 0) return false;

        foreach (var input in recipe.inputs)
        {
            if (input == null || string.IsNullOrEmpty(input.itemId)) return false;
            if (Available(input.itemId) < input.quantity * crafts) return false;
        }

        foreach (var input in recipe.inputs)
            Consume(input.itemId, input.quantity * crafts);

        return true;
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
