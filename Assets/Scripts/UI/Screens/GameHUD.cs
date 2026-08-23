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
    private Image    _spFill;
    private TMP_Text _hpText;

    private readonly List<Image>    _abilityCooldownOverlays = new();
    private readonly List<TMP_Text> _abilityLabels           = new();
    private Transform        _abilityBarRoot;
    private PlayerController _player;
    private TMP_Text         _talentBadge;

    public override void Build()
    {
        // Transparent, non-blocking root. Everything inside anchors itself.
        UIFactory.Panel(transform, "HUDBg", Color.clear, true, raycastTarget: false);

        BuildTopBar();
        BuildBottomBar();
        BuildAutoBanner();
    }

    public override void OnShow()
    {
        // This screen is cached and reused across characters, so everything it
        // displays has to be re-read here — otherwise the second character you play
        // inherits the first one's name, coins and activity.
        _player = null;
        RefreshCharInfo();
        RefreshCoins(GameManager.Inventory?.Coins ?? 0);
        RebuildAbilityBar();
        RefreshTalentBadge();
        SyncAutoMode();

        GameEvents.OnCharacterLevelUp    += OnLevelUp;
        GameEvents.OnPlayerHealthChanged += OnHealthChanged;
        GameEvents.OnPlayerResourcesChanged += OnResourcesChanged;
        GameEvents.OnCoinsChanged        += RefreshCoins;
        GameEvents.OnPlayerDied          += OnPlayerDied;
        GameEvents.OnTalentsChanged      += RefreshTalentBadge;
        GameEvents.OnClassChanged        += OnClassChanged;
        GameEvents.OnAutoModeChanged     += RefreshAutoMode;
    }

    public override void OnHide()
    {
        GameEvents.OnCharacterLevelUp    -= OnLevelUp;
        GameEvents.OnPlayerHealthChanged -= OnHealthChanged;
        GameEvents.OnPlayerResourcesChanged -= OnResourcesChanged;
        GameEvents.OnCoinsChanged        -= RefreshCoins;
        GameEvents.OnPlayerDied          -= OnPlayerDied;
        GameEvents.OnTalentsChanged      -= RefreshTalentBadge;
        GameEvents.OnClassChanged        -= OnClassChanged;
        GameEvents.OnAutoModeChanged     -= RefreshAutoMode;
    }

    /// <summary>
    /// A class change replaces the whole ability set and empties the talent tree, so
    /// the action bar and the badge both have to be rebuilt from the new class.
    /// </summary>
    private void OnClassChanged(string classId)
    {
        RefreshCharInfo();
        RebuildAbilityBar();
        RefreshTalentBadge();
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

        // Mana and stamina share the lower row. Both are real now — the MP bar sat
        // permanently full for the whole project's life because baseMp was declared
        // and never read by anything.
        var mpLbl = UIFactory.Label(bar.transform, "MP", theme.fontSizeLabel,
                                     theme.textSecondary, TextAlignmentOptions.MidlineRight);
        UIFactory.At(mpLbl, 0.21f, 0.08f, 0.245f, 0.46f);

        var (mpRoot, mpFill) = UIFactory.ProgressBar(bar.transform, "MPBar", theme.mpFill);
        _mpFill = mpFill;
        _mpFill.fillAmount = 1f;
        UIFactory.At(mpRoot.transform, 0.255f, 0.10f, 0.42f, 0.44f);

        var spLbl = UIFactory.Label(bar.transform, "SP", theme.fontSizeLabel,
                                     theme.textSecondary, TextAlignmentOptions.MidlineRight);
        UIFactory.At(spLbl, 0.425f, 0.08f, 0.46f, 0.46f);

        var (spRoot, spFill) = UIFactory.ProgressBar(bar.transform, "SPBar", theme.accentGreen);
        _spFill = spFill;
        _spFill.fillAmount = 1f;
        UIFactory.At(spRoot.transform, 0.47f, 0.10f, 0.60f, 0.44f);

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
        UIFactory.At(navStack.transform, 0.655f, 0.10f, 0.99f, 0.90f);

        BuildAutoToggle(navStack.transform);
        UIFactory.Button(navStack.transform, "CHR", () => GameManager.UI?.Push<CharacterSheet>(),  width: 54f);
        UIFactory.Button(navStack.transform, "INV", () => GameManager.UI?.Push<InventoryPanel>(), width: 54f);
        UIFactory.Button(navStack.transform, "EQP", () => GameManager.UI?.Push<EquipmentPanel>(), width: 54f);
        UIFactory.Button(navStack.transform, "SKL", () => GameManager.UI?.Push<SkillsPanel>(),    width: 54f);
        BuildTalentButton(navStack.transform);
        UIFactory.Button(navStack.transform, "SHP", () => GameManager.UI?.Push<ShopPanel>(), width: 54f);
        UIFactory.Button(navStack.transform, "MRG", () => GameEvents.FireToast("Merge board — coming in Phase 5"), width: 54f);
        UIFactory.Button(navStack.transform, "MAP", () => GameManager.UI?.Push<TravelPanel>(), width: 54f);
    }

    /// <summary>
    /// Action bar slots 1-5, populated from the character's class in class_data.json.
    /// Each slot shows its keybind, the ability name, and a radial cooldown sweep.
    /// </summary>
    private void BuildAbilityBar(Transform parent)
    {
        var stack = UIFactory.HStack(parent, UIManager.Theme.spacing, "AbilityBar");
        UIFactory.At(stack.transform, 0.28f, 0.08f, 0.68f, 0.92f);
        _abilityBarRoot = stack.transform;

        RebuildAbilityBar();
    }

    /// <summary>
    /// Repopulates the action bar from the active character's class. Called on every
    /// show, because the HUD is cached and a second character may be a different
    /// class with entirely different abilities.
    /// </summary>
    private void RebuildAbilityBar()
    {
        if (_abilityBarRoot == null) return;

        var theme = UIManager.Theme;

        for (int i = _abilityBarRoot.childCount - 1; i >= 0; i--)
            DestroyImmediate(_abilityBarRoot.GetChild(i).gameObject);
        _abilityCooldownOverlays.Clear();
        _abilityLabels.Clear();

        var stack = _abilityBarRoot;
        var cls   = GameManager.Content?.GetClass(CharacterManager.Current?.classId);

        for (int i = 0; i < 5; i++)
        {
            int slot = i;
            var ability = (cls?.abilities != null && i < cls.abilities.Length) ? cls.abilities[i] : null;

            var slotGo = UIFactory.Slot(stack, $"Ability{i + 1}");

            // Passive abilities are shown but cannot be pressed
            bool activatable = ability != null && ability.IsActivatable;

            var btn = slotGo.AddComponent<Button>();
            btn.targetGraphic = slotGo.GetComponent<Image>();
            btn.onClick.AddListener(() =>
            {
                EnsurePlayer();
                _player?.UseAbility(slot);
            });

            // Icon behind the text. Dimmed so the name stays the readable element —
            // the icon is for recognising a slot at a glance, not for reading.
            var iconSprite = GameManager.Content?.GetAbilityIcon(ability);
            if (iconSprite != null)
            {
                var iconImg = UIFactory.Icon(slotGo.transform, iconSprite, 0f, "AbilityIcon");
                UIFactory.At(iconImg, 0.12f, 0.12f, 0.88f, 0.88f);
                iconImg.color         = new Color(1f, 1f, 1f, activatable ? 0.55f : 0.22f);
                iconImg.raycastTarget = false;
                iconImg.transform.SetAsFirstSibling();
            }

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
            overlay.sprite        = UIFactory.WhiteSprite;   // Filled needs a sprite
            overlay.type          = Image.Type.Filled;
            overlay.fillMethod    = Image.FillMethod.Radial360;
            overlay.fillOrigin    = (int)Image.Origin360.Top;
            overlay.fillClockwise = false;
            overlay.fillAmount    = 0f;
            _abilityCooldownOverlays.Add(overlay);
        }
    }

    /// <summary>
    /// Talents, with the unspent-point count on the button.
    ///
    /// Unspent points are the one piece of progression a player can hold indefinitely
    /// without noticing — a badge on the button is the whole reason they open it.
    /// </summary>
    private void BuildTalentButton(Transform parent)
    {
        var btn = UIFactory.Button(parent, "TAL", () => GameManager.UI?.Push<TalentPanel>(), width: 54f);

        _talentBadge = UIFactory.Label(btn.transform, "", UIManager.Theme.fontSizeLabel,
                                        UIManager.Theme.accentGreen, TextAlignmentOptions.TopRight);
        UIFactory.At(_talentBadge, 0.35f, 0.55f, 0.95f, 0.98f);

        RefreshTalentBadge();
    }

    private void RefreshTalentBadge()
    {
        if (_talentBadge == null) return;

        int available = TalentManager.AvailablePoints(CharacterManager.Current);
        _talentBadge.text = available > 0 ? $"+{available}" : "";
    }

    /// <summary>
    /// The AUTO toggle, and the banner that says whether it is on.
    ///
    /// The button used to recolour itself inside its own click handler, which meant it
    /// showed the wrong state the moment anything redrew the HUD — and the HUD is
    /// cached and rebuilt on every character select and every ability change. State
    /// now comes from the player and arrives on an event, so the button and the
    /// banner cannot disagree with the game or with each other.
    /// </summary>
    private void BuildAutoToggle(Transform parent)
    {
        _autoButton      = UIFactory.Button(parent, "AUTO", null, width: 70f);
        _autoButtonLabel = _autoButton.GetComponentInChildren<TMP_Text>();

        _autoButton.onClick.AddListener(() =>
        {
            EnsurePlayer();
            if (_player == null) { GameEvents.FireToast("No character in the world yet."); return; }

            bool enabled = !_player.AutoModeEnabled;
            _player.SetAutoAttack(enabled);
            GameEvents.FireToast(enabled ? "Auto-mode on" : "Auto-mode off");
        });
    }

    /// <summary>
    /// A persistent badge under the top bar, because a recoloured 70px button is not
    /// something you notice from across the room — and auto-mode being on or off
    /// changes what the game does when you walk away from it.
    /// </summary>
    private void BuildAutoBanner()
    {
        var theme = UIManager.Theme;

        var badge = UIFactory.Panel(transform, "AutoBanner", theme.cardBg, false, raycastTarget: false);
        UIFactory.At(badge.transform, 0.40f, 0.865f, 0.60f, 0.915f);
        _autoBanner = badge;

        _autoBannerLabel = UIFactory.Label(badge.transform, "", theme.fontSizeLabel,
                                            theme.accentGreen, TextAlignmentOptions.Center);
        UIFactory.At(_autoBannerLabel, 0f, 0f, 1f, 1f);

        RefreshAutoMode(false);
    }

    private Button     _autoButton;
    private TMP_Text   _autoButtonLabel;
    private GameObject _autoBanner;
    private TMP_Text   _autoBannerLabel;

    /// <summary>Paints both indicators from a single source of truth.</summary>
    private void RefreshAutoMode(bool enabled)
    {
        var theme = UIManager.Theme;

        if (_autoButton != null)
        {
            // Through the colour block, not the Image — Selectable rewrites the Image
            // tint on every state change and would undo a direct assignment the first
            // time the pointer moved over the button.
            var colors = _autoButton.colors;
            colors.normalColor      = enabled ? theme.accentGreen : theme.buttonNormal;
            colors.highlightedColor = enabled ? theme.accentGreen : theme.buttonHover;
            _autoButton.colors      = colors;
        }

        if (_autoButtonLabel != null)
        {
            _autoButtonLabel.text  = enabled ? "AUTO ●" : "AUTO";
            _autoButtonLabel.color = enabled ? theme.textPrimary : theme.textSecondary;
        }

        if (_autoBanner != null) _autoBanner.SetActive(enabled);

        if (_autoBannerLabel != null)
            _autoBannerLabel.text = "● AUTO-MODE ON";
    }

    /// <summary>Reads the live state, for the moments no event will arrive — a fresh
    /// build, a character swap, or re-entering the map.</summary>
    private void SyncAutoMode()
    {
        EnsurePlayer();
        RefreshAutoMode(_player != null && _player.AutoModeEnabled);
    }

    // The current-activity readout used to live here as a permanently visible panel
    // with a show/hide toggle in the menu. It is now rendered inside MenuModal
    // instead — one place to look, and nothing occupying screen space to be toggled
    // off. See MenuModal.BuildActivityBlock.

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

            // Divide by the cooldown AFTER talent reduction, or the sweep on a
            // shortened cooldown starts part-filled and never reads as full.
            float length    = _player.GetAbilityCooldownLength(ability);
            float remaining = _player.GetAbilityCooldownRemaining(i);
            overlay.fillAmount = length > 0f ? Mathf.Clamp01(remaining / length) : 0f;
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

        // Every level is a talent point, so the badge changes on every level-up.
        RefreshTalentBadge();
    }

    private void OnHealthChanged(double current, double max)
    {
        if (max <= 0) return;
        if (_hpFill != null) _hpFill.fillAmount = Mathf.Clamp01((float)(current / max));
        if (_hpText != null) _hpText.text = $"{(long)current} / {(long)max}";
    }

    /// <summary>
    /// Mana and stamina. A pool a class does not use shows empty rather than being
    /// hidden — a Sorcerer glancing at a flat stamina bar learns something true about
    /// their character, where a missing bar would just look like a layout bug.
    /// </summary>
    private void OnResourcesChanged(float mana, float maxMana, float stamina, float maxStamina)
    {
        if (_mpFill != null)
            _mpFill.fillAmount = maxMana > 0f ? Mathf.Clamp01(mana / maxMana) : 0f;

        if (_spFill != null)
            _spFill.fillAmount = maxStamina > 0f ? Mathf.Clamp01(stamina / maxStamina) : 0f;
    }

    private void OnPlayerDied() => GameManager.UI?.Push<DeathScreen>();
}
