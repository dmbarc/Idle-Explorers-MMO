using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

/// <summary>
/// The crafting economy, checked against the rates it was priced from.
///
/// ══ WHY THESE NUMBERS NEED A TEST ═════════════════════════════════════════════
///
/// A tin chestplate used to cost eight bars. In a game designed so a character can
/// mine for twenty-four hours and come back with seventeen thousand ore, eight bars
/// is not a cost, it is a rounding error. The rebalance made a chestplate cost
/// thousands, which is the intent -- but "thousands" is only meaningful relative to
/// how fast ore actually arrives, and that rate lives in a different file.
///
/// So the assertions below do not hard-code costs. They recompute the hours a recipe
/// implies, from the same node rates the game runs on, and check the result lands in
/// the intended band. Edit either side and the check still means something; break the
/// relationship and it fails.
///
/// ══ THE DESIGN RULE ═══════════════════════════════════════════════════════════
///
/// Ore is the bottleneck. The forge never is. A player must always be able to smelt
/// everything they mined in less time than the mining took, or the anvil becomes a
/// second grind stacked on top of the first and the whole loop sours.
/// </summary>
internal static class EconomyChecks
{
    // ── The measured baseline, from zone_data.json and SkillNodeController ─────

    /// <summary>Seconds per gather action, the Inspector default on every node.</summary>
    private const double NodeSeconds = 3.0;

    /// <summary>Offline rate on the starter map's gathering nodes.</summary>
    private const double NodeAfkRate = 0.6;

    /// <summary>Offline rate on a station. Stations accrue slower than nodes by design.</summary>
    private const double StationAfkRate = 0.5;

    private static double GatherPerHour => 3600.0 / NodeSeconds * NodeAfkRate;   // 720

    /// <summary>
    /// Intended cost of one chestplate, in hours of uninterrupted offline play.
    ///
    /// Wide bands on purpose. This is not pinning a number, it is catching the two
    /// failures that matter: a tier that costs minutes, and a tier that costs a month.
    /// </summary>
    private static readonly (string Tier, string Recipe, double MinHours, double MaxHours)[] Chestplates =
    {
        ("tin",    "smith_tin_platebody",     5.0,  15.0),
        ("copper", "smith_copper_platebody", 20.0,  60.0),
        ("bronze", "smith_bronze_platebody", 70.0, 220.0),
    };

    /// <summary>Slots in descending cost order. A helmet must never cost more than a chestplate.</summary>
    private static readonly string[] SlotsHeaviestFirst =
        { "platebody", "legguards", "helmet", "shoulders", "boots", "gloves" };

    private sealed class Recipe
    {
        public string Id;
        public string SkillId;
        public string OutputItemId;
        public long   OutputQuantity;
        public double XpPerCraft;
        public double SecondsPerCraft;
        public readonly List<(string ItemId, long Quantity)> Inputs = new();
    }

    public static void Run(Action<bool, string> check, string repoRoot)
    {
        var recipes = LoadRecipes(repoRoot);
        var items   = LoadItemIds(repoRoot);

        check(recipes.Count > 0, $"recipe_data.json parses (found {recipes.Count} recipes)");
        check(items.Count > 0,   $"item_data.json parses (found {items.Count} items)");
        if (recipes.Count == 0 || items.Count == 0) return;

        Referential(check, recipes, items);
        ForgeKeepsUpWithTheMine(check, recipes);
        CostOrdering(check, recipes);
        ChestplateHours(check, recipes);
        XpIsProportionateToEffort(check, recipes);
    }

    // ── Nothing names something that does not exist ───────────────────────────

    private static void Referential(Action<bool, string> check,
                                    Dictionary<string, Recipe> recipes,
                                    HashSet<string> items)
    {
        foreach (var recipe in recipes.Values)
        {
            check(items.Contains(recipe.OutputItemId),
                  $"{recipe.Id} produces '{recipe.OutputItemId}', which item_data.json defines");

            foreach (var (itemId, quantity) in recipe.Inputs)
            {
                check(items.Contains(itemId),
                      $"{recipe.Id} consumes '{itemId}', which item_data.json defines");

                // A zero or negative input is a recipe that produces something from
                // nothing -- the one shape of content bug that mints items.
                check(quantity > 0,
                      $"{recipe.Id} asks for a positive quantity of '{itemId}' (got {quantity})");
            }

            check(recipe.OutputQuantity > 0,
                  $"{recipe.Id} produces a positive quantity (got {recipe.OutputQuantity})");

            check(recipe.SecondsPerCraft > 0d,
                  $"{recipe.Id} takes a positive time (got {recipe.SecondsPerCraft})");
        }
    }

