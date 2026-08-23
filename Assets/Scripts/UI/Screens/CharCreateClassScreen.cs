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
        Fixed(portrait, 170f);

        var preview = CharacterPreview.Create(portrait.transform, PreviewLookFor(cls), $"Preview_{cls.id}");
        if (preview == null)
        {
            // No rig prefab. Say so on the card rather than leaving a grey rectangle
            // that looks like a loading bug.
            var fallback = UIFactory.Label(portrait.transform, cls.DisplayName.ToUpper(),
                                            theme.fontSizeBody, body, TextAlignmentOptions.Center);
            UIFactory.FillParent(fallback.rectTransform);
        }

        // ── Name ──────────────────────────────────────────────────────────────
        var name = UIFactory.Label(card.transform, cls.DisplayName.ToUpper(), theme.fontSizeBody * 1.2f,
                                    heading, TextAlignmentOptions.Center);
        Fixed(name, 30f);

        // ── Flavour ───────────────────────────────────────────────────────────
        var flavor = UIFactory.Label(card.transform, cls.flavorText, theme.fontSizeSmall,
                                      body, TextAlignmentOptions.Top);
        flavor.textWrappingMode = TextWrappingModes.Normal;
        Fixed(flavor, 62f);

        // ── What it is good at ────────────────────────────────────────────────
        var stats = UIFactory.Label(card.transform, StatLine(cls), theme.fontSizeLabel,
                                     detail, TextAlignmentOptions.Center);
        Fixed(stats, 34f);

        Fixed(UIFactory.HorizontalDivider(card.transform), 8f);

        // ── Abilities ─────────────────────────────────────────────────────────
        var abilities = UIFactory.Label(card.transform, FormatAbilities(cls), theme.fontSizeLabel,
                                         detail, TextAlignmentOptions.TopLeft);
        Fixed(abilities, 92f);

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

    private static string StatLine(ClassData cls) =>
        $"HP {cls.baseHp}   MP {cls.baseMp}\n" +
        $"Damage {cls.baseAttackMin}–{cls.baseAttackMax}   Speed {cls.attackSpeedSeconds:0.0}s";

    private static string FormatAbilities(ClassData cls)
    {
        if (cls.abilities == null || cls.abilities.Length == 0) return "";

        var sb = new StringBuilder();
        for (int i = 0; i < cls.abilities.Length; i++)
        {
            var a = cls.abilities[i];
            sb.Append(a.isPassive ? "⬦ " : "• ");
            sb.Append(a.name);
            if (a.isPassive) sb.Append("  (passive)");
            if (i < cls.abilities.Length - 1) sb.Append('\n');
        }
        return sb.ToString();
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
