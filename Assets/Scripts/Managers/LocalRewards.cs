using IdleExplorers.Backend;

/// <summary>
/// The only place the client is allowed to pay itself.
///
/// ══ WHY THIS EXISTS RATHER THAN AN IF AT EACH SITE ════════════════════════════
///
/// Because "every local grant is behind an authority check" is an invariant, and an
/// invariant spread across a dozen if-statements is one somebody eventually forgets.
/// Worse, it cannot be checked: an automated check can see that a guard exists
/// somewhere nearby, which is not the same as it guarding THIS line. That distinction
/// bit twice while this was being written -- a check passed on a guard belonging to
/// the block above.
///
/// Routing every grant through one function makes the invariant structural. The rule
/// becomes "gameplay code does not call AddItem or AddSkillXP directly", which is an
/// exact thing to look for and impossible to satisfy by accident.
///
/// ══ WHY IT SILENTLY DOES NOTHING RATHER THAN THROWING ═════════════════════════
///
/// Under an authoritative server the calls still HAPPEN -- the attack loop and the
/// gathering tick have not changed, and should not have to know. They are simply
/// worth nothing, because the server already paid for the same action from its own
/// clock. Throwing would mean every caller learning about a distinction that exists to
/// keep them from having to.
/// </summary>
public static class LocalRewards
{
    /// <summary>
    /// Whether the client is the one paying.
    ///
    /// False whenever a real server is authoritative. Offline and in shadow mode the
    /// local managers ARE the game, and this is how they stay that way.
    /// </summary>
    public static bool ClientPays => !ServerState.IsAuthoritative;

    /// <summary>
    /// One gathering action's yield.
    /// </summary>
    /// <returns>
    /// False only when the client owns rewards AND the bag is full -- which is the
    /// caller's signal to stop gathering.
    ///
    /// TRUE under an authoritative server even with no room, deliberately. The server
    /// tracks the bag and reports stoppedForRoom in the settlement; a client that
    /// stopped on its own reading of a stale inventory would halt a run the server was
    /// happily paying, and the player would watch their character down tools for no
    /// visible reason.
    /// </returns>
    public static bool TryGiveItem(string itemId, long quantity)
    {
        if (!ClientPays) return true;

        if (GameManager.Inventory?.CanAddItem(itemId, quantity) != true) return false;

        GameManager.Inventory.AddItem(itemId, quantity);
        return true;
    }

    /// <summary>Experience for one action. Nothing when the server is keeping score.</summary>
    public static void GiveSkillXp(string skillId, long amount)
    {
        if (!ClientPays) return;

        GameManager.Skills?.AddSkillXP(skillId, amount);
    }

    /// <summary>
    /// One craft: spend the inputs, produce the output, pay the experience.
    ///
    /// All three together, because separating them is how a craft charges without
    /// producing. Under an authoritative server none of it happens here -- the server
    /// consumes and pays inside one transaction, which is the only place that
    /// all-or-nothing is actually guaranteed.
    /// </summary>
    /// <returns>False when the inputs could not be afforded. True when the server owns it.</returns>
    public static bool TryCraft(CraftRecipe recipe, long produced)
    {
        if (!ClientPays) return true;
        if (recipe == null)  return false;

        // All-or-nothing: verifies again and only then spends, so nothing is consumed
        // unless every input is affordable.
        if (!CraftingSupply.ConsumeFor(recipe, 1)) return false;

        GameManager.Inventory?.AddItem(recipe.outputItemId, produced);
        GameManager.Skills?.AddSkillXP(recipe.skillId, (long)recipe.xpPerCraft);

        return true;
    }
}
