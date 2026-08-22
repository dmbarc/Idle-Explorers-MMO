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
        var (scroll, content) = UIFactory.ScrollView(parent, "InventoryScroll");
        var scrollRt = scroll.GetComponent<RectTransform>();
        scrollRt.anchorMin = new Vector2(0.03f, 0.13f);
        scrollRt.anchorMax = new Vector2(0.97f, 0.88f);
        scrollRt.offsetMin = scrollRt.offsetMax = Vector2.zero;

        var grid = UIFactory.Grid(content, cols: 6, cellSize: theme.slotSize, spacing: theme.spacing);
        _grid = grid.GetComponent<RectTransform>();
        _grid.anchorMin = new Vector2(0f, 1f);
        _grid.anchorMax = new Vector2(1f, 1f);
        _grid.pivot     = new Vector2(0.5f, 1f);
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

    private void HideTooltip()
    {
        if (_tooltip != null) _tooltip.SetActive(false);
    }

    // ── Contents ──────────────────────────────────────────────────────────────

    private void Refresh()
    {
        if (_grid == null) return;

        foreach (var slot in _slotObjects)
            if (slot != null) Destroy(slot);
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

            if (items != null && i < items.Count)
            {
                var entry = items[i];
                var item  = GameManager.Content?.GetItem(entry.itemId);

                if (icon != null)
                {
                    var sprite   = GameManager.Content?.GetItemIcon(entry.itemId);
                    icon.sprite  = sprite;
                    icon.color   = Color.white;
                    icon.enabled = sprite != null;
                }
                if (qty != null) qty.text = NumberFormatter.Format(entry.quantity);

                AttachHover(slot, item, entry.quantity);
            }
            else
            {
                // Empty slot — hide the icon so the placeholder colour does not
                // read as a real item.
                if (icon != null) icon.enabled = false;
                if (qty  != null) qty.text = "";
            }
        }
    }

    private void AttachHover(GameObject slot, ItemData item, long quantity)
    {
        if (item == null) return;

        var trigger = slot.AddComponent<EventTrigger>();

        var enter = new EventTrigger.Entry { eventID = EventTriggerType.PointerEnter };
        enter.callback.AddListener(data => ShowTooltip(item, quantity, ((PointerEventData)data).position));
        trigger.triggers.Add(enter);

        var exit = new EventTrigger.Entry { eventID = EventTriggerType.PointerExit };
        exit.callback.AddListener(_ => HideTooltip());
        trigger.triggers.Add(exit);
    }
}
