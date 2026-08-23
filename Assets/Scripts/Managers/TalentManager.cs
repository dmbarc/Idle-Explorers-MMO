using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Talent points, spending rules, and the bonuses they produce.
///
/// Static, like CraftingSupply and ItemEffectResolver, because it owns no state of
/// its own — every answer is derived from the active character's saved ranks and the
/// class tree in class_data.json.
///
/// The design rule this file enforces is that a talent must DO something. Every
/// effectType below is read by real game code, and an unrecognised one is logged
/// loudly rather than treated as zero: a talent that costs a point and quietly does
/// nothing is the worst possible outcome for a progression system.
/// </summary>
public static class TalentManager
{
    // ── Effect types ──────────────────────────────────────────────────────────
    // Each is a fraction per rank unless marked flat. Where they are consumed:
    //
    //   maxHpPercent         PlayerController.ApplyClassStats
    //   attackDamagePercent  PlayerController.ApplyClassStats
    //   attackSpeedPercent   PlayerController.ApplyClassStats   (shortens the interval)
    //   abilityPowerPercent  PlayerController.ApplyAbilityEffect
    //   cooldownPercent      PlayerController.UseAbility
    //   lifestealPercent     PlayerController.AttackLogic
    //   healthRegenFlat      PlayerController.ApplyClassStats   (flat HP per tick)
    //   dropQuantityPercent  PlayerController.dropMultiplier → MonsterController.DropLoot
    //   gatherRatePercent    SkillNodeController rate + ActivityManager AFK gathering
    //   craftSpeedPercent    SkillNodeController crafting + ActivityManager AFK crafting
    //   craftDoubleChance    ActivityManager.ProcessCraftingAFKRewards + live craft
    //   skillXpPercent       SkillManager.AddSkillXP
    //   afkRatePercent       ActivityManager.ProcessAFKRewards

    public const string MaxHpPercent        = "maxHpPercent";
    public const string AttackDamagePercent = "attackDamagePercent";
    public const string AttackSpeedPercent  = "attackSpeedPercent";
    public const string AbilityPowerPercent = "abilityPowerPercent";
    public const string CooldownPercent     = "cooldownPercent";
    public const string LifestealPercent    = "lifestealPercent";
    public const string HealthRegenFlat     = "healthRegenFlat";
    public const string DropQuantityPercent = "dropQuantityPercent";
    public const string GatherRatePercent   = "gatherRatePercent";
    public const string CraftSpeedPercent   = "craftSpeedPercent";
    public const string CraftDoubleChance   = "craftDoubleChance";
    public const string SkillXpPercent      = "skillXpPercent";
    public const string AfkRatePercent      = "afkRatePercent";

    private static readonly HashSet<string> KnownEffects = new()
    {
        MaxHpPercent, AttackDamagePercent, AttackSpeedPercent, AbilityPowerPercent,
        CooldownPercent, LifestealPercent, HealthRegenFlat, DropQuantityPercent,
        GatherRatePercent, CraftSpeedPercent, CraftDoubleChance, SkillXpPercent,
        AfkRatePercent,
    };

    /// <summary>
    /// Effects that shorten a duration rather than increase an amount, so they are
    /// consumed through ReductionMultiplier and capped below 1.0 — a full 100% would
    /// divide the thing they reduce to zero, giving an infinite attack rate or a
    /// craft that takes no time at all.
    /// </summary>
    private static readonly HashSet<string> ReductionEffects = new()
    {
        AttackSpeedPercent, CooldownPercent, CraftSpeedPercent, GatherRatePercent,
    };

    // ── Points ────────────────────────────────────────────────────────────────

    /// <summary>
    /// One point per character level after the first. Level 1 characters have none,
    /// so the first level-up is when the tree becomes interesting.
    /// </summary>
    public static int TotalPoints(CharacterData character) =>
        character == null ? 0 : Mathf.Max(0, character.level - 1);

