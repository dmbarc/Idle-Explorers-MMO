using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Reimports the art folders this game actually uses as Sprites.
///
/// **Why this has to exist.** Every one of the ~58,000 PNGs in the Kenney pack
/// imports as `textureType: Default`, not Sprite. A Default texture cannot be
/// assigned to `Image.sprite` or `SpriteRenderer.sprite` at all, and
/// `AssetDatabase.FindAssets("... t:Sprite")` does not return it — so the icon and
/// theme rebuilds would search Kenney, find nothing, log "no sprite named ...", and
/// silently fall back to placeholders. That failure is invisible unless you go
/// looking, which is exactly how the first art pass ended up doing nothing.
///
/// Deliberately SCOPED to the folders in use rather than the whole pack. Reimporting
/// 58,000 textures takes a very long time and would churn the whole library for art
/// the game never references.
///
/// Menu: Idle Explorers → Import Art As Sprites
/// </summary>
public static class SpriteImportSetup
{
    private const string Kenney = "Assets/Kenney Game Assets All-in-1 3.7.0/";

    /// <summary>
    /// Folders swept for PNGs, non-recursively where a pack nests variants we do not
    /// want (Kenney ships every sprite twice, at Default and Double resolution).
    /// </summary>
    private static readonly string[] Folders =
    {
        // ── Item icons ────────────────────────────────────────────────────────
        Kenney + "2D assets/Voxel Pack/PNG/Items",                       // ores, fish, tools, weapons
        Kenney + "2D assets/Fish Pack/PNG/Default",                      // fish variety
        Kenney + "2D assets/Scribble Dungeons/PNG/Default (64px)/Items", // staff, dagger, spear
        Kenney + "2D assets/Rune Pack/PNG/Black/Slab",                   // runes
        Kenney + "3D assets/Cube Pets/Previews",                         // companion pets
        Kenney + "Icons/Board Game Icons/PNG/Default (64px)",            // skull, campfire, pouch

        // ── UI chrome ─────────────────────────────────────────────────────────
        Kenney + "UI assets/UI Pack - Adventure/PNG/Default",            // panels, slot frames, scrollbars
        Kenney + "UI assets/UI Adventure Pack/PNG",                      // buttons WITH pressed states, 3-slice bars
        Kenney + "UI assets/Fantasy UI Borders/PNG/Default/Divider",     // ornate dividers
    };

    [MenuItem("Idle Explorers/Import Art As Sprites")]
    public static void Run() => Run(showDialog: true);

    public static void Run(bool showDialog)
    {
        var toReimport = new List<string>();

        foreach (var folder in Folders)
        {
            if (!AssetDatabase.IsValidFolder(folder))
            {
                Debug.LogWarning($"[SpriteImport] Missing folder, skipped: {folder}");
                continue;
            }

            foreach (var guid in AssetDatabase.FindAssets("t:Texture2D", new[] { folder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);

                // FindAssets recurses; keep to the exact folder so we do not also drag
                // in the Double-resolution twin of every sprite.
                if (System.IO.Path.GetDirectoryName(path)?.Replace('\\', '/') != folder) continue;

                if (AssetImporter.GetAtPath(path) is not TextureImporter importer) continue;
                if (importer.textureType == TextureImporterType.Sprite) continue;   // already done

                toReimport.Add(path);
            }
        }

        if (toReimport.Count == 0)
        {
            Debug.Log("[SpriteImport] All art folders already import as Sprites.");
            if (showDialog)
                EditorUtility.DisplayDialog("Nothing to do", "Every art folder already imports as Sprites.", "OK");
            return;
        }

        // One batch, so Unity does a single asset-database refresh instead of one per
        // texture — the difference between a few seconds and several minutes.
        AssetDatabase.StartAssetEditing();
        try
        {
            for (int i = 0; i < toReimport.Count; i++)
            {
                string path = toReimport[i];

                if (showDialog && EditorUtility.DisplayCancelableProgressBar(
                        "Importing art as sprites", path, i / (float)toReimport.Count))
                    break;

                if (AssetImporter.GetAtPath(path) is not TextureImporter importer) continue;

                importer.textureType         = TextureImporterType.Sprite;
                importer.spriteImportMode    = SpriteImportMode.Single;
                importer.alphaIsTransparency = true;

                // Icons are drawn at small sizes and read badly when blurred, and
                // these packs are already crisp flat art.
                importer.filterMode  = FilterMode.Bilinear;
                importer.mipmapEnabled = false;

                importer.SaveAndReimport();
            }
        }
        finally
        {
            AssetDatabase.StopAssetEditing();
            if (showDialog) EditorUtility.ClearProgressBar();
            AssetDatabase.Refresh();
        }

        Debug.Log($"[SpriteImport] Reimported {toReimport.Count} texture(s) as Sprites.");

        if (showDialog)
            EditorUtility.DisplayDialog("Art Imported",
                $"{toReimport.Count} textures now import as Sprites.\n\n" +
                "Icon and UI theme rebuilds can find them from here.", "OK");
    }
}
