using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace IdleExplorersTests;

/// <summary>
/// The talent trees, as a design rather than as JSON.
///
/// ══ WHAT THESE PROTECT ════════════════════════════════════════════════════════
///
/// The trees were eleven nodes each, of which four unlocked abilities and the rest
/// were "+3% damage per rank" — points you spend because there is nowhere else to
/// put them. They are twenty nodes each now, and almost every one either unlocks an
/// ability, augments a specific ability, or is a passive with its own identity.
///
/// The checks below are the properties that make that true, and every one is a
/// property a future edit can quietly break by adding one convenient node.
///
/// ══ THE DANGLING-ID CHECK IS THE SHARP ONE ════════════════════════════════════
///
/// A node whose abilityId names an ability the class does not have spends a point and
/// does nothing at all — no error, no warning, and the tooltip still reads correctly.
/// </summary>
internal static class TalentTreeChecks
{
    /// <summary>Effect types the code actually reads. A node outside this list is dead.</summary>
    private static readonly HashSet<string> Honoured = new(StringComparer.Ordinal)
    {
        "grantAbility", "maxHpPercent", "attackDamagePercent", "attackSpeedPercent",
        "abilityPowerPercent", "cooldownPercent", "lifestealPercent", "healthRegenFlat",
        "dropQuantityPercent", "gatherRatePercent", "craftSpeedPercent",
        "craftDoubleChance", "skillXpPercent", "afkRatePercent",
    };

    /// <summary>Effects PlayerController.ApplyAbilityEffect can actually resolve.</summary>
    private static readonly HashSet<string> Resolvable = new(StringComparer.Ordinal)
    {
        "damage", "aoe", "heal", "haste", "slow", "stealth", "blink", "summon",
        "bladestorm", "passive",
    };

    internal static void Run(Action<bool, string> check, string root)
    {
        Console.WriteLine("Talent trees");

        string path = Path.Combine(root, "Assets", "StreamingAssets", "class_data.json");

        if (!File.Exists(path)) { check(false, "class_data.json exists"); return; }

        var classes = Parse(File.ReadAllText(path));

        check(classes.Count == 5, $"all five classes parsed ({classes.Count})");

        var idsEverywhere = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var cls in classes)
        {
            check(cls.Abilities.Count >= 6,
                  $"{cls.Id} has at least six abilities ({cls.Abilities.Count})");

            check(cls.Nodes.Count >= 18,
                  $"{cls.Id} has a tree worth spending in ({cls.Nodes.Count} nodes)");

            foreach (var ability in cls.Abilities)
                check(Resolvable.Contains(ability.Effect),
                      $"{cls.Id}/{ability.Id} has effect '{ability.Effect}', which the client " +
                      "can resolve — anything else is a button that does nothing");

            var abilityIds = cls.Abilities.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);

            foreach (var node in cls.Nodes)
            {
                // ══ IDS ARE GLOBAL ════════════════════════════════════════════
                //
                // TalentRank is keyed by node id alone, and a character can hold three
                // trees — so two classes sharing an id would silently share the ranks
                // spent in it.
                if (idsEverywhere.TryGetValue(node.Id, out string owner))
                    check(false, $"node id '{node.Id}' is in both {owner} and {cls.Id}");
                else
                    idsEverywhere[node.Id] = cls.Id;

                check(Honoured.Contains(node.EffectType),
                      $"{cls.Id}/{node.Id} uses effect '{node.EffectType}', which something reads");

                // A scoped node pointed at an ability this class does not have is a
                // point spent on nothing.
                if (node.AbilityId.Length > 0)
                    check(abilityIds.Contains(node.AbilityId),
                          $"{cls.Id}/{node.Id} augments '{node.AbilityId}', which the class has");

                check(node.MaxRank >= 1, $"{cls.Id}/{node.Id} can be taken at least once");
                check(node.Description.Length > 0, $"{cls.Id}/{node.Id} says what it does");
            }

            // ══ THE REWORK ITSELF ═════════════════════════════════════════════
            //
            // A character-wide flat stat node is the shape being moved away from. Two
            // per class is the allowance, and both are meant to be single-rank
            // capstones where the number IS the identity.
            var flat = cls.Nodes
                .Where(n => n.AbilityId.Length == 0 &&
                            (n.EffectType is "attackDamagePercent" or "maxHpPercent"
                                          or "attackSpeedPercent"))
                .ToList();

            check(flat.Count <= 2,
                  $"{cls.Id} has at most two flat stat nodes ({flat.Count}) — the rest " +
                  "should unlock, augment, or be a passive with an identity");

