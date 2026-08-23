using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The recipe list at a crafting station.
///
/// One panel serves every station — the recipe set is resolved from the node's
/// stationType, so adding a station type is a data change, not a new screen.
///
/// Stock counts combine the character's inventory and the account bank, because
/// that is exactly what the craft loop consumes from.
/// </summary>
public class CraftingPanel : UIScreen
{
    /// <summary>Set by SkillNodeController immediately before Push.</summary>
    public static SkillNodeController Station;

    /// <summary>Sits over the HUD rather than replacing it.</summary>
    public override bool IsOverlay => true;

    /// <summary>Every row reads live stock and skill level, so it rebuilds per open.</summary>
    public override bool RebuildOnShow => true;

    public override void Build()
    {
        var theme = UIManager.Theme;

        UIFactory.Panel(transform, "Backdrop", theme.overlayBg, true);

        var panel = UIFactory.Panel(transform, "CraftingPanel", theme.panelBg, false);
        UIFactory.At(panel.transform, 0.22f, 0.10f, 0.78f, 0.90f);

        BuildHeader(panel.transform, theme);
        BuildRecipeList(panel.transform, theme);

        var close = UIFactory.Button(panel.transform, "CLOSE", () => GameManager.UI?.Pop(), width: 0f);
        UIFactory.At(close, 0.35f, 0.02f, 0.65f, 0.09f);
    }

    public override void OnHide() => Station = null;

    // ── Header ────────────────────────────────────────────────────────────────

    private void BuildHeader(Transform parent, UITheme theme)
    {
        var header = UIFactory.Panel(parent, "Header", theme.headerBg, false);
        UIFactory.At(header.transform, 0f, 0.90f, 1f, 1f);

        string stationType = Station?.Entry?.stationType ?? "";
        string skillId     = Station?.Entry?.skillId ?? "";
        string skillName   = GameManager.Content?.GetSkill(skillId)?.DisplayName ?? skillId;
        int    level       = GameManager.Skills?.GetSkillLevel(skillId) ?? 1;

        var title = UIFactory.Label(header.transform, StationTitle(stationType), theme.fontSizeBody,
                                     theme.accentGold, TextAlignmentOptions.MidlineLeft);
        UIFactory.At(title, 0.03f, 0f, 0.55f, 1f);

        var levelLabel = UIFactory.Label(header.transform,
                                          string.IsNullOrEmpty(skillId) ? "" : $"{skillName} Lv. {level}",
                                          theme.fontSizeSmall, theme.textSecondary,
                                          TextAlignmentOptions.MidlineRight);
        UIFactory.At(levelLabel, 0.56f, 0f, 0.97f, 1f);
    }

    private static string StationTitle(string stationType) => stationType switch
    {
        "campfire" => "CAMPFIRE",
        "anvil"    => "ANVIL",
        "forge"    => "FORGE",
        _          => "CRAFTING",
    };

    // ── Recipe rows ───────────────────────────────────────────────────────────

    private void BuildRecipeList(Transform parent, UITheme theme)
    {
        var (scroll, content) = UIFactory.ScrollView(parent, "RecipeScroll");
        UIFactory.At(scroll, 0.03f, 0.11f, 0.97f, 0.885f);

        var vlg = content.gameObject.AddComponent<VerticalLayoutGroup>();
        vlg.spacing                = theme.spacing;
        vlg.padding                = new RectOffset(8, 8, 8, 8);
        vlg.childForceExpandWidth  = true;
        vlg.childForceExpandHeight = false;
        vlg.childControlWidth      = true;
        vlg.childControlHeight     = true;

        var fitter = content.gameObject.AddComponent<ContentSizeFitter>();
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        string stationType = Station?.Entry?.stationType ?? "";
        var recipes = GameManager.Content?.GetRecipesForStation(stationType) ?? new List<CraftRecipe>();

        if (recipes.Count == 0)
        {
            UIFactory.Label(content, "No recipes here yet.", theme.fontSizeBody,
                             theme.textSecondary, TextAlignmentOptions.Center);
            return;
        }

        foreach (var recipe in recipes)
            BuildRecipeRow(content, theme, recipe);
    }

