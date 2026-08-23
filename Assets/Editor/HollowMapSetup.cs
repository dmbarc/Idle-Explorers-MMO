using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Builds "Hollow of the Fading Light" — a hand-authored map assembled from Kenney
/// Nature Kit tiles, as an alternative to the terrain-based Goblin Camp.
///
/// The layout lives in the ASCII grid below rather than in a list of coordinates.
/// That is the whole point of the approach: moving the forest, widening the plaza or
/// adding a clearing is editing a picture of the map, and the result is reviewable in
/// a diff. Coordinates are derived, never typed.
///
/// Ground rules the builder enforces:
///   • Every row must be the same width. Checked, with the offending row reported —
///     a ragged grid would otherwise produce a subtly wrong map with no error.
///   • Water tiles get NO ground tile. A hole in the ground is a hole in the NavMesh,
///     which is what makes water unwalkable without needing NavMesh area types.
///   • Cliffs and trees block; flowers, mushrooms and grass do not. Decoration that
///     carves the NavMesh turns a meadow into a maze of one-metre holes.
///   • Placement is seeded, so rebuilding produces the identical map every time.
///
/// Menu: Idle Explorers → Build Hollow Map
/// </summary>
public static class HollowMapSetup
{
    private const string SOURCE_SCENE = "Assets/Scenes/SampleScene.unity";
    private const string MAP_SCENE    = "Assets/Scenes/Map_FadingHollow.unity";
    private const string MAP_ID       = "fading_hollow";

    private const string Nature = "Assets/Kenney Game Assets All-in-1 3.7.0/3D assets/Nature Kit/Models/FBX format/";

    /// <summary>World units per grid cell. Kenney's tiles are 1 unit; this scales them.</summary>
    private const float TileSize = 4f;

    /// <summary>Fixed seed, so the scattered detail is identical on every rebuild.</summary>
    private const int LayoutSeed = 20260823;

    // ── The map ───────────────────────────────────────────────────────────────
    //
    //   C cliff (wall)        . grass            P stone path
    //   T tree                R rock             B bush
    //   F flowers             M mushrooms        ~ water (no ground: unwalkable)
    //   @ player spawn        1-7 skill nodes    S statue/landmark
    //
    // North is row 0. Every row must be the same length.
    private static readonly string[] Layout =
    {
        "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC",
        "C.....TTT...........RRR........C",
        "C...TTTTTT.........RRRRRR......C",
        "C..TTTTTTTT........RR1RRR......C",
        "C...TTT3TT..........RRRRR......C",
        "C....TTTT............RRR.......C",
        "C.....TT......B.......R........C",
        "C......B..............RR2R.....C",
        "C.................B....RRR.....C",
        "C....PPPPPPPPPPPPPPPPPPPP......C",
        "C....P..................P......C",
        "C....P....5....S....6...P......C",
        "C....P........@.........P......C",
        "C....P.......7..........P......C",
        "C....PPPPPPPPPPPPPPPPPPPP......C",
        "C...........B..................C",
        "C..FFF................~~~~.....C",
        "C.FFFFF..............~~~~~~....C",
        "C..FFF..M...........~~~~4~~....C",
        "C......MM............~~~~~~....C",
        "C.......M.............~~~~.....C",
        "C.........B....................C",
        "C..............................C",
        "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC",
    };

    /// <summary>
    /// Which skill node each digit in the layout places. The ids must match the
    /// map's skillNodes in zone_data.json, or the node resolves to nothing at
    /// runtime and logs a warning naming itself.
    /// </summary>
    private static readonly Dictionary<char, (string NodeId, string Label, string Model, float Scale)> Nodes = new()
    {
        ['1'] = ("hollow_tin_rock",   "Tin Rock",    Nature + "rock_largeC.fbx",   2.2f),
        ['2'] = ("hollow_copper_rock","Copper Rock", Nature + "rock_largeA.fbx",   2.2f),
        ['3'] = ("hollow_tree",       "Ancient Oak", Nature + "tree_oak.fbx",      2.6f),
        ['4'] = ("hollow_fishing",    "Still Water", Nature + "log.fbx",           2.0f),
        ['5'] = ("hollow_campfire",   "Campfire",    Nature + "campfire_stones.fbx", 2.2f),
        ['6'] = ("hollow_anvil",      "Anvil",       "Assets/Kenney Game Assets All-in-1 3.7.0/3D assets/Survival Kit/Models/FBX format/workbench-anvil.fbx", 2.0f),
        ['7'] = ("hollow_bank",       "Bank Chest",  "Assets/Kenney Game Assets All-in-1 3.7.0/3D assets/Survival Kit/Models/FBX format/chest.fbx", 2.4f),
    };

