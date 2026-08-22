using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// Screen 8: In-game HUD.
///
/// The root panel is deliberately non-raycasting: it covers the whole screen, and
/// while it was a raycast target it swallowed every world click, which disabled
/// click-to-move entirely. Only the bars and buttons take input.
/// </summary>
public class GameHUD : UIScreen
{
    private TMP_Text _charNameLabel;
    private TMP_Text _charLevelLabel;
    private TMP_Text _coinsLabel;
    private Image    _hpFill;
    private Image    _mpFill;
    private TMP_Text _hpText;

    private GameObject _activityPanel;
    private TMP_Text   _activitySkillLabel;
    private TMP_Text   _activityTargetLabel;
    private TMP_Text   _activityRateLabel;

    private readonly List<Image>    _abilityCooldownOverlays = new();
    private readonly List<TMP_Text> _abilityLabels           = new();
    private PlayerController _player;

    public override void Build()
    {
        // Transparent, non-blocking root. Everything inside anchors itself.
        UIFactory.Panel(transform, "HUDBg", Color.clear, true, raycastTarget: false);

        BuildTopBar();
        BuildBottomBar();
        BuildActivityPanel();
    }

    public override void OnShow()
    {
        RefreshCharInfo();
        RefreshCoins(GameManager.Inventory?.Coins ?? 0);

        GameEvents.OnCharacterLevelUp    += OnLevelUp;
        GameEvents.OnActivityChanged     += OnActivityChanged;
        GameEvents.OnPlayerHealthChanged += OnHealthChanged;
        GameEvents.OnCoinsChanged        += RefreshCoins;
    }

    public override void OnHide()
    {
        GameEvents.OnCharacterLevelUp    -= OnLevelUp;
        GameEvents.OnActivityChanged     -= OnActivityChanged;
        GameEvents.OnPlayerHealthChanged -= OnHealthChanged;
        GameEvents.OnCoinsChanged        -= RefreshCoins;
    }

    public override void OnResume() => OnShow();

    void Update()
    {
        UpdateAbilityCooldowns();
    }

    // ── Top bar ───────────────────────────────────────────────────────────────

    private void BuildTopBar()
    {
        var theme = UIManager.Theme;
        var bar   = UIFactory.Panel(transform, "TopBar", theme.headerBg, false);
        UIFactory.At(bar.transform, 0f, 0.92f, 1f, 1f);

        var charData = CharacterManager.Current;
        string name  = charData?.characterName ?? "Explorer";
        int    level = charData?.level ?? 1;
        string cls   = GameManager.Content?.GetClass(charData?.classId)?.DisplayName ?? charData?.classId ?? "";

        _charNameLabel = UIFactory.Label(bar.transform, $"{name}  •  {cls}",
                                          theme.fontSizeSmall, theme.textPrimary, TextAlignmentOptions.MidlineLeft);
        UIFactory.At(_charNameLabel, 0.01f, 0.52f, 0.20f, 0.96f);

        _charLevelLabel = UIFactory.Label(bar.transform, $"Lv. {level}",
                                           theme.fontSizeSmall, theme.accentGold, TextAlignmentOptions.MidlineLeft);
        UIFactory.At(_charLevelLabel, 0.01f, 0.06f, 0.20f, 0.48f);

        // HP / MP. The label sits inside its own column so it cannot overlap the bar.
        var hpLbl = UIFactory.Label(bar.transform, "HP", theme.fontSizeLabel,
                                     theme.textSecondary, TextAlignmentOptions.MidlineRight);
        UIFactory.At(hpLbl, 0.21f, 0.54f, 0.245f, 0.94f);

        var (hpRoot, hpFill) = UIFactory.ProgressBar(bar.transform, "HPBar", theme.hpFill);
        _hpFill = hpFill;
        _hpFill.fillAmount = 1f;
        UIFactory.At(hpRoot.transform, 0.255f, 0.56f, 0.60f, 0.92f);

        _hpText = UIFactory.Label(bar.transform, "", theme.fontSizeLabel,
                                   theme.textPrimary, TextAlignmentOptions.Center);
        UIFactory.At(_hpText, 0.255f, 0.56f, 0.60f, 0.92f);

        var mpLbl = UIFactory.Label(bar.transform, "MP", theme.fontSizeLabel,
                                     theme.textSecondary, TextAlignmentOptions.MidlineRight);
        UIFactory.At(mpLbl, 0.21f, 0.08f, 0.245f, 0.46f);

        var (mpRoot, mpFill) = UIFactory.ProgressBar(bar.transform, "MPBar", theme.mpFill);
        _mpFill = mpFill;
        _mpFill.fillAmount = 1f;
        UIFactory.At(mpRoot.transform, 0.255f, 0.10f, 0.60f, 0.44f);

        // Coins live in the top bar because they are a currency, not an inventory item
        _coinsLabel = UIFactory.Label(bar.transform, "0", theme.fontSizeSmall,
                                       theme.accentGold, TextAlignmentOptions.MidlineRight);
        UIFactory.At(_coinsLabel, 0.62f, 0.25f, 0.90f, 0.75f);

        var menuBtn = UIFactory.Button(bar.transform, "≡", () => GameManager.UI?.Push<MenuModal>(),
                                        width: 60f, height: 0f);
        UIFactory.At(menuBtn, 0.945f, 0.12f, 0.995f, 0.88f);
    }

