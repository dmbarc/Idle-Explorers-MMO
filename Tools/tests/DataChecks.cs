using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

/// <summary>
/// Checks on the content files themselves.
///
/// Every one of these is hand-edited JSON, and a misplaced brace in one of them does
/// not fail at build time — ContentManager parses at runtime, logs, and the game comes
/// up with no items or no zones. The symptom is a world with nothing in it, which
/// looks like a code fault and is a comma.
///
/// Parsing them here costs nothing and catches it before Unity is even opened.
/// </summary>
internal static class DataChecks
{
    private static readonly string[] Files =
    {
        "base_stats.json", "class_data.json", "item_data.json", "merge_recipes.json",
        "monster_data.json", "recipe_data.json", "set_data.json", "shop_data.json",
        "skill_data.json", "slot_unlock.json", "spec_data.json", "zone_data.json",
    };

    public static void Run(Action<bool, string> check, string repoRoot)
    {
        string dir = Path.Combine(repoRoot, "Assets", "StreamingAssets");

        foreach (var name in Files)
        {
            string path = Path.Combine(dir, name);

            if (!File.Exists(path))
            {
                check(false, $"{name} exists");
                continue;
            }

            try
            {
                using var _ = JsonDocument.Parse(File.ReadAllText(path));
                check(true, $"{name} is valid JSON");
            }
            catch (JsonException e)
            {
                check(false, $"{name} is valid JSON — {e.Message}");
            }
        }

        ItemIds(check, dir);
        SetBonuses(check, dir);
    }

    /// <summary>
    /// No two items may share an id.
    ///
    /// GetItem takes the first match, so a duplicate does not error — it quietly makes
    /// one of the two unreachable, including from every recipe and drop table that
    /// names it.
    /// </summary>
    private static void ItemIds(Action<bool, string> check, string dir)
    {
        string path = Path.Combine(dir, "item_data.json");
        if (!File.Exists(path)) return;

        JsonDocument document;
        try { document = JsonDocument.Parse(File.ReadAllText(path)); }
        catch (JsonException) { return; }   // already reported above

        using (document)
        {
            var seen = new HashSet<string>();
            var duplicates = new List<string>();

            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (!item.TryGetProperty("id", out var id)) continue;

                string value = id.GetString();
                if (!seen.Add(value)) duplicates.Add(value);
            }

            check(duplicates.Count == 0,
                  $"item_data.json has no duplicate ids ({string.Join(", ", duplicates)})");
        }
    }

    /// <summary>
    /// Every set bonus names an action the resolver actually handles, and every piece
    /// it needs exists.
    ///
    /// A typo in an action is a bonus that is listed in the tooltip, promised to the
    /// player, and silently never fires — which is indistinguishable from bad luck on
    /// a 7% proc.
    /// </summary>
    private static void SetBonuses(Action<bool, string> check, string dir)
    {
        string setPath  = Path.Combine(dir, "set_data.json");
        string itemPath = Path.Combine(dir, "item_data.json");
        if (!File.Exists(setPath) || !File.Exists(itemPath)) return;

        // Kept in step with SetBonusResolver.Actions by hand, deliberately: the point
        // is to notice when the data names something the code does not implement.
        var known = new HashSet<string>
        {
            "statBonus", "thorns", "lifesteal", "durabilityGuard",
            "shardBurst", "armorThrow", "durabilitySiphon", "scavenge", "mend",
        };

        var items = new HashSet<string>();
        try
        {
            using var itemDoc = JsonDocument.Parse(File.ReadAllText(itemPath));
            foreach (var item in itemDoc.RootElement.EnumerateArray())
                if (item.TryGetProperty("id", out var id)) items.Add(id.GetString());
        }
        catch (JsonException) { return; }

        try
        {
            using var setDoc = JsonDocument.Parse(File.ReadAllText(setPath));

            foreach (var set in setDoc.RootElement.EnumerateArray())
            {
                string setId = set.TryGetProperty("id", out var sid) ? sid.GetString() : "?";

                if (set.TryGetProperty("itemIds", out var pieces))
                    foreach (var piece in pieces.EnumerateArray())
                        check(items.Contains(piece.GetString()),
                              $"set '{setId}' names item '{piece.GetString()}', which exists");

                if (!set.TryGetProperty("bonuses", out var bonuses)) continue;

                foreach (var bonus in bonuses.EnumerateArray())
                {
                    if (!bonus.TryGetProperty("action", out var action)) continue;

                    check(known.Contains(action.GetString()),
                          $"set '{setId}' uses action '{action.GetString()}', which the resolver handles");
                }
            }
        }
        catch (JsonException) { /* already reported */ }
    }
}