    // Scatter sets. One is chosen per cell from the seeded random, so a forest is
    // varied without any of it being typed out.
    private static readonly string[] TreeModels =
    {
        "tree_oak", "tree_default", "tree_tall", "tree_detailed", "tree_fat",
        "tree_pineRoundA", "tree_pineTallB", "tree_simple", "tree_thin", "tree_blocks",
    };

    private static readonly string[] RockModels =
    {
        "rock_largeA", "rock_largeB", "rock_largeC", "rock_largeD",
        "rock_tallA", "rock_tallC", "rock_tallF", "stone_largeA", "stone_largeD",
    };

    private static readonly string[] BushModels =
    {
        "plant_bush", "plant_bushDetailed", "plant_bushLarge", "plant_bushSmall", "plant_bushTriangle",
    };

    private static readonly string[] FlowerModels =
    {
        "flower_redA", "flower_redB", "flower_purpleA", "flower_purpleC",
        "flower_yellowA", "flower_yellowB", "grass", "grass_large",
    };

    private static readonly string[] MushroomModels =
    {
        "mushroom_red", "mushroom_redGroup", "mushroom_tan", "mushroom_tanTall",
    };

    private static readonly string[] StatueModels =
    {
        "statue_obelisk", "statue_column", "statue_head", "statue_ring",
    };

    // ── Entry points ──────────────────────────────────────────────────────────

    [MenuItem("Idle Explorers/Build Hollow Map")]
    public static void BuildMenu()
    {
        if (!EditorUtility.DisplayDialog("Build Hollow Map",
                $"This assembles {Path.GetFileName(MAP_SCENE)} from Kenney Nature Kit tiles " +
                $"({Layout[0].Length}x{Layout.Length} cells at {TileSize} units) and bakes its NavMesh.\n\n" +
                "SampleScene and the Goblin Camp are left alone.",
                "Build it", "Cancel"))
            return;

        Execute(showDialog: true);
    }

    public static void Execute(bool showDialog)
    {
        if (!ValidateLayout()) return;

        if (!File.Exists(SOURCE_SCENE))
        {
            Debug.LogError($"[Hollow] Source scene missing: {SOURCE_SCENE}");
            return;
        }

        // SampleScene is the donor for the things every map needs and none of which
        // are worth rebuilding: the player prefab with its controller and agent, the
        // camera, the light, the post-processing volume and the spawner.
        var scene = EditorSceneManager.OpenScene(SOURCE_SCENE, OpenSceneMode.Single);

        MapSceneSetup.StripLegacyObjects(scene);
        RemoveTerrain();

        var root  = new GameObject("Hollow");
        int cells = BuildGround(root.transform, out Vector3 spawnPoint);
        int props = BuildProps(root.transform);
        int nodes = BuildNodes(root.transform);

        PlacePlayer(spawnPoint);
        MapSceneSetup.EnsurePlayerTag();
        MapSceneSetup.EnsureCameraController();
        MapSceneSetup.ConfigureMonsterSpawner();

        bool baked = MapSceneSetup.BakeNavMesh(MAP_SCENE);

        EditorSceneManager.MarkSceneDirty(scene);
        if (!EditorSceneManager.SaveScene(scene, MAP_SCENE, saveAsCopy: false))
        {
            Debug.LogError("[Hollow] Failed to save the map scene.");
            return;
        }

        AssetDatabase.Refresh();
        MapSceneSetup.AddSceneToBuildSettings(MAP_SCENE);

        Debug.Log($"[Hollow] {MAP_SCENE} saved — {cells} ground tile(s), {props} prop(s), " +
                  $"{nodes} skill node(s), NavMesh {(baked ? "baked" : "NOT baked")}.");

        if (showDialog)
            EditorUtility.DisplayDialog("Hollow Map Built",
                $"Saved: {MAP_SCENE}\n\n" +
                $"• Ground tiles: {cells}\n• Props: {props}\n• Skill nodes: {nodes}\n" +
                $"• NavMesh: {(baked ? "baked" : "FAILED — see Console")}\n\n" +
                "Travel to it in game with the MAP button.",
                "OK");
    }