    // ── Bottom bar ────────────────────────────────────────────────────────────

    private void BuildBottomBar()
    {
        var theme = UIManager.Theme;
        var bar   = UIFactory.Panel(transform, "BottomBar", theme.headerBg, false);
        UIFactory.At(bar.transform, 0f, 0f, 1f, 0.09f);

        BuildAbilityBar(bar.transform);

        // Right side nav
        var navStack = UIFactory.HStack(bar.transform, theme.spacing, "NavButtons");
        UIFactory.At(navStack.transform, 0.70f, 0.10f, 0.99f, 0.90f);

        BuildAutoToggle(navStack.transform);
        UIFactory.Button(navStack.transform, "INV", () => GameManager.UI?.Push<InventoryPanel>(), width: 62f);
        UIFactory.Button(navStack.transform, "SKL", () => GameManager.UI?.Push<SkillsPanel>(),    width: 62f);
        UIFactory.Button(navStack.transform, "MRG", () => GameEvents.FireToast("Merge board — coming in Phase 5"), width: 62f);
        UIFactory.Button(navStack.transform, "MAP", () => GameEvents.FireToast("Zone travel — coming in Phase 6"), width: 62f);
    }

    /// <summary>
    /// Action bar slots 1-5, populated from the character's class in class_data.json.
    /// Each slot shows its keybind, the ability name, and a radial cooldown sweep.
    /// </summary>
    private void BuildAbilityBar(Transform parent)
    {
        var theme = UIManager.Theme;

        var stack = UIFactory.HStack(parent, theme.spacing, "AbilityBar");
        UIFactory.At(stack.transform, 0.28f, 0.08f, 0.68f, 0.92f);

        var cls = GameManager.Content?.GetClass(CharacterManager.Current?.classId);

        for (int i = 0; i < 5; i++)
        {
            int slot = i;
            var ability = (cls?.abilities != null && i < cls.abilities.Length) ? cls.abilities[i] : null;

            var slotGo = UIFactory.Slot(stack.transform, $"Ability{i + 1}");

            // Passive abilities are shown but cannot be pressed
            bool activatable = ability != null && ability.IsActivatable;

            var btn = slotGo.AddComponent<Button>();
            btn.targetGraphic = slotGo.GetComponent<Image>();
            btn.onClick.AddListener(() =>
            {
                EnsurePlayer();
                _player?.UseAbility(slot);
            });

            // Ability name, wrapped small so it fits the slot
            var nameLabel = UIFactory.Label(slotGo.transform, ability?.name ?? "—",
                                             theme.fontSizeLabel,
                                             activatable ? theme.textPrimary : theme.textDisabled,
                                             TextAlignmentOptions.Center);
            UIFactory.At(nameLabel, 0.02f, 0.30f, 0.98f, 0.98f);
            _abilityLabels.Add(nameLabel);

            // Keybind in the corner (the Slot factory's Quantity label)
            var keyLabel = slotGo.transform.Find("Quantity")?.GetComponent<TMP_Text>();
            if (keyLabel != null)
            {
                keyLabel.text  = (i + 1).ToString();
                keyLabel.color = theme.accentGold;
            }

            // Cooldown sweep, drawn over the slot and filled from empty to full
            var overlayGo = new GameObject("Cooldown", typeof(RectTransform), typeof(Image));
            overlayGo.transform.SetParent(slotGo.transform, false);
            UIFactory.FillParent(overlayGo.GetComponent<RectTransform>());
            var overlay = overlayGo.GetComponent<Image>();
            overlay.color         = new Color(0f, 0f, 0f, 0.72f);
            overlay.raycastTarget = false;
            overlay.type          = Image.Type.Filled;
            overlay.fillMethod    = Image.FillMethod.Radial360;
            overlay.fillOrigin    = (int)Image.Origin360.Top;
            overlay.fillClockwise = false;
            overlay.fillAmount    = 0f;
            _abilityCooldownOverlays.Add(overlay);
        }
    }

    private void BuildAutoToggle(Transform parent)
    {
        var btn = UIFactory.Button(parent, "AUTO", null, width: 70f);

        btn.onClick.AddListener(() =>
        {
            EnsurePlayer();
            if (_player == null) { GameEvents.FireToast("No character in the world yet."); return; }

            bool enabled = !_player.autoAttack;
            _player.SetAutoAttack(enabled);

            // Recolour through the colour block, not the Image — Selectable rewrites
            // the Image tint on every state change and would undo a direct assignment.
            var colors = btn.colors;
            colors.normalColor = enabled ? UIManager.Theme.accentGreen : UIManager.Theme.buttonNormal;
            btn.colors = colors;

            GameEvents.FireToast(enabled ? "Auto-mode on" : "Auto-mode off");
        });
    }

