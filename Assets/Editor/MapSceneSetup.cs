using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Editor utility: turns SampleScene into the Goblin Camp map scene.
///
/// The project rule is that no UI or scene content is hand-placed, so this does the
/// scene surgery in code: strips the legacy inventory UI (whose scripts were
/// deleted in the refit), places the five skill nodes zone_data.json defines for
/// goblin_camp, and registers the result in Build Settings.
///
/// SampleScene is used as the base because its baked terrain NavMesh is the only
/// playable ground in the project.
///
/// Menu: Idle Explorers → Prepare Map Scene
/// </summary>
public static class MapSceneSetup
{
    private const string SOURCE_SCENE = "Assets/Scenes/SampleScene.unity";
    private const string MAP_SCENE    = "Assets/Scenes/Map_GoblinCamp.unity";

    // Node placements, chosen to sit inside the baked NavMesh area (X/Z 400-500)
    // that MonsterSpawner also spawns into.
    private struct NodePlacement
    {
        public string  NodeId;
        public string  Label;
        public Vector3 Position;
        public Color   Color;
        public PrimitiveType Shape;
    }

    // The player prefab spawns at roughly (438, 432), so nodes ring that point
    // closely enough to be on screen the moment the map loads. A previous pass
    // scattered them up to 13 units away and they were off-camera.
    private static readonly Vector3 PlayerSpawn = new Vector3(438f, 0f, 432f);

    private static readonly NodePlacement[] GoblinCampNodes =
    {
        new NodePlacement { NodeId = "copper_rock_1", Position = PlayerSpawn + new Vector3(-5f, 0f,  2f),
                            Label = "Copper Rock",  Color = new Color(0.85f, 0.45f, 0.15f), Shape = PrimitiveType.Cube },
        new NodePlacement { NodeId = "tin_rock_1",    Position = PlayerSpawn + new Vector3(-5f, 0f, -3f),
                            Label = "Tin Rock",     Color = new Color(0.70f, 0.72f, 0.78f), Shape = PrimitiveType.Cube },
        new NodePlacement { NodeId = "normal_tree_1", Position = PlayerSpawn + new Vector3( 5f, 0f,  3f),
                            Label = "Tree",         Color = new Color(0.20f, 0.60f, 0.22f), Shape = PrimitiveType.Cylinder },
        new NodePlacement { NodeId = "shrimp_pool_1", Position = PlayerSpawn + new Vector3( 5f, 0f, -3f),
                            Label = "Shrimp Pool",  Color = new Color(0.25f, 0.55f, 0.90f), Shape = PrimitiveType.Cylinder },
        new NodePlacement { NodeId = "campfire_1",    Position = PlayerSpawn + new Vector3( 0f, 0f, -6f),
                            Label = "Campfire",     Color = new Color(0.95f, 0.50f, 0.12f), Shape = PrimitiveType.Sphere },
    };

    [MenuItem("Idle Explorers/Prepare Map Scene")]
    public static void PrepareMapScene()
    {
        if (!File.Exists(SOURCE_SCENE))
        {
            EditorUtility.DisplayDialog("Source scene missing",
                $"Expected to find:\n{SOURCE_SCENE}\n\nCannot build the map scene without it.", "OK");
            return;
        }

        if (!EditorUtility.DisplayDialog("Prepare Map Scene",
                $"This will open {Path.GetFileName(SOURCE_SCENE)}, strip the legacy inventory UI, " +
                $"add the Goblin Camp skill nodes, and save the result as " +
                $"{Path.GetFileName(MAP_SCENE)}.\n\nSampleScene itself is left unmodified.",
                "Go ahead", "Cancel"))
            return;

        Execute(showDialog: true);
    }

    /// <summary>
    /// Does the work without any dialogs, so it can also be driven headlessly
    /// via -executeMethod.
    /// </summary>
    public static void Execute(bool showDialog)
    {
        if (!File.Exists(SOURCE_SCENE))
        {
            Debug.LogError($"[MapSetup] Source scene missing: {SOURCE_SCENE}");
            return;
        }

        var scene = EditorSceneManager.OpenScene(SOURCE_SCENE, OpenSceneMode.Single);

        int stripped = StripLegacyObjects(scene);
        int nodes    = PlaceSkillNodes();
        EnsurePlayerTag();

        EditorSceneManager.MarkSceneDirty(scene);
        bool saved = EditorSceneManager.SaveScene(scene, MAP_SCENE, saveAsCopy: false);
        if (!saved)
        {
            Debug.LogError("[MapSetup] Failed to save the map scene.");
            return;
        }

        AssetDatabase.Refresh();
        AddSceneToBuildSettings(MAP_SCENE);

        Debug.Log($"[MapSetup] {MAP_SCENE} saved. Stripped {stripped} legacy object(s), placed {nodes} skill node(s).");

        if (showDialog)
            EditorUtility.DisplayDialog("Map Scene Ready",
                $"Saved: {MAP_SCENE}\n\n" +
                $"• Legacy objects removed: {stripped}\n" +
                $"• Skill nodes placed: {nodes}\n\n" +
                "Bootstrap remains the scene you press Play on — this map loads additively.",
                "OK");
    }

