using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;

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
    //
    // Seven nodes now, so they sit on a circle rather than a hand-placed cross:
    // adding an eighth is a new entry, not a re-plotting of every position.
    private static readonly NodePlacement[] GoblinCampNodes =
    {
        new NodePlacement { NodeId = "tin_rock_1",    Position = RingPosition(0, 7),
                            Label = "Tin Rock", Scale = 2.2f,
                            Model = KenneyModels + "Survival Kit/Models/FBX format/rock-c.fbx",
                            Color = new Color(0.70f, 0.72f, 0.78f), Shape = PrimitiveType.Cube },

        new NodePlacement { NodeId = "copper_rock_1", Position = RingPosition(1, 7),
                            Label = "Copper Rock", Scale = 2.2f,
                            Model = KenneyModels + "Survival Kit/Models/FBX format/rock-a.fbx",
                            Color = new Color(0.85f, 0.45f, 0.15f), Shape = PrimitiveType.Cube },

        new NodePlacement { NodeId = "normal_tree_1", Position = RingPosition(2, 7),
                            Label = "Tree", Scale = 2.0f,
                            Model = KenneyModels + "Nature Kit/Models/FBX format/tree_oak.fbx",
                            Color = new Color(0.20f, 0.60f, 0.22f), Shape = PrimitiveType.Cylinder },

        new NodePlacement { NodeId = "shrimp_pool_1", Position = RingPosition(3, 7),
                            Label = "Shrimp Pool", Scale = 2.0f,
                            Model = KenneyModels + "Survival Kit/Models/FBX format/campfire-fishing-stand.fbx",
                            Color = new Color(0.25f, 0.55f, 0.90f), Shape = PrimitiveType.Cylinder },

        new NodePlacement { NodeId = "campfire_1",    Position = RingPosition(4, 7),
                            Label = "Campfire", Scale = 2.0f,
                            Model = KenneyModels + "Nature Kit/Models/FBX format/campfire_stones.fbx",
                            Color = new Color(0.95f, 0.50f, 0.12f), Shape = PrimitiveType.Sphere },

        new NodePlacement { NodeId = "anvil_1",       Position = RingPosition(5, 7),
                            Label = "Anvil", Scale = 2.0f,
                            Model = KenneyModels + "Survival Kit/Models/FBX format/workbench-anvil.fbx",
                            Color = new Color(0.45f, 0.45f, 0.50f), Shape = PrimitiveType.Cube },

        new NodePlacement { NodeId = "bank_chest_1",  Position = RingPosition(6, 7),
                            Label = "Bank Chest", Scale = 2.4f,
                            Model = KenneyModels + "Survival Kit/Models/FBX format/chest.fbx",
                            Color = new Color(0.85f, 0.75f, 0.30f), Shape = PrimitiveType.Cube },
    };

    /// <summary>Radius of the node ring around the spawn point, in world units.</summary>
    private const float RingRadius = 7.5f;

    /// <summary>
    /// Evenly spaces nodes on a circle around the player spawn, so the layout stays
    /// balanced as nodes are added instead of needing every offset re-tuned by hand.
    /// </summary>
    private static Vector3 RingPosition(int index, int total)
    {
        float angle = (index / (float)Mathf.Max(1, total)) * Mathf.PI * 2f;
        return PlayerSpawn + new Vector3(Mathf.Sin(angle), 0f, Mathf.Cos(angle)) * RingRadius;
    }

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

        // Placed deliberately rather than left wherever SampleScene happened to have
        // it. It landed on the terrain by luck, which is not a property to rely on:
        // the same inherited position put the Hollow's player outside its map.
        EnsurePlayer(SnapToGround(PlayerSpawn, 0.5f));

        EnsurePlayerTag();
        EnsureCameraController();
        ConfigureMonsterSpawner();

        // After the nodes exist, so the bake sees the finished scene. Placing geometry
        // and leaving the NavMesh stale is what forced a manual rebuild every time.
        bool baked = BakeNavMesh(MAP_SCENE);

        // After the bake — before it there is no mesh to stand on and the check
        // would fail on every map.
        VerifyPlayerPlacement("Goblin Camp");

        // Before the save, not after: a map nobody can see is not a map, and the only
        // sign last time was one warning in a thirteen-step log.
        VerifyCamera("Goblin Camp");

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
    internal static bool BakeNavMesh(string scenePath)
    {
        var surface = Object.FindAnyObjectByType<Unity.AI.Navigation.NavMeshSurface>(FindObjectsInactive.Include);

        if (surface == null)
        {
            // Prefer the terrain when there is one — that is the walkable ground on
            // the terrain-based maps. A tile-built map has no terrain at all, so the
            // surface goes on its own object and collects the whole scene either way.
            var terrain = Object.FindAnyObjectByType<Terrain>(FindObjectsInactive.Include);
            var host    = terrain != null ? terrain.gameObject : new GameObject("NavMesh");

            surface = host.AddComponent<Unity.AI.Navigation.NavMeshSurface>();
            surface.collectObjects = Unity.AI.Navigation.CollectObjects.All;
            Debug.Log($"[MapSetup] Added a NavMeshSurface to '{host.name}'.");
        }

        surface.BuildNavMesh();

        if (surface.navMeshData == null)
        {
            Debug.LogWarning("[MapSetup] NavMesh bake produced no data.");
            return false;
        }

        // BuildNavMesh fills the NavMeshData in memory. If it is not already a saved
        // asset it dies with the Editor session, and the scene reloads with nothing.
        //
        // BuildNavMesh always produces a NEW NavMeshData object, so GetAssetPath is
        // always empty here and this always writes. It used to write through
        // GenerateUniqueAssetPath, which meant every rebuild left another file behind:
        // Map_FadingHollow/ had accumulated NavMesh.asset, NavMesh 1.asset and
        // NavMesh 2.asset, only the last of which anything referenced. A fixed path
        // replaces the asset in place instead — CreateAsset deletes what is already
        // there — so the scene's reference stays valid and nothing accumulates.
        string assetPath = AssetDatabase.GetAssetPath(surface.navMeshData);
        if (string.IsNullOrEmpty(assetPath))
        {
            string directory = Path.Combine(Path.GetDirectoryName(scenePath),
                                             Path.GetFileNameWithoutExtension(scenePath));
            if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);

            assetPath = Path.Combine(directory, "NavMesh.asset").Replace("\\", "/");
            AssetDatabase.CreateAsset(surface.navMeshData, assetPath);
            RemoveStaleNavMeshAssets(directory, assetPath);
        }

        EditorUtility.SetDirty(surface.navMeshData);
        EditorUtility.SetDirty(surface);
        AssetDatabase.SaveAssets();

        Debug.Log($"[MapSetup] NavMesh rebuilt → {assetPath}");
        return true;
    }

    /// <summary>
    /// Deletes the "NavMesh 1.asset", "NavMesh 2.asset" … files earlier rebuilds left
    /// behind. Only the one the surface now points at is live; the rest are dead
    /// weight in the repository, and this project's assets are on Git LFS.
    /// </summary>
    private static void RemoveStaleNavMeshAssets(string directory, string keepPath)
    {
        foreach (string file in Directory.GetFiles(directory, "NavMesh*.asset"))
        {
            string path = file.Replace("\\", "/");
            if (path == keepPath) continue;

            if (AssetDatabase.DeleteAsset(path))
                Debug.Log($"[MapSetup] Removed stale NavMesh asset {path}.");
        }
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
    internal static int StripLegacyObjects(UnityEngine.SceneManagement.Scene scene)
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

    /// <summary>
    /// Guarantees the map has a follow camera, and gives it WASD panning and zoom.
    ///
    /// ══ WHY THIS CREATES RATHER THAN CONFIGURES ═══════════════════════════════
    ///
    /// It used to return early when Camera.main was null, on the assumption that every
    /// map inherits SampleScene's camera. That assumption died the moment EnsurePlayer
    /// started replacing the inherited player: SampleScene had parked the Main Camera
    /// as a CHILD of PlayerCharacter — SPUM authors rigs inside a Canvas, so the same
    /// RectTransform that put the Hollow's player 337 units out of bounds was also the
    /// camera's parent — and DestroyImmediate on the player root took the camera, its
    /// AudioListener and its CameraController with it.
    ///
    /// Both maps then saved with zero cameras, and the game showed "Display 1 — No
    /// cameras rendering" on entering either one. Click-to-move and the billboarded
    /// node labels went with it; they all read Camera.main.
    ///
    /// The detach below was written to fix the camera-parented-to-player problem and
    /// was correct, but it ran twenty lines after the object it was fixing had already
    /// been deleted. Creating the camera here is what makes a map OWN one rather than
    /// inherit one, which is the same reason EnsurePlayer builds its player.
    /// </summary>
    internal static void EnsureCameraController()
    {
        var camera = Camera.main;
        if (camera == null) camera = CreateMapCamera();
        if (camera == null) return;

        // A follow camera cannot be parented to its own target: CameraController assigns
        // a world position every LateUpdate, but the parent has already moved and
        // rotated it by then — and the NavMeshAgent rotates the player to face travel,
        // which swings the camera around them before the follow drags it back. Kept for
        // any scene not yet regenerated; a camera built above is already a root object.
        if (camera.transform.parent != null)
        {
            Debug.Log($"[MapSetup] Detaching the camera from '{camera.transform.parent.name}'.");
            camera.transform.SetParent(null, worldPositionStays: true);
        }

        // A 15-degree lens showed roughly twelve world units at full zoom-out, so
        // zooming further barely widened the view — the focal length was the limit,
        // not the distance. 45 is an ordinary isometric-ARPG field of view.
        camera.fieldOfView = 45f;

        // The terrain runs to 2000 units; at the new maximum zoom the far plane has
        // to reach past it or the horizon visibly cuts off.
        camera.farClipPlane = 3000f;

        // The scene was saved with clearFlags = Nothing, which keeps whatever was in
        // the framebuffer when nothing draws over it. That is how the Hollow's empty
        // view presented as a flat yellow fill rather than as empty space — a
        // rendering fault dressed up as a different rendering fault. A skybox makes
        // "there is nothing here" look like nothing being here.
        camera.clearFlags = CameraClearFlags.Skybox;

        // Gameplay audio is positional and needs a listener somewhere in the scene. The
        // menu camera disables its own on OnMapEntered, so without this the map is
        // silent — the missing camera cost us every sound effect as well as the picture.
        if (camera.GetComponent<AudioListener>() == null)
        {
            camera.gameObject.AddComponent<AudioListener>();
            Debug.Log("[MapSetup] Added an AudioListener to the map camera.");
        }

        var controller = camera.GetComponent<CameraController>();
        if (controller == null)
        {
            controller = camera.gameObject.AddComponent<CameraController>();
            Debug.Log("[MapSetup] Added CameraController to the map camera.");
        }

        // Written explicitly, not left to the script defaults. The scene already holds
        // serialized values for these (distance 18, maxDistance 45) and a serialized
        // value silently wins — which is exactly how the monster spawner kept its
        // 0.01-second interval no matter what the script said.
        controller.distance    = 30f;
        controller.minDistance = 4f;
        controller.maxDistance = 300f;
        controller.zoomStep    = 1.18f;

        FrameOnPlayer(camera, controller);

        EditorUtility.SetDirty(camera);
        EditorUtility.SetDirty(controller);
    }

    /// <summary>
    /// A new Main Camera as a ROOT object, never parented to the player.
    ///
    /// CameraController.AcquireTarget resolves the player by tag every LateUpdate, so a
    /// root camera survives the player being replaced, respawned or repositioned —
    /// which is precisely what a child camera did not.
    /// </summary>
    private static Camera CreateMapCamera()
    {
        Debug.Log("[MapSetup] No MainCamera in the scene — building one.");

        var go = new GameObject("Main Camera");
        go.tag = "MainCamera";

        var camera = go.AddComponent<Camera>();
        if (camera == null)
            Debug.LogError("[MapSetup] Could not add a Camera to the new Main Camera object.");

        return camera;
    }

    /// <summary>
    /// Points the saved camera at the player from the angle CameraController settles to,
    /// so the scene looks right in the editor and frame one is not a lurch. Mirrors
    /// CameraController.DesiredPosition rather than copying SampleScene's hand-placed
    /// offset, which was specific to Goblin Camp.
    /// </summary>
    private static void FrameOnPlayer(Camera camera, CameraController controller)
    {
        if (camera == null || controller == null) return;

        var player = FindPlayer();
        if (player == null) return;

        var rotation = Quaternion.Euler(controller.pitch, controller.yaw, 0f);
        camera.transform.position = player.transform.position +
                                    rotation * Vector3.back * controller.distance;
        camera.transform.rotation = rotation;
    }

    /// <summary>
    /// Refuses to call a map finished when it cannot be seen.
    ///
    /// Both maps shipped with no camera at all, and the only sign was a single warning
    /// inside a thirteen-step log, which scrolled past. Same shape as
    /// PlayerPrefabSetup.Verify: a check that fails loudly beats a check that mentions
    /// something in passing.
    /// </summary>
    internal static bool VerifyCamera(string mapName)
    {
        var cameras = Object.FindObjectsByType<Camera>(FindObjectsInactive.Include);

        int rendering = 0;
        foreach (var cam in cameras)
            if (cam != null && cam.enabled && cam.gameObject.activeInHierarchy) rendering++;

        if (rendering != 1)
        {
            Debug.LogError($"[MapSetup] {mapName} has {rendering} enabled camera(s) — expected 1. " +
                           "The map would show \"Display 1 — No cameras rendering\".");
            return false;
        }

        if (Camera.main == null)
        {
            Debug.LogError($"[MapSetup] {mapName} has a camera but nothing tagged MainCamera — " +
                           "click-to-move and the billboarded labels both read Camera.main.");
            return false;
        }

        if (Object.FindAnyObjectByType<AudioListener>(FindObjectsInactive.Include) == null)
        {
            Debug.LogError($"[MapSetup] {mapName} has no AudioListener — all positional audio " +
                           "would be silent.");
            return false;
        }

        Debug.Log($"[MapSetup] {mapName}: one MainCamera with an AudioListener. Good.");
        return true;
    }

    /// <summary>
    /// Points the monster spawner at the playable area and gives it a sane rate.
    ///
    /// The values saved in the scene were spawnInterval 0.01 over a 2000x2000 box with
    /// a cap of 500 — one monster per frame, scattered across an area a hundred times
    /// larger than the baked NavMesh, most of them nowhere near any ground the player
    /// can reach. Five hundred NavMeshAgents is also enough to bury the frame rate on
    /// its own. Both are set here so regenerating the map corrects them.
    /// </summary>
    internal static void ConfigureMonsterSpawner()
    {
        var spawner = Object.FindAnyObjectByType<MonsterSpawner>(FindObjectsInactive.Include);
        if (spawner == null)
        {
            Debug.LogWarning("[MapSetup] No MonsterSpawner in the scene — nothing will spawn.");
            return;
        }

        // A ring around the player rather than a world-space box, so this works on
        // any map without knowing its coordinates.
        spawner.minSpawnRadius = 12f;
        spawner.maxSpawnRadius = 38f;

        spawner.spawnInterval   = 4f;
        spawner.maxMonsterCount = 12;

        EditorUtility.SetDirty(spawner);
        Debug.Log($"[MapSetup] Monster spawner: {spawner.maxMonsterCount} max, one every " +
                  $"{spawner.spawnInterval}s, {spawner.minSpawnRadius}-{spawner.maxSpawnRadius} " +
                  "units from the player.");
    }

    /// <summary>PlayerController's drop pickups depend on the Player tag being set.</summary>
    internal static void EnsurePlayerTag()
    {
        var player = FindPlayer();
        if (player == null)
        {
            Debug.LogWarning("[MapSetup] No player object found — loot pickup needs it tagged 'Player'.");
            return;
        }
        if (!player.CompareTag("Player"))
        {
            player.tag = "Player";
            Debug.Log("[MapSetup] Tagged the player as 'Player'.");
        }
    }

    // ── The player ────────────────────────────────────────────────────────────

    /// <summary>
    /// The player object, however it got into the scene.
    ///
    /// By component rather than by name: the name is what a map builder sets, so
    /// searching for it would make finding the player depend on the very step that
    /// might not have run.
    /// </summary>
    internal static GameObject FindPlayer()
    {
        var controller = Object.FindAnyObjectByType<PlayerController>(FindObjectsInactive.Include);
        if (controller != null) return controller.gameObject;

        return GameObject.Find("PlayerCharacter");
    }

    /// <summary>
    /// Replaces whatever player the donor scene carried with the generated prefab, and
    /// puts it where the map wants it.
    ///
    /// ══ WHY A PREFAB RATHER THAN THE INHERITED INSTANCE ═══════════════════════
    ///
    /// Both map builders open SampleScene and inherit its player. That player's root
    /// is a RectTransform on the UI layer, because SPUM authors its rigs inside a
    /// Canvas hierarchy — so assigning transform.position to it writes z and lets
    /// anchoredPosition win back x and y on the next rect rebuild. The Hollow shipped
    /// with its player 337 units outside the map for exactly that reason, and it
    /// produced no error of any kind.
    ///
    /// PlayerPrefabSetup builds a player whose root is an ordinary Transform. Using it
    /// here is what makes a spawn point mean what it says.
    ///
    /// Falls back to repositioning the inherited object when the prefab has not been
    /// built yet, so a half-set-up project still produces a playable map — with a
    /// warning, because that path is the one with the bug in it.
    /// </summary>
    internal static GameObject EnsurePlayer(Vector3 spawn)
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPrefabSetup.PREFAB_PATH);

        var existing = FindPlayer();
        if (prefab == null)
        {
            Debug.LogWarning($"[MapSetup] No player prefab at {PlayerPrefabSetup.PREFAB_PATH} — " +
                             "run 'Idle Explorers → Build Player Prefab'. Falling back to moving the " +
                             "scene's own player, which cannot be positioned reliably.");
            if (existing != null) PlaceAnyTransform(existing.transform, spawn);
            return existing;
        }

        // Remove the donor's player before adding ours, or the map has two.
        if (existing != null)
        {
            var root = existing.transform.root.gameObject;
            Debug.Log($"[MapSetup] Replacing the inherited player object '{root.name}'.");
            Object.DestroyImmediate(root);
        }

        var player = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        PrefabUtility.UnpackPrefabInstance(player, PrefabUnpackMode.Completely,
                                            InteractionMode.AutomatedAction);

        player.name = "PlayerCharacter";
        PlaceAnyTransform(player.transform, spawn);

        return player;
    }

    /// <summary>
    /// Writes a world position that survives whatever kind of transform it lands on.
    ///
    /// A RectTransform recomputes its local x and y from anchoredPosition, so setting
    /// position alone silently keeps only z. Belt and braces for any rig that still
    /// has one at its root.
    /// </summary>
    private static void PlaceAnyTransform(Transform target, Vector3 position)
    {
        if (target == null) return;

        if (target is RectTransform rect)
        {
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta          = Vector2.zero;
            rect.anchoredPosition3D = Vector3.zero;
        }

        target.position = position;
    }

    /// <summary>
    /// Refuses to let a map ship with the player somewhere they cannot stand.
    ///
    /// Run AFTER the NavMesh bake, because "is there ground here" is a NavMesh
    /// question and the answer before the bake is always no. Returns false and logs an
    /// error rather than throwing: the caller decides whether that is fatal, and a
    /// map that saves with a loud error in the console is still more useful than one
    /// that refuses to save at all.
    /// </summary>
    internal static bool VerifyPlayerPlacement(string mapName)
    {
        var player = FindPlayer();
        if (player == null)
        {
            Debug.LogError($"[MapSetup] {mapName} has no player object at all.");
            return false;
        }

        Vector3 at = player.transform.position;

        // ── The direct question, when it can be asked ─────────────────────────
        //
        // A NavMesh sample is the strongest possible answer: it says the player is on
        // ground an agent can path across. But the query system only answers about a
        // mesh that has been ADDED to it, and a surface baked from an editor script
        // does not reliably register one outside play mode — the data is written to
        // its asset and the scene works when you press Play, while
        // NavMesh.SamplePosition here finds nothing anywhere. Asking whether the
        // system holds any mesh at all separates "the player is in the void" from
        // "this check cannot run right now", which are not the same finding and were
        // being reported as though they were.
        if (NavMesh.CalculateTriangulation().vertices.Length > 0)
        {
            if (!NavMesh.SamplePosition(at, out NavMeshHit hit, 2f, NavMesh.AllAreas))
            {
                Debug.LogError($"[MapSetup] {mapName}: the player is at {at}, which has no NavMesh " +
                               "within 2 units. They will spawn unable to move, with nothing around " +
                               "them and no monsters — check the spawn point against the map's bounds.");
                return false;
            }

            // Drop them onto the mesh rather than merely reporting the gap. A half-unit
            // of float drift between a tile's surface and the baked mesh is normal and
            // worth correcting silently.
            player.transform.position = hit.position;

            Debug.Log($"[MapSetup] {mapName}: player at {hit.position}, on walkable ground.");
            return true;
        }

        return VerifyAgainstBakedBounds(mapName, player, at);
    }

    /// <summary>
    /// The fallback check: is the player inside the geometry that was baked, and is
    /// there something solid underneath them?
    ///
    /// Weaker than a NavMesh sample — it cannot tell a walkable tile from the top of a
    /// cliff — but it needs no query system, and it is more than enough for the failure
    /// this exists to catch: a player 337 units outside a map 128 units across is not a
    /// borderline case.
    /// </summary>
    private static bool VerifyAgainstBakedBounds(string mapName, GameObject player, Vector3 at)
    {
        var surface = Object.FindAnyObjectByType<Unity.AI.Navigation.NavMeshSurface>(FindObjectsInactive.Include);
        if (surface?.navMeshData == null)
        {
            Debug.LogError($"[MapSetup] {mapName}: no baked NavMesh data to check the player against.");
            return false;
        }

        // sourceBounds is in the data's own space; the surface's transform places it.
        var bounds = surface.navMeshData.sourceBounds;
        bounds.center += surface.transform.position;

        // Generous vertically. The bake's bounds hug the ground it collected, and the
        // player is deliberately placed half a unit above it.
        bounds.Expand(new Vector3(0f, 4f, 0f));

        if (!bounds.Contains(at))
        {
            Debug.LogError($"[MapSetup] {mapName}: the player is at {at}, outside the baked area " +
                           $"{bounds}. They will spawn unable to move, with nothing around them and " +
                           "no monsters — check the spawn point against the map's bounds.");
            return false;
        }

        bool grounded = Physics.Raycast(at + Vector3.up * 50f, Vector3.down, 200f,
                                         ~0, QueryTriggerInteraction.Ignore);
        if (!grounded)
        {
            Debug.LogError($"[MapSetup] {mapName}: the player is at {at}, with no solid ground " +
                           "beneath them.");
            return false;
        }

        Debug.Log($"[MapSetup] {mapName}: player at {at}, inside the baked area and above ground. " +
                  "(No NavMesh is registered for queries in edit mode, so this is the bounds check " +
                  "rather than a NavMesh sample.)");
        return true;
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
    internal static void EnsureUrpMaterials(GameObject root)
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
    internal static void EnsureCollider(GameObject go)
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
    internal static void AddFloatingLabel(Transform parent, string text)
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

    internal static void AddSceneToBuildSettings(string scenePath)
    {
        var scenes = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
        foreach (var s in scenes)
            if (s.path == scenePath) return; // already present

        scenes.Add(new EditorBuildSettingsScene(scenePath, true));
        EditorBuildSettings.scenes = scenes.ToArray();
        Debug.Log($"[MapSetup] Added {scenePath} to Build Settings.");
    }
}
