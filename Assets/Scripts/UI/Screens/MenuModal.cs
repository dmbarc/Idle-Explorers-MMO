using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// Screen 9: the in-game menu behind the ≡ button.
///
/// Also the only place the current activity is displayed. It used to be a panel
/// permanently occupying the corner of the HUD with a show/hide option in here to
/// get rid of it — so the menu carried an option whose only purpose was to hide
/// something the menu could simply have shown on demand.
/// </summary>
public class MenuModal : UIScreen
{
    /// <summary>Sits over the HUD rather than replacing it.</summary>
    public override bool IsOverlay => true;

    /// <summary>The activity block reflects live state, so it is rebuilt per open.</summary>
    public override bool RebuildOnShow => true;

    public override void Build()
    {
        var theme = UIManager.Theme;

        // Backdrop closes the menu when clicked outside the card
        var backdrop = UIFactory.Panel(transform, "Backdrop", theme.overlayBg, true);
        var backdropBtn = backdrop.AddComponent<Button>();
        backdropBtn.transition = Selectable.Transition.None;
        backdropBtn.onClick.AddListener(() => GameManager.UI?.Pop());

        var card = UIFactory.Panel(transform, "MenuCard", theme.panelBg, false);
        UIFactory.At(card.transform, 0.36f, 0.20f, 0.64f, 0.80f);

        var title = UIFactory.Label(card.transform, "MENU", theme.fontSizeBody,
                                     theme.accentGold, TextAlignmentOptions.Center);
        UIFactory.At(title, 0.05f, 0.92f, 0.95f, 0.98f);

        UIFactory.At(UIFactory.HorizontalDivider(card.transform).transform, 0.05f, 0.90f, 0.95f, 0.915f);

        BuildActivityBlock(card.transform, theme);

        UIFactory.At(UIFactory.HorizontalDivider(card.transform).transform, 0.05f, 0.545f, 0.95f, 0.56f);

        var stack = UIFactory.VStack(card.transform, theme.spacing, true, "MenuOptions");
        UIFactory.At(stack.transform, 0.08f, 0.06f, 0.92f, 0.53f);

        AddOption(stack.transform, "RESUME",   () => GameManager.UI?.Pop());

        // No TALENTS option here. The HUD carries a permanent TAL button that already
        // shows the unspent-point badge, so this was a second door to the same room —
        // and the worse one, because reaching it meant opening a menu that covers the
        // action bar an ability has to be dragged onto.

        AddOption(stack.transform, "SHOP",     () => GameManager.UI?.Push<ShopPanel>());
        AddOption(stack.transform, "SETTINGS", () => GameManager.UI?.Push<SettingsPanel>());
        AddOption(stack.transform, "CHARACTER SELECT", () =>
        {
            // ReturnToMainMenu stamps lastLogoutUnixTime, which is what AFK accrual
            // measures from — leaving via this button must go through it.
            GameManager.UI?.Pop();
            GameManager.Instance?.ReturnToMainMenu();
        });

        // No SAVE GAME option. SaveManager already writes on quit, on pause, on the
        // way to character select, and after AFK rewards are granted — a manual
        // button implied progress could be lost without it, which was never true.
    }

    // ── Current activity ──────────────────────────────────────────────────────

    /// <summary>
    /// What this character is doing, and therefore what accrues while logged out.
    /// Reads ActivityManager rather than caching, because the menu is rebuilt on
    /// every open.
    /// </summary>
    private void BuildActivityBlock(Transform card, UITheme theme)
    {
        var panel = UIFactory.Panel(card, "ActivityBlock", theme.cardBg, false);
        UIFactory.At(panel.transform, 0.06f, 0.58f, 0.94f, 0.885f);

        var header = UIFactory.Label(panel.transform, "CURRENT ACTIVITY", theme.fontSizeLabel,
                                      theme.accentGold, TextAlignmentOptions.Center);
        UIFactory.At(header, 0.04f, 0.80f, 0.96f, 0.97f);

        var activity = GameManager.Activity?.CurrentActivity;
        bool idle = activity == null || string.IsNullOrEmpty(activity.skillId);

        string skillText = idle
            ? "Idle"
            : GameManager.Content?.GetSkill(activity.skillId)?.DisplayName ?? activity.skillId;

        var skillLabel = UIFactory.Label(panel.transform, skillText, theme.fontSizeBody,
                                          idle ? theme.textSecondary : theme.textPrimary,
                                          TextAlignmentOptions.Center);
        UIFactory.At(skillLabel, 0.04f, 0.54f, 0.96f, 0.78f);

        string targetText = idle ? "Nothing will accrue while you are away" : activity.activityTargetName;
        var targetLabel = UIFactory.Label(panel.transform, targetText, theme.fontSizeSmall,
                                           theme.textSecondary, TextAlignmentOptions.Center);
        UIFactory.At(targetLabel, 0.04f, 0.30f, 0.96f, 0.52f);

        string rateText = idle
            ? ""
            : $"{NumberFormatter.Format((long)activity.xpPerHour)} xp/hr  •  AFK {NumberFormatter.FormatRate(activity.afkRateMulti)}";
        var rateLabel = UIFactory.Label(panel.transform, rateText, theme.fontSizeSmall,
                                         theme.accentGreen, TextAlignmentOptions.Center);
        UIFactory.At(rateLabel, 0.04f, 0.16f, 0.96f, 0.30f);

        // Crafting is the one activity that can run out, so this is the number that
        // decides whether logging out now is worth anything.
        var supplyLabel = UIFactory.Label(panel.transform, CraftingSupplyText(activity),
                                           theme.fontSizeLabel, theme.accentGold,
                                           TextAlignmentOptions.Center);
        UIFactory.At(supplyLabel, 0.04f, 0.03f, 0.96f, 0.15f);
    }

    /// <summary>
    /// How long the current recipe's materials will last, in crafts and in time.
    ///
    /// Both this and the gem confirmation ask CraftingSupply.ProjectSupply, so the
    /// menu cannot quietly disagree with the warning shown before a gem is spent.
    /// </summary>
    private static string CraftingSupplyText(SkillActivityData activity)
    {
        if (!CraftingSupply.ProjectSupply(activity, out long crafts, out long seconds,
                                           out string limitingItemId))
            return "";

        if (crafts <= 0)
        {
            string outOf = GameManager.Content?.GetItem(limitingItemId)?.DisplayName ?? limitingItemId;
            return $"Out of {outOf} — nothing will accrue";
        }

        if (seconds <= 0) return $"Materials for {NumberFormatter.Format(crafts)} more";

        return $"Materials for {NumberFormatter.Format(crafts)} more  •  " +
               $"~{NumberFormatter.FormatAFKTime(seconds).Replace(" AFK", "")} AFK";
    }

    private void AddOption(Transform parent, string label, System.Action onClick)
    {
        var btn = UIFactory.Button(parent, label, onClick, width: 0f);
        var le  = btn.gameObject.AddComponent<LayoutElement>();
        le.minHeight = le.preferredHeight = UIManager.Theme.buttonHeight;
    }
}