    // ── Current activity panel ────────────────────────────────────────────────

    private void BuildActivityPanel()
    {
        var theme = UIManager.Theme;

        _activityPanel = UIFactory.Panel(transform, "ActivityPanel", theme.cardBg, false);
        UIFactory.At(_activityPanel.transform, 0.76f, 0.12f, 0.99f, 0.32f);

        // Each row gets its own band. Previously every label was unanchored and
        // they drew on top of one another.
        var header = UIFactory.Label(_activityPanel.transform, "CURRENT ACTIVITY",
                                      theme.fontSizeLabel, theme.accentGold, TextAlignmentOptions.Center);
        UIFactory.At(header, 0.04f, 0.78f, 0.96f, 0.97f);

        UIFactory.At(UIFactory.HorizontalDivider(_activityPanel.transform).transform, 0.04f, 0.74f, 0.96f, 0.77f);

        _activitySkillLabel = UIFactory.Label(_activityPanel.transform, "Idle",
                                               theme.fontSizeBody, theme.textPrimary, TextAlignmentOptions.Center);
        UIFactory.At(_activitySkillLabel, 0.04f, 0.48f, 0.96f, 0.72f);

        _activityTargetLabel = UIFactory.Label(_activityPanel.transform, "",
                                                theme.fontSizeSmall, theme.textSecondary, TextAlignmentOptions.Center);
        UIFactory.At(_activityTargetLabel, 0.04f, 0.26f, 0.96f, 0.46f);

        _activityRateLabel = UIFactory.Label(_activityPanel.transform, "",
                                              theme.fontSizeSmall, theme.accentGreen, TextAlignmentOptions.Center);
        UIFactory.At(_activityRateLabel, 0.04f, 0.05f, 0.96f, 0.24f);

        SetActivityText(GameManager.Activity?.CurrentActivity);
    }

    /// <summary>Show/hide the activity panel — driven by the menu's toggle.</summary>
    public void SetActivityPanelVisible(bool visible)
    {
        if (_activityPanel != null) _activityPanel.SetActive(visible);
    }

    public bool IsActivityPanelVisible => _activityPanel != null && _activityPanel.activeSelf;

    // ── Refresh ───────────────────────────────────────────────────────────────

    private void EnsurePlayer()
    {
        if (_player == null) _player = FindAnyObjectByType<PlayerController>();
    }

    private void UpdateAbilityCooldowns()
    {
        EnsurePlayer();
        if (_player == null) return;

        for (int i = 0; i < _abilityCooldownOverlays.Count; i++)
        {
            var overlay = _abilityCooldownOverlays[i];
            if (overlay == null) continue;

            var ability = _player.GetAbility(i);
            if (ability == null || !ability.IsActivatable || ability.cooldownSeconds <= 0f)
            {
                overlay.fillAmount = 0f;
                continue;
            }

            float remaining = _player.GetAbilityCooldownRemaining(i);
            overlay.fillAmount = Mathf.Clamp01(remaining / ability.cooldownSeconds);
        }
    }

    private void RefreshCharInfo()
    {
        var charData = CharacterManager.Current;
        if (charData == null) return;

        string cls = GameManager.Content?.GetClass(charData.classId)?.DisplayName ?? charData.classId;
        if (_charNameLabel  != null) _charNameLabel.text  = $"{charData.characterName}  •  {cls}";
        if (_charLevelLabel != null) _charLevelLabel.text = $"Lv. {charData.level}";
    }

    private void RefreshCoins(long total)
    {
        if (_coinsLabel != null) _coinsLabel.text = $"◈ {NumberFormatter.Format(total)}";
    }

    private void OnLevelUp(int newLevel)
    {
        if (_charLevelLabel != null) _charLevelLabel.text = $"Lv. {newLevel}";
        GameEvents.FireToast($"⬆ Level {newLevel}!");
        GameManager.Audio?.PlayLevelUp();
    }

    private void OnHealthChanged(double current, double max)
    {
        if (max <= 0) return;
        if (_hpFill != null) _hpFill.fillAmount = Mathf.Clamp01((float)(current / max));
        if (_hpText != null) _hpText.text = $"{(long)current} / {(long)max}";
    }

    private void OnActivityChanged(SkillActivityData activity) => SetActivityText(activity);

    private void SetActivityText(SkillActivityData activity)
    {
        bool idle = activity == null || string.IsNullOrEmpty(activity.skillId);

        if (_activitySkillLabel != null)
        {
            _activitySkillLabel.text = idle
                ? "Idle"
                : GameManager.Content?.GetSkill(activity.skillId)?.DisplayName ?? activity.skillId;
        }
        if (_activityTargetLabel != null)
            _activityTargetLabel.text = idle ? "Nothing earning while away" : activity.activityTargetName;

        if (_activityRateLabel != null)
        {
            _activityRateLabel.text = idle
                ? ""
                : $"{NumberFormatter.Format((long)activity.xpPerHour)} xp/hr  •  AFK {NumberFormatter.FormatRate(activity.afkRateMulti)}";
        }
    }
}
