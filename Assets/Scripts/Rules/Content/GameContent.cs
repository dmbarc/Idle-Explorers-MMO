using System;
using System.Collections.Generic;

namespace IdleExplorers.Rules
{
    /// <summary>
    /// The authored game, indexed.
    ///
    /// ══ WHY THIS IS SHARED ════════════════════════════════════════════════════════
    ///
    /// The server has to answer the same questions the client answers -- what does this
    /// recipe cost, what does this monster drop, does this item exist -- and it has to
    /// answer them from the same twelve files. A parallel server-side catalogue would
    /// be a second index over the same data, and the two would eventually disagree
    /// about something small and expensive.
    ///
    /// So the INDEX is shared even though the loading is not. Unity fetches the files
    /// with UnityWebRequest and parses them with JsonUtility; the server reads them off
    /// disk with System.Text.Json. Both then hand the deserialised arrays to the same
    /// Ingest methods and ask the same questions of the result.
    ///
    /// Nothing here reads a file. Deserialisation belongs to the host, because the two
    /// hosts genuinely cannot share it -- one of them has no filesystem.
    ///
    /// ══ ON DUPLICATE IDS ══════════════════════════════════════════════════════════
    ///
    /// Last one wins, and it is reported. Silently keeping the first would make a
    /// duplicated id behave differently depending on file order, which is the kind of
    /// bug that survives for months.
    ///
    /// TODO(Phase 1): a content hash for the login payload, so a client with a stale
    /// catalogue is told to re-download rather than quietly disagreeing about prices.
    /// It belongs on the loader, which has the bytes, not here.
    /// </summary>
    public sealed class GameContent
    {
        public Dictionary<string, ItemData>    Items    { get; } = new Dictionary<string, ItemData>();
        public Dictionary<string, MonsterData> Monsters { get; } = new Dictionary<string, MonsterData>();
        public Dictionary<string, ZoneData>    Zones    { get; } = new Dictionary<string, ZoneData>();
        public Dictionary<string, MapData>     Maps     { get; } = new Dictionary<string, MapData>();
        public Dictionary<string, SkillData>   Skills   { get; } = new Dictionary<string, SkillData>();
        public Dictionary<string, ClassData>   Classes  { get; } = new Dictionary<string, ClassData>();
        public Dictionary<string, ItemSetData> ItemSets { get; } = new Dictionary<string, ItemSetData>();

        public List<CraftRecipe>            CraftRecipes { get; } = new List<CraftRecipe>();
        public List<MergeRecipe>            MergeRecipes { get; } = new List<MergeRecipe>();
        public List<SlotUnlockRequirement>  SlotUnlocks  { get; } = new List<SlotUnlockRequirement>();
        public List<SpecCombo>              SpecCombos   { get; } = new List<SpecCombo>();
        public List<RelicCoinPack>          CoinPacks    { get; } = new List<RelicCoinPack>();
        public List<ShopProduct>            ShopProducts { get; } = new List<ShopProduct>();

        /// <summary>
        /// The stat baseline every character starts from, before any class.
        ///
        /// Never null: a missing or malformed base_stats.json leaves an empty block
        /// rather than a null reference, so combat still runs -- badly, and loudly,
        /// but it runs.
        /// </summary>
        public StatBlock BaseStats { get; private set; } = new StatBlock();

        /// <summary>Every recipe by id, built once rather than scanned per lookup.</summary>
        private readonly Dictionary<string, CraftRecipe> _recipesById =
            new Dictionary<string, CraftRecipe>();

        // ── Ingest ────────────────────────────────────────────────────────────

        public void IngestItems(ItemData[] items) =>
            Index(Items, items, x => x?.id, "item");

        public void IngestMonsters(MonsterData[] monsters) =>
            Index(Monsters, monsters, x => x?.id, "monster");

        public void IngestSkills(SkillData[] skills) =>
            Index(Skills, skills, x => x?.id, "skill");

