using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Screen 10: the inventory.
///
/// The 30 cells are created once and only ever have their contents updated.
/// Rebuilding them on every refresh destroyed the cell that was mid-drag, so its
/// OnEndDrag never ran and the drag ghost was orphaned on screen. The ghost is
/// owned by this panel for the same reason.
/// </summary>
public class InventoryPanel : UIScreen, ISlotPanel
{
    private const int Columns = 6;

    /// <summary>Sits over the HUD rather than replacing it.</summary>
    public override bool IsOverlay => true;

    private RectTransform _grid;
    private GameObject    _tooltip;
    private TMP_Text      _tooltipName;
    private TMP_Text      _tooltipDesc;
    private TMP_Text      _tooltipMeta;
    private TMP_Text      _capacityLabel;
    private TMP_Text      _coinsLabel;

    private readonly List<InventorySlotView> _slots = new();
    private GameObject _dragGhost;

    public override void Build()
    {
        var theme = UIManager.Theme;

        UIFactory.Panel(transform, "Backdrop", theme.overlayBg, true);

        var panel = UIFactory.Panel(transform, "InventoryPanel", theme.panelBg, false);
        UIFactory.At(panel.transform, 0.25f, 0.15f, 0.75f, 0.88f);

        BuildHeader(panel.transform, theme);
        BuildGrid(panel.transform, theme);
        BuildTooltip(theme);
        BuildCloseButton(panel.transform, theme);

        CreateSlots();
    }

    public override void OnShow()
    {
        GameEvents.OnInventoryChanged += Refresh;
        GameEvents.OnCoinsChanged     += OnCoinsChanged;
        Refresh();
    }

    public override void OnHide()
    {
        GameEvents.OnInventoryChanged -= Refresh;
        GameEvents.OnCoinsChanged     -= OnCoinsChanged;

        HideTooltip();
        CancelDrag();   // closing mid-drag must not leave a ghost behind
    }

    // ── Layout ────────────────────────────────────────────────────────────────

    private void BuildHeader(Transform parent, UITheme theme)
    {
        var header = UIFactory.Panel(parent, "Header", theme.headerBg, false);
        UIFactory.At(header.transform, 0f, 0.90f, 1f, 1f);

        var title = UIFactory.Label(header.transform, "INVENTORY", theme.fontSizeBody,
                                     theme.accentGold, TextAlignmentOptions.MidlineLeft);
        UIFactory.At(title, 0.03f, 0f, 0.40f, 1f);

        _coinsLabel = UIFactory.Label(header.transform, "", theme.fontSizeSmall,
                                       theme.accentGold, TextAlignmentOptions.MidlineRight);
        UIFactory.At(_coinsLabel, 0.42f, 0f, 0.72f, 1f);

        _capacityLabel = UIFactory.Label(header.transform, "", theme.fontSizeSmall,
                                          theme.textSecondary, TextAlignmentOptions.MidlineRight);
        UIFactory.At(_capacityLabel, 0.74f, 0f, 0.97f, 1f);
    }

    private void BuildGrid(Transform parent, UITheme theme)
    {
        // No ScrollView: 30 slots at 6 columns always fit, and the scroll content
        // was sized from an unset sizeDelta so the grid overflowed its viewport
        // and clipped the first column.
        var container = UIFactory.Panel(parent, "GridContainer", Color.clear, false, raycastTarget: false);
        UIFactory.At(container.transform, 0.03f, 0.13f, 0.97f, 0.88f);

        var gridGo = new GameObject("Grid", typeof(RectTransform), typeof(GridLayoutGroup));
        gridGo.transform.SetParent(container.transform, false);

        var grid = gridGo.GetComponent<GridLayoutGroup>();
        grid.cellSize        = new Vector2(theme.slotSize, theme.slotSize);
        grid.spacing         = new Vector2(theme.spacing, theme.spacing);
        grid.constraint      = GridLayoutGroup.Constraint.FixedColumnCount;
        grid.constraintCount = Columns;
        grid.childAlignment  = TextAnchor.UpperCenter;

        _grid = gridGo.GetComponent<RectTransform>();
        UIFactory.At(_grid, 0f, 0f, 1f, 1f);
    }

