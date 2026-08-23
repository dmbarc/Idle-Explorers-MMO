using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// Picks a new class for the active character. Opened by consuming a Shifting Sigil.
///
/// The item is consumed by ItemEffectResolver BEFORE this appears, which is
/// deliberate: the alternative is holding the item back until a class is chosen,
/// which means an inventory slot in limbo and a modal that must not be dismissed.
/// Cancelling therefore costs the sigil, and the modal says so plainly rather than
/// letting the player find out.
/// </summary>
public class ClassChangeModal : UIScreen
{
    /// <summary>Sits over whatever opened it.</summary>
    public override bool IsOverlay => true;

    /// <summary>The current class is greyed out, so it has to be read at open time.</summary>
    public override bool RebuildOnShow => true;

    public override void Build()
    {
        var theme     = UIManager.Theme;
        var character = CharacterManager.Current;

        var backdrop = UIFactory.Panel(transform, "Backdrop", theme.overlayBg, true);
        // No click-to-dismiss on the backdrop. The sigil is already spent, so a stray
        // click outside the card would throw it away for nothing — the CANCEL button
        // is explicit about what it costs.
        backdrop.AddComponent<Button>().transition = Selectable.Transition.None;

        var card   = UIFactory.Panel(transform, "ClassChangeCard", theme.panelBg, false);
        var cardRt = card.GetComponent<RectTransform>();
        cardRt.anchorMin = new Vector2(0.24f, 0.12f);
        cardRt.anchorMax = new Vector2(0.76f, 0.88f);
        cardRt.offsetMin = cardRt.offsetMax = Vector2.zero;

        var title = UIFactory.Label(card.transform, "CHOOSE A NEW CLASS", theme.fontSizeBody,
                                     theme.accentGold, TextAlignmentOptions.Center);
        UIFactory.At(title, 0.05f, 0.925f, 0.95f, 0.98f);

        var warning = UIFactory.Label(card.transform,
                                       "Your talent points are refunded and the tree is emptied. " +
                                       "Levels, skills, items and equipment are untouched.",
                                       theme.fontSizeLabel, theme.textSecondary,
                                       TextAlignmentOptions.Center);
        UIFactory.At(warning, 0.06f, 0.855f, 0.94f, 0.92f);
        warning.textWrappingMode = TextWrappingModes.Normal;

        BuildClassList(card.transform, theme, character);

        var cancel = UIFactory.Button(card.transform, "CANCEL (SIGIL IS SPENT)",
                                       () => GameManager.UI?.Pop(), width: 0f);
        UIFactory.At(cancel, 0.08f, 0.02f, 0.92f, 0.085f);
    }

    private void BuildClassList(Transform parent, UITheme theme, CharacterData character)
    {
        var (scroll, content) = UIFactory.ScrollView(parent, "ClassScroll");
        var scrollRt = scroll.GetComponent<RectTransform>();
        scrollRt.anchorMin = new Vector2(0.05f, 0.10f);
        scrollRt.anchorMax = new Vector2(0.95f, 0.845f);
        scrollRt.offsetMin = scrollRt.offsetMax = Vector2.zero;

        var stack = UIFactory.VStack(content, theme.spacing, true, "Classes");
        UIFactory.At(stack, 0f, 0f, 1f, 1f);

        var stackFitter = stack.gameObject.AddComponent<ContentSizeFitter>();
        stackFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        var classes = GameManager.Content?.Classes;
        if (classes == null || classes.Count == 0)
        {
            UIFactory.Label(stack.transform, "No classes are defined.", theme.fontSizeBody,
                             theme.textSecondary, TextAlignmentOptions.Center);
            return;
        }

        foreach (var cls in classes.Values)
        {
            if (cls == null) continue;
            BuildClassRow(stack.transform, theme, cls, isCurrent: cls.id == character?.classId);
        }
    }

    private void BuildClassRow(Transform parent, UITheme theme, ClassData cls, bool isCurrent)
    {
        var row   = UIFactory.Panel(parent, $"Class_{cls.id}", theme.cardBg, false);
        var rowEl = row.AddComponent<LayoutElement>();
        rowEl.minHeight = rowEl.preferredHeight = 96f;

        var name = UIFactory.Label(row.transform,
                                    isCurrent ? $"{cls.DisplayName}  (current)" : cls.DisplayName,
                                    theme.fontSizeBody,
                                    isCurrent ? theme.textDisabled : theme.accentGold,
                                    TextAlignmentOptions.MidlineLeft);
        UIFactory.At(name, 0.03f, 0.60f, 0.72f, 0.95f);

        var flavor = UIFactory.Label(row.transform, cls.flavorText, theme.fontSizeSmall,
                                      theme.textSecondary, TextAlignmentOptions.TopLeft);
        UIFactory.At(flavor, 0.03f, 0.06f, 0.72f, 0.58f);
        flavor.textWrappingMode = TextWrappingModes.Normal;

        // The numbers that actually change, so the choice is informed rather than
        // made on flavour text alone.
        var stats = UIFactory.Label(row.transform,
                                     $"{cls.baseHp} HP\n{cls.baseAttackMin}-{cls.baseAttackMax} dmg\n" +
                                     $"{cls.attackSpeedSeconds:0.0}s swing",
                                     theme.fontSizeLabel, theme.textSecondary,
                                     TextAlignmentOptions.MidlineRight);
        UIFactory.At(stats, 0.72f, 0.06f, 0.98f, 0.95f);

        if (isCurrent) return;

        var button = row.AddComponent<Button>();
        button.targetGraphic = row.GetComponent<Image>();
        button.transition    = Selectable.Transition.None;
        button.onClick.AddListener(() => Choose(cls.id));
    }

    private void Choose(string classId)
    {
        // Pop before applying: the class change fires events that rebuild the HUD,
        // and this modal has no reason to still be standing while that happens.
        GameManager.UI?.Pop();
        CharacterManager.ChangeClass(classId);
    }
}
