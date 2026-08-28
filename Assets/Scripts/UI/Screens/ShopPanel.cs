using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// The relic coin shop: packs bought with money, gems bought with relic coins.
///
/// Both halves are driven entirely by shop_data.json, so changing a price or adding
/// a tier is a content edit. Coins-per-dollar is shown on every pack — a ladder that
/// hides its own value curve is one that has something to hide, and displaying it
/// keeps the pricing honest as tiers are added.
/// </summary>
public class ShopPanel : UIScreen
{
    public override bool IsOverlay     => true;

    /// <summary>Balances and affordability change on every purchase.</summary>
    public override bool RebuildOnShow => true;

    private TMP_Text _balanceLabel;

    public override void Build()
    {
        var theme = UIManager.Theme;

        var backdrop    = UIFactory.Panel(transform, "Backdrop", theme.overlayBg, true);
        var backdropBtn = backdrop.AddComponent<Button>();
        backdropBtn.transition = Selectable.Transition.None;
        backdropBtn.onClick.AddListener(() => GameManager.UI?.Pop());

        var panel   = UIFactory.Panel(transform, "ShopPanel", theme.panelBg, false);
        var panelRt = panel.GetComponent<RectTransform>();
        panelRt.anchorMin = new Vector2(0.18f, 0.08f);
        panelRt.anchorMax = new Vector2(0.82f, 0.92f);
        panelRt.offsetMin = panelRt.offsetMax = Vector2.zero;

        BuildHeader(panel.transform, theme);
        BuildList(panel.transform, theme);

        var close = UIFactory.Button(panel.transform, "CLOSE", () => GameManager.UI?.Pop(), width: 0f);
        UIFactory.At(close, 0.35f, 0.015f, 0.65f, 0.085f);
    }

    public override void OnShow() => GameEvents.OnRelicCoinsChanged += OnBalanceChanged;
    public override void OnHide() => GameEvents.OnRelicCoinsChanged -= OnBalanceChanged;

    private bool _needsRebuild;

    /// <summary>
    /// Deferred, like TalentPanel: the balance changes from inside the Button click
    /// that spent it, and RebuildContents uses DestroyImmediate — rebuilding inline
    /// would destroy the button whose handler is still on the stack.
    /// </summary>
    private void OnBalanceChanged(long _) => _needsRebuild = true;

    /// <summary>
    /// Where the list was scrolled to before the last rebuild.
    ///
    /// ══ WHY IT IS REMEMBERED ═══════════════════════════════════════════════
    ///
    /// Buying anything changes the balance, which rebuilds the panel, which builds a
    /// new ScrollRect starting at the top. So every purchase threw the player back to
    /// the first row -- and the coin packs are at the BOTTOM, which is exactly where
    /// somebody buying repeatedly is looking.
    ///
    /// 1 is the top: ScrollRect measures from the bottom, and a fresh list is at 1.
    /// </summary>
    private float _scrollAt = 1f;

    private void LateUpdate()
    {
        if (!_needsRebuild) return;
        _needsRebuild = false;

        if (_scroll != null) _scrollAt = _scroll.verticalNormalizedPosition;

        RebuildContents();

        // After the rebuild, because RebuildContents is what creates the new one.
        // Deferred a frame: the layout group has not run yet, and a position set
        // against a content rect of height zero is discarded.
        if (_scroll != null) StartCoroutine(RestoreScroll());
    }

    private System.Collections.IEnumerator RestoreScroll()
    {
        yield return null;

        if (_scroll != null) _scroll.verticalNormalizedPosition = _scrollAt;
    }

    /// <summary>The live list, so its position survives a rebuild.</summary>
    private ScrollRect _scroll;

    // ── Header ────────────────────────────────────────────────────────────────

    private void BuildHeader(Transform parent, UITheme theme)
    {
        var header   = UIFactory.Panel(parent, "Header", theme.headerBg, false);
        var headerRt = header.GetComponent<RectTransform>();
        headerRt.anchorMin = new Vector2(0f, 0.90f);
        headerRt.anchorMax = new Vector2(1f, 1f);
        headerRt.offsetMin = headerRt.offsetMax = Vector2.zero;

        var title = UIFactory.Label(header.transform, "SHOP", theme.fontSizeBody,
                                     theme.accentGold, TextAlignmentOptions.MidlineLeft);
        UIFactory.At(title, 0.02f, 0.45f, 0.45f, 0.95f);

        _balanceLabel = UIFactory.Label(header.transform,
                                         $"◆ {NumberFormatter.Format(ShopManager.Balance)} relic coins",
                                         theme.fontSizeSmall, theme.accentPurple,
                                         TextAlignmentOptions.MidlineRight);
        UIFactory.At(_balanceLabel, 0.50f, 0.45f, 0.98f, 0.95f);

        // Never let a stub shop look like a real one.
        string notice = ShopManager.PurchasingAvailable
            ? "TEST BUILD — coin packs are granted instantly. No money is taken and no store is contacted."
            : "Real-money purchases are not available yet.";

        var noticeLabel = UIFactory.Label(header.transform, notice, theme.fontSizeLabel,
                                           theme.accentRed, TextAlignmentOptions.MidlineLeft);
        UIFactory.At(noticeLabel, 0.02f, 0.05f, 0.98f, 0.44f);
    }

    // ── Rows ──────────────────────────────────────────────────────────────────

