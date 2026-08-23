using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// What the active character is wearing across the 25 slots.
///
/// Equipping swaps: whatever occupied the slot returns to the inventory, so gear is
/// never destroyed by putting something else on. When the inventory has no room for
/// the displaced item the swap is refused rather than silently dropping it.
/// </summary>
public class EquipmentManager : MonoBehaviour
{
    private List<EquipmentEntry> Entries
    {
        get
        {
            var ch = CharacterManager.Current;
            if (ch == null) return null;

            // Saves written before equipment existed have no list at all.
            ch.equipment ??= new List<EquipmentEntry>();
            return ch.equipment;
        }
    }

    // ── Queries ───────────────────────────────────────────────────────────────

    /// <summary>The itemId worn in a slot, or null.</summary>
    public string GetEquipped(string slotId)
    {
        var entries = Entries;
        if (entries == null || string.IsNullOrEmpty(slotId)) return null;

        foreach (var e in entries)
            if (e != null && e.slotId == slotId && !string.IsNullOrEmpty(e.itemId))
                return e.itemId;
        return null;
    }

    public ItemData GetEquippedItem(string slotId)
    {
        string itemId = GetEquipped(slotId);
        return string.IsNullOrEmpty(itemId) ? null : GameManager.Content?.GetItem(itemId);
    }

    /// <summary>Every worn item, for effect resolution. Skips empty slots.</summary>
    public IEnumerable<ItemData> EquippedItems()
    {
        var entries = Entries;
        if (entries == null) yield break;

        foreach (var e in entries)
        {
            if (e == null || string.IsNullOrEmpty(e.itemId)) continue;

            var item = GameManager.Content?.GetItem(e.itemId);
            if (item != null) yield return item;
        }
    }

    // ── Mutation ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Wears an item from an inventory slot. The slot is taken from the item's own
    /// equipSlot unless one is named explicitly (rings and trinkets have several).
    /// </summary>
    public bool Equip(int inventorySlot, string slotId = null)
    {
        var inv = GameManager.Inventory?.Items;
        if (inv == null || inventorySlot < 0 || inventorySlot >= inv.Count) return false;

        var entry = inv[inventorySlot];
        if (SlotContainer.IsEmpty(entry)) return false;

        var item = GameManager.Content?.GetItem(entry.itemId);
        if (item == null || !item.IsEquippable)
        {
            GameEvents.FireToast("That cannot be worn.");
            return false;
        }

        string target = string.IsNullOrEmpty(slotId) ? FirstFreeSlotFor(item) : slotId;
        if (!EquipmentSlots.Exists(target))
        {
            GameEvents.FireToast("No slot for that.");
            return false;
        }

        string displaced = GetEquipped(target);

        // Remove the incoming item first so the displaced one has somewhere to land
        // even when the inventory was otherwise full.
        if (!GameManager.Inventory.RemoveFromSlot(inventorySlot, 1)) return false;

        // Taking one off a stack of five does NOT free the slot, so "remove first"
        // only guarantees room when the incoming item was the last of its kind. With
        // a full bag and a stack of rings, the displaced item had nowhere to go and
        // was then overwritten by Set() below — destroyed outright. Put the incoming
        // item back and refuse instead.
        if (!string.IsNullOrEmpty(displaced) && !GameManager.Inventory.AddItem(displaced, 1))
        {
            GameManager.Inventory.AddItem(item.id, 1);
            GameEvents.FireToast("No room for the item you would be taking off.");
            return false;
        }

        Set(target, item.id);
        Changed();

        GameEvents.FireToast($"Equipped {item.DisplayName}.");
        return true;
    }

    /// <summary>Takes an item off and returns it to the inventory.</summary>
    public bool Unequip(string slotId)
    {
        string itemId = GetEquipped(slotId);
        if (string.IsNullOrEmpty(itemId)) return false;

        if (GameManager.Inventory?.CanAddItem(itemId) != true)
        {
            GameEvents.FireToast("No room to take that off.");
            return false;
        }

        Set(slotId, null);
        GameManager.Inventory.AddItem(itemId, 1);
        Changed();

        var item = GameManager.Content?.GetItem(itemId);
        GameEvents.FireToast($"Unequipped {item?.DisplayName ?? itemId}.");
        return true;
    }

    /// <summary>
    /// Which slot an item should go in. Multi-slot families (rings, amulets,
    /// trinkets) fill the first empty one so equipping a second ring does not
    /// silently replace the first.
    /// </summary>
    private string FirstFreeSlotFor(ItemData item)
    {
        string declared = item.equipSlot;

        // A slot id that exists verbatim is used as-is.
        if (EquipmentSlots.Exists(declared)) return declared;

        // Otherwise treat it as a family prefix: "ring" matches ring1..ring10.
        string firstMatch = null;
        foreach (var slot in EquipmentSlots.All)
        {
            if (!slot.SlotId.StartsWith(declared, System.StringComparison.Ordinal)) continue;

            firstMatch ??= slot.SlotId;
            if (string.IsNullOrEmpty(GetEquipped(slot.SlotId))) return slot.SlotId;
        }

        // Every slot in the family is occupied — replace the first.
        return firstMatch;
    }

    private void Set(string slotId, string itemId)
    {
        var entries = Entries;
        if (entries == null) return;

        foreach (var e in entries)
        {
            if (e == null || e.slotId != slotId) continue;
            e.itemId = itemId;
            return;
        }

        entries.Add(new EquipmentEntry { slotId = slotId, itemId = itemId });
    }

    private void Changed()
    {
        GameEvents.OnEquipmentChanged?.Invoke();
        GameManager.Save?.Save();
    }

    // ── Stat aggregation ──────────────────────────────────────────────────────

    /// <summary>
    /// Summed flat bonus for a stat across everything worn, from onEquipPassive
    /// effects whose param names the stat.
    /// </summary>
    public float AggregateStat(string statId)
    {
        float total = 0f;

        foreach (var item in EquippedItems())
        {
            if (item.effects == null) continue;

            foreach (var effect in item.effects)
            {
                if (effect == null) continue;
                if (effect.trigger != "onEquipPassive" || effect.action != "statBonus") continue;
                if (effect.param != statId) continue;

                total += effect.magnitude;
            }
        }

        return total;
    }
}
