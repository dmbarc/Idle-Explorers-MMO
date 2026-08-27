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

        /// <summary>
        /// Abilities that belong to no class.
        ///
        /// Every ability used to come from a class's talent tree, which is fine until
        /// an ITEM grants one -- the Goblin Destroyer's Whirling Throw belongs to a
        /// weapon, not to a spec, and putting it in a class tree would mean anyone
        /// with that class could use it without the weapon.
        /// </summary>
        public Dictionary<string, AbilityData> Abilities { get; } = new Dictionary<string, AbilityData>();

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

        public void IngestAbilities(AbilityData[] abilities) =>
            Index(Abilities, abilities, x => x?.id, "ability");

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

        /// <summary>
        /// An ability by id: class trees first, then the standalone catalogue.
        ///
        /// That order because a class ability is the common case and a standalone one
        /// is the exception -- and because if an id somehow existed in both, the class
        /// version is the one a player already has on their bar.
        /// </summary>
        public AbilityData GetAbility(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;

            foreach (var pair in Classes)
            {
                var abilities = pair.Value?.abilities;
                if (abilities == null) continue;

                foreach (var ability in abilities)
                    if (ability != null && ability.id == id) return ability;
            }

            return Get(Abilities, id);
        }

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

            foreach (var pair in Monsters)
            {
                var monster = pair.Value;
                if (monster is not { isBoss: true }) continue;

                if (monster.enrageSeconds <= 0f)
                {
                    // Without one the fight has no fail condition at all: the server
                    // cannot verify a dodge, so the clock IS the difficulty.
                    problems.Add($"boss '{pair.Key}' has no enrage timer");
                }

                if (monster.phases == null || monster.phases.Length == 0)
                {
                    problems.Add($"boss '{pair.Key}' has no phases");
                    continue;
                }

                foreach (var phase in monster.phases)
                {
                    if (phase == null) continue;

                    if (phase.fromHealthFraction is <= 0f or > 1f)
                        problems.Add($"boss '{pair.Key}' phase '{phase.name}' begins at {phase.fromHealthFraction}");

                    if (phase.abilities == null || phase.abilities.Length == 0)
                    {
                        problems.Add($"boss '{pair.Key}' phase '{phase.name}' has no abilities");
                        continue;
                    }

                    foreach (var ability in phase.abilities)
                    {
                        if (ability == null) continue;

                        if (!IsKnownShape(ability.shape))
                            problems.Add($"boss '{pair.Key}' ability '{ability.id}' has shape '{ability.shape}'");

                        // A telegraph of zero is unavoidable damage wearing a
                        // mechanic's clothes -- the wind-up is the whole thing the
                        // player is beating.
                        if (ability.telegraphSeconds <= 0f)
                            problems.Add($"boss '{pair.Key}' ability '{ability.id}' has no telegraph");

                        if (ability.range <= 0f)
                            problems.Add($"boss '{pair.Key}' ability '{ability.id}' has no range");

                        if (ability.shape == "ring" && ability.innerRadius >= ability.range)
                            problems.Add($"boss '{pair.Key}' ring '{ability.id}' is inside out");

                        if (ability.pulses < 1)
                            problems.Add($"boss '{pair.Key}' ability '{ability.id}' fires {ability.pulses} times");
                    }
                }
            }

            // Effects that name something else in the catalogue. Every one of these
            // fails SILENTLY in game -- a fuse whose result does not exist consumes
            // both halves and produces nothing, which is the worst outcome available.
            foreach (var pair in Items)
            {
                var effects = pair.Value?.effects;
                if (effects == null) continue;

                foreach (var effect in effects)
                {
                    if (effect == null) continue;

                    if (effect.action == "fuseInto")
                    {
                        if (!Items.ContainsKey(effect.param ?? ""))
                            problems.Add($"item '{pair.Key}' fuses into unknown item '{effect.param}'");

                        if (!string.IsNullOrEmpty(effect.requires) && !Items.ContainsKey(effect.requires))
                            problems.Add($"item '{pair.Key}' fuse requires unknown item '{effect.requires}'");
                    }

                    if (effect.action == "summonAlly" && !Monsters.ContainsKey(effect.param ?? ""))
                        problems.Add($"item '{pair.Key}' summons unknown monster '{effect.param}'");

                    if (effect.action == "grantAbility" && GetAbility(effect.param) is null)
                        problems.Add($"item '{pair.Key}' grants unknown ability '{effect.param}'");
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

            foreach (var pair in Items)
            {
                var item = pair.Value;
                if (item == null) continue;

                if (!string.IsNullOrEmpty(item.equipSlot) && !SlotFamilyExists(item.equipSlot))
                    problems.Add($"item '{pair.Key}' equips to unknown slot '{item.equipSlot}'");

                if (!item.IsWeapon) continue;

                if (item.weaponType != "melee" && item.weaponType != "ranged")
                    problems.Add($"item '{pair.Key}' has weaponType '{item.weaponType}'");

                // A weapon that is not in a hand is a weapon nobody can swing, and
                // nothing in the game would say so -- it equips, sits on an armour
                // slot, and contributes none of its damage.
                if (item.equipSlot != "mainhand" && item.equipSlot != "offhand")
                    problems.Add($"weapon '{pair.Key}' equips to '{item.equipSlot}' rather than a hand");

                if (item.damageMax < item.damageMin)
                    problems.Add($"weapon '{pair.Key}' has max damage below min");

                if (item.projectilesPerShot > 0 && !item.IsRanged)
                    problems.Add($"melee weapon '{pair.Key}' declares projectiles");
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

        /// <summary>
        /// Whether an ability's shape is one the hit test can resolve.
        ///
        /// Listed rather than parsed against the enum, because the enum lives beside
        /// this and a typo in content should be caught HERE with the id that has it,
        /// not thrown at runtime in the middle of a boss fight.
        /// </summary>
        private static bool IsKnownShape(string shape) =>
            shape is "single" or "circle" or "ring" or "line" or "cone";

        /// <summary>
        /// Whether a declared slot names anything real, exactly or as a family.
        ///
        /// Duplicated from EquipmentSlots deliberately: that type is Unity-side and the
        /// server cannot see it, but the RULE -- an item names a family, and ring1
        /// through ring10 all answer to "ring" -- has to hold on both. Kept to the
        /// prefix test alone so there is only one line that could drift, and
        /// SlotFamilies in the standalone suite asserts the two agree.
        /// </summary>
        private bool SlotFamilyExists(string declared)
        {
            foreach (string slotId in KnownSlotIds)
                if (slotId == declared || slotId.StartsWith(declared, StringComparison.Ordinal))
                    return true;

            return false;
        }

        /// <summary>
        /// Mirrors EquipmentSlots.All. Asserted equal by the standalone suite.
        ///
        /// Public rather than internal: the equipment endpoints resolve a slot family
        /// from it, and they live in another assembly. That is the right home for the
        /// list anyway -- it is the shared contract about what slots exist, and the
        /// server needs it as much as the catalogue does.
        /// </summary>
        public static readonly string[] KnownSlotIds =
        {
            "helmet", "cape", "chest", "shirt", "shoulders", "legs", "boots", "bracers",
            "gloves", "tabard", "aura",
            "mainhand", "offhand",
            "ring1", "ring2", "ring3", "ring4", "ring5",
            "ring6", "ring7", "ring8", "ring9", "ring10",
            "amulet1", "amulet2", "trinket1", "trinket2", "companion",
        };

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
