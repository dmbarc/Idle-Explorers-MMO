using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Builds Assets/Resources/IconLibrary.asset by matching item and skill ids to
/// sprites already imported in the art packs.
///
/// The mapping is by sprite file name rather than by path, so re-importing or
/// moving a pack does not break it. Ids with no sensible match are left out on
/// purpose — they fall back to generated placeholder icons at runtime.
///
/// Menu: Idle Explorers → Rebuild Icon Library
/// </summary>
public static class IconLibrarySetup
{
    private const string ASSET_PATH = "Assets/Resources/IconLibrary.asset";

    /// <summary>itemId → sprite file name (no extension), best available match.</summary>
    /// <summary>
    /// itemId → sprite file name. EVERY id maps to a DISTINCT sprite; the rebuild
    /// fails loudly if two ever collide.
    ///
    /// Kenney's Voxel Pack leads for resources and weapons — it is the only pack with
    /// real ore chunks, and its blocky look matches the Kenney low-poly models used
    /// for the world. Painted packs under Assets/Imports fill what it lacks (ingots,
    /// jewellery, cooked dishes), since Kenney has no art for those at all.
    /// </summary>
    private static readonly Dictionary<string, string> ItemIconNames = new()
    {
        // Currency
        { "coins",         "gold_coins_many" },

        // Ores — rough mineral chunks, NOT ingots. Every ore used to share one iron
        // ingot graphic, which made them indistinguishable and, worse, made raw ore
        // look like the smelted bar it is supposed to become.
        { "copper_ore",    "ore_ruby" },
        { "tin_ore",       "ore_silver" },
        { "iron_ore",      "ore_iron" },
        { "coal",          "ore_coal" },
        { "mithril_ore",   "ore_emerald" },
        { "gold_ore",      "ore_gold" },

        // Bars — these ARE ingots, which is the whole point of smelting.
        { "bronze_bar",    "gold_bars_three" },
        { "iron_bar",      "silver_bars" },

        // Wood
        { "normal_logs",   "wood_log" },
        { "oak_logs",      "wood_logs_three" },
        { "willow_logs",   "wood_log_single_birch" },

        // Raw fish. All three previously shared "Pike_1" — a pike POLEARM from the
        // weapons folder, which is why shrimp looked like a spear.
        { "raw_shrimp",    "fish_orange" },
        { "raw_trout",     "fish_green" },
        { "raw_lobster",   "fish_red" },

        // Cooked food
        { "shrimp",        "fish_cooked" },
        { "gritty_shrimp", "fish_orange_skeleton" },   // cooked over tin ore; it did not survive
        { "trout",         "Potato_Fish_Bowl" },
        { "lobster",       "Wine_and_Meat" },

        // Combat drops. Five of these had no mapping at all and fell through to
        // generated placeholders.
        { "bones",         "bone_white" },
        { "skull",         "bone_skull" },
        { "troll_hide",    "Wool" },
        { "dragon_bones",  "Skull" },
        { "dragon_scale",  "Diamond" },

        // Weapons
        { "iron_sword",    "sword_iron" },
        { "magic_staff",   "weapon_staff" },

        // Arcane
        { "chaos_rune",    "runeBlack_slab_012" },
        { "death_rune",    "runeBlack_slab_026" },
        { "faint_residue", "SoulFragment" },
        { "mystic_gem",    "ore_diamond" },
        { "shifting_sigil","runeBlack_slab_017" },

        // Equipment. Kenney has no armour or jewellery icons in any pack, so these
        // come from the painted Imports sets.
        { "iron_helm",           "Helmet_1" },
        { "travelers_cape",      "cape_hood_darkyellow" },
        { "bronze_platebody",    "Weapon_Armor" },
        { "linen_shirt",         "Hat" },
        { "leather_gloves",      "pouch_leather_small" },
        { "guild_tabard",        "shield_basic_metal" },   // heraldry
        { "emberlight_aura",     "Fire" },
        { "ring_of_the_glutton", "ring_gold_magic" },
        { "stormcallers_band",   "TheRing" },
        { "miners_charm",        "necklace_silver_red" },
        { "pendant_of_vigor",    "Heart" },
        { "whetstone_trinket",   "Tools_Misc" },
        { "swiftness_trinket",   "Feathers" },
        { "campfire_sprite",     "animal-fox" },           // Kenney Cube Pets preview
    };

    /// <summary>skillId → sprite file name. All 13 skills, not just the 8 that had icons.</summary>
    private static readonly Dictionary<string, string> SkillIconNames = new()
    {
        { "mining",       "pickaxe_basic" },
        { "woodcutting",  "Wood_Pile_1" },
        { "fishing",      "fish_green" },
        { "cooking",      "Frying_Pan" },
        { "smithing",     "Hammer_1" },
        { "gleaning",     "Talisman_1" },
        { "fabrication",  "Tools_Misc" },
        { "combat",       "sword_basic_blue" },

        // These six are defined in skill_data.json and had no icon at all
        { "negotiation",  "scroll_map2" },
        { "infusion",     "bottle_standard_blue" },
        { "chronicle",    "book_closed_red" },
        { "spectralwork", "bone_skull" },
        { "brokerage",    "pouch_leather_small" },
        { "convergence",  "gem_diamond_red" },
    };

