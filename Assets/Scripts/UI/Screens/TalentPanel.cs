using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The class talent tree: rows of nodes, one point at a time.
///
/// Laid out from the data rather than from a hand-built grid — a node declares its
/// tier and column, and this arranges whatever the class file contains. Adding a
/// talent is a JSON edit; adding a whole new class tree needs no code here at all.
///
/// Every node states the number it actually applies, because a talent whose text is
/// vaguer than its effect is indistinguishable from one that does nothing.
/// </summary>
public class TalentPanel : UIScreen
{
    /// <summary>Sits over the HUD rather than replacing it.</summary>
    public override bool IsOverlay => true;

    /// <summary>Ranks and available points change constantly, so nothing is cached.</summary>
    public override bool RebuildOnShow => true;

    private const float NodeHeight = 92f;
    private const float RowGap     = 10f;

    public override void Build()
    {
        var theme = UIManager.Theme;

        var backdrop    = UIFactory.Panel(transform, "Backdrop", theme.overlayBg, true);
        var backdropBtn = backdrop.AddComponent<Button>();
        backdropBtn.transition = Selectable.Transition.None;
        backdropBtn.onClick.AddListener(() => GameManager.UI?.Pop());

        var panel   = UIFactory.Panel(transform, "TalentPanel", theme.panelBg, false);
        var panelRt = panel.GetComponent<RectTransform>();
        panelRt.anchorMin = new Vector2(0.16f, 0.08f);
        panelRt.anchorMax = new Vector2(0.84f, 0.92f);
        panelRt.offsetMin = panelRt.offsetMax = Vector2.zero;

        BuildHeader(panel.transform, theme);
        BuildTree(panel.transform, theme);
        BuildFooter(panel.transform, theme);
    }

    public override void OnShow()  => GameEvents.OnTalentsChanged += Rebuild;
    public override void OnHide()  => GameEvents.OnTalentsChanged -= Rebuild;

    private void Rebuild() => RebuildContents();

    // ── Header ────────────────────────────────────────────────────────────────

    private void BuildHeader(Transform parent, UITheme theme)
    {
        var character = CharacterManager.Current;
        var cls       = GameManager.Content?.GetClass(character?.classId);

        var header   = UIFactory.Panel(parent, "Header", theme.headerBg, false);
        var headerRt = header.GetComponent<RectTransform>();
        headerRt.anchorMin = new Vector2(0f, 0.90f);
        headerRt.anchorMax = new Vector2(1f, 1f);
        headerRt.offsetMin = headerRt.offsetMax = Vector2.zero;

        var title = UIFactory.Label(header.transform,
                                     $"{cls?.DisplayName ?? "TALENTS"} — TALENTS",
                                     theme.fontSizeBody, theme.accentGold,
                                     TextAlignmentOptions.MidlineLeft);
        UIFactory.At(title, 0.02f, 0.45f, 0.60f, 0.95f);

        int available = TalentManager.AvailablePoints(character);
        int spent     = TalentManager.SpentPoints(character);

        var points = UIFactory.Label(header.transform,
                                      available > 0
                                          ? $"{available} point{(available == 1 ? "" : "s")} to spend"
                                          : "No points to spend",
                                      theme.fontSizeSmall,
                                      available > 0 ? theme.accentGreen : theme.textSecondary,
                                      TextAlignmentOptions.MidlineRight);
        UIFactory.At(points, 0.55f, 0.45f, 0.98f, 0.95f);

        // Where the next point comes from, so an empty tree is not a dead end with no
        // explanation. One point per character level after the first.
        int level = character?.level ?? 1;
        var hint  = UIFactory.Label(header.transform,
                                     $"{spent} spent  •  one point per character level (you are {level})",
                                     theme.fontSizeLabel, theme.textSecondary,
                                     TextAlignmentOptions.MidlineLeft);
        UIFactory.At(hint, 0.02f, 0.06f, 0.98f, 0.44f);
    }

    // ── Tree ──────────────────────────────────────────────────────────────────

    private void BuildTree(Transform parent, UITheme theme)
    {
        var (scroll, content) = UIFactory.ScrollView(parent, "TalentScroll");
        var scrollRt = scroll.GetComponent<RectTransform>();
        scrollRt.anchorMin = new Vector2(0.02f, 0.10f);
        scrollRt.anchorMax = new Vector2(0.98f, 0.885f);
        scrollRt.offsetMin = scrollRt.offsetMax = Vector2.zero;

        var character = CharacterManager.Current;
        var tree      = TalentManager.TreeFor(character);

        if (tree == null || tree.Length == 0)
        {
            var empty = UIFactory.Label(content, "This class has no talents yet.",
                                         theme.fontSizeBody, theme.textSecondary,
                                         TextAlignmentOptions.Center);
            UIFactory.At(empty, 0f, 0.4f, 1f, 0.6f);
            return;
        }

        // Group by tier, then order within a tier by column, so the file can list
        // nodes in any order and still lay out as its author intended.
        var tiers = new SortedDictionary<int, List<TalentNode>>();
        foreach (var node in tree)
        {
            if (node == null) continue;
            if (!tiers.TryGetValue(node.tier, out var row))
                tiers[node.tier] = row = new List<TalentNode>();
            row.Add(node);
        }
        foreach (var row in tiers.Values)
            row.Sort((a, b) => a.column.CompareTo(b.column));

        var stack = UIFactory.VStack(content, RowGap, true, "Tiers");
        UIFactory.At(stack, 0f, 0f, 1f, 1f);

        var fitter = stack.gameObject.AddComponent<ContentSizeFitter>();
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        int spent = TalentManager.SpentPoints(character);

        foreach (var kvp in tiers)
            BuildTierRow(stack.transform, theme, character, kvp.Key, kvp.Value, spent);

        // The scroll content must size itself or the rows render on top of each other.
        var contentFitter = content.gameObject.AddComponent<ContentSizeFitter>();
        contentFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        var contentLayout = content.gameObject.AddComponent<VerticalLayoutGroup>();
        contentLayout.childForceExpandHeight = false;
        contentLayout.childControlHeight     = true;
        contentLayout.childControlWidth      = true;
    }

