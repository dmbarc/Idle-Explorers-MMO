using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Screen 11: all 13 skills with level, XP progress and current AFK rate.
///
/// The AFK rate column is the point of this screen — it is where the player reads
/// what each skill will earn them while they are logged out.
/// </summary>
public class SkillsPanel : UIScreen
{
    private RectTransform _content;
    private readonly List<GameObject> _rows = new();

    /// <summary>Sits over the HUD rather than replacing it.</summary>
    public override bool IsOverlay => true;

    public override void Build()
    {
        var theme = UIManager.Theme;

        UIFactory.Panel(transform, "Backdrop", theme.overlayBg, true);

        var panel   = UIFactory.Panel(transform, "SkillsPanel", theme.panelBg, false);
        var panelRt = panel.GetComponent<RectTransform>();
        panelRt.anchorMin = new Vector2(0.22f, 0.10f);
        panelRt.anchorMax = new Vector2(0.78f, 0.90f);
        panelRt.offsetMin = panelRt.offsetMax = Vector2.zero;

        BuildHeader(panel.transform, theme);
        BuildList(panel.transform, theme);
        BuildCloseButton(panel.transform, theme);
    }

    public override void OnShow()
    {
        GameEvents.OnSkillLevelUp  += OnSkillChanged;
        GameEvents.OnSkillXPGained += OnSkillXP;
        Refresh();
    }

    public override void OnHide()
    {
        GameEvents.OnSkillLevelUp  -= OnSkillChanged;
        GameEvents.OnSkillXPGained -= OnSkillXP;
    }

    private void OnSkillChanged(string skillId, int newLevel) => Refresh();
    private void OnSkillXP(string skillId, long amount)       => Refresh();

    // ── Layout ────────────────────────────────────────────────────────────────

    private void BuildHeader(Transform parent, UITheme theme)
    {
        var header   = UIFactory.Panel(parent, "Header", theme.headerBg, false);
        var headerRt = header.GetComponent<RectTransform>();
        headerRt.anchorMin = new Vector2(0f, 0.91f);
        headerRt.anchorMax = new Vector2(1f, 1f);
        headerRt.offsetMin = headerRt.offsetMax = Vector2.zero;

        var title = UIFactory.Label(header.transform, "SKILLS",
                                     theme.fontSizeBody, theme.accentGold, TextAlignmentOptions.Center);
        UIFactory.FillParent(title.GetComponent<RectTransform>());
    }

    private void BuildList(Transform parent, UITheme theme)
    {
        var (scroll, content) = UIFactory.ScrollView(parent, "SkillsScroll");
        var scrollRt = scroll.GetComponent<RectTransform>();
        scrollRt.anchorMin = new Vector2(0.03f, 0.12f);
        scrollRt.anchorMax = new Vector2(0.97f, 0.90f);
        scrollRt.offsetMin = scrollRt.offsetMax = Vector2.zero;

        var vlg = content.gameObject.AddComponent<VerticalLayoutGroup>();
        vlg.spacing                = 6f;
        vlg.padding                = new RectOffset(6, 6, 6, 6);
        vlg.childForceExpandWidth  = true;
        vlg.childForceExpandHeight = false;
        vlg.childControlWidth      = true;
        vlg.childControlHeight     = true;

        var fitter = content.gameObject.AddComponent<ContentSizeFitter>();
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        _content = content;
    }

