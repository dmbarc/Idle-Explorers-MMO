using UnityEngine;
using UnityEngine.UI;
using TMPro;
using UnityEngine.EventSystems;

public class InventorySlot : MonoBehaviour,
    IBeginDragHandler, IDragHandler, IEndDragHandler,
    IPointerDownHandler,
    IPointerEnterHandler, IPointerExitHandler
{
    public Image icon;
    public TMP_Text quantityText;
    public Sprite defaultSprite;

    [HideInInspector] public int slotIndex;
    [HideInInspector] public InventoryUI inventoryUI;

    public void SetItem(PlayerInventory.InventoryItem item)
    {
        if (item == null || item.IsEmpty())
        {
            icon.sprite = defaultSprite;
            icon.color = Color.white;
            quantityText.text = "";
            return;
        }
        icon.sprite = item.item.icon;
        icon.color = Color.white;
        quantityText.text = item.quantity >= 1 ? item.quantity.ToString() : "";
    }

    // ── Drag & Drop ──────────────────────────────────────────────────────────

    // Required so Unity's event system initiates drag-gesture tracking from this
    // object — without it, OnBeginDrag may never fire even when the user drags.
    public void OnPointerDown(PointerEventData eventData) { }

    public void OnBeginDrag(PointerEventData eventData)
    {
        if (!inventoryUI.SlotHasItem(slotIndex)) return;

        // Dim the real icon so the slot looks "lifted"
        icon.color = new Color(1f, 1f, 1f, 0.35f);

        // Tell InventoryUI to create and position the drag ghost
        inventoryUI.BeginDrag(slotIndex, icon.sprite, quantityText.text,
            GetComponent<RectTransform>().position);
    }

    public void OnDrag(PointerEventData eventData)
    {
        // The ghost (owned by InventoryUI) follows the cursor
        inventoryUI.OnDrag(eventData);
    }

    public void OnEndDrag(PointerEventData eventData)
    {
        // Restore icon opacity regardless of outcome
        icon.color = Color.white;

        InventorySlot target = null;
        if (eventData.pointerEnter != null)
            // GetComponentInParent walks up the hierarchy so dropping onto a child
            // element (e.g. the icon Image or quantity TMP_Text) still resolves to
            // the InventorySlot root, which is what we need for a valid swap.
            target = eventData.pointerEnter.GetComponentInParent<InventorySlot>();

        if (target != null && target.slotIndex != slotIndex)
            inventoryUI.EndDrag(slotIndex, target.slotIndex);
        else
            inventoryUI.CancelDrag();
    }

    // ── Tooltip ──────────────────────────────────────────────────────────────

    public void OnPointerEnter(PointerEventData eventData)
    {
        inventoryUI.ShowTooltip(slotIndex, eventData.position);
    }

    public void OnPointerExit(PointerEventData eventData)
    {
        inventoryUI.HideTooltip();
    }
}
