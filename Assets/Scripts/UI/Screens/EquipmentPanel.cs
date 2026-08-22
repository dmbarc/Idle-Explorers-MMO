using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The paperdoll: 10 cosmetic slots on the left, 15 functional ones on the right.
///
/// Slots whose art SPUM cannot render are labelled rather than left to look broken —
/// a player who equips a tabard and sees nothing change deserves to be told why.
/// </summary>
public class EquipmentPanel : UIScreen
{
    /// <summary>Sits over the HUD rather than replacing it.</summary>
    public override bool IsOverlay => true;

    /// <summary>Every cell reads what is currently worn, so it rebuilds per open.</summary>
    public override bool RebuildOnShow => true;

    private readonly List<(string slotId, Image icon, TMP_Text label)> _cells = new();

    private GameObject _tooltip;
    private TMP_Text   _tooltipName;
    private TMP_Text   _tooltipBody;

    public override void Build()
    {
        var theme = UIManager.Theme;
        _cells.Clear();

        UIFactory.Panel(transform, "Backdrop", theme.overlayBg, true);

        var panel = UIFactory.Panel(transform, "EquipmentPanel", theme.panelBg, false);
        UIFactory.At(panel.transform, 0.14f, 0.08f, 0.86f, 0.93f);

        BuildHeader(panel.transform, theme);
        BuildColumn(panel.transform, theme, "COSMETIC", EquipmentSlots.Cosmetic(), 0.03f, 0.34f);
        BuildColumn(panel.transform, theme, "GEAR",     EquipmentSlots.Functional(), 0.52f, 0.97f);
        BuildTooltip(theme);

        var close = UIFactory.Button(panel.transform, "CLOSE", () => GameManager.UI?.Pop(), width: 0f);
        UIFactory.At(close, 0.38f, 0.02f, 0.62f, 0.08f);
    }

    public override void OnShow()
    {
        GameEvents.OnEquipmentChanged += Refresh;
        Refresh();
    }

    public override void OnHide()
    {
        GameEvents.OnEquipmentChanged -= Refresh;
        HideTooltip();
    }

    // ── Layout ────────────────────────────────────────────────────────────────

    private void BuildHeader(Transform parent, UITheme theme)
    {
        var header = UIFactory.Panel(parent, "Header", theme.headerBg, false);
        UIFactory.At(header.transform, 0f, 0.93f, 1f, 1f);

        var title = UIFactory.Label(header.transform, "EQUIPMENT", theme.fontSizeBody,
                                     theme.accentGold, TextAlignmentOptions.MidlineLeft);
        UIFactory.At(title, 0.03f, 0f, 0.40f, 1f);

        var hint = UIFactory.Label(header.transform, "Click a slot to remove what is in it",
                                    theme.fontSizeLabel, theme.textSecondary,
                                    TextAlignmentOptions.MidlineRight);
        UIFactory.At(hint, 0.41f, 0f, 0.97f, 1f);
    }

    private void BuildColumn(Transform parent, UITheme theme, string heading,
                             IEnumerable<EquipmentSlots.Slot> slots, float xMin, float xMax)
    {
        var label = UIFactory.Label(parent, heading, theme.fontSizeLabel,
                                     theme.accentGold, TextAlignmentOptions.MidlineLeft);
        UIFactory.At(label, xMin, 0.88f, xMax, 0.92f);

        var (scroll, content) = UIFactory.ScrollView(parent, $"{heading}Scroll");
        UIFactory.At(scroll, xMin, 0.10f, xMax, 0.875f);

        var vlg = content.gameObject.AddComponent<VerticalLayoutGroup>();
        vlg.spacing                = theme.spacing;
        vlg.padding                = new RectOffset(6, 6, 6, 6);
        vlg.childForceExpandWidth  = true;
        vlg.childForceExpandHeight = false;
        vlg.childControlWidth      = true;
        vlg.childControlHeight     = true;

        var fitter = content.gameObject.AddComponent<ContentSizeFitter>();
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        foreach (var slot in slots)
            BuildSlotRow(content, theme, slot);
    }

