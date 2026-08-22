using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The account bank: 120 shared slots and a coin vault on the left, the active
/// character's 30 carried slots on the right, drag freely between them.
///
/// Cells are created once and only ever have their contents updated. Rebuilding
/// them per refresh destroyed the cell that was mid-drag, so its OnEndDrag never
/// ran and the drag ghost was orphaned on screen — the same bug the inventory had.
/// </summary>
public class BankPanel : UIScreen, ISlotPanel
{
    private const int BankColumns = 8;
    private const int InvColumns  = 5;

    /// <summary>Sits over the HUD rather than replacing it.</summary>
    public override bool IsOverlay => true;

    private readonly List<InventorySlotView> _bankSlots = new();
    private readonly List<InventorySlotView> _invSlots  = new();

    private RectTransform _bankGrid;
    private RectTransform _invGrid;

    private GameObject _tooltip;
    private TMP_Text   _tooltipName;
    private TMP_Text   _tooltipDesc;
    private TMP_Text   _tooltipMeta;

    private TMP_Text       _bankCapacityLabel;
    private TMP_Text       _bankCoinsLabel;
    private TMP_Text       _charCoinsLabel;
    private TMP_InputField _coinAmountField;
    private long           _coinAmount;

    private GameObject _dragGhost;

    public override void Build()
    {
        var theme = UIManager.Theme;

        UIFactory.Panel(transform, "Backdrop", theme.overlayBg, true);

        var panel = UIFactory.Panel(transform, "BankPanel", theme.panelBg, false);
        UIFactory.At(panel.transform, 0.06f, 0.08f, 0.94f, 0.93f);

        BuildHeader(panel.transform, theme);
        BuildBankSide(panel.transform, theme);
        BuildInventorySide(panel.transform, theme);
        BuildCoinRow(panel.transform, theme);
        BuildFooter(panel.transform, theme);
        BuildTooltip(theme);

        CreateSlots();
    }

    public override void OnShow()
    {
        GameEvents.OnBankChanged      += Refresh;
        GameEvents.OnInventoryChanged += Refresh;
        GameEvents.OnCoinsChanged     += OnCoinsChanged;
        Refresh();
    }

    public override void OnHide()
    {
        GameEvents.OnBankChanged      -= Refresh;
        GameEvents.OnInventoryChanged -= Refresh;
        GameEvents.OnCoinsChanged     -= OnCoinsChanged;

        HideTooltip();
        CancelDrag();   // closing mid-drag must not leave a ghost behind
    }

    // ── Layout ────────────────────────────────────────────────────────────────

    private void BuildHeader(Transform parent, UITheme theme)
    {
        var header = UIFactory.Panel(parent, "Header", theme.headerBg, false);
        UIFactory.At(header.transform, 0f, 0.93f, 1f, 1f);

        var title = UIFactory.Label(header.transform, "ACCOUNT BANK", theme.fontSizeBody,
                                     theme.accentGold, TextAlignmentOptions.MidlineLeft);
        UIFactory.At(title, 0.02f, 0f, 0.35f, 1f);

        var subtitle = UIFactory.Label(header.transform,
                                        "Shared by every character on this account",
                                        theme.fontSizeLabel, theme.textSecondary,
                                        TextAlignmentOptions.MidlineLeft);
        UIFactory.At(subtitle, 0.36f, 0f, 0.75f, 1f);

        _bankCapacityLabel = UIFactory.Label(header.transform, "", theme.fontSizeSmall,
                                              theme.textSecondary, TextAlignmentOptions.MidlineRight);
        UIFactory.At(_bankCapacityLabel, 0.76f, 0f, 0.97f, 1f);
    }

