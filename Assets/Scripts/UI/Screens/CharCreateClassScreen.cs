using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Screen 5 of character creation: choose a class.
///
/// Only the card strip is rebuilt when the selection changes. Rebuilding the whole
/// screen (the previous approach) recreated the scroll view underneath itself and
/// left duplicate children alive for a frame, because Destroy is deferred to end
/// of frame while Build ran immediately.
/// </summary>
public class CharCreateClassScreen : UIScreen
{
    private string        _selectedClassId;
    private RectTransform _cardContent;
    private Button        _nextButton;
    private TMP_Text      _hint;

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
        UIFactory.At(title, 0.05f, 0.88f, 0.95f, 0.97f);

        var (scroll, content) = UIFactory.ScrollView(transform, "ClassScroll", vertical: false, horizontal: true);
        UIFactory.At(scroll, 0.02f, 0.22f, 0.98f, 0.86f);

        var hlg = content.gameObject.AddComponent<HorizontalLayoutGroup>();
        hlg.spacing                = theme.spacing * 2;
        hlg.padding                = new RectOffset(12, 12, 12, 12);
        hlg.childForceExpandWidth  = false;
        hlg.childForceExpandHeight = true;
        hlg.childControlWidth      = false;
        hlg.childControlHeight     = true;
        hlg.childAlignment         = TextAnchor.MiddleLeft;
        content.gameObject.AddComponent<ContentSizeFitter>().horizontalFit = ContentSizeFitter.FitMode.PreferredSize;

        _cardContent = content;
        RebuildCards();

        _hint = UIFactory.Label(transform, "Pick a class to continue.", theme.fontSizeSmall,
                                 theme.textSecondary, TextAlignmentOptions.Center);
        UIFactory.At(_hint, 0.30f, 0.15f, 0.70f, 0.20f);

        var backBtn = UIFactory.Button(transform, "← BACK", () => GameManager.UI?.Pop(), width: 0f);
        UIFactory.At(backBtn, 0.33f, 0.05f, 0.47f, 0.12f);

        _nextButton = UIFactory.Button(transform, "NEXT →", GoNext, width: 0f);
        UIFactory.At(_nextButton, 0.53f, 0.05f, 0.67f, 0.12f);

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
        // Destroy would leave the old set visible alongside the new one.
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

    private void BuildClassCard(ClassData cls)
    {
        var theme      = UIManager.Theme;
        bool isSelected = _selectedClassId == cls.id;

        var card = UIFactory.Panel(_cardContent, $"Card_{cls.id}",
                                    isSelected ? theme.accentGold : theme.cardBg, false);
        var le = card.AddComponent<LayoutElement>();
        le.minWidth = le.preferredWidth = 300f;

        Color heading = isSelected ? theme.panelBg : theme.accentGold;
        Color body    = isSelected ? theme.panelBg : theme.textSecondary;
        Color detail  = isSelected ? theme.panelBg : theme.textPrimary;

        var name = UIFactory.Label(card.transform, cls.DisplayName.ToUpper(), theme.fontSizeBody * 1.2f,
                                    heading, TextAlignmentOptions.Center);
        UIFactory.At(name, 0.05f, 0.90f, 0.95f, 0.98f);

        var flavor = UIFactory.Label(card.transform, cls.flavorText, theme.fontSizeSmall,
                                      body, TextAlignmentOptions.Top);
        UIFactory.At(flavor, 0.06f, 0.76f, 0.94f, 0.89f);

        var stats = UIFactory.Label(card.transform,
                                     $"HP {cls.baseHp}   MP {cls.baseMp}\n" +
                                     $"Damage {cls.baseAttackMin}–{cls.baseAttackMax}   " +
                                     $"Speed {cls.attackSpeedSeconds:0.0}s",
                                     theme.fontSizeLabel, detail, TextAlignmentOptions.Center);
        UIFactory.At(stats, 0.06f, 0.64f, 0.94f, 0.75f);

        UIFactory.At(UIFactory.HorizontalDivider(card.transform).transform, 0.08f, 0.61f, 0.92f, 0.63f);

        var abilities = UIFactory.Label(card.transform, FormatAbilities(cls), theme.fontSizeLabel,
                                         detail, TextAlignmentOptions.Top);
        UIFactory.At(abilities, 0.06f, 0.18f, 0.94f, 0.60f);

        var selectBtn = UIFactory.Button(card.transform, isSelected ? "✓ SELECTED" : "SELECT", () =>
        {
            _selectedClassId = cls.id;
            RebuildCards();
            RefreshNextState();
        }, width: 0f);
        UIFactory.At(selectBtn, 0.10f, 0.04f, 0.90f, 0.14f);
    }

    private static string FormatAbilities(ClassData cls)
    {
        if (cls.abilities == null || cls.abilities.Length == 0) return "";

        var sb = new StringBuilder();
        for (int i = 0; i < cls.abilities.Length; i++)
        {
            var a = cls.abilities[i];
            sb.Append(a.isPassive ? "⬦ " : $"{i + 1}. ");
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
