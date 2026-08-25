using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Runs armour set bonuses.
///
/// Every action listed in Actions is implemented here and nowhere else, so
/// ItemSetManager.ValidateContent can check a set's JSON against reality and report a
/// bonus that would have silently done nothing. A set bonus costs the player six
/// armour slots; one that quietly does not work is worse than one that does not exist.
///
/// The tin set's three bonuses are, deliberately, about durability — they break your
/// own armour to hurt things, then win it back. That only reads as a mechanic rather
/// than a bug because nothing here can destroy an item: a piece thrown at an enemy
/// lands on the ground as a real pickup, and a piece shattered to 0 stays in its slot
/// waiting to be repaired.
/// </summary>
public static class SetBonusResolver
{
    // ── The vocabulary ────────────────────────────────────────────────────────

    /// <summary>Always-on flat stat, param names the stat. Read through EquipmentManager.</summary>
    public const string StatBonus = "statBonus";

    /// <summary>Reflects a fraction of damage taken back at whatever is nearby.</summary>
    public const string Thorns = "thorns";

    /// <summary>Heals a fraction of damage dealt.</summary>
    public const string Lifesteal = "lifesteal";

    /// <summary>Multiplies durability lost per wear tick. Below 1 means gear lasts longer.</summary>
    public const string DurabilityGuard = "durabilityGuard";

    /// <summary>On taking damage: shatter one piece, hurt everything around you.</summary>
    public const string ShardBurst = "shardBurst";

    /// <summary>On dealing damage: hurl a worn piece at the target. It lands as loot.</summary>
    public const string ArmorThrow = "armorThrow";

    /// <summary>On taking damage: move durability from the healthiest pieces to the weakest.</summary>
    public const string DurabilitySiphon = "durabilitySiphon";

    /// <summary>On dealing damage: pick up and re-equip armour lying nearby.</summary>
    public const string Scavenge = "scavenge";

    /// <summary>Repairs a point of durability on every kill.</summary>
    public const string Mend = "mend";

    private static readonly HashSet<string> Actions = new()
    {
        StatBonus, Thorns, Lifesteal, DurabilityGuard,
        ShardBurst, ArmorThrow, DurabilitySiphon, Scavenge, Mend,
    };

    /// <summary>Whether an action string from JSON is one this file implements.</summary>
    public static bool Handles(string action) => !string.IsNullOrEmpty(action) && Actions.Contains(action);

    // ── Passive contributions ─────────────────────────────────────────────────

    /// <summary>Flat stat bonus from every active set, added on top of gear.</summary>
    public static float StatBonusFor(string statId) =>
        ItemSetManager.TotalMagnitude(StatBonus, statId);

    /// <summary>Pours every active set's flat stat bonuses into a block.</summary>
    public static void ContributeTo(StatBlock block)
    {
        if (block == null) return;

        foreach (var bonus in ItemSetManager.ActiveBonuses())
        {
            if (bonus.action != StatBonus) continue;
            block.Add(bonus.param, bonus.magnitude);
        }
    }

    /// <summary>
    /// A set proc's chance after Resonance.
    ///
    /// Resonance exists to make set bonuses a build axis rather than a fixed roll, so
    /// it belongs on the chance rather than the effect — a player who invests in it
    /// sees their set fire more often, which is what the stat's description promises.
    /// </summary>
    private static float EffectiveChance(ItemSetBonus bonus)
    {
        float resonance = GameManager.Stats?.Current.resonance ?? 0f;
        return Mathf.Clamp01(bonus.chance * (1f + resonance));
    }

    /// <summary>
    /// Durability a wear tick should actually cost, after durabilityGuard.
    ///
    /// Clamped at 0 rather than allowed to go negative: a guard strong enough to
    /// invert would repair armour by being hit, which is a different bonus.
    /// </summary>
    public static int DurabilityLossFor(int points)
    {
        float guard = 1f;

        foreach (var bonus in ItemSetManager.ActiveBonuses())
            if (bonus.action == DurabilityGuard) guard *= Mathf.Max(0f, bonus.magnitude);

        // Tenacity does the same job as a durabilityGuard set bonus, from the stat
        // side. Multiplied together rather than added, so a player with both does not
        // reach zero wear and make durability decorative.
        float tenacity = GameManager.Stats?.Current.tenacity ?? 0f;
        guard *= 1f / (1f + Mathf.Max(0f, tenacity));

        return Mathf.Max(0, Mathf.RoundToInt(points * guard));
    }

    // ── Triggers ──────────────────────────────────────────────────────────────

    /// <summary>Called after the player has taken a hit.</summary>
    public static void OnDamageTaken(PlayerController player, double damage)
    {
        if (player == null || !player.IsAlive()) return;

        foreach (var bonus in ItemSetManager.ActiveBonuses())
        {
            switch (bonus.action)
            {
                case Thorns:
                    Reflect(player, damage * bonus.magnitude, bonus.radius);
                    break;

                case ShardBurst:
                    if (Roll(bonus)) ShatterAndBurst(player, bonus);
                    break;

                case DurabilitySiphon:
                    if (Roll(bonus)) Siphon();
                    break;
            }
        }
    }