    // ── The design rule ───────────────────────────────────────────────────────

    /// <summary>
    /// Every smelting recipe must be able to consume ore faster than a mine produces
    /// it. If it cannot, the player finishes a day of mining and then owes the anvil
    /// another day just to convert what they already have.
    /// </summary>
    private static void ForgeKeepsUpWithTheMine(Action<bool, string> check,
                                                Dictionary<string, Recipe> recipes)
    {
        foreach (var recipe in recipes.Values)
        {
            if (!recipe.Id.StartsWith("smelt_", StringComparison.Ordinal)) continue;

            double barsPerHour = 3600.0 / recipe.SecondsPerCraft * StationAfkRate;

            long orePerBar = 0;
            foreach (var (_, quantity) in recipe.Inputs) orePerBar += quantity;

            double oreConsumedPerHour = barsPerHour * orePerBar;

            check(oreConsumedPerHour > GatherPerHour,
                  $"{recipe.Id} smelts faster than a mine fills " +
                  $"({oreConsumedPerHour:0} ore/hr consumed vs {GatherPerHour:0} mined)");
        }
    }

    // ── Cost ordering ─────────────────────────────────────────────────────────

    private static void CostOrdering(Action<bool, string> check, Dictionary<string, Recipe> recipes)
    {
        foreach (string tier in new[] { "tin", "copper", "bronze" })
        {
            double previous = double.MaxValue;
            string previousSlot = null;

            foreach (string slot in SlotsHeaviestFirst)
            {
                if (!recipes.TryGetValue($"smith_{tier}_{slot}", out var recipe)) continue;

                double cost = RawMaterialCost(recipe, recipes);

                if (previousSlot != null)
                    check(cost <= previous,
                          $"{tier} {slot} costs no more than {tier} {previousSlot} " +
                          $"({cost:0} vs {previous:0} raw materials)");

                previous     = cost;
                previousSlot = slot;
            }
        }

        // And each tier must cost more than the one below it, or there is no reason
        // to ever craft the better set.
        foreach (string slot in SlotsHeaviestFirst)
        {
            double tin    = CostOf($"smith_tin_{slot}", recipes);
            double copper = CostOf($"smith_copper_{slot}", recipes);
            double bronze = CostOf($"smith_bronze_{slot}", recipes);

            if (tin <= 0d || copper <= 0d || bronze <= 0d) continue;

            check(copper > tin,    $"copper {slot} costs more than tin {slot} ({copper:0} vs {tin:0})");
            check(bronze > copper, $"bronze {slot} costs more than copper {slot} ({bronze:0} vs {copper:0})");
        }
    }

    // ── The headline figure ───────────────────────────────────────────────────

    private static void ChestplateHours(Action<bool, string> check, Dictionary<string, Recipe> recipes)
    {
        foreach (var (tier, id, minHours, maxHours) in Chestplates)
        {
            if (!recipes.TryGetValue(id, out var recipe))
            {
                check(false, $"{id} exists");
                continue;
            }

            double hours = TotalHours(recipe, recipes);

            check(hours >= minHours && hours <= maxHours,
                  $"a {tier} chestplate is {hours:0.0} h of offline play, " +
                  $"inside the intended {minHours:0}-{maxHours:0} h");
        }
    }

    // ── XP ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Armour XP has to stay small relative to the smelting that feeds it.
    ///
    /// Before the rebalance a tin chestplate paid 110 XP for eight bars, about 14 per
    /// bar. Keeping that ratio at two thousand bars would have paid 27,500 XP for one
    /// craft and capped smithing on the spot. The action count lives in the smelting,
    /// so the XP does too, and the armour pays a completion bonus rather than a
    /// per-material rate.
    /// </summary>
    private static void XpIsProportionateToEffort(Action<bool, string> check,
                                                  Dictionary<string, Recipe> recipes)
    {
        foreach (var recipe in recipes.Values)
        {
            if (!recipe.Id.StartsWith("smith_", StringComparison.Ordinal)) continue;

            double bars = 0d;
            foreach (var (_, quantity) in recipe.Inputs) bars += quantity;
            if (bars <= 0d) continue;

            double xpPerInput = recipe.XpPerCraft / bars;

            check(xpPerInput < 1.0,
                  $"{recipe.Id} pays {xpPerInput:0.000} xp per material consumed, " +
                  "well under the smelting rate that feeds it");

            check(recipe.XpPerCraft > 0d, $"{recipe.Id} pays some xp for finishing");
        }
    }