    public static int SpentPoints(CharacterData character)
    {
        var tree = TreeFor(character);
        if (tree == null || character?.talents == null) return 0;

        int spent = 0;
        foreach (var node in tree)
        {
            if (node == null) continue;
            spent += RankOf(character, node.id) * node.PointCost;
        }
        return spent;
    }

    public static int AvailablePoints(CharacterData character) =>
        Mathf.Max(0, TotalPoints(character) - SpentPoints(character));

    // ── Tree access ───────────────────────────────────────────────────────────

    public static TalentNode[] TreeFor(CharacterData character)
    {
        if (character == null) return null;
        return GameManager.Content?.GetClass(character.classId)?.talentTree;
    }

    public static TalentNode FindNode(CharacterData character, string nodeId)
    {
        var tree = TreeFor(character);
        if (tree == null || string.IsNullOrEmpty(nodeId)) return null;

        foreach (var node in tree)
            if (node != null && node.id == nodeId) return node;
        return null;
    }

    public static int RankOf(CharacterData character, string nodeId)
    {
        if (character?.talents == null || string.IsNullOrEmpty(nodeId)) return 0;

        foreach (var entry in character.talents)
            if (entry != null && entry.nodeId == nodeId) return Mathf.Max(0, entry.rank);
        return 0;
    }

    /// <summary>
    /// Points that must already be in the tree before a tier opens. Three per tier,
    /// so the first row has to be worked through before the second is reachable.
    /// </summary>
    public static int TierPointRequirement(int tier) => Mathf.Max(0, tier) * 3;

    // ── Spending ──────────────────────────────────────────────────────────────

    /// <summary>Whether one more point can go into this node, and why not if it cannot.</summary>
    public static bool CanSpend(CharacterData character, TalentNode node, out string reason)
    {
        if (character == null || node == null)
        {
            reason = "No character.";
            return false;
        }

        if (RankOf(character, node.id) >= node.RankCap)
        {
            reason = "Already at maximum rank.";
            return false;
        }

        if (AvailablePoints(character) < node.PointCost)
        {
            reason = node.PointCost > 1
                ? $"Needs {node.PointCost} points."
                : "No talent points to spend.";
            return false;
        }

        int required = TierPointRequirement(node.tier);
        int spent    = SpentPoints(character);
        if (spent < required)
        {
            reason = $"Spend {required - spent} more point(s) first.";
            return false;
        }

        if (node.requiresNodeIds != null)
        {
            foreach (var prerequisiteId in node.requiresNodeIds)
            {
                if (string.IsNullOrEmpty(prerequisiteId)) continue;
                if (RankOf(character, prerequisiteId) > 0) continue;

                string name = FindNode(character, prerequisiteId)?.name ?? prerequisiteId;
                reason = $"Requires {name}.";
                return false;
            }
        }

        reason = null;
        return true;
    }

    /// <summary>Puts one point into a node. Returns false with a reason, and changes nothing.</summary>
    public static bool Spend(string nodeId, out string reason)
    {
        var character = CharacterManager.Current;
        var node      = FindNode(character, nodeId);

        if (!CanSpend(character, node, out reason)) return false;

        character.talents ??= new List<TalentRank>();

        var existing = character.talents.Find(t => t != null && t.nodeId == nodeId);
        if (existing != null) existing.rank++;
        else character.talents.Add(new TalentRank { nodeId = nodeId, rank = 1 });

        Commit(character);
        GameEvents.FireToast($"✦ {node.name} {RankOf(character, nodeId)}/{node.RankCap}");
        return true;
    }

    /// <summary>
    /// Refunds every point. Free, and deliberately so — the tree is meant to be
    /// experimented with, and a respec fee on a game with no economy yet is just a
    /// tax on finding out what the talents do.
    /// </summary>
    public static void ResetAll()
    {
        var character = CharacterManager.Current;
        if (character == null) return;

        int refunded = SpentPoints(character);
        character.talents = new List<TalentRank>();

        Commit(character);
        GameEvents.FireToast(refunded > 0 ? $"Refunded {refunded} talent point(s)." : "Nothing to refund.");
    }