    private void BuildCloseButton(Transform parent, UITheme theme)
    {
        var btn = UIFactory.Button(parent, "CLOSE", () => GameManager.UI?.Pop(), width: 0f);
        UIFactory.At(btn, 0.35f, 0.02f, 0.65f, 0.11f);
    }

    /// <summary>Creates the 30 cells once. They are never destroyed afterwards.</summary>
    private void CreateSlots()
    {
        _slots.Clear();

        for (int i = 0; i < InventoryManager.MaxSlots; i++)
        {
            var slotGo = UIFactory.Slot(_grid, $"Slot{i}");
            var icon   = slotGo.transform.Find("Icon")?.GetComponent<Image>();
            var qty    = slotGo.transform.Find("Quantity")?.GetComponent<TMP_Text>();

            var view = slotGo.AddComponent<InventorySlotView>();
            view.Bind(SlotContainerKind.Inventory, i, this, icon, qty);
            _slots.Add(view);
        }
    }

    // ── Tooltip ───────────────────────────────────────────────────────────────

    private void BuildTooltip(UITheme theme)
    {
        _tooltip = UIFactory.Panel(transform, "Tooltip", theme.cardBg, false);
        var rt = _tooltip.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(300f, 120f);
        rt.pivot     = new Vector2(0f, 1f);

        // Never let the tooltip intercept the pointer — that is what made the old
        // inventory tooltip flicker on and off while hovering a slot.
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

    /// <summary>Called by a cell on hover. This panel only ever shows inventory cells.</summary>
    public void ShowSlotTooltip(SlotContainerKind container, int slotIndex, Vector2 screenPos)
    {
        var items = GameManager.Inventory?.Items;
        if (items == null || slotIndex < 0 || slotIndex >= items.Count) return;

        var entry = items[slotIndex];
        if (InventoryManager.IsEmpty(entry)) { HideTooltip(); return; }

        var item = GameManager.Content?.GetItem(entry.itemId);
        if (item == null || _tooltip == null) return;

        _tooltipName.text = item.DisplayName;
        _tooltipDesc.text = item.description ?? "";

        string meta = $"Quantity: {NumberFormatter.Format(entry.quantity)}";

        // Name the skill the requirement is actually measured against. "Requires
        // level 20" said nothing about WHICH level, and was enforced nowhere at all —
        // so it was both vague and false.
        if (item.levelReq > 0 && !string.IsNullOrEmpty(item.sourceSkill))
        {
            string reqSkill = GameManager.Content?.GetSkill(item.sourceSkill)?.DisplayName ?? item.sourceSkill;
            int    have     = GameManager.Skills?.GetSkillLevel(item.sourceSkill) ?? 1;
            meta += $"\nRequires {reqSkill} {item.levelReq}" + (have < item.levelReq ? $" (you are {have})" : "");
        }

        if (!string.IsNullOrEmpty(item.sourceSkill))
        {
            string skillName = GameManager.Content?.GetSkill(item.sourceSkill)?.DisplayName ?? item.sourceSkill;
            meta += $"\nSource: {skillName}";
        }
        _tooltipMeta.text = meta;

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

    /// <summary>Destroys the ghost if one exists. Safe to call at any time.</summary>
    public void CancelDrag()
    {
        if (_dragGhost == null) return;
        Destroy(_dragGhost);
        _dragGhost = null;
    }

    // ── Contents ──────────────────────────────────────────────────────────────

    private void OnCoinsChanged(long total) => RefreshCoins();

    private void RefreshCoins()
    {
        if (_coinsLabel != null)
            _coinsLabel.text = $"◈ {NumberFormatter.Format(GameManager.Inventory?.Coins ?? 0)}";
    }

    /// <summary>Updates every cell in place — no GameObjects are created or destroyed.</summary>
    public void Refresh()
    {
        var inventory = GameManager.Inventory;
        var items     = inventory?.Items;

        if (_capacityLabel != null)
            _capacityLabel.text = $"{inventory?.UsedSlots ?? 0} / {InventoryManager.MaxSlots}";

        RefreshCoins();

        for (int i = 0; i < _slots.Count; i++)
        {
            var entry = (items != null && i < items.Count) ? items[i] : null;
            _slots[i].SetContents(entry);
        }
    }
}
