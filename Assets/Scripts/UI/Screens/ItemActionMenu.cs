using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// The right-click-style menu for an inventory item: Consume, Equip, Deposit, Drop.
///
/// Which options appear is derived from the item's own data — an item is consumable
/// because it declares an onConsume effect, and equippable because it declares an
/// equipSlot. Adding a new kind of item never means editing this screen.
///
/// The option list is STATIC, and this menu is only one of the two things that read
/// it. The other is a double-click, which performs the first option without the
/// player ever seeing the list. One definition is what makes "double-click does the
/// first option" true by construction rather than by two lists agreeing with each
/// other.
/// </summary>
public class ItemActionMenu : UIScreen
{
    /// <summary>Set by the slot cell immediately before Push.</summary>
    public static SlotContainerKind SourceContainer;
    public static int               SourceSlot = -1;

    /// <summary>Where the cell was, so the card opens next to it.</summary>
    public static Vector2 ScreenPosition;

    /// <summary>Sits over whatever panel opened it.</summary>
    public override bool IsOverlay => true;

    /// <summary>Options depend on which item was clicked, so it rebuilds every time.</summary>
    public override bool RebuildOnShow => true;

    private const float CardWidth  = 220f;
    private const float RowHeight  = 40f;

    /// <summary>
    /// How long after opening a click on the backdrop still counts as the second half
    /// of a double-click. Windows' own default is 500ms; this is deliberately shorter,
    /// because guessing wrong here performs an action rather than merely missing a
    /// shortcut.
    /// </summary>
    private const float DoubleClickSeconds = 0.4f;

    private float _openedAt;

    public override void Build()
    {
        var theme = UIManager.Theme;

        _openedAt = Time.unscaledTime;

        // Clicking anywhere else dismisses without acting — unless it is the second
        // half of a double-click on the cell this menu belongs to. See OnBackdropClick.
        var backdrop = UIFactory.Panel(transform, "Backdrop", new Color(0f, 0f, 0f, 0.35f), true);
        backdrop.AddComponent<ItemActionBackdrop>().Owner = this;

        var entry = CurrentEntry();
        if (SlotContainer.IsEmpty(entry)) { Close(); return; }

        var item = GameManager.Content?.GetItem(entry.itemId);
        if (item == null) { Close(); return; }

        var options = OptionsFor(SourceContainer, SourceSlot);

        // CANCEL is appended here rather than living in OptionsFor. It is an
        // affordance of the menu, not something an item can have done to it — and in
        // the shared list it would be the "first option" for any item that offers
        // nothing else, making a double-click on such an item do nothing at all.
        options.Add(("CANCEL", null));

        // Header plus one row per option, sized to content so the card never has a
        // gap where a disabled option would have been.
        float height = 44f + options.Count * (RowHeight + theme.spacing) + theme.spacing;

        var card = UIFactory.Panel(transform, "ActionCard", theme.panelBg, false);
        var cardRt = card.GetComponent<RectTransform>();

        // Anchored to the parent's CENTRE, with a top-left pivot so the card hangs
        // down-right of the cursor. The anchor has to be the centre because
        // ScreenPointToLocalPointInRectangle returns centre-origin coordinates —
        // anchoring top-left while positioning with those put the card up and left
        // of the screen for any click below or left of centre, which is why it
        // rendered as a backdrop with nothing on it.
        cardRt.anchorMin = cardRt.anchorMax = new Vector2(0.5f, 0.5f);
        cardRt.pivot     = new Vector2(0f, 1f);
        cardRt.sizeDelta = new Vector2(CardWidth, height);
        PositionCard(cardRt);

        var title = UIFactory.Label(card.transform, item.DisplayName, theme.fontSizeSmall,
                                     theme.accentGold, TextAlignmentOptions.Center);
        var titleRt = title.GetComponent<RectTransform>();
        titleRt.anchorMin = new Vector2(0f, 1f);
        titleRt.anchorMax = new Vector2(1f, 1f);
        titleRt.pivot     = new Vector2(0.5f, 1f);
        titleRt.sizeDelta = new Vector2(0f, 34f);
        titleRt.anchoredPosition = new Vector2(0f, -6f);

        var stack = UIFactory.VStack(card.transform, theme.spacing, true, "Options");
        var stackRt = stack.GetComponent<RectTransform>();
        stackRt.anchorMin = new Vector2(0f, 0f);
        stackRt.anchorMax = new Vector2(1f, 1f);
        stackRt.offsetMin = new Vector2(8f, 8f);
        stackRt.offsetMax = new Vector2(-8f, -44f);

        foreach (var (label, action) in options)
        {
            var captured = action;
            var btn = UIFactory.Button(stack.transform, label, () => Act(captured), width: 0f);
            var le  = btn.gameObject.AddComponent<LayoutElement>();
            le.minHeight = le.preferredHeight = RowHeight;
        }
    }