    // ── Validation ────────────────────────────────────────────────────────────

    /// <summary>
    /// Refuses to build a ragged grid. A row one character short silently shifts
    /// every cell after it, which produces a map that is wrong in a way nobody would
    /// think to look for.
    /// </summary>
    private static bool ValidateLayout()
    {
        if (Layout == null || Layout.Length == 0)
        {
            Debug.LogError("[Hollow] The layout is empty.");
            return false;
        }

        int width = Layout[0].Length;
        for (int row = 0; row < Layout.Length; row++)
        {
            if (Layout[row].Length == width) continue;

            Debug.LogError($"[Hollow] Layout row {row} is {Layout[row].Length} characters, " +
                           $"expected {width}. Every row must be the same width.");
            return false;
        }

        int spawns = 0;
        foreach (var row in Layout)
            foreach (char c in row)
                if (c == '@') spawns++;

        if (spawns != 1)
        {
            Debug.LogError($"[Hollow] Found {spawns} spawn markers ('@'). Exactly one is required.");
            return false;
        }

        return true;
    }

    // ── Geometry ──────────────────────────────────────────────────────────────

    private static int Width  => Layout[0].Length;
    private static int Height => Layout.Length;

    /// <summary>Centre of a cell in world space, with the map centred on the origin.</summary>
    private static Vector3 CellToWorld(int col, int row) => new Vector3(
        (col - (Width  - 1) / 2f) * TileSize,
        0f,
        ((Height - 1) / 2f - row) * TileSize);

    private static char At(int col, int row) =>
        (col < 0 || row < 0 || col >= Width || row >= Height) ? 'C' : Layout[row][col];

    /// <summary>
    /// Lays the ground plane, the cliff walls, and one flat collider under the lot.
    /// </summary>
    private static int BuildGround(Transform parent, out Vector3 spawnPoint)
    {
        var ground = new GameObject("Ground");
        ground.transform.SetParent(parent, false);

        var walls = new GameObject("Cliffs");
        walls.transform.SetParent(parent, false);

        spawnPoint = Vector3.zero;
        int placed = 0;

        for (int row = 0; row < Height; row++)
        {
            for (int col = 0; col < Width; col++)
            {
                char cell     = At(col, row);
                Vector3 world = CellToWorld(col, row);

                if (cell == '@') spawnPoint = world;

                if (cell == 'C')
                {
                    // Cliffs are the map's walls: they take a collider and they carve
                    // the NavMesh, which is what actually keeps the player inside.
                    var cliff = Place("cliff_block_rock", walls.transform, world, 0f, TileSize);
                    if (cliff != null) { MapSceneSetup.EnsureCollider(cliff); placed++; }
                    continue;
                }

                if (cell == '~')
                {
                    // No ground tile at all. The hole in the mesh is the hole in the
                    // NavMesh — water you cannot walk on, with no area types needed.
                    var water = Place("ground_riverTile", ground.transform,
                                       world + Vector3.down * 0.35f, 0f, TileSize);
                    IgnoreInNavMesh(water);
                    continue;
                }

                var tile = Place("ground_grass", ground.transform, world, 0f, TileSize);
                if (tile != null) placed++;

                if (cell == 'P')
                    Place("path_stone", ground.transform, world + Vector3.up * 0.02f,
                          0f, TileSize * 0.98f);
            }
        }

        // One flat collider for click-to-move, instead of a mesh collider per tile.
        // The raycast only needs a surface to hit; a thousand mesh colliders to
        // express a flat plane is a waste the player would feel.
        var floor = new GameObject("GroundCollider");
        floor.transform.SetParent(parent, false);
        var box = floor.AddComponent<BoxCollider>();
        box.size   = new Vector3(Width * TileSize, 0.2f, Height * TileSize);
        box.center = new Vector3(0f, -0.1f, 0f);
        IgnoreInNavMesh(floor);

        MarkStatic(parent.gameObject);
        return placed;
    }

