using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The shopkeeper's counter: things bought with gold rather than relic coins.
///
/// ══ WHY IT IS A SEPARATE SCREEN FROM ShopPanel ════════════════════════════════
///
/// Not because the machinery differs — a purchase goes down exactly the same path,
/// and ShopProduct carries both prices — but because the two are different PLACES.
/// ShopPanel is a menu button; this is a person standing in a camp. Folding gold
/// potions into the relic-coin shop would mean the premium currency screen is where
/// you go to spend the currency you farmed, which is the one association a game
/// selling both should not make.
///
/// ══ WHERE THE MONEY GOES ══════════════════════════════════════════════════════
///
/// Nowhere the client decides. ShopManager.Purchase routes to the server whenever
/// there is one, ShopProduct.Currency picks the wallet in the SHARED rules, and the
/// server spends from that wallet under the character lock. This screen shows a price
/// and calls the same function the relic shop calls.
/// </summary>
public class GoldShopPanel : UIScreen
{
    public override bool IsOverlay     => true;

    /// <summary>Balance and affordability change on every purchase.</summary>
    public override bool RebuildOnShow => true;

    /// <summary>Who is serving. Static because Push&lt;T&gt; takes no arguments.</summary>
    public static NpcController Keeper;

    /// <summary>Opens the counter for a given shopkeeper.</summary>
    public static void Open(NpcController keeper)
    {
        Keeper = keeper;
        GameManager.UI?.Push<GoldShopPanel>();
    }

    public override void Build()
    {
        var theme = UIManager.Theme;

        var backdrop    = UIFactory.Panel(transform, "Backdrop", theme.overlayBg, true);
        var backdropBtn = backdrop.AddComponent<Button>();
        backdropBtn.transition = Selectable.Transition.None;
        backdropBtn.onClick.AddListener(() => GameManager.UI?.Pop());

        var panel   = UIFactory.Panel(transform, "GoldShopPanel", theme.panelBg, false);
        var panelRt = panel.GetComponent<RectTransform>();
        panelRt.anchorMin = new Vector2(0.20f, 0.12f);
        panelRt.anchorMax = new Vector2(0.80f, 0.88f);
        panelRt.offsetMin = panelRt.offsetMax = Vector2.zero;

        BuildHeader(panel.transform, theme);
        BuildList(panel.transform, theme);

        var close = UIFactory.Button(panel.transform, "CLOSE", () => GameManager.UI?.Pop(), width: 0f);
        UIFactory.At(close, 0.35f, 0.02f, 0.65f, 0.10f);
    }

    public override void OnShow() => GameEvents.OnCoinsChanged += OnCoinsChanged;
    public override void OnHide() => GameEvents.OnCoinsChanged -= OnCoinsChanged;

    private bool _needsRebuild;

    /// <summary>
    /// Deferred by a frame, the same as ShopPanel and TalentPanel: the balance changes
    /// from inside the Button click that spent it, and RebuildContents uses
    /// DestroyImmediate — rebuilding inline destroys the button whose handler is still
    /// on the stack.
    /// </summary>
    private void OnCoinsChanged(long _) => _needsRebuild = true;

    private void Update()
    {
        if (!_needsRebuild) return;

        _needsRebuild = false;
        RebuildContents();
    }

    // ── Header ────────────────────────────────────────────────────────────────

    private void BuildHeader(Transform parent, UITheme theme)
    {
        var header = UIFactory.Panel(parent, "Header", theme.headerBg, false);
        UIFactory.At(header.transform, 0f, 0.88f, 1f, 1f);

        string who = string.IsNullOrEmpty(Keeper?.displayName) ? "Shopkeeper" : Keeper.displayName;

        var title = UIFactory.Label(header.transform, who.ToUpperInvariant(), theme.fontSizeBody,
                                     theme.accentGold, TextAlignmentOptions.MidlineLeft);
        UIFactory.At(title, 0.03f, 0.45f, 0.60f, 1f);

        var purse = UIFactory.Label(header.transform,
                                     $"{NumberFormatter.Format(Gold)} gold", theme.fontSizeSmall,
                                     theme.textPrimary, TextAlignmentOptions.MidlineRight);
        UIFactory.At(purse, 0.55f, 0.45f, 0.97f, 1f);

        // ══ SAID ONCE, HERE, RATHER THAN FOUR TIMES IN THE DESCRIPTIONS ═══════
        //
        // The boss freezes a stat snapshot at engage, so a potion drunk during the
        // fight is not in the numbers. A player who discovers that at twenty percent
        // health has been misled by the shop.
        var advice = UIFactory.Label(header.transform,
                                      "Drink before you go through a boss portal — the King is " +
                                      "measured against you at the moment you walk in.",
                                      theme.fontSizeLabel, theme.textSecondary,
                                      TextAlignmentOptions.MidlineLeft);
        UIFactory.At(advice, 0.03f, 0.02f, 0.97f, 0.44f);
    }