    /// <summary>Keeps the card on screen when the clicked slot is near an edge.</summary>
    private void PositionCard(RectTransform cardRt)
    {
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                (RectTransform)transform, ScreenPosition, null, out Vector2 local))
            return;

        var canvasRect = ((RectTransform)transform).rect;
        float height   = cardRt.sizeDelta.y;

        // Both the anchor and this local point are centre-origin, and the card's
        // pivot is its top-left — so the card occupies [x, x+width] and [y-height, y].
        float maxX = canvasRect.xMax - CardWidth;
        float minY = canvasRect.yMin + height;

        // A card taller than the screen makes minY exceed yMax, and Mathf.Clamp with
        // min > max silently returns min — which would push it off the top. Prefer
        // the top edge and let the bottom overflow, so the title stays reachable.
        local.x = Mathf.Clamp(local.x + 12f, canvasRect.xMin, Mathf.Max(canvasRect.xMin, maxX));
        local.y = minY > canvasRect.yMax
            ? canvasRect.yMax
            : Mathf.Clamp(local.y - 12f, minY, canvasRect.yMax);

        cardRt.anchoredPosition = local;
    }

    // ── The option list ───────────────────────────────────────────────────────

    /// <summary>
    /// What can be done with the item in a slot, best first.
    ///
    /// The ORDER is the contract. Index 0 is what a double-click performs, so it has
    /// to be the thing a player would have picked anyway: a consumable is consumed,
    /// equipment is worn, a banked item comes back out, and a material — which can
    /// only ever be dropped — drops.
    ///
    /// The actions are raw: they act and nothing else. The menu wraps each one so it
    /// dismisses itself first, which a double-click has no need of.
    /// </summary>
    public static List<(string Label, Action Action)> OptionsFor(SlotContainerKind container, int slot)
    {
        var options = new List<(string, Action)>();

        var entry = EntryIn(container, slot);
        if (SlotContainer.IsEmpty(entry)) return options;

        var item = GameManager.Content?.GetItem(entry.itemId);
        if (item == null) return options;

        bool   inBank   = container == SlotContainerKind.Bank;
        string itemId   = entry.itemId;
        long   quantity = entry.quantity;

        // Banked items are not in hand, so they cannot be used or worn from there.
        if (!inBank)
        {
            if (item.IsConsumable)
                options.Add(("CONSUME", () => Consume(itemId, slot)));

            if (item.IsEquippable)
                options.Add(("EQUIP", () => GameManager.Equipment?.Equip(slot)));
        }

        // Only offered when the bank is actually open — depositing from anywhere
        // else would be teleporting items into storage.
        if (IsBankOpen())
            options.Add((inBank ? "WITHDRAW" : "DEPOSIT",
                         () => MoveToOtherStore(slot, quantity, inBank)));

        if (!inBank)
            options.Add(("DROP", () => Drop(slot, itemId, quantity)));

        return options;
    }

    /// <summary>
    /// Performs the first option for a slot. This is what a double-click does.
    ///
    /// Returns false when the item offers nothing, so a caller can stay quiet rather
    /// than report a success that did not happen.
    /// </summary>
    public static bool InvokePrimary(SlotContainerKind container, int slot)
    {
        var options = OptionsFor(container, slot);
        if (options.Count == 0 || options[0].Action == null) return false;

        options[0].Action();
        return true;
    }

    private static bool IsBankOpen() => FindAnyObjectByType<BankPanel>() != null;

    // ── Actions ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Every menu row dismisses this menu FIRST, then acts.
    ///
    /// Acting first is a trap: consuming a Mystic Gem pushes the AFK summary, so a
    /// Close() afterwards pops that screen instead of this one and the menu is left
    /// behind. Dismissing up front makes the order safe no matter what an effect
    /// decides to open.
    /// </summary>
    private void Act(Action action)
    {
        Close();
        action?.Invoke();
    }

    private static void Consume(string itemId, int slot)
    {
        // A large Mystic Gem is worth real money and can be spent on an activity that
        // runs dry in minutes, so it gets a confirmation showing what it will actually
        // yield. Everything else — including the small gem — consumes straight away.
        if (GemConfirmModal.TryIntercept(itemId, slot)) return;

        // The resolver toasts its own reason when nothing could be applied.
        ItemEffectResolver.Consume(itemId, slot);
    }

    private static void MoveToOtherStore(int slot, long quantity, bool fromBank)
    {
        var bank = GameManager.Bank;
        if (bank == null) return;

        if (fromBank) bank.Withdraw(slot, quantity);
        else          bank.Deposit(slot, quantity);
    }

    /// <summary>
    /// Puts the whole stack on the ground as a real world pickup, so walking back
    /// over it picks it up again. Reuses the loot prefab monsters already drop.
    /// </summary>
    private static void Drop(int slot, string itemId, long quantity)
    {
        var player = FindAnyObjectByType<PlayerController>();
        if (player == null)
        {
            GameEvents.FireToast("You can only drop things out in the world.");
            return;
        }

        var prefab = Resources.Load<GameObject>("ItemDrops/GenericDrop");
        if (prefab == null)
        {
            GameEvents.FireToast("Cannot drop that here.");
            return;
        }

        // Dropped in front of the player and slightly above the ground, so the
        // pickup trigger does not fire before they have stepped away from it.
        Vector3 at = player.transform.position + player.transform.forward * 1.5f + Vector3.up * 0.5f;
        var dropObj = Instantiate(prefab, at, Quaternion.identity);
        var pickup = dropObj.GetComponent<DropPickup>();

        // ══ MARKED, SO AUTO-MODE LEAVES IT ALONE ══════════════════════════════
        //
        // Set BEFORE Setup, which starts the toss and can put the drop inside the
        // player's own trigger on the first frame. Set after, and the item would be
        // collected again before the flag that forbids it was ever read.
        if (pickup != null) pickup.RequiresManualPickup = true;

        pickup?.Setup(itemId, quantity);

        GameManager.Inventory?.ClearSlot(slot);

        var item = GameManager.Content?.GetItem(itemId);
        GameEvents.FireToast($"Dropped {NumberFormatter.Format(quantity)} " +
                             $"{item?.DisplayName ?? itemId}. Click it to pick it back up.");
    }

    // ── Double-click ──────────────────────────────────────────────────────────

    /// <summary>
    /// A click on the backdrop: normally a dismissal, sometimes the second half of a
    /// double-click on the cell that opened this menu.
    ///
    /// ══ WHY THE CELL CANNOT DETECT ITS OWN DOUBLE-CLICK ═══════════════════════
    ///
    /// The obvious implementation — read eventData.clickCount on the cell — cannot
    /// work. The first click opens this menu, whose backdrop covers the screen, so the
    /// SECOND click never reaches the cell; it arrives here. Unity also resets
    /// clickCount to 1 whenever consecutive clicks land on different objects, so the
    /// count that arrives here is no help either.
    ///
    /// So the menu decides instead. It knows which cell it was opened for and when,
    /// and a raycast says what is under the cursor now. Two clicks on the same cell
    /// inside the window is a double-click by any definition, and the menu appearing
    /// for a moment in between is honest feedback rather than a glitch.
    /// </summary>
    internal void OnBackdropClick(PointerEventData eventData)
    {
        var container = SourceContainer;
        int slot      = SourceSlot;

        bool isDoubleClick = Time.unscaledTime - _openedAt <= DoubleClickSeconds &&
                             slot >= 0 &&
                             SlotUnder(eventData) is InventorySlotView view &&
                             view.Container == container && view.SlotIndex == slot;

        // Dismissed before acting, for the same reason the menu rows are.
        Close();

        if (isDoubleClick) InvokePrimary(container, slot);
    }

    /// <summary>The inventory or bank cell under the pointer, ignoring this menu.</summary>
    private static InventorySlotView SlotUnder(PointerEventData eventData)
    {
        if (EventSystem.current == null) return null;

        var hits = new List<RaycastResult>();
        EventSystem.current.RaycastAll(eventData, hits);

        foreach (var hit in hits)
        {
            if (hit.gameObject == null) continue;

            // In the parent, not on the object: a cell's icon and its quantity label
            // are children, and either may be what the ray actually struck.
            var view = hit.gameObject.GetComponentInParent<InventorySlotView>();
            if (view != null) return view;
        }

        return null;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private InventoryEntry CurrentEntry() => EntryIn(SourceContainer, SourceSlot);

    private static InventoryEntry EntryIn(SlotContainerKind container, int slot)
    {
        var items = container == SlotContainerKind.Bank
            ? GameManager.Bank?.Items
            : GameManager.Inventory?.Items;

        if (items == null || slot < 0 || slot >= items.Count) return null;
        return items[slot];
    }

    private void Close()
    {
        SourceSlot = -1;
        GameManager.UI?.Pop();
    }
}

/// <summary>
/// The full-screen dismissal layer behind the action card.
///
/// A Button was enough while its only job was to close the menu. It is not enough
/// now: Button.onClick hands over no PointerEventData, and the double-click check
/// needs the cursor position to raycast from.
/// </summary>
public class ItemActionBackdrop : MonoBehaviour, IPointerClickHandler
{
    public ItemActionMenu Owner;

    public void OnPointerClick(PointerEventData eventData)
    {
        if (Owner != null) Owner.OnBackdropClick(eventData);
    }
}
