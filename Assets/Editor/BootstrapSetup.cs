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
    public static void CreateBootstrapScene() => CreateBootstrapScene(showDialog: true);

    /// <summary>Dialog-free variant so this can run headlessly via -executeMethod.</summary>
    public static void CreateBootstrapScene(bool showDialog)
    {
        // Ensure Scenes folder exists
        if (!Directory.Exists("Assets/Scenes"))
            Directory.CreateDirectory("Assets/Scenes");

        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        // ── Single Managers GameObject holds every singleton ─────────────────
        var managersGo = new GameObject("Managers");
        managersGo.AddComponent<GameManager>();
        managersGo.AddComponent<ContentManager>();
        managersGo.AddComponent<SaveManager>();      // before AccountManager, which loads from it
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

        // Note: AudioManager.Awake adds its own music + SFX AudioSources, so none
        // are added here — doing both would leave four sources on the object.

        // ── Save scene ────────────────────────────────────────────────────────
        EditorSceneManager.SaveScene(scene, BOOTSTRAP_SCENE_PATH);
        AssetDatabase.Refresh();

        // ── Add to Build Settings at index 0 ─────────────────────────────────
        AddSceneToBuildSettings(BOOTSTRAP_SCENE_PATH, 0);

        Debug.Log($"[Bootstrap] Scene saved: {BOOTSTRAP_SCENE_PATH}");

        if (showDialog)
            EditorUtility.DisplayDialog("Bootstrap Setup Complete",
                $"Bootstrap scene created at:\n{BOOTSTRAP_SCENE_PATH}\n\n" +
                "Next steps:\n" +
                "1. Run: Idle Explorers → Create UITheme Asset\n" +
                "2. In the Bootstrap scene Inspector, assign AudioManager clip references\n" +
                "3. Press Play — the Splash screen should appear",
                "OK");
    }

    /// <summary>
    /// Runs every setup step in the right order: theme asset, Bootstrap scene,
    /// then the map scene. Safe to re-run.
    ///
    /// Also the entry point for headless setup:
    ///   Unity -batchmode -quit -projectPath . -executeMethod BootstrapSetup.SetupAll
    /// </summary>
    [MenuItem("Idle Explorers/Setup Everything")]
    public static void SetupAll()
    {
        Debug.Log("[Setup] ── Idle Explorers full setup ──");
        CreateUIThemeAsset();
        IconLibrarySetup.Rebuild();          // before the scenes, so icons resolve immediately
        CreateBootstrapScene(showDialog: false);
        MapSceneSetup.Execute(showDialog: false);

        // Bootstrap must be index 0 — it is the scene that owns the Managers object
        // and every other scene loads additively on top of it.
        EnsureBootstrapIsFirstScene();

        AssetDatabase.SaveAssets();
        Debug.Log("[Setup] ── Complete. Open Assets/Scenes/Bootstrap.unity and press Play. ──");
    }

    private static void EnsureBootstrapIsFirstScene()
    {
        var scenes = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
        int index = scenes.FindIndex(s => s.path == BOOTSTRAP_SCENE_PATH);
        if (index <= 0) return;

        var bootstrap = scenes[index];
        scenes.RemoveAt(index);
        scenes.Insert(0, bootstrap);
        EditorBuildSettings.scenes = scenes.ToArray();
        Debug.Log("[Setup] Moved Bootstrap to build index 0.");
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
