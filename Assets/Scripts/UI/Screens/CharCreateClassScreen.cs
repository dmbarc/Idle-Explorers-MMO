using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Screen 5 of character creation: choose a class.
///
/// ══ WHY EVERY CARD COLLAPSED INTO ONE LINE ════════════════════════════════════
///
/// This asked UIFactory.ScrollView for a HORIZONTAL scroll, but that helper always
/// returns content anchored top-stretch — full width, ZERO HEIGHT — because it was
/// written for vertical lists. The screen then added a ContentSizeFitter with only
/// horizontalFit, which fixes the axis that was already fine and leaves the broken one
/// at zero. HorizontalLayoutGroup.childControlHeight then handed every card that zero
/// height, and BuildClassCard positioned its labels by FRACTIONAL anchors — so 0.90
/// and 0.04 of zero are the same point, and the name, flavour, stats, divider,
/// abilities and button all stacked on one line at the top.
///
/// Fixed twice over: UIFactory.ScrollStrip re-anchors the content left-stretch so it
/// inherits the viewport's height, and the card is now a VerticalLayoutGroup with real
/// pixel heights instead of fractions of a container it does not control.
///
/// Only the card strip is rebuilt when the selection changes. Rebuilding the whole
/// screen recreated the scroll view underneath itself and left duplicate children
/// alive for a frame, because Destroy is deferred to end of frame while Build runs
/// immediately.
/// </summary>
public class CharCreateClassScreen : UIScreen
{
    private string        _selectedClassId;
    private RectTransform _cardContent;
    private Button        _nextButton;
    private TMP_Text      _hint;

    private const float CardWidth = 300f;

    /// <summary>
    /// Portrait height. Taller than wide, because the render texture is — a shorter
    /// panel letterboxes the character into a strip through the middle of it.
    /// </summary>
    private const float PortraitHeight = 220f;

    /// <summary>Cards reflect the current selection, which changes between visits.</summary>
    public override bool RebuildOnShow => true;

    public override void Build()
    {
        var theme = UIManager.Theme;

        // Seed from the shared creation state so stepping Back from the appearance
        // screen keeps your choice, while starting a new character (which resets
        // that state) starts blank.
        _selectedClassId = CharCreateState.PendingClassId;

        UIFactory.Panel(transform, "Bg", theme.panelBg, true);

        var title = UIFactory.Label(transform, "CHOOSE YOUR CLASS", theme.fontSizeTitle,
                                     theme.accentGold, TextAlignmentOptions.Center);
        UIFactory.At(title, 0.05f, 0.90f, 0.95f, 0.98f);

        var (scroll, content) = UIFactory.ScrollStrip(transform, "ClassScroll", theme.spacing * 2);
        UIFactory.At(scroll, 0.02f, 0.19f, 0.98f, 0.89f);

        _cardContent = content;
        RebuildCards();

        _hint = UIFactory.Label(transform, "Pick a class to continue.", theme.fontSizeSmall,
                                 theme.textSecondary, TextAlignmentOptions.Center);
        UIFactory.At(_hint, 0.25f, 0.13f, 0.75f, 0.18f);

        var backBtn = UIFactory.Button(transform, "← BACK", () => GameManager.UI?.Pop(), width: 0f);
        UIFactory.At(backBtn, 0.33f, 0.04f, 0.47f, 0.11f);

        _nextButton = UIFactory.Button(transform, "NEXT →", GoNext, width: 0f);
        UIFactory.At(_nextButton, 0.53f, 0.04f, 0.67f, 0.11f);

        RefreshNextState();
    }

    private void GoNext()
    {
        if (string.IsNullOrEmpty(_selectedClassId))
        {
            GameEvents.FireToast("Choose a class first.");
            return;
        }
        CharCreateState.PendingClassId = _selectedClassId;
        GameManager.UI?.Push<CharCreateAppearanceScreen>();
    }

    // ── Cards ─────────────────────────────────────────────────────────────────