    private void BuildList(Transform parent, UITheme theme)
    {
        var (scroll, content) = UIFactory.ScrollList(parent, "ShopScroll", theme.spacing);

        _scroll = scroll;
        var scrollRt = scroll.GetComponent<RectTransform>();
        scrollRt.anchorMin = new Vector2(0.03f, 0.10f);
        scrollRt.anchorMax = new Vector2(0.97f, 0.885f);
        scrollRt.offsetMin = scrollRt.offsetMax = Vector2.zero;

        var content_ = GameManager.Content;

        SectionHeading(content, theme, "SPEND RELIC COINS");

        // ══ RELIC-COIN PRODUCTS ONLY ══════════════════════════════════════════
        //
        // ShopProduct now carries both a relic price and a gold one, and the
        // shopkeeper's potions are priced in gold. Listing them here would show a
        // "◆ 0" row with an enabled BUY button, which is a free purchase.
        int listed = 0;

        if (content_ != null)
            foreach (var product in content_.ShopProducts)
            {
                if (product == null || product.relicCoinCost <= 0L) continue;

                BuildProductRow(content, theme, product);
                listed++;
            }

        if (listed == 0) Empty(content, theme, "Nothing for sale yet.");

        SectionHeading(content, theme, "BUY RELIC COINS");
        if (content_ == null || content_.CoinPacks.Count == 0)
            Empty(content, theme, "No coin packs are defined.");
        else
            foreach (var pack in content_.CoinPacks)
                if (pack != null) BuildPackRow(content, theme, pack);
    }

    private static void SectionHeading(Transform parent, UITheme theme, string text)
    {
        var label = UIFactory.Label(parent, text, theme.fontSizeSmall,
                                     theme.accentGold, TextAlignmentOptions.MidlineLeft);
        var element = label.gameObject.AddComponent<LayoutElement>();
        element.minHeight = element.preferredHeight = 34f;
    }

    private static void Empty(Transform parent, UITheme theme, string text)
    {
        var label = UIFactory.Label(parent, text, theme.fontSizeSmall,
                                     theme.textSecondary, TextAlignmentOptions.Center);
        var element = label.gameObject.AddComponent<LayoutElement>();
        element.minHeight = element.preferredHeight = 40f;
    }

    /// <summary>A gem, bought with relic coins.</summary>
    private void BuildProductRow(Transform parent, UITheme theme, ShopProduct product)
    {
        var row   = UIFactory.Panel(parent, $"Product_{product.id}", theme.cardBg, false);
        var rowEl = row.AddComponent<LayoutElement>();
        rowEl.minHeight = rowEl.preferredHeight = 72f;

        var icon = GameManager.Content?.GetItemIcon(product.itemId);
        if (icon != null)
        {
            var image = UIFactory.Icon(row.transform, icon, 0f, "ProductIcon");
            UIFactory.At(image, 0.015f, 0.12f, 0.10f, 0.88f);
        }

        var name = UIFactory.Label(row.transform, product.DisplayName, theme.fontSizeSmall,
                                    theme.textPrimary, TextAlignmentOptions.BottomLeft);
        UIFactory.At(name, 0.12f, 0.50f, 0.66f, 0.92f);

        var description = UIFactory.Label(row.transform, product.description, theme.fontSizeLabel,
                                           theme.textSecondary, TextAlignmentOptions.TopLeft);
        UIFactory.At(description, 0.12f, 0.10f, 0.66f, 0.48f);

        bool affordable = ShopManager.Balance >= product.relicCoinCost;

        var cost = UIFactory.Label(row.transform,
                                    $"◆ {NumberFormatter.Format(product.relicCoinCost)}",
                                    theme.fontSizeSmall,
                                    affordable ? theme.accentPurple : theme.textDisabled,
                                    TextAlignmentOptions.MidlineRight);
        UIFactory.At(cost, 0.66f, 0.15f, 0.80f, 0.85f);

        var buy = UIFactory.Button(row.transform, "BUY",
                                    () => GameManager.Shop?.Purchase(product.id), width: 0f);
        UIFactory.At(buy, 0.82f, 0.18f, 0.985f, 0.82f);
        buy.interactable = affordable;
    }

    /// <summary>A relic coin pack, bought with money.</summary>
    private void BuildPackRow(Transform parent, UITheme theme, RelicCoinPack pack)
    {
        var row   = UIFactory.Panel(parent, $"Pack_{pack.id}", theme.cardBg, false);
        var rowEl = row.AddComponent<LayoutElement>();
        rowEl.minHeight = rowEl.preferredHeight = 72f;

        var name = UIFactory.Label(row.transform,
                                    $"{pack.DisplayName}  —  ◆ {NumberFormatter.Format(pack.coins)}",
                                    theme.fontSizeSmall, theme.textPrimary,
                                    TextAlignmentOptions.BottomLeft);
        UIFactory.At(name, 0.03f, 0.50f, 0.66f, 0.92f);

        // Coins per dollar, shown deliberately. It is the only figure that lets a
        // player compare tiers, and printing it keeps the ladder honest as it grows.
        string value = $"{pack.CoinsPerDollar:0} coins per $";
        if (!string.IsNullOrEmpty(pack.badge)) value = $"{pack.badge}  •  {value}";

        var subtitle = UIFactory.Label(row.transform, value, theme.fontSizeLabel,
                                        string.IsNullOrEmpty(pack.badge) ? theme.textSecondary
                                                                          : theme.accentGreen,
                                        TextAlignmentOptions.TopLeft);
        UIFactory.At(subtitle, 0.03f, 0.10f, 0.66f, 0.48f);

        var buy = UIFactory.Button(row.transform, pack.DisplayPrice,
                                    () => GameManager.Shop?.PurchaseRelicCoins(pack.id), width: 0f);
        UIFactory.At(buy, 0.70f, 0.18f, 0.97f, 0.82f);
        buy.interactable = ShopManager.PurchasingAvailable;
    }
}
