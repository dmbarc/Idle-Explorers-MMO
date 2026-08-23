using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Armour sets: which pieces you own, which you are wearing, and which bonuses that
/// earns you.
///
/// Static and stateless — everything is derived from equipment and inventory on
/// demand rather than cached, because both change from a dozen places and a stale
/// cache would hand out a set bonus for gear the player is no longer wearing. The
/// counts are over at most a handful of items, so recomputing costs nothing.
/// </summary>
public static class ItemSetManager
{
    /// <summary>Where one piece of a set currently is. Drives the tooltip colours.</summary>
    public enum PieceState
    {
        /// <summary>Not owned at all — grey.</summary>
        Missing,
        /// <summary>In the bag or the bank but not worn — yellow.</summary>
        Carried,
        /// <summary>Worn — green.</summary>
        Equipped,
    }

    // ── Lookup ────────────────────────────────────────────────────────────────

    public static ItemSetData GetSet(string setId)
    {
        if (string.IsNullOrEmpty(setId)) return null;

        var sets = GameManager.Content?.ItemSets;
        if (sets == null) return null;

        return sets.TryGetValue(setId, out var set) ? set : null;
    }

    /// <summary>The set an item belongs to, or null.</summary>
    public static ItemSetData SetFor(ItemData item) => GetSet(item?.setId);

    // ── Counting ──────────────────────────────────────────────────────────────

    /// <summary>Where a specific piece is right now.</summary>
    public static PieceState StateOf(string itemId)
    {
        if (string.IsNullOrEmpty(itemId)) return PieceState.Missing;

        var equipment = GameManager.Equipment;
        if (equipment != null)
        {
            foreach (var slot in EquipmentSlots.All)
                if (equipment.GetEquipped(slot.SlotId) == itemId)
                    return PieceState.Equipped;
        }

        // The bank counts as owning it. A player who banked their boots has the boots;
        // telling them otherwise would send them off to smith a second pair.
        if ((GameManager.Inventory?.GetQuantity(itemId) ?? 0) > 0) return PieceState.Carried;
        if ((GameManager.Bank?.GetQuantity(itemId)      ?? 0) > 0) return PieceState.Carried;

        return PieceState.Missing;
    }

    /// <summary>
    /// How many of a set's pieces are worn.
    ///
    /// A broken piece does NOT count. Set bonuses are the payoff for maintaining a
    /// full set, and letting shattered armour keep them would make durability
    /// decorative for exactly the players it is aimed at.
    /// </summary>
    public static int EquippedCount(ItemSetData set)
    {
        if (set?.itemIds == null) return 0;

        var equipment = GameManager.Equipment;
        if (equipment == null) return 0;

        int count = 0;
        foreach (var itemId in set.itemIds)
        {
            if (string.IsNullOrEmpty(itemId)) continue;

            foreach (var slot in EquipmentSlots.All)
            {
                if (equipment.GetEquipped(slot.SlotId) != itemId) continue;
                if (equipment.IsBroken(slot.SlotId))              continue;

                count++;
                break;
            }
        }
        return count;
    }

    /// <summary>Every set with at least one unbroken piece worn, and how many.</summary>
    public static List<(ItemSetData set, int worn)> ActiveSets()
    {
        var results = new List<(ItemSetData, int)>();

        var sets = GameManager.Content?.ItemSets;
        if (sets == null) return results;

        foreach (var set in sets.Values)
        {
            int worn = EquippedCount(set);
            if (worn > 0) results.Add((set, worn));
        }
        return results;
    }

    /// <summary>Every bonus currently earned, across every set the character wears.</summary>
    public static IEnumerable<ItemSetBonus> ActiveBonuses()
    {
        foreach (var (set, worn) in ActiveSets())
        {
            if (set.bonuses == null) continue;

            foreach (var bonus in set.bonuses)
                if (bonus != null && worn >= bonus.piecesRequired)
                    yield return bonus;
        }
    }

