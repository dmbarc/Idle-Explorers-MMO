using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// The paperdoll: 11 cosmetic slots on the left, 15 functional ones on the right,
/// with a summary column for what all of it actually adds up to.
///
/// Every row shows the piece's condition, and hovering one opens the same tooltip the
/// inventory uses — so an item reads identically wherever you find it, including its
/// set list and which set bonuses it has earned.
///
/// Slots whose art SPUM cannot render are still labelled rather than left to look
/// broken. That list is much shorter than it was: reading the rig turned up layers for
/// shoulders, boots, legs and bracers that the code had never used.
/// </summary>
public class EquipmentPanel : UIScreen
{
    /// <summary>Sits over the HUD rather than replacing it.</summary>
    public override bool IsOverlay => true;

    /// <summary>Every cell reads what is currently worn, so it rebuilds per open.</summary>
    public override bool RebuildOnShow => true;

    private class Cell
    {
        public string   SlotId;
        public Image    Icon;
        public TMP_Text ItemLabel;
        public Image    DurabilityFill;
        public TMP_Text DurabilityLabel;
    }

    private readonly List<Cell> _cells = new();

    private ItemTooltip.Card _tooltip;
    private TMP_Text         _statsLabel;
    private TMP_Text         _setsLabel;
    private Button           _repairButton;
    private TMP_Text         _repairLabel;

    public override void Build()
    {
        var theme = UIManager.Theme;
        _cells.Clear();

        UIFactory.Panel(transform, "Backdrop", theme.overlayBg, true);

        var panel = UIFactory.Panel(transform, "EquipmentPanel", theme.panelBg, false);
        UIFactory.At(panel.transform, 0.06f, 0.06f, 0.94f, 0.94f);

        BuildHeader(panel.transform, theme);
        BuildColumn(panel.transform, theme, "COSMETIC", EquipmentSlots.Cosmetic(), 0.02f, 0.35f);
        BuildColumn(panel.transform, theme, "GEAR",     EquipmentSlots.Functional(), 0.36f, 0.69f);
        BuildSummary(panel.transform, theme, 0.70f, 0.98f);

        // Created last so it draws over the columns rather than under them.
        _tooltip = ItemTooltip.CreateCard(transform);

        var close = UIFactory.Button(panel.transform, "CLOSE", () => GameManager.UI?.Pop(), width: 0f);
        UIFactory.At(close, 0.38f, 0.015f, 0.62f, 0.075f);
    }

    public override void OnShow()
    {
        GameEvents.OnEquipmentChanged  += Refresh;
        GameEvents.OnDurabilityChanged += Refresh;
        GameEvents.OnCoinsChanged      += OnCoinsChanged;
        Refresh();
    }

    public override void OnHide()
    {
        GameEvents.OnEquipmentChanged  -= Refresh;
        GameEvents.OnDurabilityChanged -= Refresh;
        GameEvents.OnCoinsChanged      -= OnCoinsChanged;
        HideTooltip();
    }

    private void OnCoinsChanged(long _) => RefreshRepairButton();

    // ── Layout ────────────────────────────────────────────────────────────────

    private void BuildHeader(Transform parent, UITheme theme)
    {
        var header = UIFactory.Panel(parent, "Header", theme.headerBg, false);
        UIFactory.At(header.transform, 0f, 0.94f, 1f, 1f);

        var title = UIFactory.Label(header.transform, "EQUIPMENT", theme.fontSizeBody,
                                     theme.accentGold, TextAlignmentOptions.MidlineLeft);
        UIFactory.At(title, 0.02f, 0f, 0.30f, 1f);

        var hint = UIFactory.Label(header.transform, "Hover a slot for details  •  REMOVE takes it off",
                                    theme.fontSizeLabel, theme.textSecondary,
                                    TextAlignmentOptions.MidlineRight);
        UIFactory.At(hint, 0.31f, 0f, 0.98f, 1f);
    }

    private void BuildColumn(Transform parent, UITheme theme, string heading,
                             IEnumerable<EquipmentSlots.Slot> slots, float xMin, float xMax)
    {
        var label = UIFactory.Label(parent, heading, theme.fontSizeLabel,
                                     theme.accentGold, TextAlignmentOptions.MidlineLeft);
        UIFactory.At(label, xMin, 0.89f, xMax, 0.93f);

        // ScrollList, not ScrollView: the layout group and size fitter have to live on
        // the content itself or the rows overflow off the top with nothing to scroll.
        var (scroll, content) = UIFactory.ScrollList(parent, $"{heading}Scroll", theme.spacing);
        UIFactory.At(scroll, xMin, 0.09f, xMax, 0.885f);

        foreach (var slot in slots)
            BuildSlotRow(content, theme, slot);
    }