            foreach (var node in flat)
                check(node.MaxRank == 1,
                      $"{cls.Id}/{node.Id} is a single-rank capstone rather than a drip");

            // Enough of the tree should be about abilities for it to read as a build.
            int aboutAbilities = cls.Nodes.Count(n => n.AbilityId.Length > 0);

            check(aboutAbilities >= cls.Nodes.Count / 2,
                  $"{cls.Id}: {aboutAbilities} of {cls.Nodes.Count} nodes concern a specific " +
                  "ability — a tree of loose percentages is the thing this replaced");

            // ══ AND EVERY ABILITY IS REACHABLE ════════════════════════════════
            //
            // Abilities come from talents, so one with no grant node is content nobody
            // can ever reach.
            var granted = cls.Nodes.Where(n => n.EffectType == "grantAbility")
                                   .Select(n => n.AbilityId).ToHashSet(StringComparer.Ordinal);

            foreach (var ability in cls.Abilities)
                check(granted.Contains(ability.Id),
                      $"{cls.Id}/{ability.Id} is granted by some node — an ability with no " +
                      "grant is unreachable");
        }
    }

    // ── Reading class_data.json ───────────────────────────────────────────────

    private sealed class Node
    {
        public string Id = "", EffectType = "", AbilityId = "", Description = "";
        public int MaxRank = 1;
    }

    private sealed class Ability { public string Id = "", Effect = ""; }

    private sealed class Cls
    {
        public string Id = "";
        public readonly List<Node> Nodes = new();
        public readonly List<Ability> Abilities = new();
    }

    /// <summary>
    /// A deliberately small reader for a file with a known shape.
    ///
    /// Objects are collected key by key and classified by what they turned out to
    /// contain: an `effectType` makes it a talent, an `effect` makes it an ability.
    /// Guessing from nesting depth instead would break the first time the file is
    /// reformatted, and this runs against a file a generator writes.
    /// </summary>
    private static List<Cls> Parse(string json)
    {
        var classes = new List<Cls>();
        Cls current = null;

        var block = new Dictionary<string, string>(StringComparer.Ordinal);

        void Flush()
        {
            if (current != null && block.Count > 0)
            {
                if (block.ContainsKey("effectType"))
                {
                    current.Nodes.Add(new Node
                    {
                        Id          = block.GetValueOrDefault("id", ""),
                        EffectType  = block.GetValueOrDefault("effectType", ""),
                        AbilityId   = block.GetValueOrDefault("abilityId", ""),
                        Description = block.GetValueOrDefault("description", ""),
                        MaxRank     = int.TryParse(block.GetValueOrDefault("maxRank", "1"),
                                                   out int r) ? r : 1,
                    });
                }
                else if (block.ContainsKey("effect"))
                {
                    current.Abilities.Add(new Ability
                    {
                        Id     = block.GetValueOrDefault("id", ""),
                        Effect = block.GetValueOrDefault("effect", ""),
                    });
                }
            }

            block.Clear();
        }

        foreach (string raw in json.Split('\n'))
        {
            string line   = raw.TrimEnd('\r');
            int    indent = line.Length - line.TrimStart().Length;
            string t      = line.Trim();

            // A class id sits shallower than anything inside it. Everything collected
            // so far belongs to the previous class, so it is flushed first.
            Match id = Regex.Match(t, "^\"id\":\\s*\"([^\"]*)\"");

            if (id.Success && indent <= 6)
            {
                Flush();
                current = new Cls { Id = id.Groups[1].Value };
                classes.Add(current);
                continue;
            }

            if (t.StartsWith("}")) { Flush(); continue; }

            // ══ A QUOTED VALUE MAY CONTAIN A COMMA ═══════════════════════════
            //
            // The first version of this stopped at the first comma, so every
            // description with one in it — "Learn Last Stand, and refuse briefly." —
            // came back empty and the check reported ten nodes as undocumented. The
            // data was correct and the reader was not, which is the worst way for a
            // check to fail: it accuses the thing it is meant to protect.
            //
            // Two patterns rather than one, so a quoted string is read to its CLOSING
            // quote and a bare number is read to the end of the line.
            Match quoted = Regex.Match(t, "^\"([A-Za-z]+)\":\\s*\"(.*)\",?$");

            if (quoted.Success)
            {
                block[quoted.Groups[1].Value] = quoted.Groups[2].Value;
                continue;
            }

            Match bare = Regex.Match(t, "^\"([A-Za-z]+)\":\\s*([^\",]+?),?$");

            if (bare.Success) block[bare.Groups[1].Value] = bare.Groups[2].Value;
        }

        Flush();
        return classes;
    }
}