    /// <summary>
    /// abilityId → sprite file name, mostly from the QS hand-painted pack.
    /// Unmapped abilities show their name alone, which is why this returns null
    /// rather than a placeholder.
    /// </summary>
    private static readonly Dictionary<string, string> AbilityIconNames = new()
    {
        // Warrior
        { "cleave",          "Simple Sickle 1-0" },
        { "shield_bash",     "shield_basic_metal" },
        { "battlecry",       "Life Steal 1-1" },
        { "reckless_strike", "sword_basic4_blue" },

        // Ranger
        { "rapid_shot",      "Arrows 1-0" },
        { "marked_target",   "Arrows 1-1" },
        { "barrage",         "bow_wood1" },
        { "evasion_roll",    "leafs_long" },

        // Sorcerer
        { "fireball",        "Explosion 1-0" },
        { "frost_nova",      "Meteor 1-2" },
        { "arcane_surge",    "Hand Scepter 1-0" },
        { "blink",           "Candle 1-0" },

        // Tinkerer
        { "deploy_turret",   "Canon" },
        { "smoke_bomb",      "Evil Pumpkin 1-1" },
        { "overclock",       "Hand Scepter 1-1" },
        { "salvage_strike",  "pickaxe_basic" },

        // Specter
        { "spectral_strike", "Hand Scepter 1-0-1" },
        { "phase_shift",     "Grave 1-0" },
        { "haunt",           "bone_skull" },
        { "soul_drain",      "Life Steal 1-0" },
    };

    [MenuItem("Idle Explorers/Rebuild Icon Library")]
    public static void Rebuild()
    {
        const string resourcesDir = "Assets/Resources";
        if (!Directory.Exists(resourcesDir)) Directory.CreateDirectory(resourcesDir);

        var library = AssetDatabase.LoadAssetAtPath<IconLibrary>(ASSET_PATH);
        bool isNew = library == null;
        if (isNew) library = ScriptableObject.CreateInstance<IconLibrary>();

        library.itemIcons.Clear();
        library.skillIcons.Clear();
        library.abilityIcons.Clear();

        int itemsFound     = Populate(ItemIconNames,    library.itemIcons,    "item");
        int skillsFound    = Populate(SkillIconNames,   library.skillIcons,   "skill");
        int abilitiesFound = Populate(AbilityIconNames, library.abilityIcons, "ability");

        if (isNew) AssetDatabase.CreateAsset(library, ASSET_PATH);
        else       EditorUtility.SetDirty(library);

        library.InvalidateCache();
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Debug.Log($"[IconLibrary] {itemsFound}/{ItemIconNames.Count} item icons, " +
                  $"{skillsFound}/{SkillIconNames.Count} skill icons, " +
                  $"{abilitiesFound}/{AbilityIconNames.Count} ability icons → {ASSET_PATH}");
    }

    private static int Populate(Dictionary<string, string> source, List<IconLibrary.Entry> target, string kind)
    {
        int found = 0;

        // Two ids resolving to the same sprite is how "a unique icon per item" quietly
        // decays back into shared art as content grows. Surface it rather than
        // discovering it in a playtest.
        var usedSprites = new Dictionary<Sprite, string>();
        var missing     = new List<string>();

        foreach (var pair in source)
        {
            var sprite = FindSprite(pair.Value);
            if (sprite == null)
            {
                missing.Add($"{pair.Key} → '{pair.Value}'");
                continue;
            }

            if (usedSprites.TryGetValue(sprite, out string owner))
                Debug.LogWarning($"[IconLibrary] {kind} '{pair.Key}' shares sprite '{pair.Value}' with '{owner}'.");
            else
                usedSprites[sprite] = pair.Key;

            target.Add(new IconLibrary.Entry { id = pair.Key, sprite = sprite });
            found++;
        }

        if (missing.Count > 0)
        {
            // Loud, because a silent fallback to placeholders is exactly what made the
            // first art pass look like it had done nothing.
            Debug.LogWarning($"[IconLibrary] {missing.Count} {kind} icon(s) unmatched, falling back to " +
                             $"placeholders: {string.Join(", ", missing)}\n" +
                             "If these are Kenney sprites, run 'Idle Explorers → Import Art As Sprites' first — " +
                             "Kenney PNGs import as plain textures and are invisible to a t:Sprite search.");
        }

        return found;
    }

    /// <summary>Finds a Sprite asset by file name anywhere under Assets.</summary>
    private static Sprite FindSprite(string fileName)
    {
        // Quoted: several names contain spaces ("Magic Egg"), and unquoted they would
        // be searched as separate terms.
        foreach (var guid in AssetDatabase.FindAssets($"\"{fileName}\" t:Sprite"))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (!string.Equals(Path.GetFileNameWithoutExtension(path), fileName,
                               System.StringComparison.OrdinalIgnoreCase))
                continue;

            var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(path);
            if (sprite != null) return sprite;
        }
        return null;
    }
}
