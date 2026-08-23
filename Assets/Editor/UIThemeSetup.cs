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
        // Windows: Kenney's UI Adventure Pack panels are 100x100 painted RPG frames
        // with generous corners — they carry a full-screen modal far better than the
        // 64x64 tiles, which visibly repeat when stretched that wide.
        new SkinEntry { Field = "panelSprite",   SpriteName = "panel_brown",        Border = new Vector4(32, 32, 32, 32) },
        new SkinEntry { Field = "cardSprite",    SpriteName = "panel_beige",        Border = new Vector4(32, 32, 32, 32) },
        new SkinEntry { Field = "headerSprite",  SpriteName = "panel_blue",         Border = new Vector4(32, 32, 32, 32) },

        // Inset panels are drawn as recesses, which is exactly what an inventory cell
        // should look like.
        new SkinEntry { Field = "slotSprite",    SpriteName = "panelInset_beige",   Border = new Vector4(30, 30, 30, 30) },
        new SkinEntry { Field = "inputSprite",   SpriteName = "panelInset_brown",   Border = new Vector4(30, 30, 30, 30) },

        // The ONLY Kenney UI pack shipping pressed variants, which is why buttons come
        // from here rather than UI Pack - Adventure.
        new SkinEntry { Field = "buttonSprite",        SpriteName = "buttonLong_brown",         Border = new Vector4(16, 16, 16, 16) },
        new SkinEntry { Field = "buttonPressedSprite", SpriteName = "buttonLong_brown_pressed", Border = new Vector4(16, 16, 16, 16) },

        // Bars are authored as 3-slice strips; the mid tile is the piece that stretches.
        new SkinEntry { Field = "barBgSprite",   SpriteName = "barBack_horizontalMid", Border = new Vector4(6, 0, 6, 0) },

        // barFillSprite is deliberately left unset. Every fill Kenney ships is already
        // coloured, and UIFactory tints the fill per bar — so hp red over green art
        // comes out muddy, and all three bars stop being distinguishable at a glance.
        // The generated white sprite tints cleanly, which is what a fill needs.

        new SkinEntry { Field = "dividerSprite", SpriteName = "divider-000",            NoSlice = true },
    };

    /// <summary>
    /// The palette, written into the asset on every apply.
    ///
    /// It has to be written rather than left to the C# field defaults: UITheme.asset is
    /// already serialized, so a field that exists in the asset keeps its stored value
    /// forever and changing the default in code does nothing. That is why the first
    /// palette survived being edited — the asset predated the edit.
    ///
    /// Dark blue throughout, with the panel art tinted into it rather than replaced.
    /// Kenney's UI panels are all mid-tone tan and beige; drawn untinted, the beige
    /// card sprite is the bright khaki that made the UI hard to look at.
    /// </summary>
    private static readonly (string Field, Color Value)[] Palette =
    {
        // Chrome — deep navy, darkest at the edges of the hierarchy.
        ("panelBg",       new Color(0.07f, 0.09f, 0.15f, 0.97f)),
        ("cardBg",        new Color(0.11f, 0.14f, 0.22f, 1.00f)),
        ("headerBg",      new Color(0.06f, 0.08f, 0.14f, 1.00f)),
        ("overlayBg",     new Color(0.02f, 0.03f, 0.06f, 0.78f)),

        // Slots read as recesses cut into the panel.
        ("slotBg",        new Color(0.09f, 0.12f, 0.19f, 1.00f)),
        ("slotBorder",    new Color(0.28f, 0.34f, 0.48f, 1.00f)),

        // Buttons stay warm so they read as the thing you press against cold chrome.
        ("buttonNormal",  new Color(0.24f, 0.19f, 0.14f, 1.00f)),
        ("buttonHover",   new Color(0.33f, 0.26f, 0.19f, 1.00f)),
        ("buttonPressed", new Color(0.16f, 0.13f, 0.09f, 1.00f)),

        // Text: warm off-white carries well over navy.
        ("textPrimary",   new Color(0.94f, 0.92f, 0.86f, 1.00f)),
        ("textSecondary", new Color(0.62f, 0.66f, 0.76f, 1.00f)),
        ("textDisabled",  new Color(0.38f, 0.42f, 0.50f, 1.00f)),

        ("barBg",         new Color(0.05f, 0.07f, 0.12f, 1.00f)),

        // Sprite tints — multiplied over the panel art. Blue-dominant, because
        // multiply can only remove: to end up blue, blue has to be what survives.
        ("panelSpriteTint",   new Color(0.20f, 0.38f, 0.92f, 1.00f)),
        ("cardSpriteTint",    new Color(0.22f, 0.32f, 0.62f, 1.00f)),
        ("headerSpriteTint",  new Color(0.20f, 0.27f, 0.46f, 1.00f)),
        ("slotSpriteTint",    new Color(0.18f, 0.26f, 0.50f, 1.00f)),
        ("inputSpriteTint",   new Color(0.24f, 0.32f, 0.55f, 1.00f)),
        ("buttonSpriteTint",  new Color(0.72f, 0.62f, 0.52f, 1.00f)),
        ("barBgSpriteTint",   new Color(0.30f, 0.36f, 0.52f, 1.00f)),
        ("dividerSpriteTint", new Color(0.75f, 0.68f, 0.52f, 1.00f)),
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
        int applied = 0, missing = 0, recoloured = 0;

        foreach (var (field, value) in Palette)
        {
            var colorProperty = so.FindProperty(field);
            if (colorProperty == null)
            {
                Debug.LogWarning($"[UITheme] No colour field '{field}' on UITheme — skipped.");
                continue;
            }
            colorProperty.colorValue = value;
            recoloured++;
        }

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

        Debug.Log($"[UITheme] Applied {applied}/{Skins.Length} sprites ({missing} unmatched) " +
                  $"and {recoloured} palette colours → {THEME_PATH}");

        if (!showDialog) return;

        EditorUtility.DisplayDialog("UI Theme Applied",
            $"{applied} of {Skins.Length} sprites assigned, {recoloured} palette colours written.\n\n" +
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
