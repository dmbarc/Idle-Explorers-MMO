using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The right-click-style menu for an inventory item: Consume, Equip, Deposit, Drop.
///
/// Which options appear is derived from the item's own data — an item is consumable
/// because it declares an onConsume effect, and equippable because it declares an
/// equipSlot. Adding a new kind of item never means editing this screen.
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

    public override void Build()
    {
        var theme = UIManager.Theme;

        // Clicking anywhere else dismisses without acting
        var backdrop = UIFactory.Panel(transform, "Backdrop", new Color(0f, 0f, 0f, 0.35f), true);
        var backdropBtn = backdrop.AddComponent<Button>();
        backdropBtn.transition = Selectable.Transition.None;
        backdropBtn.onClick.AddListener(Close);

        var entry = CurrentEntry();
        if (SlotContainer.IsEmpty(entry)) { Close(); return; }

        var item = GameManager.Content?.GetItem(entry.itemId);
        if (item == null) { Close(); return; }

        var options = BuildOptions(item, entry);

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
            var btn = UIFactory.Button(stack.transform, label, action, width: 0f);
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

    // ── Options ───────────────────────────────────────────────────────────────

    private List<(string label, Action action)> BuildOptions(ItemData item, InventoryEntry entry)
    {
        var options = new List<(string, Action)>();
        bool inBank = SourceContainer == SlotContainerKind.Bank;

        // Banked items are not in hand, so they cannot be used or worn from there.
        if (!inBank)
        {
            if (item.IsConsumable)
                options.Add(("CONSUME", Consume));

            if (item.IsEquippable)
                options.Add(("EQUIP", Equip));
        }

        // Only offered when the bank is actually open — depositing from anywhere
        // else would be teleporting items into storage.
        if (IsBankOpen())
            options.Add((inBank ? "WITHDRAW" : "DEPOSIT", () => MoveToOtherStore(entry)));

        if (!inBank)
            options.Add(("DROP", () => Drop(entry)));

        options.Add(("CANCEL", Close));
        return options;
    }

    private static bool IsBankOpen() => FindAnyObjectByType<BankPanel>() != null;

    // ── Actions ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Every action closes this menu FIRST, then acts.
    ///
    /// Acting first is a trap: consuming a Mystic Gem pushes the AFK summary, so a
    /// Close() afterwards pops that screen instead of this one and the menu is left
    /// behind. Capturing the slot and dismissing up front makes the order safe no
    /// matter what an effect decides to open.
    /// </summary>
    private void Act(Action<int> action)
    {
        int slot = SourceSlot;
        if (slot < 0) { Close(); return; }

        Close();
        action(slot);
    }

    private void Consume()
    {
        string itemId = CurrentEntry()?.itemId;

        // A large Mystic Gem is worth real money and can be spent on an activity that
        // runs dry in minutes, so it gets a confirmation showing what it will actually
        // yield. Everything else — including the small gem — consumes straight away.
        Act(slot =>
        {
            if (GemConfirmModal.TryIntercept(itemId, slot)) return;

            // The resolver toasts its own reason when nothing could be applied.
            ItemEffectResolver.Consume(itemId, slot);
        });
    }

    private void Equip() => Act(slot => GameManager.Equipment?.Equip(slot));

    private void MoveToOtherStore(InventoryEntry entry)
    {
        var  bank     = GameManager.Bank;
        bool fromBank = SourceContainer == SlotContainerKind.Bank;
        long quantity = entry.quantity;

        if (bank == null) { Close(); return; }

        Act(slot =>
        {
            if (fromBank) bank.Withdraw(slot, quantity);
            else          bank.Deposit(slot, quantity);
        });
    }

    /// <summary>
    /// Puts the whole stack on the ground as a real world pickup, so walking back
    /// over it picks it up again. Reuses the loot prefab monsters already drop.
    /// </summary>
    private void Drop(InventoryEntry entry)
    {
        string itemId   = entry.itemId;
        long   quantity = entry.quantity;

        Act(slot =>
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
            dropObj.GetComponent<DropPickup>()?.Setup(itemId, quantity);

            GameManager.Inventory?.ClearSlot(slot);

            var item = GameManager.Content?.GetItem(itemId);
            GameEvents.FireToast($"Dropped {NumberFormatter.Format(quantity)} {item?.DisplayName ?? itemId}.");
        });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private InventoryEntry CurrentEntry()
    {
        var items = SourceContainer == SlotContainerKind.Bank
            ? GameManager.Bank?.Items
            : GameManager.Inventory?.Items;

        if (items == null || SourceSlot < 0 || SourceSlot >= items.Count) return null;
        return items[SourceSlot];
    }

    private void Close()
    {
        SourceSlot = -1;
        GameManager.UI?.Pop();
    }
}
