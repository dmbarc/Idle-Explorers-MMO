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
    private static readonly Dictionary<string, string> ItemIconNames = new()
    {
        // Currency
        { "coins",         "gold_coins_many" },

        // Ores and bars. The RGP pack has actual rock and ingot art, which reads far
        // better than the single generic metal icon these all used to share.
        { "copper_ore",    "stone_basic_grey" },
        { "tin_ore",       "stone_blocks_grey" },
        { "iron_ore",      "UI_Graphic_Resource_Iron" },
        { "mithril_ore",   "gem_diamond_red" },
        { "gold_ore",      "UI_Graphic_Resource_Gems" },
        { "bronze_bar",    "gold_bars_three" },
        { "iron_bar",      "silver_bars" },

        // Wood
        { "normal_logs",   "wood_log" },
        { "oak_logs",      "wood_logs_three" },
        { "willow_logs",   "wood_log_single_birch" },

        // Raw fish
        { "raw_shrimp",    "fish_green" },
        { "raw_trout",     "Pike_1" },
        { "raw_lobster",   "Pike_1" },

        // Cooked food
        { "shrimp",        "Potato_Fish_Bowl" },
        { "gritty_shrimp", "Chili_Bowl" },
        { "trout",         "Meat_1" },
        { "lobster",       "Wine_and_Meat" },

        // Combat drops. These five had no mapping at all and fell through to
        // generated placeholders despite matching art sitting in the RGP pack.
        { "bones",         "bone_white" },
        { "skull",         "bone_skull" },
        { "troll_hide",    "Helmet_1" },
        { "dragon_bones",  "bone_white" },
        { "dragon_scale",  "gem_diamond_red" },

        // Equipment
        { "iron_sword",    "sword_basic_blue" },
        { "magic_staff",   "Talisman_1" },

        // Arcane
        { "chaos_rune",    "Oil_lamp" },
        { "death_rune",    "Talisman_1" },
        { "faint_residue", "mushroom_big_red" },

        // Misc
        { "coal",          "Grain_Barrel" },
        { "mystic_gem",    "gem_diamond_red" },

        // Starter equipment catalogue
        { "iron_helm",           "Helmet_2" },
        { "travelers_cape",      "cape_hood_darkyellow" },
        { "bronze_platebody",    "Weapon_Armor" },
        { "linen_shirt",         "Wool" },
        { "leather_gloves",      "pouch_leather_small" },
        { "guild_tabard",        "scroll_map2" },
        { "emberlight_aura",     "bg_swhirl_yellow" },
        { "ring_of_the_glutton", "ring_gold_magic" },
        { "stormcallers_band",   "ring_gold_magic" },
        { "miners_charm",        "necklace_silver_red" },
        { "pendant_of_vigor",    "necklace_silver_red" },
        { "whetstone_trinket",   "stone_basic_grey" },
        { "swiftness_trinket",   "key_silver" },
        { "campfire_sprite",     "Candle 1-0" },
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

        foreach (var pair in source)
        {
            var sprite = FindSprite(pair.Value);
            if (sprite == null)
            {
                // Not an error: the id simply keeps its generated placeholder.
                Debug.Log($"[IconLibrary] No sprite named '{pair.Value}' for {kind} '{pair.Key}' — using placeholder.");
                continue;
            }

            target.Add(new IconLibrary.Entry { id = pair.Key, sprite = sprite });
            found++;
        }

        return found;
    }

    /// <summary>Finds a Sprite asset by file name anywhere under Assets.</summary>
    private static Sprite FindSprite(string fileName)
    {
        foreach (var guid in AssetDatabase.FindAssets($"{fileName} t:Sprite"))
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