    private void BuildSlotRow(Transform parent, UITheme theme, EquipmentSlots.Slot slot)
    {
        var row = UIFactory.Panel(parent, $"Slot_{slot.SlotId}", theme.cardBg, false);
        var le  = row.AddComponent<LayoutElement>();
        le.minHeight = le.preferredHeight = 62f;

        var icon = UIFactory.Icon(row.transform, null, 40f, "EquippedIcon");
        UIFactory.At(icon, 0.02f, 0.14f, 0.16f, 0.86f);
        icon.enabled = false;

        var slotLabel = UIFactory.Label(row.transform, slot.DisplayName, theme.fontSizeLabel,
                                         theme.textSecondary, TextAlignmentOptions.MidlineLeft);
        UIFactory.At(slotLabel, 0.19f, 0.58f, 0.74f, 0.94f);

        var itemLabel = UIFactory.Label(row.transform, "empty", theme.fontSizeSmall,
                                         theme.textDisabled, TextAlignmentOptions.MidlineLeft);
        UIFactory.At(itemLabel, 0.19f, 0.26f, 0.74f, 0.58f);

        // Condition bar. Hidden entirely for anything that cannot wear out, so a ring
        // does not display an empty gauge it will never fill.
        var (barRoot, barFill) = UIFactory.ProgressBar(row.transform, "Durability", theme.accentGreen);
        UIFactory.At(barRoot.transform, 0.19f, 0.10f, 0.60f, 0.24f);
        barRoot.SetActive(false);

        var durLabel = UIFactory.Label(row.transform, "", theme.fontSizeLabel,
                                        theme.textSecondary, TextAlignmentOptions.MidlineLeft);
        UIFactory.At(durLabel, 0.61f, 0.06f, 0.76f, 0.26f);

        // Saying so up front beats letting a player equip a tabard, see nothing
        // change on their character, and conclude the game is broken.
        if (slot.IsCosmetic && !slot.RendersOnCharacter && slot.SlotId != "aura")
        {
            var pending = UIFactory.Label(row.transform, "art pending", theme.fontSizeLabel,
                                           theme.textDisabled, TextAlignmentOptions.MidlineRight);
            UIFactory.At(pending, 0.58f, 0.58f, 0.76f, 0.94f);
        }

        var removeBtn = UIFactory.Button(row.transform, "REMOVE",
                                          () => GameManager.Equipment?.Unequip(slot.SlotId), width: 0f);
        UIFactory.At(removeBtn, 0.78f, 0.20f, 0.98f, 0.80f);

        AttachHover(row, slot.SlotId);

        _cells.Add(new Cell
        {
            SlotId          = slot.SlotId,
            Icon            = icon,
            ItemLabel       = itemLabel,
            DurabilityFill  = barFill,
            DurabilityLabel = durLabel,
        });
    }

    /// <summary>
    /// Hover handling for a row. Uses an EventTrigger rather than a bespoke component
    /// because the row is a plain Image built by the factory, and one trigger per row
    /// is cheaper than a MonoBehaviour per row.
    /// </summary>
    private void AttachHover(GameObject row, string slotId)
    {
        var image = row.GetComponent<Image>();
        if (image != null) image.raycastTarget = true;

        var trigger = row.AddComponent<EventTrigger>();

        var enter = new EventTrigger.Entry { eventID = EventTriggerType.PointerEnter };
        enter.callback.AddListener(data => ShowSlotTooltip(slotId, ((PointerEventData)data).position));
        trigger.triggers.Add(enter);

        var exit = new EventTrigger.Entry { eventID = EventTriggerType.PointerExit };
        exit.callback.AddListener(_ => HideTooltip());
        trigger.triggers.Add(exit);
    }

    private void BuildSummary(Transform parent, UITheme theme, float xMin, float xMax)
    {
        var label = UIFactory.Label(parent, "TOTALS", theme.fontSizeLabel,
                                     theme.accentGold, TextAlignmentOptions.MidlineLeft);
        UIFactory.At(label, xMin, 0.89f, xMax, 0.93f);

        var (scroll, content) = UIFactory.ScrollList(parent, "SummaryScroll", theme.spacing);
        UIFactory.At(scroll, xMin, 0.09f, xMax, 0.885f);

        TextCard(content, theme, "FROM GEAR", out _statsLabel);

        // Repair. Lives here rather than at the anvil so a player whose armour breaks
        // mid-fight is never stranded a map away from the only place to fix it.
        _repairButton = UIFactory.Button(content, "REPAIR ALL", () =>
        {
            GameManager.Equipment?.RepairAll();
            Refresh();
        }, width: 0f);
        var repairLe = _repairButton.gameObject.AddComponent<LayoutElement>();
        repairLe.minHeight = repairLe.preferredHeight = 44f;

        _repairLabel = _repairButton.GetComponentInChildren<TMP_Text>();

        TextCard(content, theme, "SET BONUSES", out _setsLabel);
    }

