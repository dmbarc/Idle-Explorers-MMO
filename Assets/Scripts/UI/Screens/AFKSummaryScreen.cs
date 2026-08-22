using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Screen 30: what your character earned while you were gone.
///
/// This is the payoff screen for the game's central promise — the grind happened
/// without you. It is pushed after character selection whenever AFK rewards
/// accrued, and reads ActivityManager.PendingSummary.
/// </summary>
public class AFKSummaryScreen : UIScreen
{
    /// <summary>Shown over character select while the rewards are read.</summary>
    public override bool IsOverlay => true;

    /// <summary>
    /// Every row is built from PendingSummary, so this must be rebuilt per show —
    /// otherwise the second character you select still shows the first one's
    /// rewards.
    /// </summary>
    public override bool RebuildOnShow => true;

    public override void Build()
    {
        var theme = UIManager.Theme;

        // Dimmed backdrop so this reads as a modal over character select
        UIFactory.Panel(transform, "Backdrop", theme.overlayBg, true);

        // ── Card ──────────────────────────────────────────────────────────────
        var card   = UIFactory.Panel(transform, "SummaryCard", theme.panelBg, false);
        var cardRt = card.GetComponent<RectTransform>();
        cardRt.anchorMin = new Vector2(0.28f, 0.15f);
        cardRt.anchorMax = new Vector2(0.72f, 0.85f);
        cardRt.offsetMin = cardRt.offsetMax = Vector2.zero;

        var summary = GameManager.Activity?.PendingSummary;

        BuildHeader(card.transform, theme, summary);
        BuildBody(card.transform, theme, summary);
        BuildCollectButton(card.transform, theme);
    }

    // ── Header ────────────────────────────────────────────────────────────────

    private void BuildHeader(Transform parent, UITheme theme, AFKRewardSummary summary)
    {
        bool truncated = summary != null && summary.WasTruncated;

        var header   = UIFactory.Panel(parent, "Header", theme.headerBg, false);
        var headerRt = header.GetComponent<RectTransform>();
        // A truncation line needs a third row, so the header grows to fit it.
        headerRt.anchorMin = new Vector2(0f, truncated ? 0.80f : 0.84f);
        headerRt.anchorMax = new Vector2(1f, 1f);
        headerRt.offsetMin = headerRt.offsetMax = Vector2.zero;

        var title = UIFactory.Label(header.transform, "WHILE YOU WERE AWAY",
                                     theme.fontSizeTitle, theme.accentGold, TextAlignmentOptions.Center);
        var titleRt = title.GetComponent<RectTransform>();
        titleRt.anchorMin = new Vector2(0f, truncated ? 0.58f : 0.45f);
        titleRt.anchorMax = new Vector2(1f, 1f);
        titleRt.offsetMin = titleRt.offsetMax = Vector2.zero;

        string subtitle;
        if (summary == null)
        {
            subtitle = "Nothing accrued.";
        }
        else
        {
            string elapsed  = NumberFormatter.FormatAFKTime(summary.elapsedSeconds).Replace(" AFK", "");
            string skill    = GameManager.Content?.GetSkill(summary.skillId)?.DisplayName ?? summary.skillId;
            string activity = string.IsNullOrEmpty(summary.activityName)
                ? skill
                : $"{skill} — {summary.activityName}";

            // Naming the character makes it obvious at a glance which one this
            // belongs to, rather than leaving it to be inferred from the card behind.
            string who = string.IsNullOrEmpty(summary.characterName) ? "" : $"{summary.characterName}  •  ";

            subtitle = $"{who}{elapsed}  •  {activity}";
            if (summary.wasCapped)
                subtitle += "  (capped at 24h)";
        }

        var sub = UIFactory.Label(header.transform, subtitle,
                                   theme.fontSizeSmall, theme.textSecondary, TextAlignmentOptions.Center);
        var subRt = sub.GetComponent<RectTransform>();
        subRt.anchorMin = new Vector2(0f, truncated ? 0.30f : 0f);
        subRt.anchorMax = new Vector2(1f, truncated ? 0.58f : 0.45f);
        subRt.offsetMin = subRt.offsetMax = Vector2.zero;

        if (!truncated) return;

        // The whole point of the crafting AFK loop is that it can stop early. Saying
        // so plainly is the difference between "the game shorted me" and "I should
        // bank more shrimp next time".
        string itemName = GameManager.Content?.GetItem(summary.ranOutOfItemId)?.DisplayName
                          ?? summary.ranOutOfItemId;
        string earned   = NumberFormatter.FormatAFKTime(summary.effectiveSeconds).Replace(" AFK", "");
        string away     = NumberFormatter.FormatAFKTime(summary.elapsedSeconds).Replace(" AFK", "");

        var warning = UIFactory.Label(header.transform,
                                       $"You only earned {earned} of your {away} away — you ran out of {itemName}.",
                                       theme.fontSizeSmall, theme.accentRed, TextAlignmentOptions.Center);
        var warnRt = warning.GetComponent<RectTransform>();
        warnRt.anchorMin = new Vector2(0.02f, 0f);
        warnRt.anchorMax = new Vector2(0.98f, 0.30f);
        warnRt.offsetMin = warnRt.offsetMax = Vector2.zero;
    }

    // ── Body ──────────────────────────────────────────────────────────────────

