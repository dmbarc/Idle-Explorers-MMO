using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;

public class InventoryUI : MonoBehaviour
{
    public GameObject inventoryPanel;
    public Transform slotsParent;
    public GameObject slotPrefab;

    public GameObject detailPopup;
    public Image detailIcon;
    public TMP_Text detailName;
    public TMP_Text detailDescription;
    public TMP_Text detailQuantity;
    public TMP_Text coinsText;

    private InventorySlot[] slots;
    private PlayerInventory inventory;

    // Drag state
    private GameObject currentDragIcon;
    private RectTransform dragIconRect;

    private void Start()
    {
        inventory = FindAnyObjectByType<PlayerInventory>();
        if (inventory == null)
        {
            Debug.LogError("InventoryUI: No PlayerInventory found in scene!");
            return;
        }

        // Subscribe so the UI auto-refreshes whenever the inventory changes
        inventory.OnInventoryChanged += RefreshUI;

        CreateSlots();
        RefreshUI();
        inventoryPanel.SetActive(false);
        if (detailPopup != null)
        {
            detailPopup.SetActive(false);
            // Prevent the tooltip from stealing pointer events from the slot beneath it,
            // which would cause the tooltip to flash on/off while hovering.
            var tooltipCanvasGroup = detailPopup.GetComponent<CanvasGroup>();
            if (tooltipCanvasGroup == null) tooltipCanvasGroup = detailPopup.AddComponent<CanvasGroup>();
            tooltipCanvasGroup.blocksRaycasts = false;
        }
    }

    private void OnDestroy()
    {
        if (inventory != null)
            inventory.OnInventoryChanged -= RefreshUI;
    }

    private void CreateSlots()
    {
        slots = new InventorySlot[inventory.maxSlots];
        for (int i = 0; i < inventory.maxSlots; i++)
        {
            var obj = Instantiate(slotPrefab, slotsParent);
            var slot = obj.GetComponent<InventorySlot>();
            slot.slotIndex = i;
            slot.inventoryUI = this;
            slots[i] = slot;
        }
    }

    public void RefreshUI()
    {
        if (slots == null) return;
        for (int i = 0; i < slots.Length; i++)
            slots[i].SetItem(inventory.items[i]);
        if (coinsText != null)
            coinsText.text = inventory.coins.ToString("N0");
    }

    public void ToggleInventory()
    {
        bool active = !inventoryPanel.activeSelf;
        inventoryPanel.SetActive(active);
        if (!active)
        {
            HideTooltip();
            CancelDrag();
        }
    }

    // ── Tooltip ──────────────────────────────────────────────────────────────

    public void ShowTooltip(int index, Vector2 screenPos)
    {
        if (detailPopup == null) return;

        if (index < 0 || index >= inventory.items.Count || inventory.items[index].item == null)
        {
            detailPopup.SetActive(false);
            return;
        }

        var item = inventory.items[index];
        detailIcon.sprite   = item.item.icon;
        detailName.text      = item.item.itemName;
        detailDescription.text = item.item.description ?? "";
        detailQuantity.text  = "Quantity: " + item.quantity;
        detailPopup.SetActive(true);

        // Position the popup near the cursor (offset so it doesn't obscure the slot)
        Canvas canvas = GetComponentInParent<Canvas>();
        if (canvas != null)
        {
            var popupRect = detailPopup.GetComponent<RectTransform>();
            if (popupRect != null && RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    canvas.GetComponent<RectTransform>(), screenPos,
                    canvas.worldCamera, out Vector2 localPos))
            {
                popupRect.anchoredPosition = localPos + new Vector2(12f, -12f);
            }
        }
    }

    public void HideTooltip()
    {
        if (detailPopup != null)
            detailPopup.SetActive(false);
    }

    // ── Drag & Drop ──────────────────────────────────────────────────────────

    public bool SlotHasItem(int index)
    {
        return index >= 0 && index < inventory.items.Count &&
               inventory.items[index].item != null;
    }

    /// <summary>Called by InventorySlot.OnBeginDrag. Creates the floating ghost icon.</summary>
    public void BeginDrag(int index, Sprite sprite, string qtyText, Vector3 startWorldPos)
    {
        HideTooltip();

        // Spawn the ghost as a sibling of the inventory panel so it renders on top
        currentDragIcon = Instantiate(slotPrefab, inventoryPanel.transform.parent);
        dragIconRect = currentDragIcon.GetComponent<RectTransform>();

        // Block raycasts on the ghost so it doesn't intercept drop-target detection
        var dragGhostCanvasGroup = currentDragIcon.GetComponent<CanvasGroup>();
        if (dragGhostCanvasGroup == null) dragGhostCanvasGroup = currentDragIcon.AddComponent<CanvasGroup>();
        dragGhostCanvasGroup.blocksRaycasts = false;
        dragGhostCanvasGroup.alpha = 0.85f;

        // Set ghost appearance
        var img = currentDragIcon.GetComponentInChildren<Image>();
        if (img) img.sprite = sprite;
        var txt = currentDragIcon.GetComponentInChildren<TMP_Text>();
        if (txt) txt.text = qtyText;

        // Start ghost at the slot's position
        dragIconRect.position = startWorldPos;
    }

    /// <summary>Called by InventorySlot.OnDrag. Moves the ghost to follow the cursor.</summary>
    public void OnDrag(PointerEventData eventData)
    {
        if (dragIconRect != null)
            dragIconRect.position = eventData.position;
    }

    /// <summary>Called when a drag ends on a valid target slot.</summary>
    public void EndDrag(int from, int to)
    {
        if (from != to)
            inventory.SwapOrStackItems(from, to);
        CancelDrag();
    }

    /// <summary>Destroys the ghost and refreshes the UI (called on cancel or after EndDrag).</summary>
    public void CancelDrag()
    {
        if (currentDragIcon != null)
        {
            Destroy(currentDragIcon);
            currentDragIcon = null;
            dragIconRect = null;
        }
        RefreshUI();
    }
}