    private void RebuildCards()
    {
        if (_cardContent == null) return;

        // DestroyImmediate: the cards are rebuilt in the same frame, so deferred
        // Destroy would leave the old set visible alongside the new one. It also
        // disposes each preview's RenderTexture through CharacterPreview.OnDestroy —
        // reselecting a class five times would otherwise leak five stages.
        for (int i = _cardContent.childCount - 1; i >= 0; i--)
            DestroyImmediate(_cardContent.GetChild(i).gameObject);

        var classes = GameManager.Content?.Classes;
        if (classes == null || classes.Count == 0)
        {
            var warning = UIFactory.Label(_cardContent, "No class data loaded.\nCheck class_data.json in StreamingAssets.",
                                           UIManager.Theme.fontSizeBody, UIManager.Theme.accentRed,
                                           TextAlignmentOptions.Center);
            var le = warning.gameObject.AddComponent<LayoutElement>();
            le.minWidth = le.preferredWidth = 600f;
            return;
        }

        foreach (var kv in classes)
            BuildClassCard(kv.Value);

        BuildComingSoonCard();
    }

    /// <summary>
    /// A sixth card, unselectable, promising more.
    ///
    /// The strip ended in dead space with five cards in it, which reads as the roster
    /// being finished. It is not, and a silhouette says so in the place a player is
    /// already looking.
    /// </summary>
    private void BuildComingSoonCard()
    {
        var theme = UIManager.Theme;

        var card = UIFactory.Panel(_cardContent, "Card_ComingSoon", theme.cardBg, false);

        var cardLe = card.AddComponent<LayoutElement>();
        cardLe.minWidth = cardLe.preferredWidth = CardWidth;

        var vlg = card.AddComponent<VerticalLayoutGroup>();
        vlg.padding                = new RectOffset(12, 12, 12, 12);
        vlg.spacing                = 6f;
        vlg.childForceExpandWidth  = true;
        vlg.childForceExpandHeight = false;
        vlg.childControlWidth      = true;
        vlg.childControlHeight     = true;
        vlg.childAlignment         = TextAnchor.UpperCenter;

        var portrait = UIFactory.Panel(card.transform, "Portrait", theme.slotBg, false);
        Fixed(portrait, PortraitHeight);

        // The same rig every other card uses, blacked out. A drawn silhouette would
        // be a second art dependency for a card that exists to say "not yet".
        var preview = CharacterPreview.Create(portrait.transform, SpumAppearance.Default(), "Preview_Soon");
        preview?.SetSilhouette(new Color(0.06f, 0.07f, 0.10f, 1f));

        var name = UIFactory.Label(card.transform, "?", theme.fontSizeTitle,
                                    theme.textDisabled, TextAlignmentOptions.Center);
        Fixed(name, 30f);

        var soon = UIFactory.Label(card.transform, "COMING SOON", theme.fontSizeBody,
                                    theme.textSecondary, TextAlignmentOptions.Center);
        Fixed(soon, 26f);

        var blurb = UIFactory.Label(card.transform,
                                     "More ways to explore are on the way. Your first character " +
                                     "will still be here when they arrive.",
                                     theme.fontSizeSmall, theme.textDisabled, TextAlignmentOptions.Top);
        blurb.textWrappingMode = TextWrappingModes.Normal;
        Fixed(blurb, 72f);
    }