    /// <summary>Scatters the decoration described by the layout.</summary>
    private static int BuildProps(Transform parent)
    {
        var props = new GameObject("Props");
        props.transform.SetParent(parent, false);

        // Seeded, so the map is reproducible rather than different on every rebuild.
        var random  = new System.Random(LayoutSeed);
        int placed  = 0;

        for (int row = 0; row < Height; row++)
        {
            for (int col = 0; col < Width; col++)
            {
                char cell = At(col, row);

                string[] set = cell switch
                {
                    'T' => TreeModels,
                    'R' => RockModels,
                    'B' => BushModels,
                    'F' => FlowerModels,
                    'M' => MushroomModels,
                    'S' => StatueModels,
                    _   => null,
                };
                if (set == null) continue;

                // Jitter inside the cell so a block of forest does not read as a grid.
                Vector3 jitter = new Vector3(
                    (float)(random.NextDouble() - 0.5) * TileSize * 0.45f, 0f,
                    (float)(random.NextDouble() - 0.5) * TileSize * 0.45f);

                float yaw   = (float)random.NextDouble() * 360f;
                float scale = TileSize * (0.75f + (float)random.NextDouble() * 0.4f);

                var go = Place(set[random.Next(set.Length)], props.transform,
                                CellToWorld(col, row) + jitter, yaw, scale);
                if (go == null) continue;

                placed++;

                // Trees, rocks and statues are things you walk around. Flowers,
                // mushrooms and grass are things you walk through — giving them
                // colliders and NavMesh carve-outs would turn a meadow into a maze
                // of one-metre obstacles for no visual gain.
                if (cell == 'T' || cell == 'R' || cell == 'S')
                    MapSceneSetup.EnsureCollider(go);
                else
                    IgnoreInNavMesh(go);
            }
        }

        MarkStatic(props);
        return placed;
    }

    /// <summary>Places the interactive skill nodes the layout marks with digits.</summary>
    private static int BuildNodes(Transform parent)
    {
        var holder = new GameObject("SkillNodes");
        holder.transform.SetParent(parent, false);

        int placed = 0;

        for (int row = 0; row < Height; row++)
        {
            for (int col = 0; col < Width; col++)
            {
                char cell = At(col, row);
                if (!Nodes.TryGetValue(cell, out var spec)) continue;

                var go = PlaceByPath(spec.Model, holder.transform,
                                      CellToWorld(col, row), 0f, spec.Scale);
                if (go == null)
                {
                    Debug.LogWarning($"[Hollow] Model missing for node '{spec.NodeId}': {spec.Model}");
                    continue;
                }

                go.name = $"Node_{spec.NodeId}";
                MapSceneSetup.EnsureCollider(go);

                var node = go.AddComponent<SkillNodeController>();
                node.nodeId = spec.NodeId;

                // Same reasoning as the Goblin Camp: a node that carves the NavMesh
                // can wall off the very thing the player is walking to, and its
                // floating name tag is a mesh at roughly head height.
                IgnoreInNavMesh(go);

                MapSceneSetup.AddFloatingLabel(go.transform, spec.Label);
                placed++;
            }
        }

        return placed;
    }

    // ── Placement helpers ─────────────────────────────────────────────────────

    private static GameObject Place(string modelName, Transform parent, Vector3 position,
                                     float yaw, float targetSize)
        => PlaceByPath(Nature + modelName + ".fbx", parent, position, yaw, targetSize);