    private void BuildTierRow(Transform parent, UITheme theme, CharacterData character,
                               int tier, List<TalentNode> nodes, int spent)
    {
        int required = TalentManager.TierPointRequirement(tier);
        bool locked  = spent < required;

        var rowRoot = new GameObject($"Tier{tier}", typeof(RectTransform));
        rowRoot.transform.SetParent(parent, false);

        var rowLayout = rowRoot.AddComponent<LayoutElement>();
        rowLayout.minHeight = rowLayout.preferredHeight = NodeHeight + 22f;

        var caption = UIFactory.Label(rowRoot.transform,
                                       locked ? $"TIER {tier + 1} — locked, {required - spent} more point(s) needed"
                                              : $"TIER {tier + 1}",
                                       theme.fontSizeLabel,
                                       locked ? theme.textDisabled : theme.textSecondary,
                                       TextAlignmentOptions.MidlineLeft);
        UIFactory.At(caption, 0.005f, 0.80f, 0.995f, 1f);

        var row = UIFactory.HStack(rowRoot.transform, theme.spacing, "Nodes");
        UIFactory.At(row, 0f, 0f, 1f, 0.78f);
        row.childForceExpandWidth = true;
        row.childControlWidth     = true;

        foreach (var node in nodes)
            BuildNode(row.transform, theme, character, node, locked);
    }

    private void BuildNode(Transform parent, UITheme theme, CharacterData character,
                            TalentNode node, bool tierLocked)
    {
        int  rank    = TalentManager.RankOf(character, node.id);
        bool maxed   = rank >= node.RankCap;
        bool canTake = !tierLocked && TalentManager.CanSpend(character, node, out _);

        var card   = UIFactory.Panel(parent, $"Talent_{node.id}", theme.cardBg, false);
        var cardEl = card.AddComponent<LayoutElement>();
        cardEl.minHeight = cardEl.preferredHeight = NodeHeight;
        cardEl.flexibleWidth = 1f;

        // Taken talents get a gold edge so the shape of a build reads at a glance.
        if (rank > 0)
        {
            var border = card.GetComponent<Image>();
            if (border != null)
                border.color = maxed ? theme.accentGold : Color.Lerp(border.color, theme.accentGold, 0.35f);
        }

        var button = card.AddComponent<Button>();
        button.targetGraphic = card.GetComponent<Image>();
        button.transition    = Selectable.Transition.None;
        button.onClick.AddListener(() => TrySpend(node));

        var nameLabel = UIFactory.Label(card.transform, node.name, theme.fontSizeSmall,
                                         rank > 0 ? theme.accentGold
                                                  : (canTake ? theme.textPrimary : theme.textDisabled),
                                         TextAlignmentOptions.Center);
        UIFactory.At(nameLabel, 0.04f, 0.66f, 0.96f, 0.97f);

        var descLabel = UIFactory.Label(card.transform, node.description, theme.fontSizeLabel,
                                         theme.textSecondary, TextAlignmentOptions.Top);
        UIFactory.At(descLabel, 0.05f, 0.24f, 0.95f, 0.66f);
        descLabel.textWrappingMode = TextWrappingModes.Normal;

        var rankLabel = UIFactory.Label(card.transform, $"{rank} / {node.RankCap}",
                                         theme.fontSizeLabel,
                                         maxed ? theme.accentGold
                                               : (canTake ? theme.accentGreen : theme.textDisabled),
                                         TextAlignmentOptions.Center);
        UIFactory.At(rankLabel, 0.04f, 0.03f, 0.96f, 0.22f);
    }

    /// <summary>
    /// Spends a point, or explains why it cannot. The refusal is always shown — a
    /// talent button that does nothing when clicked, with no reason given, is the
    /// most common way a tree like this reads as broken.
    /// </summary>
    private void TrySpend(TalentNode node)
    {
        if (!TalentManager.Spend(node.id, out string reason))
            GameEvents.FireToast(reason ?? "Cannot take that talent.");

        // Spend fires OnTalentsChanged, which rebuilds this screen — but a refusal
        // does not, and nothing needs redrawing in that case.
    }

    // ── Footer ────────────────────────────────────────────────────────────────

    private void BuildFooter(Transform parent, UITheme theme)
    {
        var resetBtn = UIFactory.Button(parent, "RESET (FREE)", ConfirmReset, width: 200f);
        UIFactory.At(resetBtn, 0.03f, 0.015f, 0.32f, 0.09f);

        var closeBtn = UIFactory.Button(parent, "CLOSE", () => GameManager.UI?.Pop(), width: 200f);
        UIFactory.At(closeBtn, 0.68f, 0.015f, 0.97f, 0.09f);
    }

    private void ConfirmReset()
    {
        // No confirmation dialog: the respec is free and instant, so the worst case
        // of a misclick is spending the same points again.
        TalentManager.ResetAll();
    }
}