    /// <summary>Called after the player has dealt damage to a target.</summary>
    public static void OnDamageDealt(PlayerController player, MonsterController target, double damage)
    {
        if (player == null || !player.IsAlive()) return;

        foreach (var bonus in ItemSetManager.ActiveBonuses())
        {
            switch (bonus.action)
            {
                case Lifesteal:
                    player.Heal(damage * bonus.magnitude);
                    break;

                case ArmorThrow:
                    if (target != null && Roll(bonus)) ThrowArmor(player, target, bonus);
                    break;

                case Scavenge:
                    if (Roll(bonus)) Scavenged(player, bonus.radius);
                    break;
            }
        }
    }

    /// <summary>Called when a monster dies to the player.</summary>
    public static void OnKill()
    {
        var equipment = GameManager.Equipment;
        if (equipment == null) return;

        foreach (var bonus in ItemSetManager.ActiveBonuses())
        {
            if (bonus.action != Mend) continue;
            if (!Roll(bonus)) continue;

            string weakest = WeakestSlot(equipment);
            if (weakest != null) equipment.RepairSlot(weakest, Mathf.Max(1, (int)bonus.magnitude));
        }
    }

    // ── Implementations ───────────────────────────────────────────────────────

    private static void Reflect(PlayerController player, double amount, float radius)
    {
        if (amount <= 0d) return;

        foreach (var monster in MonstersNear(player.transform.position, radius))
            monster.TakeDamage(amount);
    }

    /// <summary>
    /// Tin shards: a piece of your armour takes a beating, and everything nearby
    /// takes a large hit.
    ///
    /// ══ WHY IT NO LONGER DESTROYS THE PIECE ═══════════════════════════════════
    ///
    /// It used to shatter one piece outright — DamageSlot with int.MaxValue. A broken
    /// piece does not count toward EquippedCount, so the FIRST time the 2-piece bonus
    /// fired on a full set it dropped the wearer from six pieces to five and switched
    /// off the two 6-piece bonuses. The set's own reward for completing it was
    /// dismantled by the set's own cheapest proc, and the more of it you wore the
    /// faster that happened.
    ///
    /// A large chunk of wear keeps the drama and the cost without the cliff: the piece
    /// is visibly closer to breaking, the player can repair it, and a full set stays a
    /// full set until they let it go. It can still break something that was already
    /// nearly gone, which is honest — that is wear, not the bonus deleting the set.
    /// </summary>
    private static void ShatterAndBurst(PlayerController player, ItemSetBonus bonus)
    {
        var equipment = GameManager.Equipment;
        if (equipment == null) return;

        // Chosen from what is still intact, so this cannot fire off a set that is
        // already in pieces.
        var intact = new List<string>();
        foreach (var slotId in equipment.DurableSlots())
            if (equipment.GetDurability(slotId) > 0) intact.Add(slotId);

        if (intact.Count == 0) return;

        string chosen = intact[Random.Range(0, intact.Count)];
        var    item   = equipment.GetEquippedItem(chosen);

        // Falls back to a sensible chunk if the data forgot to say. Zero here would
        // make the bonus free, which is a different bonus.
        int cost = bonus.durabilityCost > 0 ? bonus.durabilityCost : 25;
        int lost = equipment.DamageSlot(chosen, cost);

        double blow = player.AttackDamage * bonus.magnitude;
        int    hits = 0;

        foreach (var monster in MonstersNear(player.transform.position, bonus.radius))
        {
            monster.TakeDamage(blow);
            hits++;
        }

        AbilityVFX.Play("aoe_burst", player.transform.position);
        GameManager.Audio?.Play(Sfx.SetProc);

        // DamageSlot announces a break itself, so this only reports the wear.
        GameEvents.FireToast($"✦ Tin shards — your {item?.DisplayName ?? "armor"} loses " +
                             $"{lost} durability" +
                             (hits > 0 ? $", {hits} caught in the blast." : "."),
                             ChatTone.Warning);
    }

    /// <summary>
    /// Throws a worn piece at the target: real damage, and the item genuinely leaves
    /// the character and lands on the ground as a pickup. It is not destroyed — the
    /// 6-piece bonus exists to fetch it back, and without a real DropPickup there
    /// would be nothing to fetch.
    /// </summary>
    private static void ThrowArmor(PlayerController player, MonsterController target, ItemSetBonus bonus)
    {
        var equipment = GameManager.Equipment;
        if (equipment == null) return;

        var candidates = equipment.DurableSlots();
        if (candidates.Count == 0) return;

        string slotId = candidates[Random.Range(0, candidates.Count)];
        var    item   = equipment.GetEquippedItem(slotId);
        if (item == null) return;

        // Unequip WITHOUT routing it through the inventory — the whole point is that
        // it ends up on the floor. TakeOff returns the durability so the piece keeps
        // its condition rather than coming back fresh.
        if (!equipment.TakeOff(slotId, out int durability)) return;

        if (!SpawnArmorOnGround(item.id, durability, target.transform.position))
        {
            equipment.PutOn(slotId, item.id, durability);
            return;
        }

        target.TakeDamage(player.AttackDamage * bonus.magnitude);
        AbilityVFX.Play("impact", target.transform.position);
        GameManager.Audio?.Play(Sfx.SetProc);

        GameEvents.FireToast($"✦ You hurl your {item.DisplayName} at {target.name}.", ChatTone.Warning);
    }

