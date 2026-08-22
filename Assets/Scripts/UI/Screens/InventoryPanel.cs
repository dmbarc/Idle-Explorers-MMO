using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// Screen 10: the inventory.
///
/// POC scope: read-only grid with hover tooltips. Drag-and-drop is deferred —
/// InventoryManager.SwapOrStackSlots already exists for it, so wiring it later
/// is additive.
/// </summary>
public class InventoryPanel : UIScreen
{
    private RectTransform _grid;
    private GameObject    _tooltip;
    private TMP_Text      _tooltipName;
    private TMP_Text      _tooltipDesc;
    private TMP_Text      _tooltipMeta;
    private TMP_Text      _capacityLabel;

    private readonly List<GameObject> _slotObjects = new();

    private const int Columns = 6;

    /// <summary>Sits over the HUD rather than replacing it.</summary>
    public override bool IsOverlay => true;

    public override void Build()
    {
        var theme = UIManager.Theme;

        UIFactory.Panel(transform, "Backdrop", theme.overlayBg, true);

        var panel   = UIFactory.Panel(transform, "InventoryPanel", theme.panelBg, false);
        var panelRt = panel.GetComponent<RectTransform>();
        panelRt.anchorMin = new Vector2(0.25f, 0.15f);
        panelRt.anchorMax = new Vector2(0.75f, 0.88f);
        panelRt.offsetMin = panelRt.offsetMax = Vector2.zero;

        BuildHeader(panel.transform, theme);
        BuildGrid(panel.transform, theme);
        BuildTooltip(theme);
        BuildCloseButton(panel.transform, theme);
    }

    public override void OnShow()
    {
        GameEvents.OnInventoryChanged += Refresh;
        Refresh();
    }

    public override void OnHide()
    {
        GameEvents.OnInventoryChanged -= Refresh;
        HideTooltip();
    }

    // ── Layout ────────────────────────────────────────────────────────────────

    private void BuildHeader(Transform parent, UITheme theme)
    {
        var header   = UIFactory.Panel(parent, "Header", theme.headerBg, false);
        var headerRt = header.GetComponent<RectTransform>();
        headerRt.anchorMin = new Vector2(0f, 0.90f);
        headerRt.anchorMax = new Vector2(1f, 1f);
        headerRt.offsetMin = headerRt.offsetMax = Vector2.zero;

        var title = UIFactory.Label(header.transform, "INVENTORY",
                                     theme.fontSizeBody, theme.accentGold, TextAlignmentOptions.MidlineLeft);
        var titleRt = title.GetComponent<RectTransform>();
        titleRt.anchorMin = new Vector2(0.03f, 0f);
        titleRt.anchorMax = new Vector2(0.60f, 1f);
        titleRt.offsetMin = titleRt.offsetMax = Vector2.zero;

        _capacityLabel = UIFactory.Label(header.transform, "",
                                          theme.fontSizeSmall, theme.textSecondary, TextAlignmentOptions.MidlineRight);
        var capRt = _capacityLabel.GetComponent<RectTransform>();
        capRt.anchorMin = new Vector2(0.60f, 0f);
        capRt.anchorMax = new Vector2(0.88f, 1f);
        capRt.offsetMin = capRt.offsetMax = Vector2.zero;
    }

    private void BuildGrid(Transform parent, UITheme theme)
    {
        // No ScrollView: 30 slots at 6 columns always fit, and the scroll content
        // was sized from an unset sizeDelta so the grid overflowed its viewport and
        // the first column was clipped off the left edge.
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
        var rt  = btn.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.35f, 0.02f);
        rt.anchorMax = new Vector2(0.65f, 0.11f);
        rt.offsetMin = rt.offsetMax = Vector2.zero;
        rt.sizeDelta = Vector2.zero;
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

    private void ShowTooltip(ItemData item, long quantity, Vector2 screenPos)
    {
        if (_tooltip == null || item == null) return;

        _tooltipName.text = item.DisplayName;
        _tooltipDesc.text = item.description ?? "";

        string meta = $"Quantity: {NumberFormatter.Format(quantity)}";
        if (item.levelReq > 0)      meta += $"\nRequires level {item.levelReq}";
        if (!string.IsNullOrEmpty(item.sourceSkill))
        {
            string skillName = GameManager.Content?.GetSkill(item.sourceSkill)?.DisplayName ?? item.sourceSkill;
            meta += $"\nSource: {skillName}";
        }
        _tooltipMeta.text = meta;

        _tooltip.SetActive(true);
        _tooltip.transform.SetAsLastSibling();

        var rt = _tooltip.GetComponent<RectTransform>();
        if (RectTransformUtility.ScreenPointToLocalPointInRectangle(
                (RectTransform)transform, screenPos, null, out Vector2 local))
            rt.anchoredPosition = local + new Vector2(16f, -16f);
    }

    public void HideTooltip()
    {
        if (_tooltip != null) _tooltip.SetActive(false);
    }

    // ── Contents ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Rebuilds every cell. Cheap at 30 slots, and it keeps slot indices and the
    /// underlying list in exact agreement after a drag reorder.
    /// </summary>
    public void Refresh()
    {
        if (_grid == null) return;

        foreach (var slot in _slotObjects)
            if (slot != null) DestroyImmediate(slot);
        _slotObjects.Clear();

        var items = GameManager.Inventory?.Items;
        int used  = items?.Count ?? 0;

        if (_capacityLabel != null)
            _capacityLabel.text = $"{used} / {InventoryManager.MaxSlots}";

        for (int i = 0; i < InventoryManager.MaxSlots; i++)
        {
            var slot = UIFactory.Slot(_grid, $"Slot{i}");
            _slotObjects.Add(slot);

            var icon = slot.transform.Find("Icon")?.GetComponent<Image>();
            var qty  = slot.transform.Find("Quantity")?.GetComponent<TMP_Text>();

            // The cell itself must be a raycast target or it can never be a drop
            // target; UIFactory.Slot leaves the root image raycastable.
            var view = slot.AddComponent<InventorySlotView>();
            view.Bind(i, this, icon);

            bool filled = items != null && i < items.Count;
            if (filled)
            {
                var entry = items[i];

                if (icon != null)
                {
                    var sprite   = GameManager.Content?.GetItemIcon(entry.itemId);
                    icon.sprite  = sprite;
                    icon.color   = Color.white;
                    icon.enabled = sprite != null;
                }
                if (qty != null) qty.text = NumberFormatter.Format(entry.quantity);
            }
            else
            {
                if (icon != null) icon.enabled = false;
                if (qty  != null) qty.text = "";
            }
        }
    }

    /// <summary>Called by a cell on hover. Looks the item up by slot index.</summary>
    public void ShowTooltipForSlot(int slotIndex, Vector2 screenPos)
    {
        var items = GameManager.Inventory?.Items;
        if (items == null || slotIndex < 0 || slotIndex >= items.Count) return;

        var entry = items[slotIndex];
        ShowTooltip(GameManager.Content?.GetItem(entry.itemId), entry.quantity, screenPos);
    }
}
