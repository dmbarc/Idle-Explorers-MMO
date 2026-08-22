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
        { "coins",         "Coins 1" },

        // Ores and bars — one metal icon stands in for the family for now
        { "iron_ore",      "UI_Graphic_Resource_Iron" },
        { "copper_ore",    "UI_Graphic_Resource_Iron" },
        { "tin_ore",       "UI_Graphic_Resource_Iron" },
        { "mithril_ore",   "UI_Graphic_Resource_Gems" },
        { "gold_ore",      "UI_Graphic_Resource_Gems" },
        { "iron_bar",      "BlackSmith_Cooling_Barrel" },
        { "bronze_bar",    "BlackSmith_Cooling_Barrel" },

        // Wood
        { "normal_logs",   "Wood_Pile_1" },
        { "oak_logs",      "Wood_Pile_2" },
        { "willow_logs",   "Wood_Pile_2" },

        // Raw fish
        { "raw_shrimp",    "Pike_1" },
        { "raw_trout",     "Pike_1" },
        { "raw_lobster",   "Pike_1" },

        // Cooked food
        { "shrimp",        "Potato_Fish_Bowl" },
        { "trout",         "Meat_1" },
        { "lobster",       "Wine_and_Meat" },

        // Equipment
        { "iron_sword",    "Weapon_Armor" },
        { "magic_staff",   "Talisman_1" },

        // Arcane
        { "chaos_rune",    "Oil_lamp" },
        { "death_rune",    "Talisman_1" },
        { "faint_residue", "Mushroom_1" },

        // Misc
        { "coal",          "Grain_Barrel" },
        { "feathers",      "Feathers" },
    };

    /// <summary>skillId → sprite file name.</summary>
    private static readonly Dictionary<string, string> SkillIconNames = new()
    {
        { "mining",       "Hammer_1" },
        { "woodcutting",  "Wood_Pile_1" },
        { "fishing",      "Pike_1" },
        { "cooking",      "Frying_Pan" },
        { "smithing",     "Hammer_1" },
        { "gleaning",     "Talisman_1" },
        { "fabrication",  "Tools_Misc" },
        { "combat",       "Weapon_Armor" },
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

        int itemsFound  = Populate(ItemIconNames,  library.itemIcons,  "item");
        int skillsFound = Populate(SkillIconNames, library.skillIcons, "skill");

        if (isNew) AssetDatabase.CreateAsset(library, ASSET_PATH);
        else       EditorUtility.SetDirty(library);

        library.InvalidateCache();
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Debug.Log($"[IconLibrary] {itemsFound}/{ItemIconNames.Count} item icons, " +
                  $"{skillsFound}/{SkillIconNames.Count} skill icons → {ASSET_PATH}");
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