    /// <summary>
    /// One class card: portrait, name, flavour, what it is good at, and its abilities.
    ///
    /// Built as a vertical stack with pixel heights. The previous version anchored
    /// every row to a fraction of the card, which only works if the card has a height
    /// of its own — and inside a layout group it does not.
    /// </summary>
    private void BuildClassCard(ClassData cls)
    {
        var theme       = UIManager.Theme;
        bool isSelected = _selectedClassId == cls.id;

        var card = UIFactory.Panel(_cardContent, $"Card_{cls.id}",
                                    isSelected ? theme.accentGold : theme.cardBg, false);

        var cardLe = card.AddComponent<LayoutElement>();
        cardLe.minWidth = cardLe.preferredWidth = CardWidth;

        var vlg = card.AddComponent<VerticalLayoutGroup>();
        vlg.padding                = new RectOffset(12, 12, 12, 12);
        vlg.spacing                = 6f;
        vlg.childForceExpandWidth  = true;
        vlg.childForceExpandHeight = false;
        vlg.childControlWidth      = true;
        vlg.childControlHeight     = true;
        vlg.childAlignment         = TextAnchor.UpperCenter;

        Color heading = isSelected ? theme.panelBg : theme.accentGold;
        Color body    = isSelected ? theme.panelBg : theme.textSecondary;
        Color detail  = isSelected ? theme.panelBg : theme.textPrimary;

        // ── Portrait ──────────────────────────────────────────────────────────
        var portrait = UIFactory.Panel(card.transform, "Portrait", theme.slotBg, false);
        Fixed(portrait, PortraitHeight);

        var preview = CharacterPreview.Create(portrait.transform, PreviewLookFor(cls), $"Preview_{cls.id}");
        if (preview == null)
        {
            // No rig prefab. Say so on the card rather than leaving a grey rectangle
            // that looks like a loading bug.
            var fallback = UIFactory.Label(portrait.transform, cls.DisplayName.ToUpper(),
                                            theme.fontSizeBody, body, TextAlignmentOptions.Center);
            UIFactory.FillParent(fallback.rectTransform);
        }
        else
        {
            // Dress it. Every class used to show the same plate and helmet — not a
            // choice anyone made, but the SPUM rig's own clothing, which nothing took
            // off. SpumAppearance.Apply strips it now, so what a card wears is what
            // class_data.json says it wears.
            DressPreview(preview, cls);
        }

        // ── Name ──────────────────────────────────────────────────────────────
        var name = UIFactory.Label(card.transform, cls.DisplayName.ToUpper(), theme.fontSizeBody * 1.2f,
                                    heading, TextAlignmentOptions.Center);
        Fixed(name, 30f);

        // ── Flavour ───────────────────────────────────────────────────────────
        var flavor = UIFactory.Label(card.transform, cls.flavorText, theme.fontSizeSmall,
                                      body, TextAlignmentOptions.Top);
        flavor.textWrappingMode = TextWrappingModes.Normal;
        Fixed(flavor, 56f);

        // ── How it fights ─────────────────────────────────────────────────────
        var stats = UIFactory.Label(card.transform, StatLine(cls), theme.fontSizeLabel,
                                     detail, TextAlignmentOptions.Center);
        Fixed(stats, 34f);

        Fixed(UIFactory.HorizontalDivider(card.transform), 8f);

        // ── What it is actually good at ───────────────────────────────────────
        //
        // This replaced the ability list, which had become misleading: abilities are
        // earned from the talent tree now, so a level 1 character of any class has
        // none of the four a card used to promise. Skill affinities are the thing that
        // makes classes feel different over a long session, and nothing else showed
        // them before the character existed.
        var role = UIFactory.Label(card.transform, RoleLine(cls), theme.fontSizeLabel,
                                    detail, TextAlignmentOptions.TopLeft);
        role.textWrappingMode = TextWrappingModes.Normal;
        Fixed(role, 96f);

        // ── Select ────────────────────────────────────────────────────────────
        var selectBtn = UIFactory.Button(card.transform, isSelected ? "✓ SELECTED" : "SELECT", () =>
        {
            _selectedClassId = cls.id;
            RebuildCards();
            RefreshNextState();
        }, width: 0f);
        Fixed(selectBtn, 44f);
    }

    /// <summary>Gives a row a real pixel height, since the card has no height to divide up.</summary>
    private static void Fixed(GameObject row, float height)
    {
        var le = row.AddComponent<LayoutElement>();
        le.minHeight = le.preferredHeight = height;
    }