    private void BuildRecipeRow(Transform parent, UITheme theme, CraftRecipe recipe)
    {
        int  level    = GameManager.Skills?.GetSkillLevel(recipe.skillId) ?? 1;
        bool unlocked = level >= recipe.reqSkillLevel;

        long maxCrafts = CraftingSupply.MaxCrafts(recipe, out _);
        bool hasStock  = maxCrafts > 0;

        var row = UIFactory.Panel(parent, $"Recipe_{recipe.id}", theme.cardBg, false);
        var le  = row.AddComponent<LayoutElement>();
        le.minHeight = le.preferredHeight = 92f;

        // Output icon
        var icon = UIFactory.Icon(row.transform, GameManager.Content?.GetItemIcon(recipe.outputItemId), 48f);
        UIFactory.At(icon, 0.015f, 0.24f, 0.085f, 0.80f);

        // Name and output quantity
        string title = recipe.outputQuantity > 1
            ? $"{recipe.DisplayName}  ×{recipe.outputQuantity}"
            : recipe.DisplayName;

        var nameLabel = UIFactory.Label(row.transform, title, theme.fontSizeSmall,
                                         unlocked ? theme.textPrimary : theme.textDisabled,
                                         TextAlignmentOptions.MidlineLeft);
        UIFactory.At(nameLabel, 0.10f, 0.56f, 0.62f, 0.92f);

        // Inputs, with have/need. Red when short so the blocker is obvious at a glance.
        var inputsLabel = UIFactory.Label(row.transform, FormatInputs(recipe), theme.fontSizeLabel,
                                           hasStock ? theme.textSecondary : theme.accentRed,
                                           TextAlignmentOptions.MidlineLeft);
        UIFactory.At(inputsLabel, 0.10f, 0.24f, 0.62f, 0.55f);

        // Requirement / XP
        string meta = unlocked
            ? $"+{recipe.xpPerCraft:0} xp  •  {recipe.SecondsPerCraft(level):0.0}s each"
            : $"Requires {GameManager.Content?.GetSkill(recipe.skillId)?.DisplayName ?? recipe.skillId} level {recipe.reqSkillLevel}";

        var metaLabel = UIFactory.Label(row.transform, meta, theme.fontSizeLabel,
                                         unlocked ? theme.accentGreen : theme.accentRed,
                                         TextAlignmentOptions.MidlineLeft);
        UIFactory.At(metaLabel, 0.10f, 0.05f, 0.62f, 0.23f);

        // How many this stock supports — the number that makes AFK planning possible.
        // There is no "unlimited" case any more: MaxCrafts returns 0 for an
        // input-less recipe rather than long.MaxValue, so the branch that printed
        // "unlimited" was dead and would have been a lie if it ever ran.
        string craftsText = !unlocked ? "" : $"{NumberFormatter.Format(maxCrafts)} available";

        var craftsLabel = UIFactory.Label(row.transform, craftsText, theme.fontSizeLabel,
                                           theme.textSecondary, TextAlignmentOptions.MidlineRight);
        UIFactory.At(craftsLabel, 0.63f, 0.05f, 0.80f, 0.45f);

        var makeBtn = UIFactory.Button(row.transform, "MAKE", () => Select(recipe), width: 0f);
        UIFactory.At(makeBtn, 0.82f, 0.22f, 0.98f, 0.78f);
        makeBtn.interactable = unlocked && hasStock;
    }

    private string FormatInputs(CraftRecipe recipe)
    {
        var parts = new List<string>();

        foreach (var (input, have) in CraftingSupply.Stock(recipe))
        {
            string itemName = GameManager.Content?.GetItem(input.itemId)?.DisplayName ?? input.itemId;
            long   banked   = CraftingSupply.AvailableInBank(input.itemId);

            // Calling out the banked share explains why a count can exceed what the
            // player can see in their own inventory.
            string suffix = banked > 0 ? $", {NumberFormatter.Format(banked)} banked" : "";
            parts.Add($"{itemName} {NumberFormatter.Format(have)}/{input.quantity}{suffix}");
        }

        return parts.Count == 0 ? "No materials required" : string.Join("     ", parts);
    }

    private void Select(CraftRecipe recipe)
    {
        if (Station == null)
        {
            GameEvents.FireToast("Step up to the station first.");
            return;
        }

        Station.BeginCrafting(recipe);
        GameManager.UI?.Pop();
    }
}