        public void IngestClasses(ClassData[] classes) =>
            Index(Classes, classes, x => x?.id, "class");

        public void IngestItemSets(ItemSetData[] sets) =>
            Index(ItemSets, sets, x => x?.id, "item set");

        /// <summary>Zones carry their maps inline, so both indexes fill in one pass.</summary>
        public void IngestZones(ZoneData[] zones)
        {
            if (zones == null) return;

            foreach (var zone in zones)
            {
                if (zone == null || string.IsNullOrEmpty(zone.id)) continue;

                Put(Zones, zone.id, zone, "zone");
                if (zone.maps == null) continue;

                foreach (var map in zone.maps)
                    if (map != null && !string.IsNullOrEmpty(map.id))
                        Put(Maps, map.id, map, "map");
            }
        }

        public void IngestCraftRecipes(CraftRecipe[] recipes)
        {
            if (recipes == null) return;

            foreach (var recipe in recipes)
            {
                if (recipe == null || string.IsNullOrEmpty(recipe.id)) continue;

                CraftRecipes.Add(recipe);
                Put(_recipesById, recipe.id, recipe, "recipe");
            }
        }

        public void IngestMergeRecipes(MergeRecipe[] recipes) => Append(MergeRecipes, recipes);
        public void IngestSlotUnlocks(SlotUnlockRequirement[] slots) => Append(SlotUnlocks, slots);
        public void IngestSpecCombos(SpecCombo[] combos) => Append(SpecCombos, combos);

        public void IngestShop(ShopCatalog catalogue)
        {
            if (catalogue == null) return;

            Append(CoinPacks, catalogue.coinPacks);
            Append(ShopProducts, catalogue.products);
        }

        public void IngestBaseStats(StatBlock stats)
        {
            if (stats != null) BaseStats = stats;
        }

        // ── Lookups ───────────────────────────────────────────────────────────

        public ItemData    GetItem(string id)    => Get(Items, id);
        public MonsterData GetMonster(string id) => Get(Monsters, id);
        public SkillData   GetSkill(string id)   => Get(Skills, id);
        public ClassData   GetClass(string id)   => Get(Classes, id);
        public MapData     GetMap(string id)     => Get(Maps, id);
        public ZoneData    GetZone(string id)    => Get(Zones, id);
        public ItemSetData GetItemSet(string id) => Get(ItemSets, id);
        public CraftRecipe GetRecipe(string id)  => Get(_recipesById, id);

        /// <summary>Every recipe a given station offers, in file order.</summary>
        public List<CraftRecipe> GetRecipesForStation(string stationType)
        {
            var results = new List<CraftRecipe>();
            if (string.IsNullOrEmpty(stationType)) return results;

            foreach (var recipe in CraftRecipes)
                if (recipe != null && recipe.stationType == stationType)
                    results.Add(recipe);

            return results;
        }

        public MergeRecipe GetMergeRecipe(string inputItemId)
        {
            if (string.IsNullOrEmpty(inputItemId)) return null;

            foreach (var recipe in MergeRecipes)
                if (recipe != null && recipe.inputItemId == inputItemId) return recipe;

            return null;
        }

        public RelicCoinPack GetCoinPack(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;

            foreach (var pack in CoinPacks)
                if (pack != null && pack.id == id) return pack;

            return null;
        }

        public ShopProduct GetShopProduct(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;

            foreach (var product in ShopProducts)
                if (product != null && product.id == id) return product;

            return null;
        }

        public bool IsSlotUnlocked(int slotIndex, int accountLevel, int highestCharLevel)
        {
            foreach (var req in SlotUnlocks)
                if (req != null && req.slot == slotIndex)
                    return accountLevel >= req.reqAccountLevel &&
                           highestCharLevel >= req.reqAnyCharLevel;

            return false;
        }

        // ── Validation ────────────────────────────────────────────────────────