    // ── Cost model ────────────────────────────────────────────────────────────

    private static double CostOf(string recipeId, Dictionary<string, Recipe> recipes) =>
        recipes.TryGetValue(recipeId, out var recipe) ? RawMaterialCost(recipe, recipes) : 0d;

    /// <summary>
    /// Total raw materials behind one output, expanding intermediate recipes.
    ///
    /// A bronze bar is not a raw material -- it is four tin ore and eight copper ore,
    /// and comparing bronze gear to tin gear by bar count alone would understate it
    /// twelvefold.
    /// </summary>
    private static double RawMaterialCost(Recipe recipe, Dictionary<string, Recipe> recipes,
                                          int depth = 0)
    {
        if (depth > 8) return 0d;   // content is hand-authored; a cycle must not hang the suite

        double total = 0d;

        foreach (var (itemId, quantity) in recipe.Inputs)
        {
            var producer = FindProducer(itemId, recipes);

            total += producer == null
                ? quantity
                : quantity * RawMaterialCost(producer, recipes, depth + 1)
                           / Math.Max(1L, producer.OutputQuantity);
        }

        return total;
    }

    /// <summary>
    /// Offline hours to produce one output: gathering the raw materials, plus the
    /// station time for every intermediate craft along the way.
    ///
    /// Deliberately approximate. Bones drop from monsters rather than a node, and a
    /// player alternates between two ore types rather than mining them in series. The
    /// figure is an order-of-magnitude sanity check on the cost curve, not a promise
    /// to the player, and the assertion bands are wide enough to say so.
    /// </summary>
    private static double TotalHours(Recipe recipe, Dictionary<string, Recipe> recipes)
    {
        double gatherHours = RawMaterialCost(recipe, recipes) / GatherPerHour;
        double craftHours  = CraftHours(recipe, recipes);

        return gatherHours + craftHours;
    }

    private static double CraftHours(Recipe recipe, Dictionary<string, Recipe> recipes, int depth = 0)
    {
        if (depth > 8) return 0d;

        double hours = recipe.SecondsPerCraft / 3600.0 / StationAfkRate;

        foreach (var (itemId, quantity) in recipe.Inputs)
        {
            var producer = FindProducer(itemId, recipes);
            if (producer == null) continue;

            double runs = quantity / (double)Math.Max(1L, producer.OutputQuantity);
            hours += runs * CraftHours(producer, recipes, depth + 1);
        }

        return hours;
    }

    private static Recipe FindProducer(string itemId, Dictionary<string, Recipe> recipes)
    {
        foreach (var candidate in recipes.Values)
            if (candidate.OutputItemId == itemId) return candidate;

        return null;
    }

    // ── Loading ───────────────────────────────────────────────────────────────

    private static Dictionary<string, Recipe> LoadRecipes(string repoRoot)
    {
        var byId = new Dictionary<string, Recipe>();

        string path = Path.Combine(repoRoot, "Assets", "StreamingAssets", "recipe_data.json");
        if (!File.Exists(path)) return byId;

        using var document = JsonDocument.Parse(File.ReadAllText(path));

        foreach (var element in document.RootElement.EnumerateArray())
        {
            var recipe = new Recipe
            {
                Id              = Text(element, "id"),
                SkillId         = Text(element, "skillId"),
                OutputItemId    = Text(element, "outputItemId"),
                OutputQuantity  = Number(element, "outputQuantity", 1),
                XpPerCraft      = Number(element, "xpPerCraft", 0),
                SecondsPerCraft = Number(element, "baseSecondsPerCraft", 3),
            };

            if (element.TryGetProperty("inputs", out var inputs))
                foreach (var input in inputs.EnumerateArray())
                    recipe.Inputs.Add((Text(input, "itemId"), Number(input, "quantity", 1)));

            if (!string.IsNullOrEmpty(recipe.Id)) byId[recipe.Id] = recipe;
        }

        return byId;
    }

    private static HashSet<string> LoadItemIds(string repoRoot)
    {
        var ids = new HashSet<string>();

        string path = Path.Combine(repoRoot, "Assets", "StreamingAssets", "item_data.json");
        if (!File.Exists(path)) return ids;

        using var document = JsonDocument.Parse(File.ReadAllText(path));

        foreach (var element in document.RootElement.EnumerateArray())
        {
            string id = Text(element, "id");
            if (!string.IsNullOrEmpty(id)) ids.Add(id);
        }

        return ids;
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : "";

    private static long Number(JsonElement element, string name, long fallback) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? (long)value.GetDouble()
            : fallback;
}
