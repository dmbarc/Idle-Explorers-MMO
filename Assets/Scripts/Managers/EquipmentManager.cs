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

    /// <summary>
    /// Every worn item, for effect resolution. Skips empty slots and — by default —
    /// broken ones, because a shattered helmet should not still be granting its stats.
    /// </summary>
    public IEnumerable<ItemData> EquippedItems(bool includeBroken = false)
    {
        var entries = Entries;
        if (entries == null) yield break;

        foreach (var e in entries)
        {
            if (e == null || string.IsNullOrEmpty(e.itemId)) continue;

            var item = GameManager.Content?.GetItem(e.itemId);
            if (item == null) continue;

            if (!includeBroken && IsBrokenEntry(e, item)) continue;

            yield return item;
        }
    }

    /// <summary>The raw entry for a slot, or null. Durability lives here, not on ItemData.</summary>
    public EquipmentEntry GetEntry(string slotId)
    {
        var entries = Entries;
        if (entries == null || string.IsNullOrEmpty(slotId)) return null;

        foreach (var e in entries)
            if (e != null && e.slotId == slotId && !string.IsNullOrEmpty(e.itemId))
                return e;
        return null;
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

        if (!MeetsRequirement(item, out string blocked))
        {
            GameEvents.FireToast(blocked);
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
        RestoreCondition(target, item);
        Changed();

        GameManager.Audio?.PlayEquip();
        GameEvents.FireToast($"Equipped {item.DisplayName}.");
        return true;
    }

    /// <summary>
    /// Whether the character may wear this item.
    ///
    /// levelReq was shown on the tooltip as "Requires level 20" and enforced nowhere,
    /// so a level 1 character could wear anything and the tooltip was simply untrue.
    /// It is measured against the item's own sourceSkill, which is what the number
    /// was authored to mean: bronze armour is levelReq 20 because bronze smithing is
    /// level 20. Anyone who made the armour already qualifies, so this only ever
    /// blocks gear acquired some other way before its time.
    ///
    /// An item with no sourceSkill cannot be gated by this and is always allowed.
    /// </summary>
    public static bool MeetsRequirement(ItemData item, out string reason)
    {
        reason = null;

        if (item == null || item.levelReq <= 0)         return true;
        if (string.IsNullOrEmpty(item.sourceSkill))     return true;

        int level = GameManager.Skills?.GetSkillLevel(item.sourceSkill) ?? 1;
        if (level >= item.levelReq) return true;

        string skill = GameManager.Content?.GetSkill(item.sourceSkill)?.DisplayName ?? item.sourceSkill;
        reason = $"Requires {skill} level {item.levelReq} (you are {level}).";
        return false;
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

        // Read the condition BEFORE the slot is cleared, or it goes with it.
        Remember(itemId, GetDurability(slotId));

        Set(slotId, null);
        GameManager.Inventory.AddItem(itemId, 1);
        Changed();

        var item = GameManager.Content?.GetItem(itemId);
        GameManager.Audio?.Play(Sfx.Unequip);
        GameEvents.FireToast($"Unequipped {item?.DisplayName ?? itemId}.");
        return true;
    }

    // ── Direct slot access (world effects, not player actions) ───────────────
    //
    // Equip/Unequip both move the item through the inventory, which is right for
    // something the player did. Set bonuses that hurl a piece of armour at an enemy
    // need it to leave the character WITHOUT touching the bag — the item's whole
    // journey is character → ground → character. These two are that seam, and they
    // deliberately do no capacity checking, because no container is involved.

    /// <summary>
    /// Removes what is in a slot and hands back its durability, so the piece can be
    /// reconstructed elsewhere in the same condition. Returns false for an empty slot.
    /// </summary>
    public bool TakeOff(string slotId, out int durability)
    {
        durability = 0;

        var entry = GetEntry(slotId);
        if (entry == null) return false;

        var item = GameManager.Content?.GetItem(entry.itemId);
        if (item != null && item.HasDurability)
        {
            Initialise(entry, item);
            durability = entry.durability;
        }

        Remember(entry.itemId, durability);

        entry.itemId        = null;
        entry.durability    = 0;
        entry.durabilitySet = false;

        Changed();
        return true;
    }

    /// <summary>
    /// Puts an item straight into a slot at a given condition. Refuses to overwrite an
    /// occupied slot — silently replacing gear here would destroy it, since nothing is
    /// holding the displaced item.
    /// </summary>
    public bool PutOn(string slotId, string itemId, int durability)
    {
        if (!EquipmentSlots.Exists(slotId) || string.IsNullOrEmpty(itemId)) return false;
        if (!string.IsNullOrEmpty(GetEquipped(slotId))) return false;

        var item = GameManager.Content?.GetItem(itemId);
        if (item == null) return false;

        Set(slotId, itemId);

        var entry = GetEntry(slotId);
        if (entry != null && item.HasDurability)
        {
            entry.durability    = Mathf.Clamp(durability, 0, item.maxDurability);
            entry.durabilitySet = true;
        }

        Changed();
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

    /// <summary>
    /// Writes a slot. Always clears durability, because the entry is per SLOT and the
    /// incoming item is not the one whose condition is recorded there — inheriting it
    /// would put a fresh helmet on at the shattered one's durability, or vice versa.
    /// </summary>
    private void Set(string slotId, string itemId)
    {
        var entries = Entries;
        if (entries == null) return;

        foreach (var e in entries)
        {
            if (e == null || e.slotId != slotId) continue;
            e.itemId        = itemId;
            e.durability    = 0;
            e.durabilitySet = false;
            return;
        }

        entries.Add(new EquipmentEntry { slotId = slotId, itemId = itemId });
    }

    // ── Condition of gear that is not being worn ─────────────────────────────
    //
    // The inventory carries quantities, not conditions, so a piece taken off has
    // nowhere to keep its wear. Remembering it on the character closes what would
    // otherwise be a free repair: unequip, re-equip, full durability.

    private static List<ItemDurability> Ledger
    {
        get
        {
            var ch = CharacterManager.Current;
            if (ch == null) return null;

            ch.storedDurability ??= new List<ItemDurability>();
            return ch.storedDurability;
        }
    }

    /// <summary>Brings a slot up to whatever condition its item was last seen in.</summary>
    private void RestoreCondition(string slotId, ItemData item)
    {
        if (item == null || !item.HasDurability) return;

        var entry = GetEntry(slotId);
        if (entry == null) return;

        entry.durability    = Recall(item);
        entry.durabilitySet = true;
    }

    /// <summary>Files the condition of a piece that has just left the character.</summary>
    private static void Remember(string itemId, int durability)
    {
        var ledger = Ledger;
        if (ledger == null || string.IsNullOrEmpty(itemId)) return;

        foreach (var record in ledger)
        {
            if (record == null || record.itemId != itemId) continue;
            record.durability = durability;
            return;
        }

        ledger.Add(new ItemDurability { itemId = itemId, durability = durability });
    }

    /// <summary>
    /// The condition a piece should come back at, or its maximum when nothing is on
    /// file — a newly smithed helmet has never been worn and starts full.
    /// </summary>
    private static int Recall(ItemData item)
    {
        if (item == null || !item.HasDurability) return 0;

        var ledger = Ledger;
        if (ledger != null)
        {
            foreach (var record in ledger)
                if (record != null && record.itemId == item.id)
                    return Mathf.Clamp(record.durability, 0, item.maxDurability);
        }

        return item.maxDurability;
    }

    private void Changed()
    {
        GameEvents.OnEquipmentChanged?.Invoke();
        GameManager.Save?.Save();
    }

    // ── Durability ────────────────────────────────────────────────────────────
    //
    // Armour wears out as it is used, and a worn-out piece stops working until it is
    // repaired. Two rules make that a mechanic rather than a punishment:
    //
    //   • Nothing is ever destroyed. A broken piece stays in its slot at 0 and its
    //     stats simply stop counting, so the worst outcome is a trip to the anvil.
    //   • maxDurability of 0 means indestructible. That is the default for everything
    //     authored before this existed, and for rings, trinkets and companions, which
    //     have no business degrading.

    /// <summary>Wear left on the piece in a slot. 0 for empty, indestructible or broken.</summary>
    public int GetDurability(string slotId)
    {
        var entry = GetEntry(slotId);
        if (entry == null) return 0;

        var item = GameManager.Content?.GetItem(entry.itemId);
        if (item == null || !item.HasDurability) return 0;

        Initialise(entry, item);
        return entry.durability;
    }

    public int GetMaxDurability(string slotId) =>
        GameManager.Content?.GetItem(GetEntry(slotId)?.itemId)?.maxDurability ?? 0;

    /// <summary>0-1 condition, or 1 for anything that does not wear out.</summary>
    public float GetCondition(string slotId)
    {
        int max = GetMaxDurability(slotId);
        return max <= 0 ? 1f : Mathf.Clamp01(GetDurability(slotId) / (float)max);
    }

    public bool IsBroken(string slotId)
    {
        var entry = GetEntry(slotId);
        if (entry == null) return false;

        var item = GameManager.Content?.GetItem(entry.itemId);
        return item != null && IsBrokenEntry(entry, item);
    }

    private static bool IsBrokenEntry(EquipmentEntry entry, ItemData item)
    {
        if (item == null || !item.HasDurability) return false;
        Initialise(entry, item);
        return entry.durability <= 0;
    }

    /// <summary>
    /// Fills in durability the first time a piece is looked at.
    ///
    /// A save written before durability existed deserializes to durability 0, which
    /// would read as broken — so the flag, not the number, decides whether the value
    /// has ever been written. Also re-clamps, in case content lowered the ceiling.
    /// </summary>
    private static void Initialise(EquipmentEntry entry, ItemData item)
    {
        if (!entry.durabilitySet)
        {
            entry.durability    = item.maxDurability;
            entry.durabilitySet = true;
            return;
        }

        if (entry.durability > item.maxDurability) entry.durability = item.maxDurability;
        if (entry.durability < 0)                  entry.durability = 0;
    }

    /// <summary>
    /// Wears down one specific slot. Returns the points actually lost — zero for an
    /// empty slot, an indestructible item, or one that was already broken.
    /// </summary>
    public int DamageSlot(string slotId, int points, bool announce = true)
    {
        if (points <= 0) return 0;

        var entry = GetEntry(slotId);
        if (entry == null) return 0;

        var item = GameManager.Content?.GetItem(entry.itemId);
        if (item == null || !item.HasDurability) return 0;

        Initialise(entry, item);
        if (entry.durability <= 0) return 0;

        int lost = Mathf.Min(points, entry.durability);
        entry.durability -= lost;

        bool broke = entry.durability <= 0;
        if (broke && announce)
        {
            GameManager.Audio?.PlayBreak();
            GameEvents.FireToast($"✖ Your {item.DisplayName} breaks.");
        }

        DurabilityChanged(statsChanged: broke);
        return lost;
    }

    /// <summary>Restores durability to one slot. Returns the points actually restored.</summary>
    public int RepairSlot(string slotId, int points)
    {
        var entry = GetEntry(slotId);
        if (entry == null || points <= 0) return 0;

        var item = GameManager.Content?.GetItem(entry.itemId);
        if (item == null || !item.HasDurability) return 0;

        Initialise(entry, item);

        int gained = Mathf.Min(points, item.maxDurability - entry.durability);
        if (gained <= 0) return 0;

        bool wasBroken = entry.durability <= 0;
        entry.durability += gained;

        DurabilityChanged(statsChanged: wasBroken);
        return gained;
    }

    /// <summary>
    /// Wears down ONE randomly chosen worn piece. Called when the player takes a hit.
    ///
    /// One piece rather than all of them: spreading a point across every slot means a
    /// full set degrades six times faster than a single item, which would punish
    /// exactly the players who did the most work to assemble one.
    /// </summary>
    public int DamageRandom(int points)
    {
        var candidates = new List<string>();

        foreach (var slot in EquipmentSlots.All)
        {
            var entry = GetEntry(slot.SlotId);
            if (entry == null) continue;

            var item = GameManager.Content?.GetItem(entry.itemId);
            if (item == null || !item.HasDurability) continue;

            Initialise(entry, item);
            if (entry.durability <= 0) continue;

            candidates.Add(slot.SlotId);
        }

        if (candidates.Count == 0) return 0;
        return DamageSlot(candidates[Random.Range(0, candidates.Count)], points);
    }

    /// <summary>Every worn slot id that can wear out, broken or not.</summary>
    public List<string> DurableSlots()
    {
        var result = new List<string>();

        foreach (var slot in EquipmentSlots.All)
        {
            var entry = GetEntry(slot.SlotId);
            if (entry == null) continue;

            var item = GameManager.Content?.GetItem(entry.itemId);
            if (item != null && item.HasDurability) result.Add(slot.SlotId);
        }
        return result;
    }

    /// <summary>Total points missing across everything worn — what a repair would cost.</summary>
    public int MissingDurability()
    {
        int missing = 0;
        foreach (var slotId in DurableSlots())
            missing += GetMaxDurability(slotId) - GetDurability(slotId);
        return missing;
    }

    /// <summary>Coins to restore everything to full. Zero when nothing needs it.</summary>
    public long RepairCost() => MissingDurability() * RepairCoinsPerPoint;

    /// <summary>
    /// Price of one point of repair.
    ///
    /// Deliberately cheap. Durability exists to make gear something you maintain, not
    /// a coin sink that competes with the rest of the economy — and an idle game where
    /// a long AFK session can leave you unable to afford to fight again is one nobody
    /// comes back to.
    /// </summary>
    public const int RepairCoinsPerPoint = 2;

    /// <summary>
    /// Repairs everything, charging coins. Returns false and changes nothing when the
    /// player cannot afford it — a partial repair that silently ate the whole wallet
    /// is the shape of bug this codebase keeps finding.
    /// </summary>
    public bool RepairAll()
    {
        long cost = RepairCost();
        if (cost <= 0)
        {
            GameEvents.FireToast("Nothing needs repairing.");
            return false;
        }

        var inventory = GameManager.Inventory;
        if (inventory == null) return false;

        if (!inventory.TrySpendCoins(cost))
        {
            GameEvents.FireToast($"Repairs cost {NumberFormatter.Format(cost)} coins.");
            return false;
        }

        int restored = 0;
        foreach (var slotId in DurableSlots())
            restored += RepairSlot(slotId, int.MaxValue);

        GameManager.Audio?.Play(Sfx.Repair);
        GameEvents.FireToast($"Repaired {restored} point(s) for {NumberFormatter.Format(cost)} coins.");
        return true;
    }

    /// <summary>
    /// Announces a durability change, and saves — eventually.
    ///
    /// Wear ticks on every hit taken, so neither of the obvious approaches works:
    /// firing OnEquipmentChanged each time would recompute every stat and redraw the
    /// whole rig several times a second in a fight, and saving each time would
    /// serialize and rewrite the account file just as often.
    ///
    /// So the expensive signal is reserved for the moment a piece actually breaks or
    /// comes back — the only points where the numbers really change — and the save is
    /// coalesced onto a timer. A crash can therefore lose a few seconds of wear, which
    /// is a trade worth making in the player's favour.
    /// </summary>
    private void DurabilityChanged(bool statsChanged)
    {
        GameEvents.OnDurabilityChanged?.Invoke();

        if (statsChanged)
        {
            // A break or a repair changes what the character's stats actually are.
            GameEvents.OnEquipmentChanged?.Invoke();
            _saveDueAt = 0f;
            GameManager.Save?.Save();
            return;
        }

        // Ordinary wear: schedule a write rather than performing one.
        if (_saveDueAt <= 0f) _saveDueAt = Time.time + SaveCoalesceSeconds;
    }

    private const float SaveCoalesceSeconds = 10f;
    private float _saveDueAt;

    void Update()
    {
        if (_saveDueAt > 0f && Time.time >= _saveDueAt) FlushPendingSave();
    }

    /// <summary>Writes out coalesced wear. Also runs on logout, via OnDisable.</summary>
    public void FlushPendingSave()
    {
        if (_saveDueAt <= 0f) return;
        _saveDueAt = 0f;
        GameManager.Save?.Save();
    }

    void OnDisable() => FlushPendingSave();

    // ── Stat aggregation ──────────────────────────────────────────────────────

    /// <summary>
    /// Summed flat bonus for a stat across everything worn, from onEquipPassive
    /// effects whose param names the stat — plus whatever active armour sets add.
    ///
    /// Broken pieces are excluded by EquippedItems, so a shattered helmet stops
    /// granting its health the moment it breaks and gets it back on repair.
    /// </summary>
    public float AggregateStat(string statId)
    {
        float total = SetBonusResolver.StatBonusFor(statId);

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
