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

        /// <summary>Kenney FBX to instantiate. Falls back to the primitive when absent.</summary>
        public string  Model;

        /// <summary>Uniform scale for the model — the kits are authored at ~1 unit.</summary>
        public float   Scale;

        // Fallback appearance, used only when the model cannot be loaded so a missing
        // asset leaves a visible coloured marker rather than an invisible node.
        public Color   Color;
        public PrimitiveType Shape;
    }

    private const string KenneyModels = "Assets/Kenney Game Assets All-in-1 3.7.0/3D assets/";

    // The player prefab spawns at roughly (438, 432), so nodes ring that point
    // closely enough to be on screen the moment the map loads. A previous pass
    // scattered them up to 13 units away and they were off-camera.
    private static readonly Vector3 PlayerSpawn = new Vector3(438f, 0f, 432f);

    // A ring around the spawn point, evenly spaced so every node — the bank chest
    // included — is visible and reachable the moment the map loads. The chest used to
    // sit alone behind the player.
    private static readonly NodePlacement[] GoblinCampNodes =
    {
        new NodePlacement { NodeId = "copper_rock_1", Position = PlayerSpawn + new Vector3(-6f, 0f,  3f),
                            Label = "Copper Rock", Scale = 2.2f,
                            Model = KenneyModels + "Survival Kit/Models/FBX format/rock-a.fbx",
                            Color = new Color(0.85f, 0.45f, 0.15f), Shape = PrimitiveType.Cube },

        new NodePlacement { NodeId = "tin_rock_1",    Position = PlayerSpawn + new Vector3(-6f, 0f, -3f),
                            Label = "Tin Rock", Scale = 2.2f,
                            Model = KenneyModels + "Survival Kit/Models/FBX format/rock-c.fbx",
                            Color = new Color(0.70f, 0.72f, 0.78f), Shape = PrimitiveType.Cube },

        new NodePlacement { NodeId = "normal_tree_1", Position = PlayerSpawn + new Vector3( 6f, 0f,  3f),
                            Label = "Tree", Scale = 2.0f,
                            Model = KenneyModels + "Nature Kit/Models/FBX format/tree_oak.fbx",
                            Color = new Color(0.20f, 0.60f, 0.22f), Shape = PrimitiveType.Cylinder },

        new NodePlacement { NodeId = "shrimp_pool_1", Position = PlayerSpawn + new Vector3( 6f, 0f, -3f),
                            Label = "Shrimp Pool", Scale = 2.0f,
                            Model = KenneyModels + "Survival Kit/Models/FBX format/campfire-fishing-stand.fbx",
                            Color = new Color(0.25f, 0.55f, 0.90f), Shape = PrimitiveType.Cylinder },

        new NodePlacement { NodeId = "campfire_1",    Position = PlayerSpawn + new Vector3( 0f, 0f, -6f),
                            Label = "Campfire", Scale = 2.0f,
                            Model = KenneyModels + "Nature Kit/Models/FBX format/campfire_stones.fbx",
                            Color = new Color(0.95f, 0.50f, 0.12f), Shape = PrimitiveType.Sphere },

        new NodePlacement { NodeId = "bank_chest_1",  Position = PlayerSpawn + new Vector3( 0f, 0f,  6f),
                            Label = "Bank Chest", Scale = 2.4f,
                            Model = KenneyModels + "Survival Kit/Models/FBX format/chest.fbx",
                            Color = new Color(0.85f, 0.75f, 0.30f), Shape = PrimitiveType.Cube },
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
        EnsureCameraController();

        // After the nodes exist, so the bake sees the finished scene. Placing geometry
        // and leaving the NavMesh stale is what forced a manual rebuild every time.
        bool baked = BakeNavMesh();

        EditorSceneManager.MarkSceneDirty(scene);
        bool saved = EditorSceneManager.SaveScene(scene, MAP_SCENE, saveAsCopy: false);
        if (!saved)
        {
            Debug.LogError("[MapSetup] Failed to save the map scene.");
            return;
        }

        AssetDatabase.Refresh();
        AddSceneToBuildSettings(MAP_SCENE);

        Debug.Log($"[MapSetup] {MAP_SCENE} saved. Stripped {stripped} legacy object(s), " +
                  $"placed {nodes} skill node(s), NavMesh {(baked ? "rebuilt" : "NOT rebuilt")}.");

        if (showDialog)
            EditorUtility.DisplayDialog("Map Scene Ready",
                $"Saved: {MAP_SCENE}\n\n" +
                $"• Legacy objects removed: {stripped}\n" +
                $"• Skill nodes placed: {nodes}\n" +
                $"• NavMesh: {(baked ? "rebuilt automatically" : "could not be rebuilt — see Console")}\n\n" +
                "Bootstrap remains the scene you press Play on — this map loads additively.",
                "OK");
    }

    // ── NavMesh ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Rebuilds the map's NavMesh and writes it back to its asset.
    ///
    /// This runs on every map build because the alternative — remembering to do it by
    /// hand — has already failed once: the agent silently keeps whatever mesh was
    /// baked before the scene was regenerated, and a stale NavMesh looks exactly like
    /// broken pathfinding rather than like missing data.
    ///
    /// Returns false when there is nothing to bake, which is worth reporting rather
    /// than passing over quietly.
    /// </summary>
    private static bool BakeNavMesh()
    {
        var surface = Object.FindAnyObjectByType<Unity.AI.Navigation.NavMeshSurface>(FindObjectsInactive.Include);

        if (surface == null)
        {
            // The terrain is the walkable ground, so that is where the surface belongs.
            var terrain = Object.FindAnyObjectByType<Terrain>(FindObjectsInactive.Include);
            if (terrain == null)
            {
                Debug.LogWarning("[MapSetup] No NavMeshSurface and no Terrain — NavMesh not baked. " +
                                 "The player and every monster will be unable to move.");
                return false;
            }

            surface = terrain.gameObject.AddComponent<Unity.AI.Navigation.NavMeshSurface>();
            surface.collectObjects = Unity.AI.Navigation.CollectObjects.All;
            Debug.Log("[MapSetup] Added a NavMeshSurface to the Terrain.");
        }

        surface.BuildNavMesh();

        if (surface.navMeshData == null)
        {
            Debug.LogWarning("[MapSetup] NavMesh bake produced no data.");
            return false;
        }

        // BuildNavMesh fills the NavMeshData in memory. If it is not already a saved
        // asset it dies with the Editor session, and the scene reloads with nothing.
        string assetPath = AssetDatabase.GetAssetPath(surface.navMeshData);
        if (string.IsNullOrEmpty(assetPath))
        {
            string directory = Path.Combine(Path.GetDirectoryName(MAP_SCENE),
                                             Path.GetFileNameWithoutExtension(MAP_SCENE));
            if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);

            assetPath = AssetDatabase.GenerateUniqueAssetPath(
                Path.Combine(directory, "NavMesh-Terrain.asset").Replace("\\", "/"));
            AssetDatabase.CreateAsset(surface.navMeshData, assetPath);
        }

        EditorUtility.SetDirty(surface.navMeshData);
        EditorUtility.SetDirty(surface);
        AssetDatabase.SaveAssets();

        Debug.Log($"[MapSetup] NavMesh rebuilt → {assetPath}");
        return true;
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

    /// <summary>Gives the map camera WASD panning, scroll zoom and player follow.</summary>
    private static void EnsureCameraController()
    {
        var camera = Camera.main;
        if (camera == null)
        {
            Debug.LogWarning("[MapSetup] No MainCamera in the scene — camera controls not added.");
            return;
        }

        if (camera.GetComponent<CameraController>() == null)
        {
            camera.gameObject.AddComponent<CameraController>();
            Debug.Log("[MapSetup] Added CameraController to the map camera.");
        }
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
            bool isModel = !string.IsNullOrEmpty(placement.Model);

            var go = BuildNodeVisual(placement);
            go.name = $"Node_{placement.NodeId}";
            go.transform.SetParent(parent.transform, false);

            // Kenney models are authored with their pivot on the ground, so they sit
            // flush. A primitive's pivot is its centre and needs lifting by half its
            // height or it is buried in the terrain.
            go.transform.position = SnapToGround(placement.Position, isModel ? 0f : 0.75f);

            // PlayerController raycasts to find nodes. Kenney's FBX models carry no
            // collider of their own, so without this the node is invisible to
            // targeting and clicks pass straight through it.
            EnsureCollider(go);

            var node = go.AddComponent<SkillNodeController>();
            node.nodeId = placement.NodeId;

            // Keep nodes out of the NavMesh bake.
            //
            // The surface collects render meshes across the whole scene, so a node left
            // in would carve a hole the size of its model — and the floating name tag
            // above it is a mesh too, at roughly agent head height. Between them they
            // can wall off the very node the player is walking to, which presents as
            // pathfinding being broken rather than as a baking decision. Nodes are
            // reached by interactionRange (2.5 units), not by standing inside them, so
            // nothing is lost by letting the player walk over the footprint.
            var modifier = go.AddComponent<Unity.AI.Navigation.NavMeshModifier>();
            modifier.ignoreFromBuild = true;

            AddFloatingLabel(go.transform, placement.Label);

            placed++;
        }

        return placed;
    }

    /// <summary>
    /// Instantiates the Kenney model for a node, falling back to the old coloured
    /// primitive if it cannot be loaded — a missing asset should leave a visible
    /// marker you can still click, not an invisible hole in the map.
    /// </summary>
    private static GameObject BuildNodeVisual(NodePlacement placement)
    {
        if (!string.IsNullOrEmpty(placement.Model))
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(placement.Model);
            if (prefab != null)
            {
                var model = (GameObject)PrefabUtility.InstantiatePrefab(prefab);

                // Unpack so the node is a plain scene object; leaving it linked to the
                // FBX would make every node an override of an imported asset.
                PrefabUtility.UnpackPrefabInstance(model, PrefabUnpackMode.Completely,
                                                    InteractionMode.AutomatedAction);

                model.transform.localScale = Vector3.one * (placement.Scale > 0f ? placement.Scale : 1f);
                EnsureUrpMaterials(model);
                return model;
            }

            Debug.LogWarning($"[MapSetup] Model not found for '{placement.NodeId}': {placement.Model} — " +
                             "using a coloured primitive instead.");
        }

        var go = GameObject.CreatePrimitive(placement.Shape);
        go.transform.localScale = Vector3.one * 2f;

        // Distinct colours so fallback nodes stay tellable apart. Emission keeps them
        // visible under the map's fairly dim lighting.
        var renderer = go.GetComponent<Renderer>();
        if (renderer != null)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            var mat = new Material(shader) { color = placement.Color };
            mat.EnableKeyword("_EMISSION");
            mat.SetColor("_EmissionColor", placement.Color * 0.55f);
            renderer.sharedMaterial = mat;
        }

        return go;
    }

    /// <summary>
    /// Re-points any material still on a Built-in shader at URP's Lit.
    ///
    /// This project renders with URP, where a Built-in-shader material draws as solid
    /// magenta. Imported FBX materials usually come in correct, but a pack imported
    /// before the pipeline was set — or copied in from elsewhere — will not, and a
    /// field of magenta rocks is a worse outcome than a moment of defensive code.
    /// </summary>
    private static void EnsureUrpMaterials(GameObject root)
    {
        var urpLit = Shader.Find("Universal Render Pipeline/Lit");
        if (urpLit == null) return;   // not a URP project after all; leave well alone

        foreach (var renderer in root.GetComponentsInChildren<Renderer>(includeInactive: true))
        {
            var materials = renderer.sharedMaterials;
            bool changed = false;

            for (int i = 0; i < materials.Length; i++)
            {
                var material = materials[i];
                if (material == null) continue;
                if (material.shader == null || material.shader.name != "Standard") continue;

                // Preserve what the original described; the rest is shader defaults.
                var replacement = new Material(urpLit)
                {
                    name        = material.name,
                    color       = material.HasProperty("_Color")   ? material.color       : Color.white,
                    mainTexture = material.HasProperty("_MainTex") ? material.mainTexture : null,
                };

                materials[i] = replacement;
                changed = true;
            }

            if (changed) renderer.sharedMaterials = materials;
        }
    }

    /// <summary>
    /// Gives a node a collider sized to its rendered bounds. Primitives already have
    /// one; imported models never do.
    /// </summary>
    private static void EnsureCollider(GameObject go)
    {
        if (go.GetComponentInChildren<Collider>() != null) return;

        var box = go.AddComponent<BoxCollider>();

        var renderers = go.GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0) return;

        // Combine child bounds in world space, then express them relative to the node
        // so the collider tracks the model however its parts are laid out.
        var bounds = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);

        box.center = go.transform.InverseTransformPoint(bounds.center);
        box.size   = go.transform.InverseTransformVector(bounds.size);

        // InverseTransformVector can produce negatives on mirrored scales, and a
        // negative extent makes a collider that cannot be hit.
        box.size = new Vector3(Mathf.Abs(box.size.x), Mathf.Abs(box.size.y), Mathf.Abs(box.size.z));
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

        // Sit just above whatever this node actually is. Nodes are now models of very
        // different heights — a chest is knee-high, an oak is not — so a fixed offset
        // would bury half the labels and float the rest.
        float localTop = 1f;
        var renderers = parent.GetComponentsInChildren<Renderer>();
        if (renderers.Length > 0)
        {
            var bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);

            float worldTop = bounds.max.y - parent.position.y;
            float scaleY   = Mathf.Approximately(parent.lossyScale.y, 0f) ? 1f : parent.lossyScale.y;
            localTop = worldTop / scaleY;
        }

        labelGo.transform.localPosition = new Vector3(0f, localTop + 0.25f, 0f);

        // Counter-scale so text renders at a readable size whatever the parent's scale.
        float parentScale = Mathf.Approximately(parent.lossyScale.x, 0f) ? 1f : parent.lossyScale.x;
        labelGo.transform.localScale = Vector3.one * (1f / parentScale);

        var tmp = labelGo.AddComponent<TMPro.TextMeshPro>();
        tmp.text                = text;
        tmp.fontSize            = 4f;
        tmp.alignment           = TMPro.TextAlignmentOptions.Center;
        tmp.color               = Color.white;
        tmp.GetComponent<RectTransform>().sizeDelta = new Vector2(6f, 1.5f);

        labelGo.AddComponent<Billboard>();
    }

    /// <summary>
    /// Drops a node onto the terrain so it does not float or sink. lift raises it off
    /// the surface, which centre-pivoted primitives need and ground-pivoted models
    /// do not.
    /// </summary>
    private static Vector3 SnapToGround(Vector3 position, float lift)
    {
        var origin = new Vector3(position.x, 500f, position.z);
        if (Physics.Raycast(origin, Vector3.down, out RaycastHit hit, 1000f))
            return hit.point + Vector3.up * lift;

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