        /// <summary>
        /// Every dangling reference in the catalogue, as readable lines.
        ///
        /// Run at import time on the server and at load time in the editor, because
        /// the failure modes are all silent otherwise: a recipe naming an item that
        /// does not exist produces nothing and says nothing, and a monster dropping a
        /// missing id simply never drops it. Neither looks like a bug from inside the
        /// game -- they look like bad luck.
        /// </summary>
        public List<string> Validate()
        {
            var problems = new List<string>();

            foreach (var recipe in CraftRecipes)
            {
                if (recipe == null) continue;

                if (!string.IsNullOrEmpty(recipe.outputItemId) && !Items.ContainsKey(recipe.outputItemId))
                    problems.Add($"recipe '{recipe.id}' outputs unknown item '{recipe.outputItemId}'");

                if (recipe.inputs == null) continue;

                foreach (var input in recipe.inputs)
                {
                    if (input == null || string.IsNullOrEmpty(input.itemId)) continue;

                    if (!Items.ContainsKey(input.itemId))
                        problems.Add($"recipe '{recipe.id}' needs unknown item '{input.itemId}'");

                    if (input.quantity <= 0L)
                        problems.Add($"recipe '{recipe.id}' asks for {input.quantity}x '{input.itemId}'");
                }
            }

            foreach (var pair in Monsters)
            {
                var loot = pair.Value?.lootTable;
                if (loot == null) continue;

                foreach (var entry in loot)
                {
                    if (entry == null || string.IsNullOrEmpty(entry.itemId)) continue;

                    if (!Items.ContainsKey(entry.itemId))
                        problems.Add($"monster '{pair.Key}' drops unknown item '{entry.itemId}'");

                    if (entry.maxQty < entry.minQty)
                        problems.Add($"monster '{pair.Key}' drops '{entry.itemId}' with max below min");
                }
            }

            foreach (var pair in ItemSets)
            {
                var pieces = pair.Value?.itemIds;
                if (pieces == null) continue;

                foreach (string itemId in pieces)
                    if (!string.IsNullOrEmpty(itemId) && !Items.ContainsKey(itemId))
                        problems.Add($"set '{pair.Key}' names unknown item '{itemId}'");
            }

            foreach (var recipe in MergeRecipes)
            {
                if (recipe == null) continue;

                if (!string.IsNullOrEmpty(recipe.inputItemId) && !Items.ContainsKey(recipe.inputItemId))
                    problems.Add($"merge '{recipe.id}' consumes unknown item '{recipe.inputItemId}'");

                if (!string.IsNullOrEmpty(recipe.outputItemId) && !Items.ContainsKey(recipe.outputItemId))
                    problems.Add($"merge '{recipe.id}' produces unknown item '{recipe.outputItemId}'");
            }

            problems.AddRange(_duplicates);
            return problems;
        }

        // ── Internals ─────────────────────────────────────────────────────────

        private readonly List<string> _duplicates = new List<string>();

        private void Index<T>(Dictionary<string, T> into, T[] rows,
                              Func<T, string> idOf, string label)
        {
            if (rows == null) return;

            foreach (var row in rows)
            {
                string id = idOf(row);
                if (string.IsNullOrEmpty(id)) continue;

                Put(into, id, row, label);
            }
        }

        private void Put<T>(Dictionary<string, T> into, string id, T row, string label)
        {
            if (into.ContainsKey(id))
                _duplicates.Add($"duplicate {label} id '{id}' -- the later one wins");

            into[id] = row;
        }

        private static void Append<T>(List<T> into, T[] rows)
        {
            if (rows == null) return;

            foreach (var row in rows)
                if (row != null) into.Add(row);
        }

        private static T Get<T>(Dictionary<string, T> from, string id) where T : class =>
            !string.IsNullOrEmpty(id) && from.TryGetValue(id, out T value) ? value : null;
    }
}