    private void BuildCloseButton(Transform parent, UITheme theme)
    {
        var btn = UIFactory.Button(parent, "CLOSE", () => GameManager.UI?.Pop(), width: 0f);
        var rt  = btn.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.35f, 0.02f);
        rt.anchorMax = new Vector2(0.65f, 0.10f);
        rt.offsetMin = rt.offsetMax = Vector2.zero;
        rt.sizeDelta = Vector2.zero;
    }

    // ── Contents ──────────────────────────────────────────────────────────────

    private void Refresh()
    {
        if (_content == null) return;

        foreach (var row in _rows)
            if (row != null) Destroy(row);
        _rows.Clear();

        var skills = GameManager.Content?.Skills;
        if (skills == null || skills.Count == 0)
        {
            UIFactory.Label(_content, "No skill data loaded.\nCheck skill_data.json in StreamingAssets.",
                             UIManager.Theme.fontSizeSmall, UIManager.Theme.textSecondary,
                             TextAlignmentOptions.Center);
            return;
        }

        foreach (var kvp in skills)
            _rows.Add(BuildSkillRow(kvp.Value));
    }

    private GameObject BuildSkillRow(SkillData skill)
    {
        var theme = UIManager.Theme;

        var row = UIFactory.Panel(_content, $"Skill_{skill.id}", theme.cardBg, false);
        var le  = row.AddComponent<LayoutElement>();
        le.minHeight = le.preferredHeight = 58f;

        int  level   = GameManager.Skills?.GetSkillLevel(skill.id) ?? 1;
        long xp      = GameManager.Skills?.GetSkillXP(skill.id) ?? 0;

        // Skill icon
        var icon = UIFactory.Icon(row.transform, GameManager.Content?.GetSkillIcon(skill.id), 36f);
        UIFactory.At(icon, 0.015f, 0.18f, 0.075f, 0.82f);

        // Name + level
        var nameLabel = UIFactory.Label(row.transform, $"{skill.DisplayName}  —  Level {level}",
                                         theme.fontSizeSmall, theme.textPrimary, TextAlignmentOptions.MidlineLeft);
        var nameRt = nameLabel.GetComponent<RectTransform>();
        nameRt.anchorMin = new Vector2(0.09f, 0.52f);
        nameRt.anchorMax = new Vector2(0.62f, 0.95f);
        nameRt.offsetMin = nameRt.offsetMax = Vector2.zero;

        // AFK rate — Brokerage is the one skill with no AFK mode at all
        string rateText;
        if (!skill.hasAfkMode)
        {
            rateText = "Active only";
        }
        else
        {
            float afkRate = GameManager.Skills?.GetAFKRateMultiplier(skill.id, level) ?? skill.afkRateDefault;
            rateText = $"AFK {NumberFormatter.FormatRate(afkRate)}";
        }

        var rateLabel = UIFactory.Label(row.transform, rateText, theme.fontSizeLabel,
                                         skill.hasAfkMode ? theme.accentGreen : theme.textDisabled,
                                         TextAlignmentOptions.MidlineRight);
        var rateRt = rateLabel.GetComponent<RectTransform>();
        rateRt.anchorMin = new Vector2(0.62f, 0.52f);
        rateRt.anchorMax = new Vector2(0.98f, 0.95f);
        rateRt.offsetMin = rateRt.offsetMax = Vector2.zero;

        // XP progress toward the next level
        long levelStart = SkillManager.SkillLevelToXP(level);
        long levelEnd   = SkillManager.SkillLevelToXP(level + 1);
        float progress  = levelEnd > levelStart
            ? Mathf.Clamp01((xp - levelStart) / (float)(levelEnd - levelStart))
            : 1f;

        var (barRoot, barFill) = UIFactory.ProgressBar(row.transform, "XPBar", theme.xpFill);
        barFill.fillAmount = progress;
        var barRt = barRoot.GetComponent<RectTransform>();
        barRt.anchorMin = new Vector2(0.09f, 0.28f);
        barRt.anchorMax = new Vector2(0.72f, 0.46f);
        barRt.offsetMin = barRt.offsetMax = Vector2.zero;
        barRt.sizeDelta = Vector2.zero;

        string xpText = level >= 999
            ? "MAX"
            : $"{NumberFormatter.FormatXP(xp)} / {NumberFormatter.FormatXP(levelEnd)}";

        var xpLabel = UIFactory.Label(row.transform, xpText, theme.fontSizeLabel,
                                       theme.textSecondary, TextAlignmentOptions.MidlineRight);
        var xpRt = xpLabel.GetComponent<RectTransform>();
        xpRt.anchorMin = new Vector2(0.74f, 0.22f);
        xpRt.anchorMax = new Vector2(0.98f, 0.50f);
        xpRt.offsetMin = xpRt.offsetMax = Vector2.zero;

        return row;
    }
}