    private void BuildBankSide(Transform parent, UITheme theme)
    {
        var label = UIFactory.Label(parent, "VAULT", theme.fontSizeLabel,
                                     theme.accentGold, TextAlignmentOptions.MidlineLeft);
        UIFactory.At(label, 0.03f, 0.87f, 0.40f, 0.92f);

        // 120 slots do not fit on screen, so unlike the inventory this side scrolls.
        // The content needs a ContentSizeFitter or its height stays at whatever the
        // unset sizeDelta was and the grid clips.
        var (scroll, content) = UIFactory.ScrollView(parent, "BankScroll", vertical: true, horizontal: false);
        UIFactory.At(scroll, 0.02f, 0.22f, 0.58f, 0.865f);

        var grid = content.gameObject.AddComponent<GridLayoutGroup>();
        grid.cellSize        = new Vector2(theme.slotSize, theme.slotSize);
        grid.spacing         = new Vector2(theme.spacing, theme.spacing);
        grid.constraint      = GridLayoutGroup.Constraint.FixedColumnCount;
        grid.constraintCount = BankColumns;
        grid.childAlignment  = TextAnchor.UpperLeft;
        grid.padding         = new RectOffset(8, 8, 8, 8);

        var fitter = content.gameObject.AddComponent<ContentSizeFitter>();
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        _bankGrid = content;
    }

    private void BuildInventorySide(Transform parent, UITheme theme)
    {
        var label = UIFactory.Label(parent, "CARRIED", theme.fontSizeLabel,
                                     theme.accentGold, TextAlignmentOptions.MidlineLeft);
        UIFactory.At(label, 0.62f, 0.87f, 0.98f, 0.92f);

        // 30 slots at 5 columns always fit, so no scroll view here.
        var container = UIFactory.Panel(parent, "InvContainer", Color.clear, false, raycastTarget: false);
        UIFactory.At(container.transform, 0.60f, 0.22f, 0.98f, 0.865f);

        var gridGo = new GameObject("InvGrid", typeof(RectTransform), typeof(GridLayoutGroup));
        gridGo.transform.SetParent(container.transform, false);

        var grid = gridGo.GetComponent<GridLayoutGroup>();
        grid.cellSize        = new Vector2(theme.slotSize, theme.slotSize);
        grid.spacing         = new Vector2(theme.spacing, theme.spacing);
        grid.constraint      = GridLayoutGroup.Constraint.FixedColumnCount;
        grid.constraintCount = InvColumns;
        grid.childAlignment  = TextAnchor.UpperCenter;

        _invGrid = gridGo.GetComponent<RectTransform>();
        UIFactory.At(_invGrid, 0f, 0f, 1f, 1f);
    }

    private void BuildCoinRow(Transform parent, UITheme theme)
    {
        var row = UIFactory.Panel(parent, "CoinRow", theme.cardBg, false);
        UIFactory.At(row.transform, 0.02f, 0.09f, 0.98f, 0.20f);

        _bankCoinsLabel = UIFactory.Label(row.transform, "", theme.fontSizeSmall,
                                           theme.accentGold, TextAlignmentOptions.MidlineLeft);
        UIFactory.At(_bankCoinsLabel, 0.02f, 0.5f, 0.28f, 1f);

        _charCoinsLabel = UIFactory.Label(row.transform, "", theme.fontSizeSmall,
                                           theme.textSecondary, TextAlignmentOptions.MidlineLeft);
        UIFactory.At(_charCoinsLabel, 0.02f, 0f, 0.28f, 0.5f);

        _coinAmountField = UIFactory.InputField(row.transform, "Amount...",
                                                 OnCoinAmountChanged, width: 0f);
        UIFactory.At(_coinAmountField, 0.30f, 0.22f, 0.46f, 0.78f);
        _coinAmountField.contentType = TMP_InputField.ContentType.IntegerNumber;
        _coinAmountField.characterLimit = 19;

        var depositBtn = UIFactory.Button(row.transform, "DEPOSIT",
                                           () => MoveCoins(deposit: true, all: false), width: 0f);
        UIFactory.At(depositBtn, 0.48f, 0.22f, 0.60f, 0.78f);

        var withdrawBtn = UIFactory.Button(row.transform, "WITHDRAW",
                                            () => MoveCoins(deposit: false, all: false), width: 0f);
        UIFactory.At(withdrawBtn, 0.62f, 0.22f, 0.74f, 0.78f);

        var depositAllBtn = UIFactory.Button(row.transform, "DEP. ALL",
                                              () => MoveCoins(deposit: true, all: true), width: 0f);
        UIFactory.At(depositAllBtn, 0.76f, 0.22f, 0.87f, 0.78f);

        var withdrawAllBtn = UIFactory.Button(row.transform, "W/D ALL",
                                               () => MoveCoins(deposit: false, all: true), width: 0f);
        UIFactory.At(withdrawAllBtn, 0.88f, 0.22f, 0.98f, 0.78f);
    }

