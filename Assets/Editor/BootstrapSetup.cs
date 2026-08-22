using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Editor utility: one-click Bootstrap scene setup.
/// Phase 1: JSON loaded from StreamingAssets — no Addressables package needed.
/// Phase 2+: install com.unity.addressables and swap ContentManager.LoadJsonRoutine.
///
/// Menu: Idle Explorers → Setup Bootstrap Scene
///       Idle Explorers → Create UITheme Asset
/// </summary>
public static class BootstrapSetup
{
    private const string BOOTSTRAP_SCENE_PATH = "Assets/Scenes/Bootstrap.unity";

    [MenuItem("Idle Explorers/Setup Bootstrap Scene")]
    public static void CreateBootstrapScene()
    {
        // Ensure Scenes folder exists
        if (!Directory.Exists("Assets/Scenes"))
            Directory.CreateDirectory("Assets/Scenes");

        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        // ── Single Managers GameObject holds every singleton ─────────────────
        var managersGo = new GameObject("Managers");
        managersGo.AddComponent<GameManager>();
        managersGo.AddComponent<ContentManager>();
        managersGo.AddComponent<AccountManager>();
        managersGo.AddComponent<CharacterManager>();
        managersGo.AddComponent<InventoryManager>();
        managersGo.AddComponent<SkillManager>();
        managersGo.AddComponent<ActivityManager>();
        managersGo.AddComponent<MergeManager>();
        managersGo.AddComponent<ZoneManager>();
        managersGo.AddComponent<CollectionManager>();
        managersGo.AddComponent<AuctionManager>();
        managersGo.AddComponent<GuildManager>();
        managersGo.AddComponent<QuestManager>();
        managersGo.AddComponent<SlayerManager>();
        managersGo.AddComponent<GhostSpawner>();
        managersGo.AddComponent<AudioManager>();
        managersGo.AddComponent<UIManager>();

        // AudioManager needs two AudioSource components (music + SFX)
        managersGo.AddComponent<AudioSource>();
        managersGo.AddComponent<AudioSource>();

        // ── Save scene ────────────────────────────────────────────────────────
        EditorSceneManager.SaveScene(scene, BOOTSTRAP_SCENE_PATH);
        AssetDatabase.Refresh();

        // ── Add to Build Settings at index 0 ─────────────────────────────────
        AddSceneToBuildSettings(BOOTSTRAP_SCENE_PATH, 0);

        Debug.Log($"[Bootstrap] Scene saved: {BOOTSTRAP_SCENE_PATH}");

        EditorUtility.DisplayDialog("Bootstrap Setup Complete",
            $"Bootstrap scene created at:\n{BOOTSTRAP_SCENE_PATH}\n\n" +
            "Next steps:\n" +
            "1. Run: Idle Explorers → Create UITheme Asset\n" +
            "2. In the Bootstrap scene Inspector, assign AudioManager clip references\n" +
            "3. Press Play — the Splash screen should appear",
            "OK");
    }

    [MenuItem("Idle Explorers/Create UITheme Asset")]
    public static void CreateUIThemeAsset()
    {
        // UIManager uses Resources.Load<UITheme>("UITheme"), so the asset
        // MUST live in an Assets/Resources folder with the filename UITheme.asset
        const string resourcesDir  = "Assets/Resources";
        const string assetPath     = "Assets/Resources/UITheme.asset";

        if (!Directory.Exists(resourcesDir))
            Directory.CreateDirectory(resourcesDir);

        if (File.Exists(assetPath))
        {
            Debug.Log($"[Bootstrap] UITheme already exists at {assetPath}");
            Selection.activeObject = AssetDatabase.LoadAssetAtPath<UITheme>(assetPath);
            return;
        }

        var theme = ScriptableObject.CreateInstance<UITheme>();
        AssetDatabase.CreateAsset(theme, assetPath);
        AssetDatabase.SaveAssets();
        Selection.activeObject = theme;
        EditorGUIUtility.PingObject(theme);
        Debug.Log($"[Bootstrap] UITheme created at {assetPath}");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static void AddSceneToBuildSettings(string scenePath, int index)
    {
        var scenes = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
        foreach (var s in scenes)
            if (s.path == scenePath) return; // already present

        scenes.Insert(Mathf.Clamp(index, 0, scenes.Count),
                      new EditorBuildSettingsScene(scenePath, true));
        EditorBuildSettings.scenes = scenes.ToArray();
    }
}