    /// <summary>
    /// Moves durability from every other piece into whichever is worst off. Net
    /// neutral by design — this is triage, not repair, and the 6-piece bonus is
    /// supposed to keep a set fighting rather than make it immortal.
    /// </summary>
    private static void Siphon()
    {
        var equipment = GameManager.Equipment;
        if (equipment == null) return;

        string weakest = WeakestSlot(equipment);
        if (weakest == null) return;

        int stolen = 0;
        foreach (var slotId in equipment.DurableSlots())
        {
            if (slotId == weakest) continue;
            if (equipment.GetDurability(slotId) <= 1) continue;   // never break a piece to do this

            stolen += equipment.DamageSlot(slotId, 1, announce: false);
        }

        if (stolen <= 0) return;

        equipment.RepairSlot(weakest, stolen);
        GameEvents.FireToast($"✦ Your armor redistributes {stolen} point(s) of wear.", ChatTone.Good);
    }

    /// <summary>
    /// Picks up armour lying on the ground nearby and puts it straight back on.
    /// Only pieces that ARE armour and whose slot is free, so this cannot steal a
    /// better item off the character to make room for the one they just threw.
    /// </summary>
    private static void Scavenged(PlayerController player, float radius)
    {
        var equipment = GameManager.Equipment;
        if (equipment == null) return;

        foreach (var drop in Object.FindObjectsByType<DropPickup>(FindObjectsInactive.Exclude))
        {
            if (drop == null || string.IsNullOrEmpty(drop.itemId)) continue;
            if (Vector3.Distance(player.transform.position, drop.transform.position) > radius) continue;

            var item = GameManager.Content?.GetItem(drop.itemId);
            if (item == null || !item.IsEquippable) continue;

            string slotId = item.equipSlot;
            if (!EquipmentSlots.Exists(slotId)) continue;
            if (!string.IsNullOrEmpty(equipment.GetEquipped(slotId))) continue;

            // -1 means the drop was looted rather than thrown, so it has no recorded
            // condition. Clamping that to 0 would put a shattered helmet on someone
            // who just picked up a perfectly good one.
            int condition = drop.RecoveredDurability >= 0
                ? drop.RecoveredDurability
                : item.maxDurability;

            if (!equipment.PutOn(slotId, item.id, condition)) continue;

            GameEvents.FireToast($"✦ You scoop up your {item.DisplayName} and put it back on.", ChatTone.Good);
            Object.Destroy(drop.gameObject);
            return;   // one per proc, so a field of loot is not vacuumed in a frame
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool Roll(ItemSetBonus bonus) =>
        bonus.chance > 0f && Random.value <= EffectiveChance(bonus);

    private static string WeakestSlot(EquipmentManager equipment)
    {
        string worst     = null;
        float  worstCond = float.MaxValue;

        foreach (var slotId in equipment.DurableSlots())
        {
            float condition = equipment.GetCondition(slotId);
            if (condition >= worstCond) continue;
            worstCond = condition;
            worst     = slotId;
        }
        return worst;
    }

    private static List<MonsterController> MonstersNear(Vector3 origin, float radius)
    {
        var found = new List<MonsterController>();

        foreach (var m in Object.FindObjectsByType<MonsterController>(FindObjectsInactive.Exclude))
        {
            if (m == null || !m.IsAlive()) continue;
            if (Vector3.Distance(origin, m.transform.position) > radius) continue;
            found.Add(m);
        }
        return found;
    }

    /// <summary>
    /// Puts a thrown piece on the ground. Returns false when it could not, which is
    /// the caller's cue to put the armour back on rather than let it evaporate —
    /// destroying gear because a prefab failed to load is not an acceptable outcome
    /// of a set bonus.
    /// </summary>
    private static bool SpawnArmorOnGround(string itemId, int durability, Vector3 near)
    {
        var prefab = Resources.Load<GameObject>("ItemDrops/GenericDrop");
        if (prefab == null)
        {
            Debug.LogWarning("[SetBonus] No ItemDrops/GenericDrop prefab — armor throw cancelled " +
                             $"rather than destroying '{itemId}'. " +
                             "Run: Idle Explorers → Rebuild Item Drop Prefab.");
            return false;
        }

        var dropObj = Object.Instantiate(prefab, near + Vector3.up * 0.8f, Quaternion.identity);
        var pickup  = dropObj.GetComponent<DropPickup>();
        if (pickup == null)
        {
            Object.Destroy(dropObj);
            return false;
        }

        pickup.Setup(itemId, 1);
        pickup.RecoveredDurability = durability;
        return true;
    }
}
