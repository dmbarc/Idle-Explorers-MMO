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

    /// <summary>
    /// Unlocks the ability named in the node's abilityId, and each further rank makes
    /// it stronger by effectValue.
    ///
    /// This is what makes abilities part of progression rather than something handed
    /// out at character creation: a level 1 character has an empty action bar, and the
    /// first talent point buys their first ability out of the tree.
    /// </summary>
    public const string GrantAbility = "grantAbility";

    private static readonly HashSet<string> KnownEffects = new()
    {
        MaxHpPercent, AttackDamagePercent, AttackSpeedPercent, AbilityPowerPercent,
        CooldownPercent, LifestealPercent, HealthRegenFlat, DropQuantityPercent,
        GatherRatePercent, CraftSpeedPercent, CraftDoubleChance, SkillXpPercent,
        AfkRatePercent, GrantAbility,
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
        character == null ? 0 : IdleExplorers.Rules.Talents.TotalPoints(character.level);

    /// <summary>
    /// Points spent across EVERY tree the character has specced into.
    ///
    /// One shared pool rather than a pool per class: with up to three trees, separate
    /// pools would make a third class free and remove the decision entirely. Sharing
    /// them is what makes spreading yourself across three trees a real cost, and what
    /// keeps a pure class competitive.
    /// </summary>
    public static int SpentPoints(CharacterData character)
    {
        if (character?.talents == null) return 0;

        return IdleExplorers.Rules.Talents.SpentPoints(character.talents, NodesFor(character));
    }

    /// <summary>
    /// Every node the character can reach, as a list the shared rules can read.
    ///
    /// Materialised rather than passed as the existing lazy enumerable, because the
    /// rules index into it repeatedly and re-walking three class trees per lookup is
    /// the kind of thing that is invisible in the editor and measurable on a server.
    /// </summary>
    private static List<TalentNode> NodesFor(CharacterData character)
    {
        var nodes = new List<TalentNode>();

        foreach (var node in AllNodes(character)) nodes.Add(node);

        return nodes;
    }

    public static int AvailablePoints(CharacterData character) =>
        Mathf.Max(0, TotalPoints(character) - SpentPoints(character));

    // ── Tree access ───────────────────────────────────────────────────────────

    /// <summary>The tree for one specific class.</summary>
    public static TalentNode[] TreeOf(string classId) =>
        GameManager.Content?.GetClass(classId)?.talentTree;

    /// <summary>
    /// The character's PRIMARY tree. Kept for callers that only ever meant one tree;
    /// anything that should span a cross-specced character wants AllNodes instead.
    /// </summary>
    public static TalentNode[] TreeFor(CharacterData character)
    {
        if (character == null) return null;
        return TreeOf(character.classId);
    }

    /// <summary>Every node from every class this character has specced into.</summary>
    public static IEnumerable<TalentNode> AllNodes(CharacterData character)
    {
        if (character == null) yield break;

        foreach (var classId in character.ClassIds())
        {
            var tree = TreeOf(classId);
            if (tree == null) continue;

            foreach (var node in tree)
                if (node != null) yield return node;
        }
    }

    public static TalentNode FindNode(CharacterData character, string nodeId)
    {
        if (string.IsNullOrEmpty(nodeId)) return null;

        foreach (var node in AllNodes(character))
            if (node.id == nodeId) return node;
        return null;
    }

    /// <summary>
    /// Which class a node belongs to, or null.
    ///
    /// Needed because node ids are globally unique but a character can hold three
    /// trees at once — replacing one class has to refund only that tree's points, not
    /// wipe everything the character has ever taken.
    /// </summary>
    public static string ClassOf(string nodeId)
    {
        var classes = GameManager.Content?.Classes;
        if (classes == null || string.IsNullOrEmpty(nodeId)) return null;

        foreach (var kv in classes)
        {
            var tree = kv.Value?.talentTree;
            if (tree == null) continue;

            foreach (var node in tree)
                if (node != null && node.id == nodeId) return kv.Key;
        }
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
    public static int TierPointRequirement(int tier) =>
        IdleExplorers.Rules.Talents.TierRequirement(tier);

    // ── Spending ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Whether one more point can go into this node, and why not if it cannot.
    ///
    /// Delegated to the shared rules, so the answer here is the answer the SERVER will
    /// give. Two implementations would produce a button that looks available and fails
    /// with no explanation -- the worst possible version of this feature.
    /// </summary>
    public static bool CanSpend(CharacterData character, TalentNode node, out string reason)
    {
        if (character == null)
        {
            reason = "No character.";
            return false;
        }

        return IdleExplorers.Rules.Talents.CanSpend(
            node, character.level, character.talents, NodesFor(character), out reason);
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

        // A newly learned ability goes straight into the first empty bar slot.
        //
        // Dragging is how the bar is ARRANGED, but an ability that is learned and then
        // sits nowhere is a purchase with no visible effect — and the very first one a
        // character buys would land on a completely empty bar with nothing to suggest
        // what to do next. Filling only EMPTY slots means this can never displace an
        // arrangement the player chose.
        if (node.GrantsAbility && RankOf(character, nodeId) == 1)
            PlaceInFirstEmptySlot(character, node.abilityId);

        Commit(character);
        GameEvents.FireToast($"✦ {node.name} {RankOf(character, nodeId)}/{node.RankCap}", ChatTone.Good);
        return true;
    }

    private static void PlaceInFirstEmptySlot(CharacterData character, string abilityId)
    {
        var ability = FindAbilityFor(character, abilityId);
        if (ability == null || !ability.IsActivatable) return;   // passives never occupy a slot

        var hotbar = character.Hotbar();
        if (hotbar.Contains(abilityId)) return;

        for (int i = 0; i < hotbar.Count; i++)
        {
            if (!string.IsNullOrEmpty(hotbar[i])) continue;

            hotbar[i] = abilityId;
            GameEvents.OnHotbarChanged?.Invoke();
            return;
        }
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
        PruneHotbar(character);
        Commit(character);
    }

    /// <summary>
    /// Refunds only the points in ONE class's tree.
    ///
    /// Replacing a second class must not cost a cross-specced character the talents
    /// they earned in the other two. ClassOf is what makes this possible: node ids are
    /// globally unique, so each saved rank can be traced back to the tree it came from.
    /// </summary>
    public static void ClearClassTalents(CharacterData character, string classId)
    {
        if (character?.talents == null || string.IsNullOrEmpty(classId)) return;

        var tree = TreeOf(classId);
        if (tree == null) return;

        var ids = new HashSet<string>();
        foreach (var node in tree)
            if (node != null && !string.IsNullOrEmpty(node.id)) ids.Add(node.id);

        character.talents.RemoveAll(t => t != null && ids.Contains(t.nodeId));
        PruneHotbar(character);
        Commit(character);
    }

    /// <summary>
    /// Drops anything from the hotbar the character can no longer use.
    ///
    /// Refunding a talent takes its ability back, and a bar slot still pointing at it
    /// would be a button that silently does nothing — which is the exact failure this
    /// codebase keeps having to hunt down.
    /// </summary>
    public static void PruneHotbar(CharacterData character)
    {
        var hotbar = character?.Hotbar();
        if (hotbar == null) return;

        for (int i = 0; i < hotbar.Count; i++)
        {
            if (string.IsNullOrEmpty(hotbar[i])) continue;
            if (HasAbility(character, hotbar[i])) continue;

            hotbar[i] = "";
        }
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
        if (character == null || string.IsNullOrEmpty(effectType)) return 0f;

        // Delegated, so the number the client shows in a tooltip is the number the
        // SERVER uses when it computes damage per second. Two implementations of this
        // would show a player a talent that does nothing.
        return IdleExplorers.Rules.Talents.Bonus(character.talents, NodesFor(character), effectType);
    }

    /// <summary>1 + Bonus, for the many callers that want a straight multiplier.</summary>
    public static float Multiplier(string effectType) => 1f + Bonus(effectType);

    /// <summary>
    /// 1 - Bonus, for the reduction effects. Kept separate from Multiplier so the
    /// sign is decided here rather than at each of a dozen call sites.
    /// </summary>
    public static float ReductionMultiplier(string effectType) =>
        Mathf.Max(1f - IdleExplorers.Rules.Talents.MaxReduction, 1f - Bonus(effectType));

    // ── Abilities earned from the tree ────────────────────────────────────────

    /// <summary>
    /// Every ability this character has unlocked, in the order their trees list them.
    ///
    /// Derived, never stored. Storing an unlock list would mean two places that could
    /// disagree about what a character has — and the tree is already the truth, since
    /// a refunded talent should take its ability back with it.
    /// </summary>
    public static List<AbilityData> UnlockedAbilities(CharacterData character)
    {
        var results = new List<AbilityData>();
        if (character == null) return results;

        var seen = new HashSet<string>();

        foreach (var classId in character.ClassIds())
        {
            var cls = GameManager.Content?.GetClass(classId);
            if (cls?.talentTree == null) continue;

            foreach (var node in cls.talentTree)
            {
                if (node == null || node.effectType != GrantAbility) continue;
                if (string.IsNullOrEmpty(node.abilityId))            continue;
                if (RankOf(character, node.id) <= 0)                 continue;

                var ability = FindAbility(cls, node.abilityId);
                if (ability == null || !seen.Add(ability.id)) continue;

                results.Add(ability);
            }
        }
        return results;
    }

    /// <summary>Whether a specific ability has been unlocked.</summary>
    public static bool HasAbility(CharacterData character, string abilityId)
    {
        if (string.IsNullOrEmpty(abilityId)) return false;

        foreach (var ability in UnlockedAbilities(character))
            if (ability.id == abilityId) return true;
        return false;
    }

    /// <summary>
    /// An ability by id, searched across every class the character has.
    ///
    /// Not just their primary: with cross-speccing a Paladin's bar can hold Cleave and
    /// Fireball at once, and resolving only against one class would make half the bar
    /// stop working.
    /// </summary>
    public static AbilityData FindAbilityFor(CharacterData character, string abilityId)
    {
        if (character == null || string.IsNullOrEmpty(abilityId)) return null;

        foreach (var classId in character.ClassIds())
        {
            var ability = FindAbility(GameManager.Content?.GetClass(classId), abilityId);
            if (ability != null) return ability;
        }
        return null;
    }

    private static AbilityData FindAbility(ClassData cls, string abilityId)
    {
        if (cls?.abilities == null) return null;

        foreach (var ability in cls.abilities)
            if (ability != null && ability.id == abilityId) return ability;
        return null;
    }

    /// <summary>
    /// How much a talent has upgraded one ability, summed across ability-scoped nodes
    /// with the given effect type. Ranks past the first on a grantAbility node count.
    /// </summary>
    public static float AbilityBonus(CharacterData character, string abilityId, string effectType)
    {
        if (character == null || string.IsNullOrEmpty(abilityId)) return 0f;

        float total = 0f;

        foreach (var node in AllNodes(character))
        {
            if (node.abilityId != abilityId) continue;

            int rank = RankOf(character, node.id);
            if (rank <= 0) continue;

            // The node that GRANTS an ability spends its first rank on the unlock
            // itself; only the ranks after that are an upgrade.
            if (node.effectType == GrantAbility)
            {
                if (effectType != GrantAbility) continue;
                total += node.effectValue * (rank - 1);
                continue;
            }

            if (node.effectType != effectType) continue;
            total += node.effectValue * rank;
        }
        return total;
    }

    /// <summary>Power multiplier for an ability, from the ranks invested in it.</summary>
    public static float AbilityPowerMultiplier(CharacterData character, string abilityId) =>
        1f + AbilityBonus(character, abilityId, GrantAbility);

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

        // Node ids must be unique across EVERY class, not just within one. A character
        // can hold three trees at once, and TalentRank is keyed by node id alone — so
        // two classes sharing an id would silently share the ranks spent in it.
        var seenGlobally = new Dictionary<string, string>();

        foreach (var cls in content.Classes.Values)
        {
            if (cls?.talentTree == null) continue;

            var grantedHere = new HashSet<string>();

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

                if (seenGlobally.TryGetValue(node.id, out string owner))
                {
                    Debug.LogWarning($"[Talents] node id '{node.id}' appears in both '{owner}' and " +
                                     $"'{cls.id}'. Ids are global — a cross-specced character would " +
                                     "share ranks between the two.");
                    broken++;
                }
                else seenGlobally[node.id] = cls.id;

                if (!KnownEffects.Contains(node.effectType))
                {
                    Debug.LogWarning($"[Talents] {cls.id}/{node.id}: effectType '{node.effectType}' " +
                                     "is not read by anything — this talent would cost a point and do nothing.");
                    broken++;
                }

                if (node.effectType != GrantAbility) continue;

                if (string.IsNullOrEmpty(node.abilityId))
                {
                    Debug.LogWarning($"[Talents] {cls.id}/{node.id}: grantAbility with no abilityId — " +
                                     "it would unlock nothing.");
                    broken++;
                    continue;
                }

                if (FindAbility(cls, node.abilityId) == null)
                {
                    Debug.LogWarning($"[Talents] {cls.id}/{node.id}: grants '{node.abilityId}', which " +
                                     "is not in that class's ability list.");
                    broken++;
                }

                grantedHere.Add(node.abilityId);
            }

            // The other direction: an ability nothing unlocks is content the player can
            // never reach, which is the same silent gap in reverse.
            if (cls.abilities == null) continue;

            foreach (var ability in cls.abilities)
            {
                if (ability == null || grantedHere.Contains(ability.id)) continue;

                Debug.LogWarning($"[Talents] {cls.id}: ability '{ability.id}' is not granted by any " +
                                 "talent — no character can ever learn it.");
                broken++;
            }
        }

        if (checkedNodes == 0)
            Debug.LogWarning("[Talents] No class defines a talent tree.");
        else
            Debug.Log($"[Talents] Validated {checkedNodes} node(s) across {content.Classes.Count} class(es)" +
                      (broken > 0 ? $" — {broken} problem(s) above." : "."));
    }
}
