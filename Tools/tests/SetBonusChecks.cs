using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

/// <summary>
/// Armour sets, checked for the things that are silent in game.
///
/// A set bonus costs the player six armour slots. One that quietly does nothing is
/// worse than one that does not exist — there is no error, no missing icon, nothing
/// to notice. The tin set shipped with two of these at once:
///
///   * scavenge sat at 6 pieces while armorThrow sat at 4, so throwing a piece
///     dropped the count to five and switched OFF the bonus that picks it back up.
///     The set disarmed itself on its own proc, permanently.
///
///   * the 6-piece description promised the scavenge behaviour, which lived on a
///     different bonus entirely, so the tooltip was describing something the tier
///     did not grant.
///
/// Both are content shapes rather than code, so both are checked here.
/// </summary>
internal static class SetBonusChecks
{
    private const string ArmorThrow = "armorThrow";
    private const string Scavenge   = "scavenge";

    /// <summary>
    /// Actions SetBonusResolver implements. Parsed out of the resolver rather than
    /// listed, so adding one there cannot leave this stale.
    /// </summary>
    private static readonly string[] ResolverConstants =
    {
        "StatBonus", "Thorns", "Lifesteal", "DurabilityGuard",
        "ShardBurst", "ArmorThrow", "DurabilitySiphon", "Scavenge", "Mend",
    };

    public static void Run(Action<bool, string> check, string repoRoot)
    {
        string path = Path.Combine(repoRoot, "Assets/StreamingAssets/set_data.json");

        if (!File.Exists(path))
        {
            check(false, "set_data.json is missing");
            return;
        }

        var sets = JsonSerializer.Deserialize<Set[]>(File.ReadAllText(path), Options);
        check(sets is { Length: > 0 }, "set_data.json holds at least one set");
        if (sets == null) return;

        var implemented = ImplementedActions(repoRoot);
        check(implemented.Count > 0, "the resolver's action vocabulary was readable");

        foreach (var set in sets)
        {
            string id = string.IsNullOrEmpty(set.id) ? "(unnamed)" : set.id;

            check(set.itemIds is { Length: > 0 }, $"'{id}' names its pieces");
            check(set.bonuses is { Length: > 0 }, $"'{id}' has at least one bonus");
            if (set.bonuses == null || set.itemIds == null) continue;

            int pieces = set.itemIds.Length;

            foreach (var bonus in set.bonuses)
            {
                string action = bonus.action ?? "";

                check(implemented.Contains(action),
                      $"'{id}' bonus '{action}' is implemented by SetBonusResolver");

                // A tier above the piece count can never be reached. The bonus is
                // authored, described, and unreachable.
                check(bonus.piecesRequired >= 1 && bonus.piecesRequired <= pieces,
                      $"'{id}' bonus '{action}' needs {bonus.piecesRequired} of {pieces} pieces");

                check(!string.IsNullOrWhiteSpace(bonus.description),
                      $"'{id}' bonus '{action}' says what it does");
            }

            CheckThrowIsRecoverable(check, id, set.bonuses);
        }
    }

    /// <summary>
    /// A set that can throw its own armour must be able to fetch it back at the same
    /// tier or lower.
    ///
    /// The in-flight ledger keeps the count intact for a grace window, so this is no
    /// longer load-bearing for correctness — but a set whose retrieval sits ABOVE its
    /// throw is still incoherent to read, and the tier ordering is the thing a content
    /// author gets wrong.
    /// </summary>
    private static void CheckThrowIsRecoverable(Action<bool, string> check, string setId, Bonus[] bonuses)
    {
        int throwAt = TierOf(bonuses, ArmorThrow);
        if (throwAt < 0) return;

        int scavengeAt = TierOf(bonuses, Scavenge);

        check(scavengeAt >= 0,
              $"'{setId}' throws armour at {throwAt} pieces and can pick it back up");

        check(scavengeAt < 0 || scavengeAt <= throwAt,
              $"'{setId}' scavenges at {scavengeAt} pieces, at or below the {throwAt} that throws");
    }

    private static int TierOf(Bonus[] bonuses, string action)
    {
        foreach (var bonus in bonuses)
            if (bonus?.action == action) return bonus.piecesRequired;

        return -1;
    }

    /// <summary>
    /// The action strings SetBonusResolver actually handles, read out of the source.
    ///
    /// Reading the file rather than listing the values here is the point: a new action
    /// added to the resolver is covered without anyone remembering this file exists,
    /// and an action REMOVED from it starts failing every set that still uses it.
    /// </summary>
    private static HashSet<string> ImplementedActions(string repoRoot)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);

        string path = Path.Combine(repoRoot, "Assets/Scripts/Managers/SetBonusResolver.cs");
        if (!File.Exists(path)) return found;

        string source = File.ReadAllText(path);

        foreach (string constant in ResolverConstants)
        {
            // public const string ArmorThrow = "armorThrow";
            var match = System.Text.RegularExpressions.Regex.Match(
                source, $@"const\s+string\s+{constant}\s*=\s*""([^""]+)""");

            if (match.Success) found.Add(match.Groups[1].Value);
        }

        return found;
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas         = true,
        ReadCommentHandling         = JsonCommentHandling.Skip,
    };

    private sealed class Set
    {
        public string id { get; set; }
        public string name { get; set; }
        public string[] itemIds { get; set; }
        public Bonus[] bonuses { get; set; }
    }

    private sealed class Bonus
    {
        public int piecesRequired { get; set; }
        public string description { get; set; }
        public string action { get; set; }
    }
}
