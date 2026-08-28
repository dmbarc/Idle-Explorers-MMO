using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

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
    private Image    _classIcon;
    private Image    _hpFill;
    private Image    _mpFill;
    private Image    _spFill;
    private Image    _xpFill;
    private TMP_Text _hpText;
    private TMP_Text _xpText;

    private readonly List<Image>    _abilityCooldownOverlays = new();
    private readonly List<TMP_Text> _abilityLabels           = new();
    private Transform        _abilityBarRoot;
    private PlayerController _player;
    private TMP_Text         _talentBadge;

    public override void Build()
    {
        // ══ THE BOSS BAR LIVES HERE ═══════════════════════════════════════════
        //
        // BossHealthBar subscribes to the boss events itself and builds on engage, so
        // it only ever needed to EXIST. Nothing had created it, so the King wore an
        // ordinary monster bar over his head and the overlay never appeared.
        //
        // On the HUD rather than in the map scene, because it must survive the arena
        // loading and unloading around it.
        if (GetComponent<BossHealthBar>() == null) gameObject.AddComponent<BossHealthBar>();

        // Transparent, non-blocking root. Everything inside anchors itself.
        UIFactory.Panel(transform, "HUDBg", Color.clear, true, raycastTarget: false);

        BuildTopBar();
        BuildBottomBar();
        BuildAutoBanner();
        BuildBuffStrip();
        BuildChatBar();

        // Last, so it draws over everything the HUD just built. A tooltip that appears
        // behind the ability bar is a tooltip nobody can read.
        _worldTip = ItemTooltip.CreateCard(transform);
    }

    public override void OnShow()
    {
        // This screen is cached and reused across characters, so everything it
        // displays has to be re-read here — otherwise the second character you play
        // inherits the first one's name, coins and activity.
        _player = null;
        RefreshCharInfo();
        RefreshXp();
        RefreshCoins(GameManager.Inventory?.Coins ?? 0);
        RebuildAbilityBar();
        RefreshTalentBadge();
        SyncAutoMode();

        GameEvents.OnCharacterXPGained   += OnXpGained;
        GameEvents.OnCharacterLevelUp    += OnLevelUp;
        GameEvents.OnPlayerHealthChanged += OnHealthChanged;
        GameEvents.OnPlayerResourcesChanged += OnResourcesChanged;
        GameEvents.OnCoinsChanged        += RefreshCoins;
        GameEvents.OnPlayerDied          += OnPlayerDied;
        GameEvents.OnTalentsChanged      += RefreshTalentBadge;
        GameEvents.OnClassChanged        += OnClassChanged;
        GameEvents.OnAutoModeChanged     += RefreshAutoMode;
        GameEvents.OnHotbarChanged       += RebuildAbilityBar;
        GameEvents.OnHotbarChanged       += RebuildAbilityBar;
        GameEvents.OnTalentsChanged      += RebuildAbilityBar;

        // The chat window is the display for everything the game says while the HUD
        // is up; ToastLayer checks this before popping so nothing is said twice.
        ChatLog.OnLine += OnChatLine;
        ChatLog.HasWindow = true;
    }

    public override void OnHide()
    {
        // A stuck flag would leave the world camera deaf to WASD for the rest of the
        // session, with nothing on screen to explain why. Cleared here because this
        // runs whether the HUD was left by a panel, a death or a trip to the menu.
        UIManager.TextInputFocused = false;

        GameEvents.OnCharacterXPGained   -= OnXpGained;
        GameEvents.OnCharacterLevelUp    -= OnLevelUp;
        GameEvents.OnPlayerHealthChanged -= OnHealthChanged;
        GameEvents.OnPlayerResourcesChanged -= OnResourcesChanged;
        GameEvents.OnCoinsChanged        -= RefreshCoins;
        GameEvents.OnPlayerDied          -= OnPlayerDied;
        GameEvents.OnTalentsChanged      -= RefreshTalentBadge;
        GameEvents.OnClassChanged        -= OnClassChanged;
        GameEvents.OnAutoModeChanged     -= RefreshAutoMode;
        GameEvents.OnHotbarChanged       -= RebuildAbilityBar;
        GameEvents.OnTalentsChanged      -= RebuildAbilityBar;

        ChatLog.OnLine -= OnChatLine;
        ChatLog.HasWindow = false;
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
        HandleChatKeys();
        UpdateAbilityCooldowns();
        UpdateWorldHover();
        UpdateChatWheel();
        UpdateBuffStrip();
    }

    // ── Corner ────────────────────────────────────────────────────────────────

    /// <summary>
    /// All that is left at the top: the menu button and the coin count.
    ///
    /// The HUD used to run a full-width bar across the top holding the name, level,
    /// three resource bars, coins and the menu — a second horizon above the play area,
    /// on a game whose whole subject is the world in the middle. Everything except
    /// these two moved down to sit beside the ability bar, where a player's eyes
    /// already are. Coins stay up here because they are a number you glance at, not
    /// something you act on mid-fight.
    /// </summary>
    private void BuildTopBar()
    {
        var theme = UIManager.Theme;
        var corner = UIFactory.Panel(transform, "TopCorner", theme.headerBg, false);
        UIFactory.At(corner.transform, 0.80f, 0.925f, 1f, 1f);

        _coinsLabel = UIFactory.Label(corner.transform, "0", theme.fontSizeSmall,
                                       theme.accentGold, TextAlignmentOptions.MidlineRight);
        UIFactory.At(_coinsLabel, 0.05f, 0.20f, 0.68f, 0.80f);

        var menuBtn = UIFactory.Button(corner.transform, "≡", () => GameManager.UI?.Push<MenuModal>(),
                                        width: 60f, height: 0f);
        UIFactory.At(menuBtn, 0.74f, 0.14f, 0.97f, 0.86f);
    }

    // ── Bottom bar ────────────────────────────────────────────────────────────

    /// <summary>
    /// Everything the player watches, in one band: who they are on the left, what they
    /// can press in the middle, where they can go on the right.
    /// </summary>
    private void BuildBottomBar()
    {
        var theme = UIManager.Theme;
        var bar   = UIFactory.Panel(transform, "BottomBar", theme.headerBg, false);

        // Taller than the old 0.09 because it now carries the character block and four
        // bars as well as the buttons.
        UIFactory.At(bar.transform, 0f, 0f, 1f, 0.145f);

        BuildCharacterBlock(bar.transform);
        BuildAbilityBar(bar.transform);

        // Right side nav
        var navStack = UIFactory.HStack(bar.transform, theme.spacing, "NavButtons");
        UIFactory.At(navStack.transform, 0.655f, 0.30f, 0.99f, 0.86f);

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
    /// Who the character is, and how they are doing: class emblem, name, level, then
    /// health, mana, stamina and experience.
    ///
    /// Laid out in fractions of the bar rather than a layout group because the rows
    /// are different shapes — one emblem beside two lines of text, then four bars of
    /// two different widths — and expressing that as nested layout groups is more
    /// machinery than the arrangement is worth.
    /// </summary>
    private void BuildCharacterBlock(Transform parent)
    {
        var theme = UIManager.Theme;

        var charData = CharacterManager.Current;
        string name  = charData?.characterName ?? "Explorer";
        int    level = charData?.level ?? 1;

        // ── Identity ──────────────────────────────────────────────────────────
        //
        // The emblem replaces the class NAME, which is the change that makes room for
        // all this: a cross-specced character's title runs to "Arcane Archer" or
        // "Soulsmith", and at three classes the name and the title together were
        // wider than the space either had.
        _classIcon = UIFactory.Icon(parent, null, 0f, "ClassIcon");
        UIFactory.At(_classIcon, 0.010f, 0.50f, 0.038f, 0.96f);
        _classIcon.preserveAspect = true;
        _classIcon.raycastTarget  = false;

        _charNameLabel = UIFactory.Label(parent, name, theme.fontSizeSmall,
                                          theme.textPrimary, TextAlignmentOptions.MidlineLeft);
        UIFactory.At(_charNameLabel, 0.044f, 0.68f, 0.168f, 0.99f);

        _charLevelLabel = UIFactory.Label(parent, $"Lv. {level}", theme.fontSizeLabel,
                                           theme.accentGold, TextAlignmentOptions.MidlineLeft);
        UIFactory.At(_charLevelLabel, 0.044f, 0.46f, 0.168f, 0.70f);

        // ── Health ────────────────────────────────────────────────────────────
        var hpLbl = UIFactory.Label(parent, "HP", theme.fontSizeLabel,
                                     theme.textSecondary, TextAlignmentOptions.MidlineRight);
        UIFactory.At(hpLbl, 0.172f, 0.66f, 0.198f, 0.94f);

        var (hpRoot, hpFill) = UIFactory.ProgressBar(parent, "HPBar", theme.hpFill);
        _hpFill = hpFill;
        _hpFill.fillAmount = 1f;
        UIFactory.At(hpRoot.transform, 0.203f, 0.68f, 0.415f, 0.92f);

        _hpText = UIFactory.Label(parent, "", theme.fontSizeLabel,
                                   theme.textPrimary, TextAlignmentOptions.Center);
        UIFactory.At(_hpText, 0.203f, 0.68f, 0.415f, 0.92f);

        // ── Mana and stamina ──────────────────────────────────────────────────
        //
        // Both are real: the MP bar sat permanently full for the whole project's life
        // because baseMp was declared and never read by anything.
        var mpLbl = UIFactory.Label(parent, "MP", theme.fontSizeLabel,
                                     theme.textSecondary, TextAlignmentOptions.MidlineRight);
        UIFactory.At(mpLbl, 0.172f, 0.38f, 0.198f, 0.64f);

        var (mpRoot, mpFill) = UIFactory.ProgressBar(parent, "MPBar", theme.mpFill);
        _mpFill = mpFill;
        _mpFill.fillAmount = 1f;
        UIFactory.At(mpRoot.transform, 0.203f, 0.40f, 0.300f, 0.62f);

        var spLbl = UIFactory.Label(parent, "SP", theme.fontSizeLabel,
                                     theme.textSecondary, TextAlignmentOptions.MidlineRight);
        UIFactory.At(spLbl, 0.306f, 0.38f, 0.332f, 0.64f);

        var (spRoot, spFill) = UIFactory.ProgressBar(parent, "SPBar", theme.accentGreen);
        _spFill = spFill;
        _spFill.fillAmount = 1f;
        UIFactory.At(spRoot.transform, 0.337f, 0.40f, 0.415f, 0.62f);

        // ── Experience ────────────────────────────────────────────────────────
        //
        // The XP system has existed since the start — a quarter of every skill's XP
        // goes to the character — and nothing ever showed it. A level was something
        // that happened to you with no sense of approach.
        var xpLbl = UIFactory.Label(parent, "XP", theme.fontSizeLabel,
                                     theme.textSecondary, TextAlignmentOptions.MidlineRight);
        UIFactory.At(xpLbl, 0.172f, 0.10f, 0.198f, 0.36f);

        var (xpRoot, xpFill) = UIFactory.ProgressBar(parent, "XPBar", theme.accentGold);
        _xpFill = xpFill;
        _xpFill.fillAmount = 0f;
        UIFactory.At(xpRoot.transform, 0.203f, 0.12f, 0.415f, 0.34f);

        _xpText = UIFactory.Label(parent, "", theme.fontSizeLabel,
                                   theme.textPrimary, TextAlignmentOptions.Center);
        UIFactory.At(_xpText, 0.203f, 0.12f, 0.415f, 0.34f);

        RefreshCharInfo();
        RefreshXp();
    }

    /// <summary>
    /// Action bar slots 1-5, populated from the character's hotbar.
    /// Each slot shows its keybind, the ability name, and a radial cooldown sweep.
    /// </summary>
    private void BuildAbilityBar(Transform parent)
    {
        var stack = UIFactory.HStack(parent, UIManager.Theme.spacing, "AbilityBar");
        UIFactory.At(stack.transform, 0.435f, 0.10f, 0.635f, 0.90f);
        _abilityBarRoot = stack.transform;

        RebuildAbilityBar();
    }

    /// <summary>
    /// Repopulates the action bar from the character's HOTBAR — the five ability ids
    /// they have chosen — rather than from their class's ability array.
    ///
    /// The bar used to be `cls.abilities[i]`, resolved here AND independently in
    /// PlayerController, which meant slot position was an ability's only identity and
    /// there was nothing to rearrange. Everything now goes through
    /// PlayerController.GetAbility, so the two cannot disagree.
    ///
    /// A slot can legitimately be empty: abilities are earned from the talent tree, so
    /// a level 1 character has an entirely blank bar until they spend their first point.
    /// </summary>
    private void RebuildAbilityBar()
    {
        if (_abilityBarRoot == null) return;

        var theme = UIManager.Theme;

        for (int i = _abilityBarRoot.childCount - 1; i >= 0; i--)
            DestroyImmediate(_abilityBarRoot.GetChild(i).gameObject);
        _abilityCooldownOverlays.Clear();
        _abilityLabels.Clear();

        EnsurePlayer();
        var stack = _abilityBarRoot;

        for (int i = 0; i < CharacterData.HotbarSlots; i++)
        {
            int slot = i;
            var ability = _player != null ? _player.GetAbility(i) : null;

            var slotGo = UIFactory.Slot(stack, $"Ability{i + 1}");

            // Passive abilities are shown but cannot be pressed
            bool activatable = ability != null && ability.IsActivatable;

            var btn = slotGo.AddComponent<Button>();
            btn.targetGraphic = slotGo.GetComponent<Image>();
            btn.onClick.AddListener(() =>
            {
                EnsurePlayer();
                if (_player == null) return;

                if (_player.GetAbility(slot) == null)
                {
                    GameEvents.FireToast("Drag an ability here from the talent tree.");
                    return;
                }
                _player.UseAbility(slot);
            });

            // Every slot accepts a drop, including empty ones — an empty slot is the
            // most likely place a player drags their first ability to.
            slotGo.AddComponent<AbilitySlotDrop>().Bind(i);

            // A filled slot is also a drag SOURCE, so the bar can be rearranged.
            var iconSprite = GameManager.Content?.GetAbilityIcon(ability);
            if (ability != null)
                slotGo.AddComponent<AbilityDragHandle>().Bind(ability.id, iconSprite, i);

            // Icon behind the text. Dimmed so the name stays the readable element —
            // the icon is for recognising a slot at a glance, not for reading.
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

        // Sits just above the bottom bar rather than under a top bar that no longer
        // exists — it belongs with the rest of the HUD, not floating in the sky.
        var badge = UIFactory.Panel(transform, "AutoBanner", theme.cardBg, false, raycastTarget: false);
        UIFactory.At(badge.transform, 0.40f, 0.155f, 0.60f, 0.205f);
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

    // ── What you drank ────────────────────────────────────────────────────────

    private TMP_Text _buffLabel;
    private float    _nextBuffRedrawAt;

    /// <summary>
    /// A line under the coin counter naming every potion still in force.
    ///
    /// ══ WHY IT NEEDS TO BE ON SCREEN AT ALL ═══════════════════════════════════
    ///
    /// Because the whole point of these potions is to be drunk BEFORE walking into a
    /// boss fight, and a player who cannot see whether the Kingsbane Tonic is still
    /// running has no way to make that decision except by drinking another one.
    ///
    /// A line of text rather than a row of icons: there are at most three buffs, they
    /// are distinguished by a name and a countdown rather than by silhouette, and a
    /// row of icons would need art none of them have.
    /// </summary>
    private void BuildBuffStrip()
    {
        var theme = UIManager.Theme;

        _buffLabel = UIFactory.Label(transform, "", theme.fontSizeLabel,
                                      theme.accentGold, TextAlignmentOptions.MidlineRight);

        // Directly beneath the coin corner, which is where a player's eyes already go
        // for "what do I have".
        UIFactory.At(_buffLabel, 0.60f, 0.885f, 0.995f, 0.923f);

        _buffLabel.raycastTarget = false;

        RefreshBuffStrip();
    }

    /// <summary>
    /// Redraws the strip, once a second.
    ///
    /// Not per frame: it is a countdown shown to the second, and rebuilding a
    /// TextMeshPro mesh sixty times a second to move a digit once is work nobody sees.
    /// </summary>
    private void UpdateBuffStrip()
    {
        if (_buffLabel == null || Time.unscaledTime < _nextBuffRedrawAt) return;

        _nextBuffRedrawAt = Time.unscaledTime + 1f;
        RefreshBuffStrip();
    }

    private void RefreshBuffStrip()
    {
        if (_buffLabel == null) return;

        var live = BuffManager.Active();

        if (live.Count == 0)
        {
            _buffLabel.text = "";
            return;
        }

        var line = new System.Text.StringBuilder();

        foreach (var buff in live)
        {
            if (line.Length > 0) line.Append("   ");

            int seconds = Mathf.Max(0, Mathf.CeilToInt((float)buff.secondsRemaining));

            line.Append($"{buff.label} {seconds / 60}:{seconds % 60:00}");
        }

        _buffLabel.text = line.ToString();
    }

    // ── Hovering something in the world ───────────────────────────────────────

    private ItemTooltip.Card _worldTip;
    private SkillNodeController _hoveredNode;
    private float _nextHoverProbeAt;

    /// <summary>
    /// How often the world under the cursor is re-read.
    ///
    /// A raycast per frame for a tooltip is a raycast per frame nobody asked for, and
    /// a tenth of a second is faster than a pointer can settle on something anyway.
    /// The CARD still follows the mouse every frame — only the question of what is
    /// under it is throttled.
    /// </summary>
    private const float HoverProbeSeconds = 0.1f;

    /// <summary>
    /// Describes whatever the pointer is resting on out in the world.
    ///
    /// ══ WHY THE HUD OWNS THIS ═════════════════════════════════════════════════
    ///
    /// The obvious home is a component on each node, which is also how it would end
    /// up drawing a different tooltip from every other hover in the game. Every
    /// hover card in the project comes from ItemTooltip so they all look and behave
    /// the same, and ItemTooltip needs a canvas to live on. The HUD is the canvas
    /// that is up while the player is in the world.
    ///
    /// ══ WHY STATIONS NEEDED IT MOST ═══════════════════════════════════════════
    ///
    /// A rock says what it is by looking like a rock. An anvil is a shape in a camp
    /// that turns out, when you walk to it, to be a menu of eleven recipes — and
    /// nothing on the way there tells you whether the one you want is on it.
    /// </summary>
    private void UpdateWorldHover()
    {
        if (_worldTip?.Root == null) return;

        var mouse = Mouse.current;

        // No pointer at all (a gamepad, a touch device): nothing hovers, and the card
        // must not be left on screen from the last mouse the machine had.
        if (mouse == null || Camera.main == null)
        {
            HideWorldHover();
            return;
        }

        Vector2 screenPos = mouse.position.ReadValue();

        // Over a panel, a button or the chat box. The world is not what is being
        // pointed at, and the UI has its own tooltips.
        if (EventSystem.current != null &&
            EventSystem.current.IsPointerOverGameObject(mouse.deviceId))
        {
            HideWorldHover();
            return;
        }

        if (Time.unscaledTime >= _nextHoverProbeAt)
        {
            _nextHoverProbeAt = Time.unscaledTime + HoverProbeSeconds;
            _hoveredNode      = ProbeForNode(screenPos);
        }

        if (_hoveredNode == null || _hoveredNode.Entry == null)
        {
            HideWorldHover();
            return;
        }

        ItemTooltip.ShowText(_worldTip, (RectTransform)transform,
                             NodeTitle(_hoveredNode), NodeBody(_hoveredNode), screenPos);
    }

    private void HideWorldHover()
    {
        _hoveredNode = null;
        _worldTip?.Hide();
    }

    /// <summary>What the cursor is over, or null. Triggers included — a station's
    /// collider may be one, and the default query would look straight through it.</summary>
    private static SkillNodeController ProbeForNode(Vector2 screenPos)
    {
        Ray ray = Camera.main.ScreenPointToRay(screenPos);

        return Physics.Raycast(ray, out RaycastHit hit, Mathf.Infinity,
                               Physics.DefaultRaycastLayers, QueryTriggerInteraction.Collide)
            ? hit.collider.GetComponentInParent<SkillNodeController>()
            : null;
    }

    private static string NodeTitle(SkillNodeController node)
    {
        var entry = node.Entry;

        if (node.IsStation)
            return entry.stationType switch
            {
                "campfire" => "Campfire",
                "anvil"    => "Anvil",
                "forge"    => "Forge",
                "bank"     => "Bank Chest",
                _          => "Workstation",
            };

        var yielded = GameManager.Content?.GetItem(entry.targetItemId);
        return yielded?.DisplayName ?? entry.nodeId;
    }

    /// <summary>
    /// The useful half: what working here actually produces.
    ///
    /// A station lists what it can make and marks what the player's level does not
    /// reach yet, because "come back at Smithing 30" is the single most useful thing
    /// a crafting station can say. A gathering node states its yield and its rate.
    /// </summary>
    private static string NodeBody(SkillNodeController node)
    {
        var entry = node.Entry;
        var lines = new List<string>();

        string skillName = GameManager.Content?.GetSkill(entry.skillId)?.DisplayName ?? entry.skillId;

        if (node.IsStation)
        {
            if (entry.stationType == "bank")
                return "Stores what you are not carrying.";

            int level = GameManager.Skills?.GetSkillLevel(entry.skillId) ?? 1;
            var recipes = GameManager.Content?.GetRecipesForStation(entry.stationType)
                          ?? new List<CraftRecipe>();

            lines.Add($"{skillName}  ·  Lv. {level}");

            if (recipes.Count == 0)
            {
                lines.Add("Nothing can be made here yet.");
                return string.Join("\n", lines);
            }

            lines.Add("");

            // Capped, because the forge offers enough recipes to run a card off the
            // bottom of the screen. The count that follows is what stops the cap
            // reading as "this is everything".
            const int Shown = 6;

            for (int i = 0; i < recipes.Count && i < Shown; i++)
            {
                CraftRecipe recipe = recipes[i];

                var output = GameManager.Content?.GetItem(recipe.outputItemId);
                string name = output?.DisplayName ?? recipe.DisplayName;

                lines.Add(level >= recipe.reqSkillLevel
                              ? $"· {name}"
                              : $"· {name}  (needs Lv. {recipe.reqSkillLevel})");
            }

            if (recipes.Count > Shown) lines.Add($"…and {recipes.Count - Shown} more.");

            return string.Join("\n", lines);
        }

        var item = GameManager.Content?.GetItem(entry.targetItemId);

        lines.Add($"{skillName}  ·  needs Lv. {Mathf.Max(1, entry.reqSkillLevel)}");
        lines.Add("");
        lines.Add($"Yields {item?.DisplayName ?? entry.targetItemId} " +
                  $"every {node.SecondsPerAction:0.0}s.");

        if (!string.IsNullOrEmpty(entry.specialLabel) && entry.specialChance > 0f)
            lines.Add($"Sometimes {entry.specialLabel} ({entry.specialChance * 100f:0.#}%).");

        return string.Join("\n", lines);
    }

    // ── Chat ──────────────────────────────────────────────────────────────────

    private TMP_InputField _chatField;
    private Transform      _chatLines;
    private ScrollRect     _chatScroll;

    /// <summary>
    /// The chat window: a column of recent lines on the left, with the box you type
    /// into underneath it.
    ///
    /// ══ WHY THE BOX DID NOTHING ═══════════════════════════════════════════════
    ///
    /// The first version had only the input, opened by pressing Enter. Enter both
    /// activated the field AND was then read by that same field as a submit on the
    /// frame it woke up, so it opened and closed again before a character could be
    /// typed. It looked like a text box that ignored the keyboard; it was a text box
    /// that was never focused for longer than a frame.
    ///
    /// Now the field is always there and always clickable, Enter focuses it only when
    /// it is not already focused, and submitting an empty line does nothing rather
    /// than counting as sending.
    ///
    /// Nothing here blocks raycasts except the input itself. A chat window that ate
    /// world clicks would take a fifth of the screen away from click-to-move.
    /// </summary>
    private void BuildChatBar()
    {
        var theme = UIManager.Theme;

        // ── The log ───────────────────────────────────────────────────────────
        var (scroll, content) = UIFactory.ScrollList(transform, "ChatLog", 2f);
        UIFactory.At(scroll, 0.005f, 0.155f, 0.30f, 0.52f);

        _chatScroll = scroll;
        _chatLines  = content;

        // Left-aligned and bottom-up, the way every chat window in the genre reads.
        var layout = content.GetComponent<VerticalLayoutGroup>();
        if (layout != null)
        {
            layout.childAlignment      = TextAnchor.LowerLeft;
            layout.childForceExpandHeight = false;
            layout.childControlHeight  = true;
        }

        // The panel is scenery. Only the input below takes input.
        foreach (var image in scroll.GetComponentsInChildren<Image>(true))
            image.raycastTarget = false;

        // AFTER the sweep above, deliberately: the scrollbar is the one part of this
        // window that MUST take the pointer, and building it first would have its
        // raycast turned off by the very next line.
        BuildChatScrollbar(scroll, theme);

        // ── The box ───────────────────────────────────────────────────────────
        _chatField = UIFactory.InputField(transform, "Press Enter to chat  (/p /g /w)",
                                           width: 0f, height: 34f);
        UIFactory.At(_chatField.transform, 0.005f, 0.152f, 0.30f, 0.152f + 0.038f);

        _chatField.characterLimit = ChatBubble.MaxMessageLength;

        // SingleLine matters: the default MultiLineNewline swallows Enter as a newline
        // and onSubmit never fires, which reads as a field that has stopped working.
        _chatField.lineType = TMP_InputField.LineType.SingleLine;
        _chatField.onSubmit.AddListener(SendChat);

        _chatField.onSelect.AddListener(_ => UIManager.TextInputFocused = true);
        _chatField.onDeselect.AddListener(_ => UIManager.TextInputFocused = false);

        // Whatever was said before this panel existed. The HUD is rebuilt per
        // character and the log outlives it, so history is redrawn rather than lost.
        foreach (var line in ChatLog.Lines) AppendLine(line);
        ScrollToBottom();
    }

    /// <summary>
    /// The bar down the right-hand edge of the chat window.
    ///
    /// ══ WHY THERE WAS NO WAY TO READ BACK ═════════════════════════════════════
    ///
    /// The log has always been a ScrollRect, and it has never been scrollable. Two
    /// reasons, both invisible: there was no scrollbar, and the sweep above turns off
    /// raycasting on every Image in the window — including the viewport — so the wheel
    /// had nothing to land on and ScrollRect never heard it either.
    ///
    /// That is most of what "the chat log seems out of sync" was. It was not showing
    /// the wrong lines; it was showing only the newest ones, permanently, with no way
    /// to look at the rest.
    ///
    /// The bar is narrow on purpose. Everything else in this window is deliberately
    /// click-through so the log does not take a fifth of the screen away from
    /// click-to-move, and this is the smallest exception that gives the player a
    /// handle to drag.
    /// </summary>
    private void BuildChatScrollbar(ScrollRect scroll, UITheme theme)
    {
        var barGo = new GameObject("ChatScrollbar", typeof(RectTransform), typeof(Image));
        barGo.transform.SetParent(scroll.transform, false);

        var barRt = barGo.GetComponent<RectTransform>();

        // A strip on the inner right edge, full height.
        barRt.anchorMin        = new Vector2(1f, 0f);
        barRt.anchorMax        = new Vector2(1f, 1f);
        barRt.pivot            = new Vector2(1f, 0.5f);
        barRt.sizeDelta        = new Vector2(ScrollbarWidth, 0f);
        barRt.anchoredPosition = Vector2.zero;

        var track = barGo.GetComponent<Image>();
        track.color         = new Color(0f, 0f, 0f, 0.35f);
        track.raycastTarget = true;

        var handleGo = new GameObject("Handle", typeof(RectTransform), typeof(Image));
        handleGo.transform.SetParent(barGo.transform, false);

        var handleRt = handleGo.GetComponent<RectTransform>();
        handleRt.sizeDelta = Vector2.zero;

        var handle = handleGo.GetComponent<Image>();
        handle.color         = theme.textSecondary;
        handle.raycastTarget = true;

        var bar = barGo.AddComponent<Scrollbar>();
        bar.direction     = Scrollbar.Direction.BottomToTop;
        bar.handleRect    = handleRt;
        bar.targetGraphic = handle;

        scroll.verticalScrollbar = bar;

        // AutoHide, not Permanent: an empty log with a full-height bar down the side
        // reads as a broken widget. It appears the moment there is something to
        // scroll back to.
        scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;
    }

    /// <summary>Narrow enough not to reclaim the window's click-through, wide enough to grab.</summary>
    private const float ScrollbarWidth = 10f;

    /// <summary>
    /// The mouse wheel, read directly rather than through the EventSystem.
    ///
    /// ScrollRect's own wheel handling needs a raycast target under the pointer, and
    /// giving the log one would put a click-eating rectangle over a fifth of the
    /// screen — which is the exact thing the window was built to avoid. Reading the
    /// device and testing the rectangle costs nothing and blocks nothing.
    /// </summary>
    private void UpdateChatWheel()
    {
        if (_chatScroll == null) return;

        var mouse = Mouse.current;
        if (mouse == null) return;

        float wheel = mouse.scroll.ReadValue().y;
        if (Mathf.Abs(wheel) < 0.01f) return;

        // Screen-space overlay, so the canvas camera is null here.
        if (!RectTransformUtility.RectangleContainsScreenPoint(
                (RectTransform)_chatScroll.transform, mouse.position.ReadValue(), null))
            return;

        float hidden = ContentOverflow();
        if (hidden <= 1f) return;

        // The raw value is ±120 per notch on Windows and something else everywhere
        // else, so only its SIGN is used and the step is ours.
        float step = Mathf.Sign(wheel) * WheelStepPixels / hidden;

        _chatScroll.verticalNormalizedPosition =
            Mathf.Clamp01(_chatScroll.verticalNormalizedPosition + step);
    }

    /// <summary>How far one notch of the wheel moves the log, in pixels.</summary>
    private const float WheelStepPixels = 70f;

    /// <summary>How much taller the content is than the window showing it. Zero when it fits.</summary>
    private float ContentOverflow()
    {
        if (_chatScroll?.content == null || _chatScroll.viewport == null) return 0f;

        return Mathf.Max(0f, _chatScroll.content.rect.height - _chatScroll.viewport.rect.height);
    }

    /// <summary>
    /// True when the newest line is on screen.
    ///
    /// The other half of "the chat log is out of sync": every new line yanked the view
    /// back to the bottom, so reading back through a fight was impossible while
    /// anything was still happening — and something is always still happening.
    /// </summary>
    private bool ChatIsAtBottom()
    {
        if (_chatScroll == null) return true;

        // Nothing to scroll: the newest line is on screen by definition, and
        // verticalNormalizedPosition is meaningless (and can be NaN) in that state.
        if (ContentOverflow() <= 1f) return true;

        return _chatScroll.verticalNormalizedPosition <= 0.02f;
    }

    /// <summary>One line in the window, coloured by what kind of line it is.</summary>
    private void AppendLine(ChatLog.Line line)
    {
        if (_chatLines == null || string.IsNullOrEmpty(line.Text)) return;

        var theme = UIManager.Theme;

        var label = UIFactory.Label(_chatLines, line.Text, theme.fontSizeLabel,
                                     theme.ChatColor(line.Tone), TextAlignmentOptions.TopLeft);
        label.textWrappingMode = TextWrappingModes.Normal;
        label.raycastTarget    = false;

        // Sized to its own wrapped height, so a long line is not clipped to one row.
        var fitter = label.gameObject.AddComponent<ContentSizeFitter>();
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        // The log is capped in ChatLog; this keeps the view from outgrowing it.
        // Counted once rather than looped on childCount, because Destroy is deferred
        // to the end of the frame and the count would not drop until then.
        int excess = _chatLines.childCount - ChatLog.Capacity;
        for (int i = 0; i < excess; i++) Destroy(_chatLines.GetChild(i).gameObject);
    }

    private void OnChatLine(ChatLog.Line line)
    {
        // A cleared log arrives as a null line rather than its own event.
        if (string.IsNullOrEmpty(line.Text))
        {
            if (_chatLines == null) return;
            for (int i = _chatLines.childCount - 1; i >= 0; i--)
                Destroy(_chatLines.GetChild(i).gameObject);
            return;
        }

        // Asked BEFORE the line is added, because adding it is what makes the view no
        // longer be at the bottom. A player who has scrolled back to read something
        // keeps their place; one who is watching the newest line follows it.
        bool following = ChatIsAtBottom();

        AppendLine(line);

        if (following) ScrollToBottom();
    }

    /// <summary>
    /// Pins the view to the newest line. Deferred by a frame because the layout has
    /// not measured the line that was just added, so scrolling now would scroll to
    /// where the bottom used to be.
    /// </summary>
    private void ScrollToBottom()
    {
        if (_chatScroll == null) return;
        StartCoroutine(ScrollNextFrame());
    }

    private System.Collections.IEnumerator ScrollNextFrame()
    {
        yield return null;
        if (_chatScroll != null) _chatScroll.verticalNormalizedPosition = 0f;
    }

    /// <summary>
    /// Says it out loud: into the chat window, and above the character's head.
    ///
    /// A leading /p, /g or /w picks a channel. Those have no traffic until there is a
    /// server, but they are how the four channel colours can be seen at all today.
    /// </summary>
    private void SendChat(string message)
    {
        // Submitting nothing is not sending. It used to clear focus, which is what
        // made a stray Enter feel like the box had closed itself.
        if (string.IsNullOrWhiteSpace(message)) return;

        var tone = ChatLog.ParseChannel(ref message);
        if (string.IsNullOrWhiteSpace(message)) { _chatField.text = ""; return; }

        // ══ LOCAL CHAT GOES THROUGH THE SERVER ═════════════════════════════
        //
        // So other players hear it. It used to be drawn straight into this client's
        // own log and a bubble over this client's own head, which is a conversation
        // with nobody -- speaking in one browser showed nothing in another.
        //
        // The line comes BACK on the next presence poll and is drawn from there, by
        // the same code that draws everybody else's. One path, and no way for our view
        // of a conversation to drift from the view other people have of it. At worst
        // it appears a couple of seconds late, which is a conversation rather than a
        // problem.
        if (tone == ChatTone.Local && IdleExplorers.Backend.ServerState.IsAuthoritative)
        {
            IdleExplorers.Backend.PresenceSync.Say(message);
        }
        else
        {
            // Offline, and the channels with no traffic yet, still echo locally --
            // otherwise typing into them looks broken rather than empty.
            string speaker = CharacterManager.Current?.characterName ?? "You";
            ChatLog.Say($"{speaker}: {message}", tone);

            EnsurePlayer();
            if (_player != null) ChatBubble.Say(_player.transform, message);
        }

        _chatField.text = "";

        // Kept focused, so a conversation does not need the mouse between lines.
        _chatField.ActivateInputField();
    }

    /// <summary>
    /// Enter opens the box; Escape closes it without sending.
    ///
    /// Read from the keyboard device rather than through the EventSystem, because the
    /// field is not focused yet at the moment Enter has to be noticed — that IS the
    /// event being waited for. Guarded on isFocused so the Enter that submits a line
    /// is not also read here as a request to open the box again.
    /// </summary>
    private void HandleChatKeys()
    {
        var kb = Keyboard.current;
        if (kb == null || _chatField == null) return;

        if (_chatField.isFocused)
        {
            if (kb.escapeKey.wasPressedThisFrame)
            {
                _chatField.text = "";
                ReleaseChatFocus();
            }
            return;
        }

        // Not while a panel is up: Enter is a confirm key in most of them, and
        // stealing it here would break every one of those at once.
        if (UIManager.IsModalOpen) return;

        if (kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame)
            _chatField.ActivateInputField();
    }

    private void ReleaseChatFocus()
    {
        _chatField.DeactivateInputField();

        // Deselect as well as deactivate. Deactivating alone leaves the field as the
        // EventSystem's selected object, so the next Enter is delivered straight back
        // to it and the player is typing again without having asked to be.
        if (EventSystem.current != null &&
            EventSystem.current.currentSelectedGameObject == _chatField.gameObject)
            EventSystem.current.SetSelectedGameObject(null);

        UIManager.TextInputFocused = false;
    }

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

        if (_charNameLabel  != null) _charNameLabel.text  = charData.characterName;
        if (_charLevelLabel != null) _charLevelLabel.text = $"Lv. {charData.level}";

        if (_classIcon != null)
        {
            // The PRIMARY class. A cross-specced character has a combined title —
            // "Paladin", "Soulsmith" — and no combined emblem; the title itself is on
            // the character sheet, which is where a player goes to read about
            // themselves rather than to check their health.
            var classIds = charData.ClassIds();
            string primary = classIds != null && classIds.Count > 0 ? classIds[0] : charData.classId;

            _classIcon.sprite = GameManager.Content?.GetClassIcon(primary);
            _classIcon.enabled = _classIcon.sprite != null;
        }
    }

    /// <summary>
    /// The experience bar: how far through the current level, and how much is left.
    ///
    /// Character XP is a quarter of every skill's XP, so this moves while mining as
    /// readily as while fighting — which is the point of showing it on an idle game's
    /// HUD rather than only on the character sheet.
    /// </summary>
    private void RefreshXp()
    {
        var charData = CharacterManager.Current;
        if (charData == null) return;

        long floor = CharacterManager.LevelToXP(charData.level);
        long roof  = CharacterManager.LevelToXP(charData.level + 1);

        long span = roof - floor;
        long into = charData.xp - floor;

        float progress = span > 0 ? Mathf.Clamp01(into / (float)span) : 1f;

        if (_xpFill != null) _xpFill.fillAmount = progress;
        if (_xpText != null)
            _xpText.text = span > 0
                ? $"{NumberFormatter.Format(System.Math.Max(0, into))} / {NumberFormatter.Format(span)}"
                : "MAX";
    }

    private void OnXpGained(long amount) => RefreshXp();

    private void RefreshCoins(long total)
    {
        if (_coinsLabel != null) _coinsLabel.text = $"◈ {NumberFormatter.Format(total)}";
    }

    private void OnLevelUp(int newLevel)
    {
        if (_charLevelLabel != null) _charLevelLabel.text = $"Lv. {newLevel}";
        GameEvents.FireToast($"⬆ Level {newLevel}!");
        GameManager.Audio?.PlayLevelUp();

        // The bar's floor and ceiling both moved — without this it stays where the
        // previous level left it until the next scrap of XP arrives.
        RefreshXp();

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