    /// <summary>
    /// Wipes talents without a toast. Used when a character changes class — the old
    /// tree's node ids mean nothing in the new one.
    /// </summary>
    public static void ClearForClassChange(CharacterData character)
    {
        if (character == null) return;
        character.talents = new List<TalentRank>();
        Commit(character);
    }

    private static void Commit(CharacterData character)
    {
        GameEvents.OnTalentsChanged?.Invoke();

        // Talents feed straight into combat stats, so the live player has to be told
        // rather than waiting for the next equipment change to recompute them.
        GameEvents.OnEquipmentChanged?.Invoke();

        GameManager.Save?.Save();
    }

    // ── Bonuses ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Summed value of an effect across every talent the active character has taken.
    /// Returns 0 when nothing contributes, so callers can add it unconditionally.
    /// </summary>
    public static float Bonus(string effectType) => Bonus(CharacterManager.Current, effectType);

    public static float Bonus(CharacterData character, string effectType)
    {
        var tree = TreeFor(character);
        if (tree == null || string.IsNullOrEmpty(effectType)) return 0f;

        float total = 0f;

        foreach (var node in tree)
        {
            if (node == null || node.effectType != effectType) continue;

            int rank = RankOf(character, node.id);
            if (rank <= 0) continue;

            total += node.effectValue * rank;
        }

        if (ReductionEffects.Contains(effectType))
        {
            // A reduction of 1.0 would divide the thing it reduces to nothing — an
            // infinite attack rate, or a zero-second craft. Capped well short of it.
            total = Mathf.Clamp(total, 0f, 0.75f);
        }

        return total;
    }

    /// <summary>1 + Bonus, for the many callers that want a straight multiplier.</summary>
    public static float Multiplier(string effectType) => 1f + Bonus(effectType);

    /// <summary>
    /// 1 - Bonus, for the reduction effects. Kept separate from Multiplier so the
    /// sign is decided here rather than at each of a dozen call sites.
    /// </summary>
    public static float ReductionMultiplier(string effectType) =>
        Mathf.Max(0.25f, 1f - Bonus(effectType));

    // ── Validation ────────────────────────────────────────────────────────────

    /// <summary>
    /// Reports any talent whose effectType nothing reads. Called once after content
    /// loads: a typo in class_data.json otherwise produces a talent that spends a
    /// point and does nothing, which is invisible until a player notices the stat
    /// never moved.
    /// </summary>
    public static void ValidateContent()
    {
        var content = GameManager.Content;
        if (content == null) return;

        int checkedNodes = 0, broken = 0;

        foreach (var cls in content.Classes.Values)
        {
            if (cls?.talentTree == null) continue;

            var seen = new HashSet<string>();

            foreach (var node in cls.talentTree)
            {
                if (node == null) continue;
                checkedNodes++;

                if (string.IsNullOrEmpty(node.id))
                {
                    Debug.LogWarning($"[Talents] {cls.id}: a node has no id — it can never be taken.");
                    broken++;
                    continue;
                }

                if (!seen.Add(node.id))
                {
                    Debug.LogWarning($"[Talents] {cls.id}: duplicate node id '{node.id}' — " +
                                     "ranks in one will be read from the other.");
                    broken++;
                }

                if (!KnownEffects.Contains(node.effectType))
                {
                    Debug.LogWarning($"[Talents] {cls.id}/{node.id}: effectType '{node.effectType}' " +
                                     "is not read by anything — this talent would cost a point and do nothing.");
                    broken++;
                }
            }
        }

        if (checkedNodes == 0)
            Debug.LogWarning("[Talents] No class defines a talent tree.");
        else
            Debug.Log($"[Talents] Validated {checkedNodes} node(s) across {content.Classes.Count} class(es)" +
                      (broken > 0 ? $" — {broken} problem(s) above." : "."));
    }
}