    /// <summary>
    /// Instantiates a model, scaled so its footprint matches targetSize.
    ///
    /// Measured rather than assumed: Kenney's kits are mostly authored on a one-unit
    /// grid but not uniformly, and a hardcoded multiplier would leave gaps between
    /// ground tiles that are visible from the first frame.
    /// </summary>
    private static GameObject PlaceByPath(string assetPath, Transform parent, Vector3 position,
                                           float yaw, float targetSize)
    {
        var prefab = LoadModel(assetPath);
        if (prefab == null) return null;

        var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent);
        PrefabUtility.UnpackPrefabInstance(go, PrefabUnpackMode.Completely,
                                            InteractionMode.AutomatedAction);

        float footprint = MeasureFootprint(prefab);
        float scale     = footprint > 0.001f ? targetSize / footprint : targetSize;

        go.transform.localScale = Vector3.one * scale;
        go.transform.position   = position;
        go.transform.rotation   = Quaternion.Euler(0f, yaw, 0f);

        MapSceneSetup.EnsureUrpMaterials(go);
        return go;
    }

    private static readonly Dictionary<string, GameObject> _modelCache = new();
    private static readonly Dictionary<GameObject, float>  _footprintCache = new();

    private static GameObject LoadModel(string assetPath)
    {
        if (_modelCache.TryGetValue(assetPath, out var cached)) return cached;

        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
        if (prefab == null)
            Debug.LogWarning($"[Hollow] Model not found: {assetPath}");

        _modelCache[assetPath] = prefab;
        return prefab;
    }

    /// <summary>Largest horizontal dimension of a model, in its own units.</summary>
    private static float MeasureFootprint(GameObject prefab)
    {
        if (_footprintCache.TryGetValue(prefab, out float cached)) return cached;

        float largest = 0f;
        foreach (var filter in prefab.GetComponentsInChildren<MeshFilter>(includeInactive: true))
        {
            if (filter.sharedMesh == null) continue;
            var size = filter.sharedMesh.bounds.size;
            largest  = Mathf.Max(largest, size.x, size.z);
        }

        _footprintCache[prefab] = largest;
        return largest;
    }

    /// <summary>Keeps an object out of the NavMesh bake without disabling it.</summary>
    private static void IgnoreInNavMesh(GameObject go)
    {
        if (go == null) return;
        var modifier = go.GetComponent<Unity.AI.Navigation.NavMeshModifier>()
                       ?? go.AddComponent<Unity.AI.Navigation.NavMeshModifier>();
        modifier.ignoreFromBuild = true;
    }

    /// <summary>
    /// Marks the scenery batching-static. Around a thousand small meshes is a lot of
    /// draw calls otherwise, and none of it ever moves.
    /// </summary>
    private static void MarkStatic(GameObject root)
    {
        foreach (var transform in root.GetComponentsInChildren<Transform>(includeInactive: true))
            GameObjectUtility.SetStaticEditorFlags(transform.gameObject,
                                                    StaticEditorFlags.BatchingStatic);
    }

    // ── Player ────────────────────────────────────────────────────────────────

    private static void PlacePlayer(Vector3 spawnPoint)
    {
        var player = GameObject.Find("PlayerCharacter");
        if (player == null)
        {
            Debug.LogWarning("[Hollow] No PlayerCharacter in the source scene — nothing to place.");
            return;
        }

        // Slightly above the tiles; the agent drops onto the NavMesh on the first frame.
        player.transform.position = spawnPoint + Vector3.up * 0.5f;
        Debug.Log($"[Hollow] Player placed at {player.transform.position}.");
    }

    /// <summary>Removes SampleScene's terrain — this map builds its own ground.</summary>
    private static void RemoveTerrain()
    {
        foreach (var terrain in UnityEngine.Object.FindObjectsByType<Terrain>(FindObjectsInactive.Include))
        {
            if (terrain == null) continue;
            Debug.Log($"[Hollow] Removing terrain '{terrain.name}' — the hollow builds its own ground.");
            UnityEngine.Object.DestroyImmediate(terrain.gameObject);
        }
    }
}
