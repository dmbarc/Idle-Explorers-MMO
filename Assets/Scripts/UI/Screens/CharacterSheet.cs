using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// The character sheet: who you are, every stat you have, and what each one does.
///
/// Every row is hoverable, because a stat the player cannot interpret is a stat they
/// cannot build towards — and several of these (Momentum, Insight, Resonance,
/// Tenacity) do things no other game's stat sheet would have taught them. The hover
/// text lives with the stat definition in Stats.All rather than here, so there is one
/// place to change what a stat means.
///
/// Reuses ItemTooltip.Card so a stat explanation looks and behaves exactly like an
/// item tooltip — same panel, same edge-flipping, same non-blocking raycast.
/// </summary>
public class CharacterSheet : UIScreen
{
    /// <summary>Sits over the HUD rather than replacing it.</summary>
    public override bool IsOverlay => true;

    /// <summary>Every number is read at build time, so it rebuilds per open.</summary>
    public override bool RebuildOnShow => true;

    private ItemTooltip.Card _tooltip;
    private CharacterPreview _preview;

    public override void Build()
    {
        var theme = UIManager.Theme;

        UIFactory.Panel(transform, "Backdrop", theme.overlayBg, true);

        var panel = UIFactory.Panel(transform, "SheetPanel", theme.panelBg, false);
        UIFactory.At(panel.transform, 0.10f, 0.06f, 0.90f, 0.94f);

        BuildHeader(panel.transform, theme);
        BuildIdentity(panel.transform, theme, 0.02f, 0.34f);
        BuildStats(panel.transform, theme, 0.36f, 0.98f);

        _tooltip = ItemTooltip.CreateCard(transform);

        var close = UIFactory.Button(panel.transform, "CLOSE", () => GameManager.UI?.Pop(), width: 0f);
        UIFactory.At(close, 0.38f, 0.015f, 0.62f, 0.075f);
    }

    private void BuildHeader(Transform parent, UITheme theme)
    {
        var header = UIFactory.Panel(parent, "Header", theme.headerBg, false);
        UIFactory.At(header.transform, 0f, 0.94f, 1f, 1f);

        var character = CharacterManager.Current;

        var title = UIFactory.Label(header.transform,
                                     character?.characterName?.ToUpper() ?? "CHARACTER",
                                     theme.fontSizeBody, theme.accentGold,
                                     TextAlignmentOptions.MidlineLeft);
        UIFactory.At(title, 0.02f, 0f, 0.45f, 1f);

        var subtitle = UIFactory.Label(header.transform, ClassManager.TitleFor(character),
                                        theme.fontSizeSmall, theme.textSecondary,
                                        TextAlignmentOptions.MidlineRight);
        UIFactory.At(subtitle, 0.46f, 0f, 0.98f, 1f);
    }

    // ── Left column: portrait, level, affinities ──────────────────────────────

    private void BuildIdentity(Transform parent, UITheme theme, float xMin, float xMax)
    {
        var (scroll, content) = UIFactory.ScrollList(parent, "IdentityScroll", theme.spacing);
        UIFactory.At(scroll, xMin, 0.09f, xMax, 0.93f);

        var character = CharacterManager.Current;

        // Portrait
        var portrait = UIFactory.Panel(content, "Portrait", theme.slotBg, false);
        var portraitLe = portrait.AddComponent<LayoutElement>();
        portraitLe.minHeight = portraitLe.preferredHeight = 220f;

        _preview = CharacterPreview.Create(portrait.transform, character?.spumConfig, "SheetPreview");

        // Level and class
        var levelCard = TextCard(content, theme, "STANDING", out var levelBody);
        levelBody.text = BuildStanding(character);

        // Skill affinities — the reason a class matters outside combat
        var affinityCard = TextCard(content, theme, "SKILL AFFINITY", out var affinityBody);
        affinityBody.text = BuildAffinities();
    }

