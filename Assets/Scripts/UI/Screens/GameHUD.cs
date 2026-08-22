using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// Screen 8: In-game HUD.
/// Phase 1: structural stub — all panels created, most show placeholder text.
/// Phase 3: full wiring to SkillManager, ActivityManager, InventoryManager, action bar.
/// </summary>
public class GameHUD : UIScreen
{
    // References to updateable elements
    private TMP_Text  _charNameLabel;
    private TMP_Text  _charLevelLabel;
    private Image     _hpFill;
    private Image     _mpFill;
    private TMP_Text  _activityLabel;

    public override void Build()
    {
        // Transparent root — everything inside is anchored
        var bg = UIFactory.Panel(transform, "HUDBg", Color.clear, true);

        BuildTopBar();
        BuildBottomBar();
        BuildActivityPanel();
    }

    public override void OnShow()
    {
        RefreshCharInfo();
        GameEvents.OnCharacterLevelUp += OnLevelUp; // Action<int>
        GameEvents.OnActivityChanged  += OnActivityChanged;
    }

    public override void OnHide()
    {
        GameEvents.OnCharacterLevelUp -= OnLevelUp;
        GameEvents.OnActivityChanged  -= OnActivityChanged;
    }

    // ── Top bar: HP/MP + char info + Menu button ─────────────────────────────

    private void BuildTopBar()
    {
        var bar = UIFactory.Panel(transform, "TopBar", UIManager.Theme.headerBg, false);
        var rt  = bar.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0f, 0.92f);
        rt.anchorMax = Vector2.one;
        rt.offsetMin = rt.offsetMax = Vector2.zero;

        // Character name + level (left side)
        var charData = GameManager.Character?.ActiveCharacter;
        string name  = charData?.characterName ?? "Explorer";
        int    level = charData?.level ?? 1;
        string cls   = charData?.classId ?? "";

        _charNameLabel = UIFactory.Label(bar.transform, $"{name}  •  {cls}",
                                          UIManager.Theme.fontSizeSmall, UIManager.Theme.textPrimary,
                                          TextAlignmentOptions.MidlineLeft);
        var nameLabelRt = _charNameLabel.GetComponent<RectTransform>();
        nameLabelRt.anchorMin = new Vector2(0.01f, 0.55f);
        nameLabelRt.anchorMax = new Vector2(0.30f, 0.95f);
        nameLabelRt.offsetMin = nameLabelRt.offsetMax = Vector2.zero;

        _charLevelLabel = UIFactory.Label(bar.transform, $"Lv. {level}",
                                           UIManager.Theme.fontSizeSmall, UIManager.Theme.accentGold,
                                           TextAlignmentOptions.MidlineLeft);
        var lvlRt = _charLevelLabel.GetComponent<RectTransform>();
        lvlRt.anchorMin = new Vector2(0.01f, 0.05f);
        lvlRt.anchorMax = new Vector2(0.20f, 0.50f);
        lvlRt.offsetMin = lvlRt.offsetMax = Vector2.zero;

        // HP bar
        var (hpRoot, hpFill) = UIFactory.ProgressBar(bar.transform, "HPBar", UIManager.Theme.hpFill);
        _hpFill = hpFill;
        _hpFill.fillAmount = 1f;
        var hpRt = hpRoot.GetComponent<RectTransform>();
        hpRt.anchorMin = new Vector2(0.30f, 0.60f);
        hpRt.anchorMax = new Vector2(0.60f, 0.90f);
        hpRt.offsetMin = hpRt.offsetMax = Vector2.zero;
        hpRt.sizeDelta = Vector2.zero;

        var hpLbl = UIFactory.Label(bar.transform, "HP", UIManager.Theme.fontSizeLabel,
                                     UIManager.Theme.textPrimary, TextAlignmentOptions.MidlineLeft);
        hpLbl.GetComponent<RectTransform>().anchorMin = new Vector2(0.285f, 0.60f);
        hpLbl.GetComponent<RectTransform>().anchorMax = new Vector2(0.31f,  0.90f);

        // MP bar
        var (mpRoot, mpFill) = UIFactory.ProgressBar(bar.transform, "MPBar", UIManager.Theme.mpFill);
        _mpFill = mpFill;
        _mpFill.fillAmount = 1f;
        var mpRt = mpRoot.GetComponent<RectTransform>();
        mpRt.anchorMin = new Vector2(0.30f, 0.10f);
        mpRt.anchorMax = new Vector2(0.60f, 0.50f);
        mpRt.offsetMin = mpRt.offsetMax = Vector2.zero;
        mpRt.sizeDelta = Vector2.zero;

        var mpLbl = UIFactory.Label(bar.transform, "MP", UIManager.Theme.fontSizeLabel,
                                     UIManager.Theme.textPrimary, TextAlignmentOptions.MidlineLeft);
        mpLbl.GetComponent<RectTransform>().anchorMin = new Vector2(0.285f, 0.10f);
        mpLbl.GetComponent<RectTransform>().anchorMax = new Vector2(0.31f,  0.50f);