    private void BuildFooter(Transform parent, UITheme theme)
    {
        var depositAllItems = UIFactory.Button(parent, "DEPOSIT ALL ITEMS", () =>
        {
            int moved = GameManager.Bank?.DepositAllItems() ?? 0;
            GameEvents.FireToast(moved > 0
                ? $"Banked {moved} stack{(moved == 1 ? "" : "s")}."
                : "Nothing to bank.");
            Refresh();
        }, width: 0f);
        UIFactory.At(depositAllItems, 0.30f, 0.015f, 0.55f, 0.08f);

        var close = UIFactory.Button(parent, "CLOSE", () => GameManager.UI?.Pop(), width: 0f);
        UIFactory.At(close, 0.58f, 0.015f, 0.72f, 0.08f);
    }

    /// <summary>Creates every cell once. They are never destroyed afterwards.</summary>
    private void CreateSlots()
    {
        _bankSlots.Clear();
        _invSlots.Clear();

        for (int i = 0; i < BankManager.Capacity; i++)
            _bankSlots.Add(MakeSlot(_bankGrid, SlotContainerKind.Bank, i, $"BankSlot{i}"));

        for (int i = 0; i < InventoryManager.MaxSlots; i++)
            _invSlots.Add(MakeSlot(_invGrid, SlotContainerKind.Inventory, i, $"InvSlot{i}"));
    }

    private InventorySlotView MakeSlot(Transform parent, SlotContainerKind kind, int index, string name)
    {
        var slotGo = UIFactory.Slot(parent, name);
        var icon   = slotGo.transform.Find("Icon")?.GetComponent<Image>();
        var qty    = slotGo.transform.Find("Quantity")?.GetComponent<TMP_Text>();

        var view = slotGo.AddComponent<InventorySlotView>();
        view.Bind(kind, index, this, icon, qty);
        return view;
    }

    // ── Coins ─────────────────────────────────────────────────────────────────

    private void OnCoinAmountChanged(string raw)
    {
        // ContentType.IntegerNumber blocks non-digits, but a value past long.MaxValue
        // still fails to parse — treat that as zero rather than throwing.
        _coinAmount = long.TryParse(raw, out long parsed) ? System.Math.Max(0, parsed) : 0;
    }

    private void MoveCoins(bool deposit, bool all)
    {
        var bank = GameManager.Bank;
        if (bank == null) return;

        long amount = all
            ? (deposit ? GameManager.Inventory?.Coins ?? 0 : bank.Coins)
            : _coinAmount;

        if (amount <= 0)
        {
            GameEvents.FireToast(all ? "No coins to move." : "Enter an amount first.");
            return;
        }

        bool ok = deposit ? bank.DepositCoins(amount) : bank.WithdrawCoins(amount);
        if (!ok)
        {
            GameEvents.FireToast(deposit ? "Not enough coins carried." : "Not enough coins banked.");
            return;
        }

        if (_coinAmountField != null) _coinAmountField.text = "";
        _coinAmount = 0;
        Refresh();
    }

    private void OnCoinsChanged(long _) => RefreshCoins();

    private void RefreshCoins()
    {
        if (_bankCoinsLabel != null)
            _bankCoinsLabel.text = $"Vault:  ◈ {NumberFormatter.Format(GameManager.Bank?.Coins ?? 0)}";
        if (_charCoinsLabel != null)
            _charCoinsLabel.text = $"Carried: ◈ {NumberFormatter.Format(GameManager.Inventory?.Coins ?? 0)}";
    }

    // ── Tooltip ───────────────────────────────────────────────────────────────