    // ── Cleanup ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Strips everything the map scene must not carry into the new architecture:
    ///
    ///  • Every hand-built Canvas. All UI is code-generated by UIManager now, and a
    ///    leftover canvas would draw underneath the real HUD with dead buttons
    ///    (its onClick targets — InventoryUI.ToggleInventory — no longer exist).
    ///  • The scene's EventSystem. UIManager creates one in Bootstrap; a second one
    ///    in an additively-loaded scene makes Unity disable one at random and UI
    ///    clicks stop working intermittently.
    ///  • Objects left holding deleted scripts, which Unity shows as
    ///    "Missing (Mono Script)".
    /// </summary>
    private static int StripLegacyObjects(UnityEngine.SceneManagement.Scene scene)
    {
        int removed = 0;
        var toDestroy = new List<GameObject>();

        foreach (var root in scene.GetRootGameObjects())
        {
            // Hand-built canvases
            foreach (var canvas in root.GetComponentsInChildren<Canvas>(includeInactive: true))
            {
                var canvasRoot = canvas.gameObject;
                if (!toDestroy.Contains(canvasRoot)) toDestroy.Add(canvasRoot);
            }

            // Duplicate EventSystem
            foreach (var es in root.GetComponentsInChildren<UnityEngine.EventSystems.EventSystem>(includeInactive: true))
            {
                var esRoot = es.gameObject;
                if (!toDestroy.Contains(esRoot)) toDestroy.Add(esRoot);
            }

            // Dangling components from deleted scripts
            foreach (var t in root.GetComponentsInChildren<Transform>(includeInactive: true))
            {
                var go = t.gameObject;
                if (toDestroy.Contains(go)) continue;
                if (GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(go) > 0)
                    removed += GameObjectUtility.RemoveMonoBehavioursWithMissingScript(go);
            }
        }

        foreach (var go in toDestroy)
        {
            if (go == null) continue;
            Debug.Log($"[MapSetup] Removing legacy scene object '{go.name}'.");
            Object.DestroyImmediate(go);
            removed++;
        }

        return removed;
    }

    /// <summary>PlayerController's drop pickups depend on the Player tag being set.</summary>
    private static void EnsurePlayerTag()
    {
        var player = GameObject.Find("PlayerCharacter");
        if (player == null)
        {
            Debug.LogWarning("[MapSetup] No 'PlayerCharacter' object found — loot pickup needs it tagged 'Player'.");
            return;
        }
        if (!player.CompareTag("Player"))
        {
            player.tag = "Player";
            Debug.Log("[MapSetup] Tagged PlayerCharacter as 'Player'.");
        }
    }

    // ── Skill nodes ───────────────────────────────────────────────────────────

    private static int PlaceSkillNodes()
    {
        // Remove any previous run's nodes so this is safely re-runnable
        var existing = GameObject.Find("SkillNodes");
        if (existing != null) Object.DestroyImmediate(existing);

        var parent = new GameObject("SkillNodes");
        int placed = 0;

        foreach (var placement in GoblinCampNodes)
        {
            var go = GameObject.CreatePrimitive(placement.Shape);
            go.name = $"Node_{placement.NodeId}";
            go.transform.SetParent(parent.transform, false);
            go.transform.position   = SnapToGround(placement.Position);
            go.transform.localScale = Vector3.one * 2f;

            // Placeholder material — distinct colours so nodes are tellable apart
            // until real art replaces the primitives. Emission keeps them visible
            // under the map's fairly dim lighting.
            var renderer = go.GetComponent<Renderer>();
            if (renderer != null)
            {
                var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
                var mat = new Material(shader) { color = placement.Color };
                mat.EnableKeyword("_EMISSION");
                mat.SetColor("_EmissionColor", placement.Color * 0.55f);
                renderer.sharedMaterial = mat;
            }

            // The collider the primitive ships with is what PlayerController raycasts
            var node = go.AddComponent<SkillNodeController>();
            node.nodeId = placement.NodeId;

            AddFloatingLabel(go.transform, placement.Label);

            placed++;
        }

        return placed;
    }

    /// <summary>
    /// A world-space name tag above a node, so the coloured primitives are
    /// self-explanatory rather than anonymous blocks. Billboard keeps it facing
    /// the camera.
    /// </summary>
    private static void AddFloatingLabel(Transform parent, string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        var labelGo = new GameObject("Label");
        labelGo.transform.SetParent(parent, false);
        // Parent is scaled 2x; counter-scale so the text renders at a sane size
        labelGo.transform.localPosition = new Vector3(0f, 0.9f, 0f);
        labelGo.transform.localScale    = Vector3.one * 0.5f;

        var tmp = labelGo.AddComponent<TMPro.TextMeshPro>();
        tmp.text                = text;
        tmp.fontSize            = 4f;
        tmp.alignment           = TMPro.TextAlignmentOptions.Center;
        tmp.color               = Color.white;
        tmp.GetComponent<RectTransform>().sizeDelta = new Vector2(6f, 1.5f);

        labelGo.AddComponent<Billboard>();
    }

    /// <summary>Drops a node onto the terrain so it does not float or sink.</summary>
    private static Vector3 SnapToGround(Vector3 position)
    {
        var origin = new Vector3(position.x, 500f, position.z);
        if (Physics.Raycast(origin, Vector3.down, out RaycastHit hit, 1000f))
            return hit.point + Vector3.up * 0.75f;

        Debug.LogWarning($"[MapSetup] No ground under {position} — placing at y=1.");
        return new Vector3(position.x, 1f, position.z);
    }

    // ── Build settings ────────────────────────────────────────────────────────

    private static void AddSceneToBuildSettings(string scenePath)
    {
        var scenes = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
        foreach (var s in scenes)
            if (s.path == scenePath) return; // already present

        scenes.Add(new EditorBuildSettingsScene(scenePath, true));
        EditorBuildSettings.scenes = scenes.ToArray();
        Debug.Log($"[MapSetup] Added {scenePath} to Build Settings.");
    }
}