    private void BuildBody(Transform parent, UITheme theme, AFKRewardSummary summary)
    {
        var (scroll, content) = UIFactory.ScrollView(parent, "RewardsScroll");
        var scrollRt = scroll.GetComponent<RectTransform>();
        scrollRt.anchorMin = new Vector2(0.04f, 0.14f);
        scrollRt.anchorMax = new Vector2(0.96f, 0.82f);
        scrollRt.offsetMin = scrollRt.offsetMax = Vector2.zero;

        var vlg = content.gameObject.AddComponent<VerticalLayoutGroup>();
        vlg.spacing                = theme.spacing;
        vlg.padding                = new RectOffset(8, 8, 8, 8);
        vlg.childForceExpandWidth  = true;
        vlg.childForceExpandHeight = false;
        vlg.childControlWidth      = true;
        vlg.childControlHeight     = true;

        var fitter = content.gameObject.AddComponent<ContentSizeFitter>();
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        if (summary == null || !summary.HasAnything)
        {
            UIFactory.Label(content, "Your character was idle.\nPick an activity before logging out to earn while away.",
                             theme.fontSizeBody, theme.textSecondary, TextAlignmentOptions.Center);
            return;
        }

        if (summary.kills > 0)
        {
            SectionHeader(content, theme, "COMBAT");
            RewardRow(content, theme, "Monsters defeated", NumberFormatter.Format(summary.kills), null);
        }

        if (summary.xpGained.Count > 0)
        {
            SectionHeader(content, theme, "EXPERIENCE");
            foreach (var entry in summary.xpGained)
            {
                string skillName = GameManager.Content?.GetSkill(entry.itemId)?.DisplayName ?? entry.itemId;
                int    level     = GameManager.Skills?.GetSkillLevel(entry.itemId) ?? 1;
                RewardRow(content, theme,
                          $"{skillName}  (now level {level})",
                          $"+{NumberFormatter.Format(entry.quantity)} xp",
                          null);
            }
        }

        if (summary.itemsGained.Count > 0)
        {
            SectionHeader(content, theme, "ITEMS");
            foreach (var entry in summary.itemsGained)
            {
                var item = GameManager.Content?.GetItem(entry.itemId);
                RewardRow(content, theme,
                          item?.DisplayName ?? entry.itemId,
                          NumberFormatter.Format(entry.quantity),
                          GameManager.Content?.GetItemIcon(entry.itemId));
            }
        }
    }

    private void SectionHeader(Transform parent, UITheme theme, string text)
    {
        var label = UIFactory.Label(parent, text, theme.fontSizeLabel,
                                     theme.accentGold, TextAlignmentOptions.MidlineLeft);
        var le = label.gameObject.AddComponent<LayoutElement>();
        le.minHeight = le.preferredHeight = 26f;
    }

    private void RewardRow(Transform parent, UITheme theme, string label, string value, Sprite icon)
    {
        var row = UIFactory.Panel(parent, "Row", theme.cardBg, false);
        var le  = row.AddComponent<LayoutElement>();
        le.minHeight = le.preferredHeight = 40f;

        float textLeft = 0.03f;
        if (icon != null)
        {
            var img   = UIFactory.Icon(row.transform, icon, 30f);
            var imgRt = img.GetComponent<RectTransform>();
            imgRt.anchorMin = new Vector2(0.02f, 0.5f);
            imgRt.anchorMax = new Vector2(0.02f, 0.5f);
            imgRt.pivot     = new Vector2(0f, 0.5f);
            imgRt.anchoredPosition = Vector2.zero;
            textLeft = 0.10f;
        }

        var nameLabel = UIFactory.Label(row.transform, label, theme.fontSizeSmall,
                                         theme.textPrimary, TextAlignmentOptions.MidlineLeft);
        var nameRt = nameLabel.GetComponent<RectTransform>();
        nameRt.anchorMin = new Vector2(textLeft, 0f);
        nameRt.anchorMax = new Vector2(0.68f, 1f);
        nameRt.offsetMin = nameRt.offsetMax = Vector2.zero;

        var valueLabel = UIFactory.Label(row.transform, value, theme.fontSizeSmall,
                                          theme.accentGreen, TextAlignmentOptions.MidlineRight);
        var valueRt = valueLabel.GetComponent<RectTransform>();
        valueRt.anchorMin = new Vector2(0.68f, 0f);
        valueRt.anchorMax = new Vector2(0.97f, 1f);
        valueRt.offsetMin = valueRt.offsetMax = Vector2.zero;
    }

    // ── Footer ────────────────────────────────────────────────────────────────

    private void BuildCollectButton(Transform parent, UITheme theme)
    {
        var btn = UIFactory.Button(parent, "COLLECT", () =>
        {
            GameManager.Activity?.ConsumePendingSummary();

            // Rewards are already banked by this point, so this only decides where to
            // go next. Arriving from character select means continuing into the world;
            // arriving from a Mystic Gem means we are ALREADY in the world, and
            // calling GoToGame there would reload the map out from under the player.
            if (GameManager.Instance?.CurrentState == GameManager.GameState.InGame)
                GameManager.UI?.Pop();
            else
                GameManager.Instance?.GoToGame();
        }, width: 0f);

        var rt = btn.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.30f, 0.03f);
        rt.anchorMax = new Vector2(0.70f, 0.12f);
        rt.offsetMin = rt.offsetMax = Vector2.zero;
        rt.sizeDelta = Vector2.zero;
    }
}