    /// <summary>Summed magnitude of every active bonus with a given action.</summary>
    public static float TotalMagnitude(string action, string param = null)
    {
        float total = 0f;

        foreach (var bonus in ActiveBonuses())
        {
            if (bonus.action != action) continue;
            if (!string.IsNullOrEmpty(param) && bonus.param != param) continue;
            total += bonus.magnitude;
        }
        return total;
    }

    // ── Tooltip colouring ─────────────────────────────────────────────────────

    /// <summary>
    /// State of a bonus line for display, given how many pieces are worn.
    ///
    /// One rule covers every threshold the spec described: grey below the
    /// requirement, green once the whole set is on, yellow in between. That reproduces
    /// "2-set: grey at 1, yellow at 2-5, green at 6" and its 4- and 6-piece siblings
    /// without three special cases.
    /// </summary>
    public static PieceState BonusState(ItemSetData set, ItemSetBonus bonus, int worn)
    {
        if (set == null || bonus == null)         return PieceState.Missing;
        if (worn < bonus.piecesRequired)          return PieceState.Missing;
        if (worn >= set.PieceCount)               return PieceState.Equipped;
        return PieceState.Carried;
    }

    /// <summary>Overall state of the set header: none / partial / complete.</summary>
    public static PieceState HeaderState(ItemSetData set)
    {
        if (set == null) return PieceState.Missing;

        int worn = EquippedCount(set);
        if (worn <= 0)                return PieceState.Missing;
        if (worn >= set.PieceCount)   return PieceState.Equipped;
        return PieceState.Carried;
    }

    // ── Validation ────────────────────────────────────────────────────────────

    /// <summary>
    /// Reports set content that cannot possibly work: pieces naming an item that does
    /// not exist, items claiming a set that does not exist, bonuses needing more
    /// pieces than the set has, and actions nothing implements.
    ///
    /// Called once after content loads. A set bonus that silently does nothing costs
    /// the player six armour slots and gives no sign of it, which is the one failure
    /// mode this project keeps rediscovering.
    /// </summary>
    public static void ValidateContent()
    {
        var content = GameManager.Content;
        if (content?.ItemSets == null) return;

        var problems = new List<string>();
        var claimed  = new HashSet<string>();

        foreach (var set in content.ItemSets.Values)
        {
            if (set.itemIds == null || set.itemIds.Length == 0)
            {
                problems.Add($"set '{set.id}' lists no pieces");
                continue;
            }

            foreach (var itemId in set.itemIds)
            {
                claimed.Add(itemId);

                var item = content.GetItem(itemId);
                if (item == null)
                {
                    problems.Add($"set '{set.id}' names item '{itemId}', which does not exist");
                    continue;
                }

                if (item.setId != set.id)
                    problems.Add($"'{itemId}' is listed in set '{set.id}' but its setId is " +
                                 $"'{item.setId}' — the tooltip will not find the set from the item");
            }

            if (set.bonuses == null) continue;

            foreach (var bonus in set.bonuses)
            {
                if (bonus == null) continue;

                if (bonus.piecesRequired > set.PieceCount)
                    problems.Add($"set '{set.id}' has a {bonus.piecesRequired}-piece bonus but only " +
                                 $"{set.PieceCount} pieces — it can never be earned");

                if (!SetBonusResolver.Handles(bonus.action))
                    problems.Add($"set '{set.id}' uses action '{bonus.action}', which nothing implements");
            }
        }

        // The other direction: an item pointing at a set that has never heard of it.
        foreach (var item in content.Items.Values)
        {
            if (string.IsNullOrEmpty(item.setId)) continue;
            if (claimed.Contains(item.id))        continue;

            problems.Add($"'{item.id}' claims set '{item.setId}' but that set does not list it");
        }

        if (problems.Count == 0)
        {
            Debug.Log($"[ItemSets] {content.ItemSets.Count} set(s) validated.");
            return;
        }

        Debug.LogWarning($"[ItemSets] {problems.Count} problem(s):\n  • " + string.Join("\n  • ", problems));
    }
}
