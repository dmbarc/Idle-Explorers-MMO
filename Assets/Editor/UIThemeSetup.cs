using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Configures the UI art: sets 9-slice borders on the sprites the theme uses, then
/// assigns them to UITheme.asset.
///
/// Both halves are needed and the order matters. Image.Type.Sliced silently degrades
/// to a stretched quad when a sprite has a zero border, so assigning art without
/// setting borders first produces smeared corners that look like a rendering bug
/// rather than a missing import setting.
///
/// NOT ONE sprite in this project ships with a border — not GUI_Parts, not Fantasy
/// Wooden, and not Kenney, contrary to what its reputation suggests. Every sprite the
/// theme references is configured here.
///
/// Menu: Idle Explorers → Apply UI Sprite Theme
/// </summary>
public static class UIThemeSetup
{
    private const string THEME_PATH = "Assets/Resources/UITheme.asset";

    /// <summary>
    /// A sprite the theme uses, the field it fills, and its 9-slice border in pixels.
    /// Borders are left/bottom/right/top, matching Unity's Vector4 convention.
    /// </summary>
    private class SkinEntry
    {
        public string  Field;
        public string  SpriteName;
        public Vector4 Border;
        public bool    NoSlice;    // true for art that must not be sliced (bars, dividers)
    }

    /// <summary>
    /// Kenney's UI Pack - Adventure for panels and buttons (64x64 panels, 48x24
    /// buttons), Fantasy Wooden for the heavier window frames. Both are free and
    /// CC0/free-licence, so nothing here adds an attribution obligation.
    /// </summary>
    private static readonly SkinEntry[] Skins =
    {
        new SkinEntry { Field = "panelSprite",         SpriteName = "panel_brown",         Border = new Vector4(16, 16, 16, 16) },
        new SkinEntry { Field = "cardSprite",          SpriteName = "panel_brown_dark",    Border = new Vector4(16, 16, 16, 16) },
        new SkinEntry { Field = "headerSprite",        SpriteName = "panel_border_brown",  Border = new Vector4(16, 16, 16, 16) },
        new SkinEntry { Field = "buttonSprite",        SpriteName = "button_brown",        Border = new Vector4(8,  8,  8,  8)  },
        new SkinEntry { Field = "slotSprite",          SpriteName = "panel_grey",          Border = new Vector4(12, 12, 12, 12) },
        new SkinEntry { Field = "inputSprite",         SpriteName = "panel_grey_bolts",    Border = new Vector4(12, 12, 12, 12) },
        new SkinEntry { Field = "barBgSprite",         SpriteName = "panel_brown_dark",    Border = new Vector4(8,  8,  8,  8)  },

        // The fill of a Filled image is not sliced — it is masked by fillAmount — so
        // a bordered sprite here would distort as the bar drains.
        new SkinEntry { Field = "barFillSprite",       SpriteName = "Hp_line",             NoSlice = true },
        new SkinEntry { Field = "dividerSprite",       SpriteName = "divider-000",         NoSlice = true },
    };

    [MenuItem("Idle Explorers/Apply UI Sprite Theme")]
    public static void Apply() => Apply(showDialog: true);

    /// <summary>Dialog-free overload so "Setup Everything" can chain it.</summary>
    public static void Apply(bool showDialog)
    {
        var theme = AssetDatabase.LoadAssetAtPath<UITheme>(THEME_PATH);
        if (theme == null)
        {
            Debug.LogError($"[UITheme] Expected {THEME_PATH} — run 'Create UITheme Asset' first.");
            if (showDialog)
                EditorUtility.DisplayDialog("No UITheme",
                    $"Expected {THEME_PATH}.\n\nRun 'Idle Explorers → Create UITheme Asset' first.", "OK");
            return;
        }

        var so = new SerializedObject(theme);
        int applied = 0, missing = 0;

        foreach (var skin in Skins)
        {
            var property = so.FindProperty(skin.Field);
            if (property == null)
            {
                Debug.LogWarning($"[UITheme] No field '{skin.Field}' on UITheme — skipped.");
                continue;
            }

            string path = FindSpritePath(skin.SpriteName);
            if (path == null)
            {
                Debug.Log($"[UITheme] No sprite named '{skin.SpriteName}' for {skin.Field} — " +
                          "field left empty, that element keeps its flat colour.");
                missing++;
                continue;
            }

            if (!skin.NoSlice) ConfigureBorder(path, skin.Border);
            else               EnsureSpriteType(path);

            property.objectReferenceValue = AssetDatabase.LoadAssetAtPath<Sprite>(path);
            applied++;
        }

        so.ApplyModifiedProperties();
        EditorUtility.SetDirty(theme);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Debug.Log($"[UITheme] Applied {applied}/{Skins.Length} sprites ({missing} unmatched) → {THEME_PATH}");

        if (!showDialog) return;

        EditorUtility.DisplayDialog("UI Theme Applied",
            $"{applied} of {Skins.Length} sprites assigned.\n\n" +
            (missing > 0
                ? $"{missing} had no matching art and were left empty — those elements keep their flat colour.\n\n"
                : "") +
            "Press Play to see the reskin. Clearing a field in UITheme.asset reverts that element.",
            "OK");
    }

    /// <summary>
    /// Sets a sprite's 9-slice border, importing it as a Sprite first if needed.
    /// Skips the reimport when nothing would change, so re-running is cheap.
    /// </summary>
    private static void ConfigureBorder(string path, Vector4 border)
    {
        var importer = AssetImporter.GetAtPath(path) as TextureImporter;
        if (importer == null) return;

        bool changed = false;

        if (importer.textureType != TextureImporterType.Sprite)
        {
            importer.textureType = TextureImporterType.Sprite;
            changed = true;
        }
        if (importer.spriteImportMode != SpriteImportMode.Single)
        {
            importer.spriteImportMode = SpriteImportMode.Single;
            changed = true;
        }
        if (importer.spriteBorder != border)
        {
            importer.spriteBorder = border;
            changed = true;
        }

        if (!changed) return;

        importer.SaveAndReimport();
        Debug.Log($"[UITheme] Border {border} → {Path.GetFileName(path)}");
    }

    private static void EnsureSpriteType(string path)
    {
        var importer = AssetImporter.GetAtPath(path) as TextureImporter;
        if (importer == null || importer.textureType == TextureImporterType.Sprite) return;

        importer.textureType      = TextureImporterType.Sprite;
        importer.spriteImportMode = SpriteImportMode.Single;
        importer.SaveAndReimport();
    }

    /// <summary>
    /// Finds a texture by exact file name. Searches for Texture rather than Sprite
    /// because a freshly imported PNG may not be a Sprite yet — which is the very
    /// thing ConfigureBorder is about to fix.
    /// </summary>
    private static string FindSpritePath(string fileName)
    {
        var matches = new List<string>();

        foreach (var guid in AssetDatabase.FindAssets($"\"{fileName}\" t:Texture2D"))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (string.Equals(Path.GetFileNameWithoutExtension(path), fileName,
                              System.StringComparison.OrdinalIgnoreCase))
                matches.Add(path);
        }

        if (matches.Count == 0) return null;

        // Several packs ship identically-named files (Kenney duplicates every sprite
        // across Default and Double resolutions). Prefer the plain one for crispness
        // at UI scale, and otherwise take the first deterministically.
        matches.Sort(System.StringComparer.Ordinal);
        foreach (var path in matches)
            if (path.Contains("/Default/")) return path;

        return matches[0];
    }
}
