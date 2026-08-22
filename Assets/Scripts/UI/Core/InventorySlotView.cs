using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>Which backing store a slot cell reads and writes.</summary>
public enum SlotContainerKind
{
    Inventory,
    Bank,
}

/// <summary>
/// What a slot cell needs from the screen that owns it. The bank panel shows two
/// grids at once, so cells cannot assume their owner is the inventory panel.
/// </summary>
public interface ISlotPanel
{
    void ShowSlotTooltip(SlotContainerKind container, int slotIndex, Vector2 screenPos);
    void HideTooltip();
    void BeginDrag(Sprite sprite, Vector2 screenPos);
    void MoveDrag(Vector2 screenPos);
    void CancelDrag();
    void Refresh();
}

/// <summary>
/// One slot cell: hover tooltip plus drag-and-drop, within a container or between
/// the inventory and the bank.
///
/// The cell owns no drag state beyond its own icon tint — the ghost belongs to the
/// panel, so a refresh mid-drag cannot orphan it on screen.
/// </summary>
public class InventorySlotView : MonoBehaviour,
    IBeginDragHandler, IDragHandler, IEndDragHandler, IDropHandler,
    IPointerEnterHandler, IPointerExitHandler, IPointerClickHandler
{
    public int               SlotIndex { get; private set; }
    public SlotContainerKind Container { get; private set; }

    private ISlotPanel _owner;
    private Image      _icon;
    private TMP_Text   _quantity;
    private bool       _isEmpty = true;

    public void Bind(SlotContainerKind container, int slotIndex, ISlotPanel owner,
                     Image icon, TMP_Text quantity)
    {
        Container = container;
        SlotIndex = slotIndex;
        _owner    = owner;
        _icon     = icon;
        _quantity = quantity;
    }

    /// <summary>Updates this cell's visuals. Pass null or an empty entry to blank it.</summary>
    public void SetContents(InventoryEntry entry)
    {
        _isEmpty = SlotContainer.IsEmpty(entry);

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
        if (!_isEmpty) _owner?.ShowSlotTooltip(Container, SlotIndex, eventData.position);
    }

    public void OnPointerExit(PointerEventData eventData) => _owner?.HideTooltip();

    // ── Click ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Opens the item action menu. Which options it offers comes from the item's own
    /// data, so this does not need to know what kind of item was clicked.
    /// </summary>
    public void OnPointerClick(PointerEventData eventData)
    {
        // A drag that ends over its own cell also raises a click. Acting on it would
        // pop the menu every time a drag was cancelled.
        if (eventData.dragging || _isEmpty) return;

        _owner?.HideTooltip();

        ItemActionMenu.SourceContainer = Container;
        ItemActionMenu.SourceSlot      = SlotIndex;
        ItemActionMenu.ScreenPosition  = eventData.position;
        GameManager.UI?.Push<ItemActionMenu>();
    }

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

        // Empty targets work too: both containers are fixed-length lists, so an empty
        // cell is a real slot that can receive a plain move.
        if (source.Container == Container)
        {
            if (Container == SlotContainerKind.Inventory)
                GameManager.Inventory?.SwapOrStackSlots(source.SlotIndex, SlotIndex);
            else
                GameManager.Bank?.SwapOrStackSlots(source.SlotIndex, SlotIndex);
        }
        else if (Container == SlotContainerKind.Bank)
        {
            GameManager.Bank?.DepositSlot(source.SlotIndex, SlotIndex);
        }
        else
        {
            GameManager.Bank?.WithdrawSlot(source.SlotIndex, SlotIndex);
        }

        _owner?.Refresh();
    }
}
