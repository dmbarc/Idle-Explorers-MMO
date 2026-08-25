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

    /// <summary>
    /// Which specced class's tree is on screen. Persisted across rebuilds — spending a
    /// point rebuilds the panel, and snapping back to the first tab every time would
    /// make a second tree almost unusable.
    /// </summary>
    private static string _viewedClassId;

    public override void Build()
    {
        var theme = UIManager.Theme;

        var backdrop    = UIFactory.Panel(transform, "Backdrop", theme.overlayBg, true);
        var backdropBtn = backdrop.AddComponent<Button>();
        backdropBtn.transition = Selectable.Transition.None;
        backdropBtn.onClick.AddListener(() => GameManager.UI?.Pop());

        // Narrower than full width on purpose: the HUD's ability bar has to stay
        // visible and reachable underneath, because abilities are dragged from a
        // talent card down onto it.
        var panel   = UIFactory.Panel(transform, "TalentPanel", theme.panelBg, false);
        var panelRt = panel.GetComponent<RectTransform>();
        panelRt.anchorMin = new Vector2(0.14f, 0.12f);
        panelRt.anchorMax = new Vector2(0.86f, 0.95f);
        panelRt.offsetMin = panelRt.offsetMax = Vector2.zero;

        ResolveViewedClass();

        BuildHeader(panel.transform, theme);
        BuildTabs(panel.transform, theme);
        BuildTree(panel.transform, theme);
        BuildFooter(panel.transform, theme);
    }

    /// <summary>
    /// Keeps the viewed tab pointing at a class the character actually has — they may
    /// have changed class, or this may be a different character entirely.
    /// </summary>
    private static void ResolveViewedClass()
    {
        var ids = CharacterManager.Current?.ClassIds();
        if (ids == null || ids.Count == 0) { _viewedClassId = null; return; }

        if (string.IsNullOrEmpty(_viewedClassId) || !ids.Contains(_viewedClassId))
            _viewedClassId = ids[0];
    }

    // ── Class tabs ────────────────────────────────────────────────────────────

    /// <summary>
    /// One tab per specced class, plus an invitation when a class slot is free.
    ///
    /// Built even for a single-class character, so the row does not appear from
    /// nowhere at level 50 — and so the "another class unlocks at…" line has somewhere
    /// to live before then.
    /// </summary>
    private void BuildTabs(Transform parent, UITheme theme)
    {
        var character = CharacterManager.Current;
        if (character == null) return;

        var tabs = UIFactory.HStack(parent, theme.spacing, "ClassTabs");
        UIFactory.At(tabs, 0.02f, 0.845f, 0.98f, 0.90f);
        tabs.childForceExpandWidth = true;
        tabs.childControlWidth     = true;

        foreach (var classId in character.ClassIds())
        {
            string id  = classId;
            var    cls = GameManager.Content?.GetClass(classId);
            bool   on  = classId == _viewedClassId;

            var btn = UIFactory.Button(tabs.transform, cls?.DisplayName ?? classId, () =>
            {
                _viewedClassId = id;
                RebuildContents();
            }, width: 0f);

            var colors = btn.colors;
            colors.normalColor = on ? theme.accentGold : theme.buttonNormal;
            btn.colors         = colors;
            btn.interactable   = !on;
        }

        if (character.HasUnusedClassSlot())
        {
            UIFactory.Button(tabs.transform, "+ ADD CLASS",
                             () => GameManager.UI?.Push<SpecSelectModal>(), width: 0f);
            return;
        }

        if (character.ClassSlots() >= 3) return;

        int next = character.ClassSlots() == 1 ? CharacterData.ClassSlotTwoLevel
                                               : CharacterData.ClassSlotThreeLevel;

        var locked = UIFactory.Label(tabs.transform, $"next class: level {next}",
                                      theme.fontSizeLabel, theme.textDisabled,
                                      TextAlignmentOptions.Center);
        locked.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;
    }

    /// <summary>
    /// Rebuilt on a talent change AND on a class change.
    ///
    /// The class one is why "+ ADD CLASS" stayed on the tab strip after a class had
    /// been added: SpecSelectModal is an overlay, so popping it does not resume the
    /// panel underneath, and nothing else asked this screen to look again. The tab
    /// only disappeared once some other button happened to rebuild the panel.
    /// </summary>
    public override void OnShow()
    {
        GameEvents.OnTalentsChanged += MarkDirty;
        GameEvents.OnClassChanged   += OnClassChanged;
    }

    public override void OnHide()
    {
        GameEvents.OnTalentsChanged -= MarkDirty;
        GameEvents.OnClassChanged   -= OnClassChanged;
    }

    private void OnClassChanged(string classId)
    {
        // The tab being viewed may be a class the character no longer has.
        _viewedClassId = null;
        MarkDirty();
    }

    private bool _needsRebuild;

    /// <summary>
    /// Defers the redraw to the end of the frame rather than doing it inline.
    ///
    /// OnTalentsChanged fires from inside the Button click that spent the point, and
    /// RebuildContents uses DestroyImmediate — so rebuilding inline would destroy the
    /// button whose click handler is still on the stack.
    /// </summary>
    private void MarkDirty() => _needsRebuild = true;

    private void LateUpdate()
    {
        if (!_needsRebuild) return;
        _needsRebuild = false;
        RebuildContents();
    }

    // ── Header ────────────────────────────────────────────────────────────────

    private void BuildHeader(Transform parent, UITheme theme)
    {
        var character = CharacterManager.Current;

        var header   = UIFactory.Panel(parent, "Header", theme.headerBg, false);
        var headerRt = header.GetComponent<RectTransform>();
        headerRt.anchorMin = new Vector2(0f, 0.905f);
        headerRt.anchorMax = new Vector2(1f, 1f);
        headerRt.offsetMin = headerRt.offsetMax = Vector2.zero;

        // The character's title, not a single class name — a cross-specced character
        // is a Paladin, and calling the screen "Warrior — Talents" would contradict
        // every other place their title appears.
        var title = UIFactory.Label(header.transform,
                                     $"{ClassManager.TitleFor(character)} — TALENTS",
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
                                     $"{spent} spent across all trees  •  one point per level (you are {level})  " +
                                     "•  drag an ability onto the bar below",
                                     theme.fontSizeLabel, theme.textSecondary,
                                     TextAlignmentOptions.MidlineLeft);
        UIFactory.At(hint, 0.02f, 0.06f, 0.98f, 0.44f);
    }

    // ── Tree ──────────────────────────────────────────────────────────────────

    private void BuildTree(Transform parent, UITheme theme)
    {
        // ScrollList puts the layout group on the content itself. Building rows into a
        // stretched child of a zero-height content is what cut the top off the class
        // picker, and this tree would do the same the moment it outgrew its viewport.
        var (scroll, content) = UIFactory.ScrollList(parent, "TalentScroll", RowGap);
        var scrollRt = scroll.GetComponent<RectTransform>();
        scrollRt.anchorMin = new Vector2(0.02f, 0.10f);
        scrollRt.anchorMax = new Vector2(0.98f, 0.835f);
        scrollRt.offsetMin = scrollRt.offsetMax = Vector2.zero;

        var character = CharacterManager.Current;
        var tree      = TalentManager.TreeOf(_viewedClassId);

        if (tree == null || tree.Length == 0)
        {
            // No At() call: the content now drives its children through a layout
            // group, and anchoring a child inside one is a fight the layout wins.
            UIFactory.Label(content, "This class has no talents yet.",
                             theme.fontSizeBody, theme.textSecondary,
                             TextAlignmentOptions.Center);
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

        int spent = TalentManager.SpentPoints(character);

        foreach (var kvp in tiers)
            BuildTierRow(content, theme, character, kvp.Key, kvp.Value, spent);
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

        // An unlocked ability node is a drag SOURCE — this is how an ability reaches
        // the action bar. Added only once the node is actually taken, so dragging a
        // talent you have not bought cannot put a dead button on the bar.
        if (node.GrantsAbility && rank > 0)
        {
            var ability = TalentManager.FindAbilityFor(character, node.abilityId);
            var icon    = GameManager.Content?.GetAbilityIcon(ability);

            card.AddComponent<AbilityDragHandle>().Bind(node.abilityId, icon);
        }

        var nameLabel = UIFactory.Label(card.transform, node.name, theme.fontSizeSmall,
                                         rank > 0 ? theme.accentGold
                                                  : (canTake ? theme.textPrimary : theme.textDisabled),
                                         TextAlignmentOptions.Center);
        UIFactory.At(nameLabel, 0.04f, 0.66f, 0.96f, 0.97f);

        var descLabel = UIFactory.Label(card.transform, node.description, theme.fontSizeLabel,
                                         theme.textSecondary, TextAlignmentOptions.Top);
        UIFactory.At(descLabel, 0.05f, 0.24f, 0.95f, 0.66f);
        descLabel.textWrappingMode = TextWrappingModes.Normal;

        // The rank counter doubles as the ability node's affordance. A card you can
        // drag but which looks identical to one you cannot is a feature nobody finds.
        string rankText = node.GrantsAbility && rank > 0
            ? $"{rank} / {node.RankCap}   ✥ drag to bar"
            : $"{rank} / {node.RankCap}";

        var rankLabel = UIFactory.Label(card.transform, rankText,
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