    private static string BuildStanding(CharacterData character)
    {
        if (character == null) return "No character selected.";

        var lines = new List<string>
        {
            $"Level {character.level}",
        };

        int slots = character.ClassSlots();
        int used  = character.ClassIds().Count;

        foreach (var classId in character.ClassIds())
        {
            var cls = GameManager.Content?.GetClass(classId);
            lines.Add($"  {cls?.DisplayName ?? classId}");
        }

        if (used < slots)
            lines.Add($"\n<color=#67D28B>A class slot is free — visit the talent screen.</color>");
        else if (slots < 3)
        {
            int next = slots == 1 ? CharacterData.ClassSlotTwoLevel : CharacterData.ClassSlotThreeLevel;
            lines.Add($"\n<color=#7C8497>Next class slot at level {next}.</color>");
        }

        int points = TalentManager.AvailablePoints(character);
        if (points > 0)
            lines.Add($"\n<color=#E8C46A>{points} unspent talent point(s).</color>");

        return string.Join("\n", lines);
    }

    private static string BuildAffinities()
    {
        var stats = GameManager.Stats;
        if (stats == null) return "None.";

        var affinities = stats.Affinities();
        if (affinities.Count == 0)
            return "<color=#7C8497>No class favours a skill yet.</color>";

        var lines = new List<string>();
        foreach (var (skillId, bonus) in affinities)
        {
            string skillName = GameManager.Content?.GetSkill(skillId)?.DisplayName ?? skillId;
            lines.Add($"<color=#8FD8FF>+{bonus * 100f:0}%</color>  {skillName}");
        }
        return string.Join("\n", lines);
    }

    // ── Right column: the stats ───────────────────────────────────────────────

    private void BuildStats(Transform parent, UITheme theme, float xMin, float xMax)
    {
        var (scroll, content) = UIFactory.ScrollList(parent, "StatScroll", 2f);
        UIFactory.At(scroll, xMin, 0.09f, xMax, 0.93f);

        var block = GameManager.Stats?.Current;
        if (block == null)
        {
            UIFactory.Label(content, "No stats — is a character selected?",
                            theme.fontSizeSmall, theme.textDisabled, TextAlignmentOptions.Center);
            return;
        }

        foreach (var group in Stats.Groups)
        {
            BuildGroupHeading(content, theme, group);

            foreach (var info in Stats.All)
            {
                if (info.Group != group) continue;
                BuildStatRow(content, theme, info, block);
            }
        }

        // Derived values are what the player actually feels, and none of them is a
        // stored stat — the damage band in particular is the product of four fields
        // and a cascade, so showing only its inputs would hide the answer.
        BuildGroupHeading(content, theme, "IN COMBAT");
        BuildDerivedRows(content, theme, block);
    }

    private static void BuildGroupHeading(Transform parent, UITheme theme, string text)
    {
        var heading = UIFactory.Label(parent, text, theme.fontSizeLabel,
                                       theme.accentGold, TextAlignmentOptions.MidlineLeft);
        var le = heading.gameObject.AddComponent<LayoutElement>();
        le.minHeight = le.preferredHeight = 26f;
    }

    private void BuildStatRow(Transform parent, UITheme theme, Stats.Info info, StatBlock block)
    {
        float value = block.Get(info.Id);

        var row = UIFactory.Panel(parent, $"Stat_{info.Id}", theme.cardBg, false);
        var le  = row.AddComponent<LayoutElement>();
        le.minHeight = le.preferredHeight = 26f;

        var nameLabel = UIFactory.Label(row.transform, info.Name, theme.fontSizeSmall,
                                         theme.textSecondary, TextAlignmentOptions.MidlineLeft);
        UIFactory.At(nameLabel, 0.03f, 0f, 0.62f, 1f);

        // Zero is greyed rather than hidden. A stat you have none of is exactly the
        // one a player is deciding whether to invest in, so it has to be visible and
        // hoverable — hiding it would make the sheet a list of what you already have.
        bool  none  = Mathf.Approximately(value, 0f);
        Color tint  = none ? theme.textDisabled : theme.textPrimary;

        var valueLabel = UIFactory.Label(row.transform, Stats.Render(info.Id, value),
                                          theme.fontSizeSmall, tint, TextAlignmentOptions.MidlineRight);
        UIFactory.At(valueLabel, 0.63f, 0f, 0.97f, 1f);

        AttachHover(row, info);
    }

