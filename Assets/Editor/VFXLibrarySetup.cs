using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Builds Assets/Resources/VFXLibrary.asset by matching effect ids to particle
/// prefabs already imported in the VFX packs.
///
/// Matching is by prefab file name rather than path, so re-importing or moving a
/// pack does not break it — the same approach IconLibrarySetup uses for sprites.
/// Ids with no match are left out on purpose: AbilityVFX simply plays nothing,
/// which is a missing visual rather than a broken ability.
///
/// Menu: Idle Explorers → Rebuild VFX Library
/// </summary>
public static class VFXLibrarySetup
{
    private const string ASSET_PATH = "Assets/Resources/VFXLibrary.asset";

    /// <summary>
    /// effectId → prefab file name. Primary source is Cartoon FX Remaster (JMO
    /// Assets); the Unity Particle Pack and Kenney sets cover anything it lacks.
    /// </summary>
    private static readonly Dictionary<string, string> EffectPrefabs = new()
    {
        // Ability effect-type defaults, keyed to PlayerController.AbilityVfxId
        { "impact",     "CFXR Hit A (Red)"            },
        { "aoe_burst",  "CFXR Explosion 1"            },
        { "heal",       "CFXR Magic Poof"             },
        { "haste_aura", "CFXR3 Magic Aura A (Runic)"  },
        { "frost",      "CFXR3 Hit Ice B (Air)"       },
        { "smoke",      "CFXR3 Hit Misc F Smoke"      },
        { "blink",      "CFXR Flash"                  },

        // Item procs and summons
        { "lightning_ball", "CFXR3 Hit Electric C (Air)" },
        { "turret",         "CFXR3 LightGlow A (Loop)"   },

        // Cosmetic auras — looping, so they persist while the item is worn
        { "aura_ember",  "CFXR Fire"                   },
        { "aura_runic",  "CFXR3 Magic Aura A (Runic)"  },
        { "aura_glow",   "CFXR3 LightGlow A (Loop)"    },
    };

    [MenuItem("Idle Explorers/Rebuild VFX Library")]
    public static void Rebuild()
    {
        const string resourcesDir = "Assets/Resources";
        if (!Directory.Exists(resourcesDir)) Directory.CreateDirectory(resourcesDir);

        var library = AssetDatabase.LoadAssetAtPath<VFXLibrary>(ASSET_PATH);
        bool isNew = library == null;
        if (isNew) library = ScriptableObject.CreateInstance<VFXLibrary>();

        library.effects.Clear();

        int found = 0;
        foreach (var pair in EffectPrefabs)
        {
            var prefab = FindPrefab(pair.Value);
            if (prefab == null)
            {
                // Not an error: the effect just does not render.
                Debug.Log($"[VFXLibrary] No prefab named '{pair.Value}' for effect '{pair.Key}' — skipped.");
                continue;
            }

            library.effects.Add(new VFXLibrary.Entry { id = pair.Key, prefab = prefab });
            found++;
        }

        if (isNew) AssetDatabase.CreateAsset(library, ASSET_PATH);
        else       EditorUtility.SetDirty(library);

        library.InvalidateCache();
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Debug.Log($"[VFXLibrary] {found}/{EffectPrefabs.Count} effects → {ASSET_PATH}");
    }

    /// <summary>Finds a prefab asset by exact file name anywhere under Assets.</summary>
    private static GameObject FindPrefab(string fileName)
    {
        foreach (var guid in AssetDatabase.FindAssets($"\"{fileName}\" t:Prefab"))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (!string.Equals(Path.GetFileNameWithoutExtension(path), fileName,
                               System.StringComparison.OrdinalIgnoreCase))
                continue;

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab != null) return prefab;
        }
        return null;
    }
}