    private static void Fixed(Component row, float height) => Fixed(row.gameObject, height);

    /// <summary>
    /// How the preview is dressed for a class. Falls back to the plain default, so a
    /// class added to class_data.json without a previewLook still shows a character.
    /// </summary>
    private static SpumSaveData PreviewLookFor(ClassData cls)
    {
        if (cls.previewLook != null && !cls.previewLook.IsEmpty) return cls.previewLook;
        return SpumAppearance.Default();
    }

    /// <summary>Puts a class's signature armour on its preview.</summary>
    private static void DressPreview(CharacterPreview preview, ClassData cls)
    {
        if (cls.previewEquipment == null) return;

        foreach (var piece in cls.previewEquipment)
        {
            if (piece == null || string.IsNullOrEmpty(piece.slot)) continue;
            preview.SetEquipment(piece.slot, piece.sprite);
        }
    }

    private static string StatLine(ClassData cls) =>
        $"HP {cls.baseHp}   MP {cls.baseMp}\n" +
        $"Damage {cls.baseAttackMin}–{cls.baseAttackMax}   Speed {cls.attackSpeedSeconds:0.0}s";

    /// <summary>
    /// What a class is for, in the two terms that actually differ between them: which
    /// resource its abilities spend, and which skills it advances faster.
    ///
    /// The affinities come from the class's own stat block rather than from
    /// StatsManager, which answers for the LIVE character — and on this screen there
    /// is no character yet.
    /// </summary>
    private static string RoleLine(ClassData cls)
    {
        var sb = new StringBuilder();

        sb.Append("Spends: ").Append(ResourceName(cls)).Append('\n');

        var affinities = TopAffinities(cls, 3);
        if (affinities.Count == 0) return sb.ToString();

        sb.Append("Learns faster:\n");
        foreach (var (skillId, bonus) in affinities)
        {
            string skillName = GameManager.Content?.GetSkill(skillId)?.DisplayName
                               ?? char.ToUpper(skillId[0]) + skillId.Substring(1);
            sb.Append("  • ").Append(skillName)
              .Append("  +").Append(Mathf.RoundToInt(bonus * 100f)).Append("%\n");
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Which pool this class's abilities draw on. Read from the abilities themselves
    /// rather than declared separately, so it cannot disagree with what they cost.
    /// </summary>
    private static string ResourceName(ClassData cls)
    {
        if (cls.abilities == null) return "nothing";

        int mana = 0, stamina = 0;
        foreach (var ability in cls.abilities)
        {
            if (ability == null || ability.cost <= 0f) continue;
            if (ability.costType == "mana")    mana++;
            if (ability.costType == "stamina") stamina++;
        }

        if (mana == 0 && stamina == 0) return "nothing";
        if (mana > 0 && stamina > 0)   return "mana and stamina";
        return mana > 0 ? "mana" : "stamina";
    }

    private static List<(string SkillId, float Bonus)> TopAffinities(ClassData cls, int count)
    {
        var results = new List<(string, float)>();

        var affinities = cls.stats?.skillAffinity;
        if (affinities == null) return results;

        foreach (var entry in affinities)
            if (entry != null && entry.value > 0.001f) results.Add((entry.skillId, entry.value));

        results.Sort((a, b) => b.Item2.CompareTo(a.Item2));
        if (results.Count > count) results.RemoveRange(count, results.Count - count);
        return results;
    }

    private void RefreshNextState()
    {
        bool ready = !string.IsNullOrEmpty(_selectedClassId);
        if (_nextButton != null) _nextButton.interactable = ready;
        if (_hint != null)
        {
            _hint.text = ready
                ? $"{GameManager.Content?.GetClass(_selectedClassId)?.DisplayName} selected."
                : "Pick a class to continue.";
            _hint.color = ready ? UIManager.Theme.accentGreen : UIManager.Theme.textSecondary;
        }
    }
}