    private void BuildSlotRow(Transform parent, UITheme theme, EquipmentSlots.Slot slot)
    {
        var row = UIFactory.Panel(parent, $"Slot_{slot.SlotId}", theme.cardBg, false);
        var le  = row.AddComponent<LayoutElement>();
        le.minHeight = le.preferredHeight = 56f;

        var icon = UIFactory.Icon(row.transform, null, 40f, "EquippedIcon");
        UIFactory.At(icon, 0.02f, 0.12f, 0.14f, 0.88f);
        icon.enabled = false;

        var slotLabel = UIFactory.Label(row.transform, slot.DisplayName, theme.fontSizeLabel,
                                         theme.textSecondary, TextAlignmentOptions.MidlineLeft);
        UIFactory.At(slotLabel, 0.17f, 0.52f, 0.72f, 0.92f);

        var itemLabel = UIFactory.Label(row.transform, "empty", theme.fontSizeSmall,
                                         theme.textDisabled, TextAlignmentOptions.MidlineLeft);
        UIFactory.At(itemLabel, 0.17f, 0.10f, 0.72f, 0.50f);

        // Saying so up front beats letting a player equip a tabard, see nothing
        // change on their character, and conclude the game is broken.
        if (slot.IsCosmetic && !slot.RendersOnCharacter && slot.SlotId != "aura")
        {
            var pending = UIFactory.Label(row.transform, "art pending", theme.fontSizeLabel,
                                           theme.textDisabled, TextAlignmentOptions.MidlineRight);
            UIFactory.At(pending, 0.60f, 0.10f, 0.76f, 0.50f);
        }

        var removeBtn = UIFactory.Button(row.transform, "REMOVE",
                                          () => GameManager.Equipment?.Unequip(slot.SlotId), width: 0f);
        UIFactory.At(removeBtn, 0.78f, 0.15f, 0.98f, 0.85f);

        _cells.Add((slot.SlotId, icon, itemLabel));
    }

    // ── Tooltip ───────────────────────────────────────────────────────────────

    private void BuildTooltip(UITheme theme)
    {
        _tooltip = UIFactory.Panel(transform, "Tooltip", theme.cardBg, false);
        var rt = _tooltip.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(320f, 110f);
        rt.pivot     = new Vector2(0f, 1f);

        var group = _tooltip.AddComponent<CanvasGroup>();
        group.blocksRaycasts = false;
        group.interactable   = false;

        var vlg = _tooltip.AddComponent<VerticalLayoutGroup>();
        vlg.padding                = new RectOffset(10, 10, 8, 8);
        vlg.spacing                = 4f;
        vlg.childForceExpandWidth  = true;
        vlg.childControlWidth      = true;
        vlg.childControlHeight     = true;

        var fitter = _tooltip.AddComponent<ContentSizeFitter>();
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        _tooltipName = UIFactory.Label(_tooltip.transform, "", theme.fontSizeBody,
                                        theme.accentGold, TextAlignmentOptions.TopLeft);
        _tooltipBody = UIFactory.Label(_tooltip.transform, "", theme.fontSizeSmall,
                                        theme.textPrimary, TextAlignmentOptions.TopLeft);

        _tooltip.SetActive(false);
    }

    private void HideTooltip()
    {
        if (_tooltip != null) _tooltip.SetActive(false);
    }

    // ── Contents ──────────────────────────────────────────────────────────────

    private void Refresh()
    {
        var equipment = GameManager.Equipment;

        foreach (var (slotId, icon, label) in _cells)
        {
            var item = equipment?.GetEquippedItem(slotId);

            if (item == null)
            {
                if (icon != null) { icon.sprite = null; icon.enabled = false; }
                if (label != null)
                {
                    label.text  = "empty";
                    label.color = UIManager.Theme.textDisabled;
                }
                continue;
            }

            if (icon != null)
            {
                var sprite   = GameManager.Content?.GetItemIcon(item.id);
                icon.sprite  = sprite;
                icon.color   = Color.white;
                icon.enabled = sprite != null;
            }

            if (label != null)
            {
                label.text  = item.DisplayName;
                label.color = UIManager.Theme.textPrimary;
            }
        }
    }
}