    private void BuildDerivedRows(Transform parent, UITheme theme, StatBlock block)
    {
        var profile = block.Resolve();

        Row("Damage per hit", $"{profile.Min:0.#} – {profile.Max:0.#}",
            "What a single swing actually rolls between, after the minimum-hit cascade.");

        Row("Critical hits", $"{profile.CritChance * 100f:0.#}% for {profile.CritMultiplier:0.##}x",
            "How often you crit and how hard, including anything converted from " +
            "surplus minimum hit.");

        Row("Swing interval", $"{block.EffectiveAttackSpeed:0.00}s",
            "Seconds between attacks, after every speed bonus.");

        Row("Damage taken", $"{StatBlock.DamageThrough(block.EffectiveArmor) * 100f:0.#}%",
            $"The share of incoming damage that gets past {block.EffectiveArmor:0} armour.");

        Row("Effective health", $"{block.EffectiveHealth:0}",
            "Your health after percentage bonuses.");

        void Row(string label, string value, string explanation)
        {
            var row = UIFactory.Panel(parent, $"Derived_{label}", theme.cardBg, false);
            var le  = row.AddComponent<LayoutElement>();
            le.minHeight = le.preferredHeight = 26f;

            var nameLabel = UIFactory.Label(row.transform, label, theme.fontSizeSmall,
                                             theme.textSecondary, TextAlignmentOptions.MidlineLeft);
            UIFactory.At(nameLabel, 0.03f, 0f, 0.55f, 1f);

            var valueLabel = UIFactory.Label(row.transform, value, theme.fontSizeSmall,
                                              theme.accentGreen, TextAlignmentOptions.MidlineRight);
            UIFactory.At(valueLabel, 0.56f, 0f, 0.97f, 1f);

            AttachHover(row, new Stats.Info { Name = label, Description = explanation });
        }
    }

    // ── Hover ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// EventTrigger rather than a bespoke component: the rows are plain factory-built
    /// panels, and one trigger per row is cheaper than a MonoBehaviour per row on a
    /// sheet with thirty of them.
    /// </summary>
    private void AttachHover(GameObject row, Stats.Info info)
    {
        var image = row.GetComponent<Image>();
        if (image != null) image.raycastTarget = true;

        var trigger = row.AddComponent<EventTrigger>();

        var enter = new EventTrigger.Entry { eventID = EventTriggerType.PointerEnter };
        enter.callback.AddListener(data => ShowStatTooltip(info, ((PointerEventData)data).position));
        trigger.triggers.Add(enter);

        var exit = new EventTrigger.Entry { eventID = EventTriggerType.PointerExit };
        exit.callback.AddListener(_ => _tooltip?.Hide());
        trigger.triggers.Add(exit);
    }

    private void ShowStatTooltip(Stats.Info info, Vector2 screenPos)
    {
        if (_tooltip == null || info == null) return;

        ItemTooltip.ShowText(_tooltip, (RectTransform)transform,
                             info.Name, info.Description, screenPos);
    }

    public override void OnHide() => _tooltip?.Hide();

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>A card that grows to fit its text: heading above, body below.</summary>
    private static GameObject TextCard(Transform parent, UITheme theme, string heading,
                                        out TMP_Text body)
    {
        var card = UIFactory.Panel(parent, $"{heading}Card", theme.cardBg, false);

        var vlg = card.AddComponent<VerticalLayoutGroup>();
        vlg.padding               = new RectOffset(10, 10, 8, 10);
        vlg.spacing               = 4f;
        vlg.childControlWidth     = true;
        vlg.childControlHeight    = true;
        vlg.childForceExpandWidth = true;

        var fitter = card.AddComponent<ContentSizeFitter>();
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        UIFactory.Label(card.transform, heading, theme.fontSizeLabel,
                        theme.textSecondary, TextAlignmentOptions.TopLeft);

        body = UIFactory.Label(card.transform, "", theme.fontSizeSmall,
                                theme.textPrimary, TextAlignmentOptions.TopLeft);

        return card;
    }
}
