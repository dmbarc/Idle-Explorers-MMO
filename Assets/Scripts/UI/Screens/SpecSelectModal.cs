using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Picking a second or third class.
///
/// Unlike the Shifting Sigil's class CHANGE, this is purely additive: nothing is lost,
/// no talents are refunded, and the character keeps everything they already had. The
/// real cost is the shared talent pool — a second tree wants points the first one was
/// going to get — which is a decision the player keeps making every level rather than
/// once at this prompt.
///
/// So there is no dire warning and no consumed item. It can be dismissed freely,
/// because opening it costs nothing.
/// </summary>
public class SpecSelectModal : UIScreen
{
    public override bool IsOverlay     => true;
    public override bool RebuildOnShow => true;

    public override void Build()
    {
        var theme     = UIManager.Theme;
        var character = CharacterManager.Current;

        var backdrop    = UIFactory.Panel(transform, "Backdrop", theme.overlayBg, true);
        var backdropBtn = backdrop.AddComponent<Button>();
        backdropBtn.transition = Selectable.Transition.None;
        backdropBtn.onClick.AddListener(() => GameManager.UI?.Pop());

        var panel   = UIFactory.Panel(transform, "SpecPanel", theme.panelBg, false);
        var panelRt = panel.GetComponent<RectTransform>();
        panelRt.anchorMin = new Vector2(0.22f, 0.10f);
        panelRt.anchorMax = new Vector2(0.78f, 0.90f);
        panelRt.offsetMin = panelRt.offsetMax = Vector2.zero;

        BuildHeader(panel.transform, theme, character);
        BuildList(panel.transform, theme, character);

        var close = UIFactory.Button(panel.transform, "NOT YET", () => GameManager.UI?.Pop(), width: 0f);
        UIFactory.At(close, 0.30f, 0.02f, 0.70f, 0.09f);
    }

    private void BuildHeader(Transform parent, UITheme theme, CharacterData character)
    {
        var header = UIFactory.Panel(parent, "Header", theme.headerBg, false);
        UIFactory.At(header.transform, 0f, 0.90f, 1f, 1f);

        int taken = character?.ClassIds().Count ?? 0;

        var title = UIFactory.Label(header.transform,
                                     taken >= 2 ? "CHOOSE A THIRD CALLING" : "CHOOSE A SECOND CALLING",
                                     theme.fontSizeBody, theme.accentGold,
                                     TextAlignmentOptions.Center);
        UIFactory.At(title, 0.02f, 0.48f, 0.98f, 0.96f);

        var note = UIFactory.Label(header.transform,
                                    "You keep everything you already have. Talent points are shared " +
                                    "between all your trees, so this widens what you can do rather " +
                                    "than replacing it.",
                                    theme.fontSizeLabel, theme.textSecondary,
                                    TextAlignmentOptions.Center);
        UIFactory.At(note, 0.04f, 0.04f, 0.96f, 0.46f);
        note.textWrappingMode = TextWrappingModes.Normal;
    }

    private void BuildList(Transform parent, UITheme theme, CharacterData character)
    {
        var (scroll, content) = UIFactory.ScrollList(parent, "SpecScroll", theme.spacing);
        UIFactory.At(scroll, 0.03f, 0.11f, 0.97f, 0.885f);

        var options = ClassManager.AvailableToAdd(character);

        if (options.Count == 0)
        {
            UIFactory.Label(content, "No class slot is free.", theme.fontSizeBody,
                            theme.textSecondary, TextAlignmentOptions.Center);
            return;
        }

        foreach (var cls in options)
            BuildRow(content, theme, character, cls);
    }

    private void BuildRow(Transform parent, UITheme theme, CharacterData character, ClassData cls)
    {
        var row = UIFactory.Panel(parent, $"Spec_{cls.id}", theme.cardBg, false);
        var le  = row.AddComponent<LayoutElement>();
        le.minHeight = le.preferredHeight = 104f;

        var name = UIFactory.Label(row.transform, cls.DisplayName.ToUpper(), theme.fontSizeSmall,
                                    theme.accentGold, TextAlignmentOptions.MidlineLeft);
        UIFactory.At(name, 0.04f, 0.70f, 0.60f, 0.94f);

        // What this combination would be called, worked out before they commit. Naming
        // the result is most of the appeal of cross-speccing, so it should not be a
        // surprise discovered afterwards.
        string title = PreviewTitle(character, cls.id);
        var titleLabel = UIFactory.Label(row.transform, title, theme.fontSizeSmall,
                                          theme.accentGreen, TextAlignmentOptions.MidlineRight);
        UIFactory.At(titleLabel, 0.55f, 0.70f, 0.96f, 0.94f);

        var flavour = UIFactory.Label(row.transform, cls.flavorText, theme.fontSizeLabel,
                                       theme.textSecondary, TextAlignmentOptions.TopLeft);
        UIFactory.At(flavour, 0.04f, 0.38f, 0.74f, 0.68f);
        flavour.textWrappingMode = TextWrappingModes.Normal;

        var affinity = UIFactory.Label(row.transform, AffinityLine(cls), theme.fontSizeLabel,
                                        theme.textPrimary, TextAlignmentOptions.MidlineLeft);
        UIFactory.At(affinity, 0.04f, 0.08f, 0.74f, 0.36f);

        var take = UIFactory.Button(row.transform, "TAKE", () =>
        {
            GameManager.UI?.Pop();
            ClassManager.AddClass(cls.id);
        }, width: 0f);
        UIFactory.At(take, 0.78f, 0.15f, 0.96f, 0.60f);
    }

    /// <summary>What the character would be called after adding this class.</summary>
    private static string PreviewTitle(CharacterData character, string newClassId)
    {
        if (character == null) return "";

        var ids = new System.Collections.Generic.List<string>(character.ClassIds()) { newClassId };

        var combo = ClassManager.FindSpec(ids);
        return combo != null ? $"→ {combo.DisplayName}" : "";
    }

    /// <summary>The skills this class is good at, which is most of why you would add it.</summary>
    private static string AffinityLine(ClassData cls)
    {
        if (cls?.stats?.skillAffinity == null || cls.stats.skillAffinity.Count == 0) return "";

        var parts = new System.Collections.Generic.List<string>();
        foreach (var entry in cls.stats.skillAffinity)
        {
            if (entry == null || entry.value <= 0f) continue;

            string skill = GameManager.Content?.GetSkill(entry.skillId)?.DisplayName ?? entry.skillId;
            parts.Add($"{skill} +{entry.value * 100f:0}%");
        }
        return parts.Count == 0 ? "" : string.Join("   ", parts);
    }
}