        // ≡ Menu button (right)
        var menuBtn = UIFactory.Button(bar.transform, "≡", () =>
        {
            GameEvents.OnReturnToMainMenu?.Invoke();
            GameManager.Instance?.ReturnToMainMenu();
        }, width: 60f, height: 0f);
        var menuRt = menuBtn.GetComponent<RectTransform>();
        menuRt.anchorMin = new Vector2(0.95f, 0.1f);
        menuRt.anchorMax = new Vector2(1.00f, 0.9f);
        menuRt.offsetMin = menuRt.offsetMax = Vector2.zero;
        menuRt.sizeDelta = Vector2.zero;
    }

    // ── Bottom bar: action bar 1-5 + nav buttons ─────────────────────────────

    private void BuildBottomBar()
    {
        var bar = UIFactory.Panel(transform, "BottomBar", UIManager.Theme.headerBg, false);
        var rt  = bar.GetComponent<RectTransform>();
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = new Vector2(1f, 0.08f);
        rt.offsetMin = rt.offsetMax = Vector2.zero;

        // Action bar slots 1-5 (centred)
        var hlg = UIFactory.HStack(bar.transform, UIManager.Theme.spacing, "ActionBar");
        var hlgRt = hlg.GetComponent<RectTransform>();
        hlgRt.anchorMin = new Vector2(0.30f, 0.05f);
        hlgRt.anchorMax = new Vector2(0.70f, 0.95f);
        hlgRt.offsetMin = hlgRt.offsetMax = Vector2.zero;

        for (int i = 1; i <= 5; i++)
        {
            int keybind = i;
            var slot = UIFactory.Slot(hlg.transform, $"Ability{i}");
            // Show keybind number in slot
            var keyLbl = slot.transform.Find("Quantity")?.GetComponent<TMP_Text>();
            if (keyLbl != null) keyLbl.text = keybind.ToString();
        }

        // Right side nav buttons: INV, SKL, MRG, MAP
        string[] navLabels = { "INV", "SKL", "MRG", "MAP" };
        var navStack = UIFactory.HStack(bar.transform, UIManager.Theme.spacing, "NavButtons");
        var navRt = navStack.GetComponent<RectTransform>();
        navRt.anchorMin = new Vector2(0.72f, 0.05f);
        navRt.anchorMax = new Vector2(0.99f, 0.95f);
        navRt.offsetMin = navRt.offsetMax = Vector2.zero;

        foreach (var lbl in navLabels)
        {
            UIFactory.Button(navStack.transform, lbl, () =>
            {
                // Phase 4: wire to InventoryPanel, SkillsPanel, MergeBoardPanel, ZoneMapPanel
                GameEvents.FireToast($"{lbl} — coming in Phase 4");
            }, width: 60f);
        }
    }

    // ── Current activity panel (mid-right) ──────────────────────────────────

    private void BuildActivityPanel()
    {
        var panel = UIFactory.Panel(transform, "ActivityPanel", UIManager.Theme.cardBg, false);
        var rt    = panel.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.75f, 0.10f);
        rt.anchorMax = new Vector2(0.99f, 0.30f);
        rt.offsetMin = rt.offsetMax = Vector2.zero;

        UIFactory.Label(panel.transform, "CURRENT ACTIVITY", UIManager.Theme.fontSizeLabel,
                         UIManager.Theme.accentGold, TextAlignmentOptions.Center);

        var activity = GameManager.Character?.ActiveCharacter?.currentActivity;
        string actText = (activity != null && !string.IsNullOrEmpty(activity.skillId))
            ? $"{activity.skillId}\n{activity.activityTargetId}\n{activity.xpPerHour:N0} xp/hr"
            : "Idle";

        _activityLabel = UIFactory.Label(panel.transform, actText, UIManager.Theme.fontSizeSmall,
                                          UIManager.Theme.textPrimary, TextAlignmentOptions.Center);
    }

    // ── Event handlers ────────────────────────────────────────────────────────

    private void RefreshCharInfo()
    {
        var charData = GameManager.Character?.ActiveCharacter;
        if (charData == null) return;
        if (_charNameLabel != null)
            _charNameLabel.text = $"{charData.characterName}  •  {charData.classId}";
        if (_charLevelLabel != null)
            _charLevelLabel.text = $"Lv. {charData.level}";
    }

    private void OnLevelUp(int newLevel)
    {
        if (_charLevelLabel != null)
            _charLevelLabel.text = $"Lv. {newLevel}";
    }

    private void OnActivityChanged(SkillActivityData activity)
    {
        if (_activityLabel == null) return;
        _activityLabel.text = (activity != null && !string.IsNullOrEmpty(activity.skillId))
            ? $"{activity.skillId}\n{activity.activityTargetId}\n{activity.xpPerHour:N0} xp/hr"
            : "Idle";
    }
}
