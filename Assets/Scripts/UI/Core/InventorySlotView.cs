using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// One inventory cell: hover tooltip plus drag-and-drop reordering.
///
/// The cell owns no drag state beyond its own icon tint — the ghost belongs to
/// InventoryPanel, so a refresh mid-drag cannot orphan it on screen.
/// </summary>
public class InventorySlotView : MonoBehaviour,
    IBeginDragHandler, IDragHandler, IEndDragHandler, IDropHandler,
    IPointerEnterHandler, IPointerExitHandler
{
    public int SlotIndex { get; private set; }

    private InventoryPanel _owner;
    private Image          _icon;
    private TMP_Text       _quantity;
    private bool           _isEmpty = true;

    public void Bind(int slotIndex, InventoryPanel owner, Image icon, TMP_Text quantity)
    {
        SlotIndex = slotIndex;
        _owner    = owner;
        _icon     = icon;
        _quantity = quantity;
    }

    /// <summary>Updates this cell's visuals. Pass null or an empty entry to blank it.</summary>
    public void SetContents(InventoryEntry entry)
    {
        _isEmpty = InventoryManager.IsEmpty(entry);

        if (_isEmpty)
        {
            if (_icon != null)
            {
                _icon.sprite  = null;
                _icon.enabled = false;
            }
            if (_quantity != null) _quantity.text = "";
            return;
        }

        if (_icon != null)
        {
            var sprite    = GameManager.Content?.GetItemIcon(entry.itemId);
            _icon.sprite  = sprite;
            _icon.color   = Color.white;
            _icon.enabled = sprite != null;
        }
        if (_quantity != null) _quantity.text = NumberFormatter.Format(entry.quantity);
    }

    // ── Tooltip ───────────────────────────────────────────────────────────────

    public void OnPointerEnter(PointerEventData eventData)
    {
        if (!_isEmpty) _owner?.ShowTooltipForSlot(SlotIndex, eventData.position);
    }

    public void OnPointerExit(PointerEventData eventData) => _owner?.HideTooltip();

    // ── Drag ──────────────────────────────────────────────────────────────────

    public void OnBeginDrag(PointerEventData eventData)
    {
        if (_isEmpty) return;

        // Dim the real icon so the cell reads as "lifted"
        if (_icon != null) _icon.color = new Color(1f, 1f, 1f, 0.35f);

        _owner?.BeginDrag(_icon != null ? _icon.sprite : null, eventData.position);
    }

    public void OnDrag(PointerEventData eventData) => _owner?.MoveDrag(eventData.position);

    public void OnEndDrag(PointerEventData eventData)
    {
        // Always runs, whether the drop landed on a cell or on empty space, so this
        // is the one reliable place to clear the ghost.
        if (_icon != null) _icon.color = Color.white;

        _owner?.CancelDrag();
        _owner?.Refresh();
    }

    /// <summary>Fires on the cell under the cursor when a drag is released.</summary>
    public void OnDrop(PointerEventData eventData)
    {
        var source = eventData.pointerDrag != null
            ? eventData.pointerDrag.GetComponent<InventorySlotView>()
            : null;

        if (source == null || source == this) return;

        // Works for empty targets too: the inventory is a fixed 30-slot list, so an
        // empty cell is a real slot that can receive a plain move.
        GameManager.Inventory?.SwapOrStackSlots(source.SlotIndex, SlotIndex);
        _owner?.Refresh();
    }
}