    private void BuildTooltip(UITheme theme)
    {
        _tooltip = UIFactory.Panel(transform, "Tooltip", theme.cardBg, false);
        var rt = _tooltip.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(300f, 120f);
        rt.pivot     = new Vector2(0f, 1f);

        // Never let the tooltip intercept the pointer, or it flickers on and off as
        // it appears under the cursor it was triggered by.
        var group = _tooltip.AddComponent<CanvasGroup>();
        group.blocksRaycasts = false;
        group.interactable   = false;

        var vlg = _tooltip.AddComponent<VerticalLayoutGroup>();
        vlg.padding                = new RectOffset(10, 10, 8, 8);
        vlg.spacing                = 4f;
        vlg.childForceExpandWidth  = true;
        vlg.childForceExpandHeight = false;
        vlg.childControlWidth      = true;
        vlg.childControlHeight     = true;

        var fitter = _tooltip.AddComponent<ContentSizeFitter>();
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        _tooltipName = UIFactory.Label(_tooltip.transform, "", theme.fontSizeBody,
                                        theme.accentGold, TextAlignmentOptions.TopLeft);
        _tooltipDesc = UIFactory.Label(_tooltip.transform, "", theme.fontSizeSmall,
                                        theme.textPrimary, TextAlignmentOptions.TopLeft);
        _tooltipMeta = UIFactory.Label(_tooltip.transform, "", theme.fontSizeLabel,
                                        theme.textSecondary, TextAlignmentOptions.TopLeft);

        _tooltip.SetActive(false);
    }

    public void ShowSlotTooltip(SlotContainerKind container, int slotIndex, Vector2 screenPos)
    {
        var items = container == SlotContainerKind.Bank
            ? GameManager.Bank?.Items
            : GameManager.Inventory?.Items;

        if (items == null || slotIndex < 0 || slotIndex >= items.Count) return;

        var entry = items[slotIndex];
        if (SlotContainer.IsEmpty(entry)) { HideTooltip(); return; }

        var item = GameManager.Content?.GetItem(entry.itemId);
        if (item == null || _tooltip == null) return;

        _tooltipName.text = item.DisplayName;
        _tooltipDesc.text = item.description ?? "";
        _tooltipMeta.text = $"Quantity: {NumberFormatter.Format(entry.quantity)}\n" +
                            (container == SlotContainerKind.Bank ? "In the vault" : "Carried");

        _tooltip.SetActive(true);
        _tooltip.transform.SetAsLastSibling();

        if (RectTransformUtility.ScreenPointToLocalPointInRectangle(
                (RectTransform)transform, screenPos, null, out Vector2 local))
            _tooltip.GetComponent<RectTransform>().anchoredPosition = local + new Vector2(16f, -16f);
    }

    public void HideTooltip()
    {
        if (_tooltip != null) _tooltip.SetActive(false);
    }

    // ── Drag ghost (owned here, not by the cell) ──────────────────────────────

    public void BeginDrag(Sprite sprite, Vector2 screenPos)
    {
        HideTooltip();
        CancelDrag();

        var canvas = UIManager.DragCanvas != null ? UIManager.DragCanvas.transform : transform;

        _dragGhost = new GameObject("DragGhost", typeof(RectTransform), typeof(Image), typeof(CanvasGroup));
        _dragGhost.transform.SetParent(canvas, false);

        var rt = _dragGhost.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(UIManager.Theme.slotSize, UIManager.Theme.slotSize);
        rt.position  = screenPos;

        var img = _dragGhost.GetComponent<Image>();
        img.sprite         = sprite;
        img.preserveAspect = true;
        img.raycastTarget  = false;
        img.enabled        = sprite != null;

        var group = _dragGhost.GetComponent<CanvasGroup>();
        group.alpha          = 0.85f;
        group.blocksRaycasts = false;   // must not shadow the drop target
    }

    public void MoveDrag(Vector2 screenPos)
    {
        if (_dragGhost != null)
            _dragGhost.GetComponent<RectTransform>().position = screenPos;
    }

    public void CancelDrag()
    {
        if (_dragGhost == null) return;
        Destroy(_dragGhost);
        _dragGhost = null;
    }

    // ── Contents ──────────────────────────────────────────────────────────────

    /// <summary>Updates every cell in place — no GameObjects are created or destroyed.</summary>
    public void Refresh()
    {
        var bank = GameManager.Bank;
        var inv  = GameManager.Inventory;

        var bankItems = bank?.Items;
        var invItems  = inv?.Items;

        if (_bankCapacityLabel != null)
            _bankCapacityLabel.text = $"{bank?.UsedSlots ?? 0} / {BankManager.Capacity}";

        for (int i = 0; i < _bankSlots.Count; i++)
            _bankSlots[i].SetContents((bankItems != null && i < bankItems.Count) ? bankItems[i] : null);

        for (int i = 0; i < _invSlots.Count; i++)
            _invSlots[i].SetContents((invItems != null && i < invItems.Count) ? invItems[i] : null);

        RefreshCoins();
    }
}