    /// <summary>What is in the purse. Gold is a wallet, not an inventory item.</summary>
    private static long Gold => GameManager.Inventory?.Coins ?? 0L;

    // ── Rows ──────────────────────────────────────────────────────────────────

    private void BuildList(Transform parent, UITheme theme)
    {
        var (scroll, content) = UIFactory.ScrollList(parent, "GoldShopScroll", theme.spacing);

        var scrollRt = scroll.GetComponent<RectTransform>();
        scrollRt.anchorMin = new Vector2(0.03f, 0.12f);
        scrollRt.anchorMax = new Vector2(0.97f, 0.865f);
        scrollRt.offsetMin = scrollRt.offsetMax = Vector2.zero;

        var catalogue = GameManager.Content;
        int shown = 0;

        if (catalogue != null)
            foreach (var product in catalogue.ShopProducts)
            {
                // Gold only. The relic-coin products belong to the other screen, and a
                // premium item priced at "0 gold" would be a free purchase button.
                if (product == null || product.goldCost <= 0L) continue;

                BuildRow(content, theme, product);
                shown++;
            }

        if (shown == 0)
        {
            var empty = UIFactory.Label(content, "The stall is bare today.", theme.fontSizeSmall,
                                         theme.textSecondary, TextAlignmentOptions.Center);
            var element = empty.gameObject.AddComponent<LayoutElement>();
            element.minHeight = element.preferredHeight = 40f;
        }
    }

    private void BuildRow(Transform parent, UITheme theme, ShopProduct product)
    {
        var row   = UIFactory.Panel(parent, $"Gold_{product.id}", theme.cardBg, false);
        var rowEl = row.AddComponent<LayoutElement>();
        rowEl.minHeight = rowEl.preferredHeight = 78f;

        var icon = GameManager.Content?.GetItemIcon(product.itemId);
        if (icon != null)
        {
            var image = UIFactory.Icon(row.transform, icon, 0f, "ProductIcon");
            UIFactory.At(image, 0.015f, 0.12f, 0.10f, 0.88f);
        }

        var name = UIFactory.Label(row.transform, product.DisplayName, theme.fontSizeSmall,
                                    theme.textPrimary, TextAlignmentOptions.BottomLeft);
        UIFactory.At(name, 0.12f, 0.52f, 0.66f, 0.92f);

        var description = UIFactory.Label(row.transform, product.description, theme.fontSizeLabel,
                                           theme.textSecondary, TextAlignmentOptions.TopLeft);
        description.textWrappingMode = TextWrappingModes.Normal;
        UIFactory.At(description, 0.12f, 0.08f, 0.66f, 0.50f);

        bool affordable = Gold >= product.goldCost;

        var cost = UIFactory.Label(row.transform,
                                    NumberFormatter.Format(product.goldCost) + " g",
                                    theme.fontSizeSmall,
                                    affordable ? theme.accentGold : theme.textDisabled,
                                    TextAlignmentOptions.MidlineRight);
        UIFactory.At(cost, 0.66f, 0.15f, 0.80f, 0.85f);

        var buy = UIFactory.Button(row.transform, "BUY",
                                    () => GameManager.Shop?.Purchase(product.id), width: 0f);
        UIFactory.At(buy, 0.82f, 0.18f, 0.985f, 0.82f);
        buy.interactable = affordable;
    }
}
