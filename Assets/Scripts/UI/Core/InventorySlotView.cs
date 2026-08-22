using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// One inventory cell: hover tooltip plus drag-and-drop reordering.
///
/// The drag ghost is parented to UIManager.DragCanvas (sortingOrder 999) so it
/// draws above the inventory panel, and has raycasts disabled so it cannot
/// intercept the drop target underneath the cursor — the two mistakes that made
/// the old prefab-based inventory drag unusable.
/// </summary>
public class InventorySlotView : MonoBehaviour,
    IBeginDragHandler, IDragHandler, IEndDragHandler, IDropHandler,
    IPointerEnterHandler, IPointerExitHandler
{
    public int          SlotIndex { get; set; }
    public InventoryPanel Owner   { get; set; }

    private Image _icon;
    private GameObject _dragGhost;

    public void Bind(int slotIndex, InventoryPanel owner, Image icon)
    {
        SlotIndex = slotIndex;
        Owner     = owner;
        _icon     = icon;
    }

    private bool HasItem
    {
        get
        {
            var items = GameManager.Inventory?.Items;
            return items != null && SlotIndex < items.Count;
        }
    }

    // ── Tooltip ───────────────────────────────────────────────────────────────

    public void OnPointerEnter(PointerEventData eventData)
    {
        if (HasItem) Owner?.ShowTooltipForSlot(SlotIndex, eventData.position);
    }

    public void OnPointerExit(PointerEventData eventData) => Owner?.HideTooltip();

    // ── Drag ──────────────────────────────────────────────────────────────────

    public void OnBeginDrag(PointerEventData eventData)
    {
        if (!HasItem) return;

        Owner?.HideTooltip();

        // Dim the real icon so the cell reads as "lifted"
        if (_icon != null) _icon.color = new Color(1f, 1f, 1f, 0.35f);

        var canvas = UIManager.DragCanvas != null ? UIManager.DragCanvas.transform : transform.root;

        _dragGhost = new GameObject("DragGhost", typeof(RectTransform), typeof(Image), typeof(CanvasGroup));
        _dragGhost.transform.SetParent(canvas, false);

        var ghostRt = _dragGhost.GetComponent<RectTransform>();
        ghostRt.sizeDelta = new Vector2(UIManager.Theme.slotSize, UIManager.Theme.slotSize);

        var ghostImg = _dragGhost.GetComponent<Image>();
        ghostImg.sprite         = _icon != null ? _icon.sprite : null;
        ghostImg.preserveAspect = true;
        ghostImg.raycastTarget  = false;

        var group = _dragGhost.GetComponent<CanvasGroup>();
        group.alpha          = 0.85f;
        group.blocksRaycasts = false;   // must not shadow the drop target

        ghostRt.position = eventData.position;
    }

    public void OnDrag(PointerEventData eventData)
    {
        if (_dragGhost != null)
            _dragGhost.GetComponent<RectTransform>().position = eventData.position;
    }

    public void OnEndDrag(PointerEventData eventData)
    {
        if (_icon != null) _icon.color = Color.white;

        if (_dragGhost != null)
        {
            Destroy(_dragGhost);
            _dragGhost = null;
        }

        // OnDrop on the target does the actual move; this just refreshes in case
        // the drop landed on empty space.
        Owner?.Refresh();
    }

    /// <summary>Fires on the cell under the cursor when a drag is released.</summary>
    public void OnDrop(PointerEventData eventData)
    {
        var source = eventData.pointerDrag != null
            ? eventData.pointerDrag.GetComponent<InventorySlotView>()
            : null;

        if (source == null || source == this) return;

        GameManager.Inventory?.SwapOrStackSlots(source.SlotIndex, SlotIndex);
        Owner?.Refresh();
    }
}