    /// <summary>
    /// A card that grows to fit whatever text is put in it: heading above, body below.
    /// Both summary blocks vary in height with the character's gear, so neither can be
    /// anchored to a fixed fraction of the panel.
    /// </summary>
    private static GameObject TextCard(Transform parent, UITheme theme, string heading,
                                        out TMP_Text body)
    {
        var card = UIFactory.Panel(parent, $"{heading}Card", theme.cardBg, false);

        var vlg = card.AddComponent<VerticalLayoutGroup>();
        vlg.padding               = new RectOffset(10, 10, 8, 10);
        vlg.spacing               = 4f;
        vlg.childControlWidth     = true;
        vlg.childControlHeight    = true;
        vlg.childForceExpandWidth = true;

        var fitter = card.AddComponent<ContentSizeFitter>();
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        UIFactory.Label(card.transform, heading, theme.fontSizeLabel,
                        theme.textSecondary, TextAlignmentOptions.TopLeft);

        body = UIFactory.Label(card.transform, "", theme.fontSizeSmall,
                                theme.textPrimary, TextAlignmentOptions.TopLeft);

        return card;
    }

    // ── Tooltip ───────────────────────────────────────────────────────────────

    private void ShowSlotTooltip(string slotId, Vector2 screenPos)
    {
        var item = GameManager.Equipment?.GetEquippedItem(slotId);
        if (item == null) { HideTooltip(); return; }

        ItemTooltip.Show(_tooltip, (RectTransform)transform, item, screenPos,
                         quantity: 1, location: "Equipped", equippedSlotId: slotId);
    }

    private void HideTooltip() => _tooltip?.Hide();

    // ── Contents ──────────────────────────────────────────────────────────────

    private void Refresh()
    {
        var equipment = GameManager.Equipment;
        var theme     = UIManager.Theme;

        foreach (var cell in _cells)
        {
            var item = equipment?.GetEquippedItem(cell.SlotId);

            if (item == null)
            {
                if (cell.Icon != null) { cell.Icon.sprite = null; cell.Icon.enabled = false; }
                if (cell.ItemLabel != null)
                {
                    cell.ItemLabel.text  = "empty";
                    cell.ItemLabel.color = theme.textDisabled;
                }
                SetDurability(cell, 0, 0);
                continue;
            }

            if (cell.Icon != null)
            {
                var sprite    = GameManager.Content?.GetItemIcon(item.id);
                cell.Icon.sprite  = sprite;
                cell.Icon.color   = Color.white;
                cell.Icon.enabled = sprite != null;
            }

            if (cell.ItemLabel != null)
            {
                bool broken = equipment.IsBroken(cell.SlotId);
                cell.ItemLabel.text  = broken ? $"{item.DisplayName}  (broken)" : item.DisplayName;
                cell.ItemLabel.color = broken ? theme.hpFill : theme.textPrimary;
            }

            SetDurability(cell, equipment.GetDurability(cell.SlotId), item.maxDurability);
        }

        if (_statsLabel != null) _statsLabel.text = ItemTooltip.EquippedStatsSummary();
        if (_setsLabel  != null) _setsLabel.text  = ItemTooltip.ActiveSetsSummary();

        RefreshRepairButton();
    }

    private void SetDurability(Cell cell, int current, int max)
    {
        if (cell.DurabilityFill == null) return;

        var barRoot = cell.DurabilityFill.transform.parent.gameObject;

        if (max <= 0)
        {
            barRoot.SetActive(false);
            if (cell.DurabilityLabel != null) cell.DurabilityLabel.text = "";
            return;
        }

        float fraction = Mathf.Clamp01(current / (float)max);

        barRoot.SetActive(true);
        cell.DurabilityFill.fillAmount = fraction;
        cell.DurabilityFill.color = fraction <= 0f    ? UIManager.Theme.hpFill
                                  : fraction < 0.3f   ? UIManager.Theme.accentGold
                                  :                     UIManager.Theme.accentGreen;

        if (cell.DurabilityLabel != null)
            cell.DurabilityLabel.text = $"{current}/{max}";
    }

    private void RefreshRepairButton()
    {
        if (_repairButton == null) return;

        var equipment = GameManager.Equipment;
        long cost     = equipment?.RepairCost() ?? 0;
        long coins    = GameManager.Inventory?.Coins ?? 0;

        _repairButton.interactable = cost > 0 && coins >= cost;

        if (_repairLabel == null) return;

        _repairLabel.text = cost <= 0
            ? "ALL REPAIRED"
            : $"REPAIR ALL — {NumberFormatter.Format(cost)} coins";
    }
}
