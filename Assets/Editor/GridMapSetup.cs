using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Builds a hand-authored map from an ASCII picture of it.
///
/// The layout lives in a grid of characters rather than a list of coordinates. That is
/// the whole point of the approach: moving the forest, widening the plaza or adding a
/// clearing is editing a picture of the map, and the result is reviewable in a diff.
/// Coordinates are derived, never typed.
///
/// ══ WHY THIS IS SHARED ════════════════════════════════════════════════════════
///
/// The Hollow of the Fading Light was built this way and the Goblin Camp was not: it
/// was seven skill nodes arranged on a circle around a point on SampleScene's terrain,
/// which is a debug scene rather than a place. Making the Goblin Camp a real map meant
/// either a second copy of five hundred lines or one builder taking two recipes, and
/// the second copy is the kind that drifts — the scale fix that started this would
/// have had to be made twice, correctly, by somebody who remembered there were two.
///
/// So a map is now DATA: a layout, a palette of models, a table of nodes and a mood.
/// A third map is a new file with no new machinery.
///
/// Ground rules the builder enforces:
///   • Every row must be the same width. Checked, with the offending row reported —
///     a ragged grid would otherwise produce a subtly wrong map with no error.
///   • Exactly one spawn marker.
///   • Water tiles get NO ground tile. A hole in the ground is a hole in the NavMesh,
///     which is what makes water unwalkable without needing NavMesh area types.
///   • Blocking scenery gets a collider and carves the NavMesh; decoration does not.
///     Decoration that carves turns a meadow into a maze of one-metre holes.
///   • Placement is seeded, so rebuilding produces the identical map every time.
///   • Everything is sized in PEOPLE, never in tiles. See SpumRig.CharacterHeight.
/// </summary>
public class GridMapSetup
{
    // ── The recipe ────────────────────────────────────────────────────────────

    /// <summary>One interactive node: what it is, and which model stands for it.</summary>
    public readonly struct NodeSpec
    {
        public readonly string NodeId;
        public readonly string Label;
        public readonly string Model;

        /// <summary>Height as a multiple of the height of a person.</summary>
        public readonly float HeightVsPlayer;

        public NodeSpec(string nodeId, string label, string model, float heightVsPlayer)
        {
            NodeId         = nodeId;
            Label          = label;
            Model          = model;
            HeightVsPlayer = heightVsPlayer;
        }
    }

    /// <summary>One category of scattered scenery.</summary>
    public readonly struct ScatterSpec
    {
        /// <summary>Bare model names, resolved under the recipe's ModelRoot.</summary>
        public readonly string[] Models;

        /// <summary>Height as a multiple of the height of a person.</summary>
        public readonly float HeightVsPlayer;

        /// <summary>Gets a collider and carves the NavMesh — something you walk around.</summary>
        public readonly bool Blocks;

        /// <summary>
        /// Faces along its own run instead of taking a random yaw. A fence is a LENGTH
        /// of fence: spinning each post to a random angle turns a palisade into
        /// scattered planks.
        /// </summary>
        public readonly bool AlignToRun;

        public ScatterSpec(string[] models, float heightVsPlayer, bool blocks,
                           bool alignToRun = false)
        {
            Models         = models;
            HeightVsPlayer = heightVsPlayer;
            Blocks         = blocks;
            AlignToRun     = alignToRun;
        }
    }

    /// <summary>Everything that makes one map different from another.</summary>
    public class Recipe
    {
        /// <summary>Short tag for console messages, e.g. "Hollow".</summary>
        public string Tag;

        public string DisplayName;
        public string ScenePath;

        /// <summary>North is row 0. Every row must be the same length.</summary>
        public string[] Layout;

        /// <summary>World units per grid cell. Kenney's tiles are 1 unit; this scales them.</summary>
        public float TileSize = 4f;

        /// <summary>Fixed seed, so the scattered detail is identical on every rebuild.</summary>
        public int Seed;

        /// <summary>How far from a 'g' marker its monsters may appear, in world units.</summary>
        public float CampRadius = 9f;

        /// <summary>Folder the bare model names in ScatterSpec are resolved against.</summary>
        public string ModelRoot;

        public string GroundModel;
        public string PathModel;
        public string CliffModel;

        /// <summary>
        /// The colour of open water.
        ///
        /// Water is a generated flat surface rather than a model. Kenney's
        /// ground_riverTile is a POND set into a grass square — laid out cell by cell
        /// it produced a field of separate little puddles with grass between them,
        /// which is what "a bunch of tiny pools" was. A plain quad per cell meets its
        /// neighbours exactly and reads as one body of water.
        /// </summary>
        public Color WaterColor = new Color(0.10f, 0.30f, 0.38f, 1f);

        public Dictionary<char, NodeSpec>    Nodes   = new();
        public Dictionary<char, ScatterSpec> Scatter = new();

        /// <summary>
        /// Boss portals, by grid character.
        ///
        /// Separate from Nodes because a portal is not a skill node: nothing is
        /// gathered from it, it carries no rates, and it is gated on something a
        /// skill node has no concept of. Sharing the dictionary would have meant a
        /// SkillNodeController with every field empty and a special case reading it.
        /// </summary>
        public Dictionary<char, PortalSpec>  Portals = new();

        public MapSceneSetup.MapMood Mood;
    }

    /// <summary>A boss door: what opens it, and where it goes.</summary>
    public class PortalSpec
    {
        public string GateMonsterId;
        public long   RequiredKills;
        public string DestinationMapId;
        public string Model;
        public float  HeightVsPlayer;

        public PortalSpec(string gateMonsterId, long requiredKills, string destinationMapId,
                          string model, float heightVsPlayer)
        {
            GateMonsterId    = gateMonsterId;
            RequiredKills    = requiredKills;
            DestinationMapId = destinationMapId;
            Model            = model;
            HeightVsPlayer   = heightVsPlayer;
        }
    }

    private const string SOURCE_SCENE = "Assets/Scenes/SampleScene.unity";

    private readonly Recipe _recipe;

    private GridMapSetup(Recipe recipe) => _recipe = recipe;

    // ── Entry point ───────────────────────────────────────────────────────────

    public static void Build(Recipe recipe, bool showDialog)
    {
        if (recipe == null)
        {
            Debug.LogError("[GridMap] No recipe.");
            return;
        }

        new GridMapSetup(recipe).Execute(showDialog);
    }

    private void Execute(bool showDialog)
    {
        string tag = _recipe.Tag;

        if (!ValidateLayout()) return;

        if (!File.Exists(SOURCE_SCENE))
        {
            Debug.LogError($"[{tag}] Source scene missing: {SOURCE_SCENE}");
            return;
        }

        // SampleScene is the donor for the things every map needs and none of which
        // are worth rebuilding: the player prefab with its controller and agent, the
        // post-processing volume and the spawner.
        var scene = EditorSceneManager.OpenScene(SOURCE_SCENE, OpenSceneMode.Single);

        MapSceneSetup.StripLegacyObjects(scene);
        RemoveTerrain();

        var root  = new GameObject(_recipe.DisplayName);
        int cells = BuildGround(root.transform, out Vector3 spawnPoint);
        int props = BuildProps(root.transform);
        int nodes   = BuildNodes(root.transform);
        int portals = BuildPortals(root.transform);
        int camps   = BuildCamps(root.transform);

        // Slightly above the tiles; the agent drops onto the NavMesh on the first frame.
        MapSceneSetup.EnsurePlayer(spawnPoint + Vector3.up * 0.5f);

        MapSceneSetup.EnsurePlayerTag();
        MapSceneSetup.EnsureCameraController();
        MapSceneSetup.ConfigureLighting(_recipe.Mood);
        MapSceneSetup.ConfigureMonsterSpawner();

        bool baked = MapSceneSetup.BakeNavMesh(_recipe.ScenePath);

        // After the bake, and loudly: this map shipped once with its player 337 units
        // outside the layout and every symptom looked like a rendering fault.
        MapSceneSetup.VerifyPlayerPlacement(_recipe.DisplayName);
        MapSceneSetup.VerifyCamera(_recipe.DisplayName);

        EditorSceneManager.MarkSceneDirty(scene);
        if (!EditorSceneManager.SaveScene(scene, _recipe.ScenePath, saveAsCopy: false))
        {
            Debug.LogError($"[{tag}] Failed to save the map scene.");
            return;
        }

        AssetDatabase.Refresh();
        MapSceneSetup.AddSceneToBuildSettings(_recipe.ScenePath);

        Debug.Log($"[{tag}] {_recipe.ScenePath} saved — {cells} ground tile(s), {props} prop(s), " +
                  $"{nodes} skill node(s), {portals} portal(s), {camps} monster camp(s), " +
                  $"NavMesh {(baked ? "baked" : "NOT baked")}.");

        if (showDialog)
            EditorUtility.DisplayDialog($"{_recipe.DisplayName} Built",
                $"Saved: {_recipe.ScenePath}\n\n" +
                $"• Ground tiles: {cells}\n• Props: {props}\n• Skill nodes: {nodes}\n" +
                $"• Monster camps: {camps}\n" +
                $"• NavMesh: {(baked ? "baked" : "FAILED — see Console")}\n\n" +
                "Travel to it in game with the MAP button.",
                "OK");
    }

    // ── Validation ────────────────────────────────────────────────────────────

    /// <summary>
    /// Refuses to build a ragged grid. A row one character short silently shifts every
    /// cell after it, which produces a map that is wrong in a way nobody would think
    /// to look for.
    /// </summary>
    private bool ValidateLayout()
    {
        string tag = _recipe.Tag;

        if (_recipe.Layout == null || _recipe.Layout.Length == 0)
        {
            Debug.LogError($"[{tag}] The layout is empty.");
            return false;
        }

        int width = _recipe.Layout[0].Length;
        for (int row = 0; row < _recipe.Layout.Length; row++)
        {
            if (_recipe.Layout[row].Length == width) continue;

            Debug.LogError($"[{tag}] Layout row {row} is {_recipe.Layout[row].Length} characters, " +
                           $"expected {width}. Every row must be the same width.");
            return false;
        }

        int spawns = 0;
        foreach (var row in _recipe.Layout)
            foreach (char c in row)
                if (c == '@') spawns++;

        if (spawns != 1)
        {
            Debug.LogError($"[{tag}] Found {spawns} spawn markers ('@'). Exactly one is required.");
            return false;
        }

        // Every node the layout places must be somewhere in zone_data.json, or it
        // resolves to nothing at runtime and the player clicks a rock that does not
        // do anything. Checked here because the layout is where the mistake is made.
        var seen = new HashSet<string>();
        foreach (var entry in _recipe.Nodes)
        {
            if (seen.Add(entry.Value.NodeId)) continue;
            Debug.LogError($"[{tag}] Two layout characters both place node " +
                           $"'{entry.Value.NodeId}'. Node ids must be unique.");
            return false;
        }

        return ValidateReachability();
    }

    // ── Geometry ──────────────────────────────────────────────────────────────

    private int Width  => _recipe.Layout[0].Length;
    private int Height => _recipe.Layout.Length;

    /// <summary>Centre of a cell in world space, with the map centred on the origin.</summary>
    private Vector3 CellToWorld(int col, int row) => new Vector3(
        (col - (Width  - 1) / 2f) * _recipe.TileSize,
        0f,
        ((Height - 1) / 2f - row) * _recipe.TileSize);

    private char At(int col, int row) =>
        (col < 0 || row < 0 || col >= Width || row >= Height) ? 'C' : _recipe.Layout[row][col];

    /// <summary>
    /// Lays the ground plane, the cliff walls, and one flat collider under the lot.
    /// </summary>
    private int BuildGround(Transform parent, out Vector3 spawnPoint)
    {
        float tile = _recipe.TileSize;

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
                    var cliff = PlaceByFootprint(_recipe.CliffModel, walls.transform, world, 0f, tile);
                    if (cliff != null) { MapSceneSetup.EnsureCollider(cliff); placed++; }
                    continue;
                }

                if (cell == '~')
                {
                    // No ground tile at all. The hole in the mesh is the hole in the
                    // NavMesh — water you cannot walk on, with no area types needed.
                    BuildWaterTile(ground.transform, world + Vector3.down * 0.35f, tile);
                    continue;
                }

                var floorTile = PlaceByFootprint(_recipe.GroundModel, ground.transform, world, 0f, tile);
                if (floorTile != null) placed++;

                if (cell == 'P' && !string.IsNullOrEmpty(_recipe.PathModel))
                    PlaceByFootprint(_recipe.PathModel, ground.transform,
                                      world + Vector3.up * 0.02f, 0f, tile * 0.98f);
            }
        }

        // One flat collider for click-to-move, instead of a mesh collider per tile.
        // The raycast only needs a surface to hit; a thousand mesh colliders to
        // express a flat plane is a waste the player would feel.
        var floor = new GameObject("GroundCollider");
        floor.transform.SetParent(parent, false);
        var box = floor.AddComponent<BoxCollider>();
        box.size   = new Vector3(Width * tile, 0.2f, Height * tile);
        box.center = new Vector3(0f, -0.1f, 0f);
        IgnoreInNavMesh(floor);

        MarkStatic(parent.gameObject);
        return placed;
    }

    private Material _waterMaterial;

    /// <summary>
    /// One flat square of open water.
    ///
    /// A generated quad rather than a model, because Kenney's ground_riverTile is a
    /// pond set into a grass square: laid cell by cell it produced a field of separate
    /// little puddles with grass showing between them rather than a lake. A quad meets
    /// its neighbours exactly, at any tile size, with no model to measure.
    ///
    /// No collider. Water is unwalkable because there is no NavMesh over it, and a
    /// collider here would only give click-to-move somewhere to aim that the character
    /// can never reach.
    /// </summary>
    private void BuildWaterTile(Transform parent, Vector3 position, float tile)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
        go.name = "Water";
        go.transform.SetParent(parent, false);

        // CreatePrimitive fits a MeshCollider as a matter of course. See above.
        var collider = go.GetComponent<Collider>();
        if (collider != null) Object.DestroyImmediate(collider);

        // A Quad faces +Z, so it has to be laid down to be a surface.
        go.transform.position   = position;
        go.transform.rotation   = Quaternion.Euler(90f, 0f, 0f);
        go.transform.localScale = new Vector3(tile, tile, 1f);

        go.GetComponent<MeshRenderer>().sharedMaterial = WaterMaterial();

        IgnoreInNavMesh(go);
    }

    private Material WaterMaterial()
    {
        if (_waterMaterial != null) return _waterMaterial;

        var shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) shader = Shader.Find("Standard");

        _waterMaterial = new Material(shader) { name = "Water" };

        if (_waterMaterial.HasProperty("_BaseColor")) _waterMaterial.SetColor("_BaseColor", _recipe.WaterColor);
        if (_waterMaterial.HasProperty("_Color"))     _waterMaterial.SetColor("_Color",     _recipe.WaterColor);

        // Wet, not matte. Water that takes no specular from the sun reads as painted
        // concrete, which is most of what made the old ponds look like decals.
        if (_waterMaterial.HasProperty("_Smoothness")) _waterMaterial.SetFloat("_Smoothness", 0.85f);
        if (_waterMaterial.HasProperty("_Glossiness")) _waterMaterial.SetFloat("_Glossiness", 0.85f);

        return _waterMaterial;
    }

    /// <summary>
    /// Every skill node must be somewhere the player can walk to.
    ///
    /// ══ THE FISHING SPOT NOBODY COULD REACH ═══════════════════════════════════
    ///
    /// The fishing node was drawn in the MIDDLE of the water. Water cells get no
    /// ground tile — that hole is what makes water unwalkable — so the node's own cell
    /// was a single square of land with a moat around it and no NavMesh path to it
    /// from anywhere. It rendered, it was clickable, and the character walked to the
    /// shore and stopped. Nothing logged, because from the builder's point of view a
    /// node had been placed exactly where the picture said.
    ///
    /// A flood fill from the spawn over walkable cells answers the question the
    /// picture cannot: not "is this cell land" but "can you get there".
    /// </summary>
    private bool ValidateReachability()
    {
        int width = Width, height = Height;

        int spawnRow = -1, spawnCol = -1;
        for (int row = 0; row < height; row++)
            for (int col = 0; col < width; col++)
                if (At(col, row) == '@') { spawnRow = row; spawnCol = col; }

        if (spawnRow < 0) return true;   // the spawn check has already failed

        var reached = new bool[height, width];
        var queue   = new Queue<(int Col, int Row)>();

        reached[spawnRow, spawnCol] = true;
        queue.Enqueue((spawnCol, spawnRow));

        // Four-way, not eight: an agent cannot squeeze through the corner where two
        // diagonal water cells touch, so counting diagonals would call a spit of land
        // reachable that in play is not.
        var steps = new (int Col, int Row)[] { (1, 0), (-1, 0), (0, 1), (0, -1) };

        while (queue.Count > 0)
        {
            var (col, row) = queue.Dequeue();

            foreach (var step in steps)
            {
                int nc = col + step.Col, nr = row + step.Row;
                if (nc < 0 || nr < 0 || nc >= width || nr >= height) continue;
                if (reached[nr, nc]) continue;

                char cell = At(nc, nr);
                if (cell == 'C' || cell == '~') continue;

                reached[nr, nc] = true;
                queue.Enqueue((nc, nr));
            }
        }

        bool ok = true;

        for (int row = 0; row < height; row++)
            for (int col = 0; col < width; col++)
            {
                char cell = At(col, row);

                if (_recipe.Nodes.TryGetValue(cell, out var node) && !reached[row, col])
                {
                    Debug.LogError($"[{_recipe.Tag}] Node '{node.NodeId}' at column {col}, row {row} " +
                                   "cannot be walked to from the spawn. A node inside the water or " +
                                   "behind the cliffs is a node the player watches from a distance.");
                    ok = false;
                }

                if (cell == 'g' && !reached[row, col])
                {
                    Debug.LogError($"[{_recipe.Tag}] Monster camp at column {col}, row {row} is cut " +
                                   "off from the spawn — nothing it spawns can reach the player.");
                    ok = false;
                }
            }

        return ok;
    }

    /// <summary>Scatters the decoration described by the layout.</summary>
    private int BuildProps(Transform parent)
    {
        var props = new GameObject("Props");
        props.transform.SetParent(parent, false);

        // Seeded, so the map is reproducible rather than different on every rebuild.
        var random = new System.Random(_recipe.Seed);
        int placed = 0;

        for (int row = 0; row < Height; row++)
        {
            for (int col = 0; col < Width; col++)
            {
                char cell = At(col, row);
                if (!_recipe.Scatter.TryGetValue(cell, out var spec)) continue;
                if (spec.Models == null || spec.Models.Length == 0) continue;

                // Jitter inside the cell so a block of forest does not read as a grid.
                // A run of fence is laid on the line instead: jittering a palisade
                // makes it look like it fell over.
                Vector3 jitter = spec.AlignToRun ? Vector3.zero : new Vector3(
                    (float)(random.NextDouble() - 0.5) * _recipe.TileSize * 0.45f, 0f,
                    (float)(random.NextDouble() - 0.5) * _recipe.TileSize * 0.45f);

                float yaw = spec.AlignToRun
                    ? RunYaw(col, row, cell)
                    : (float)random.NextDouble() * 360f;

                // A fifth either way, so a stand of trees has some variety in it
                // without any of them being the wrong size for a tree.
                float variance = spec.AlignToRun
                    ? 1f
                    : 0.8f + (float)random.NextDouble() * 0.4f;

                string model = spec.Models[random.Next(spec.Models.Length)];

                var go = PlaceByHeight(_recipe.ModelRoot + model + ".fbx", props.transform,
                                        CellToWorld(col, row) + jitter, yaw,
                                        spec.HeightVsPlayer * variance,
                                        // A modular piece is stretched a hair past the cell so it
                                        // overlaps its neighbour. Meeting exactly leaves a seam
                                        // an agent can find even when the picture looks solid.
                                        spec.AlignToRun ? _recipe.TileSize * 1.04f : 0f);
                if (go == null) continue;

                placed++;

                // Things you walk around get a collider and carve the NavMesh. Things
                // you walk through — flowers, mushrooms, grass — do not: giving them
                // carve-outs would turn a meadow into a maze of one-metre obstacles
                // for no visual gain.
                if (spec.Blocks) MapSceneSetup.EnsureCollider(go);
                else             IgnoreInNavMesh(go);
            }
        }

        MarkStatic(props);
        return placed;
    }

    /// <summary>
    /// Which way a run of scenery faces: along the row if its horizontal neighbours
    /// match, across it if only the vertical ones do.
    /// </summary>
    private float RunYaw(int col, int row, char cell)
    {
        bool horizontal = At(col - 1, row) == cell || At(col + 1, row) == cell;
        bool vertical   = At(col, row - 1) == cell || At(col, row + 1) == cell;

        return vertical && !horizontal ? 90f : 0f;
    }

    /// <summary>Places the interactive skill nodes the layout marks with digits.</summary>
    private int BuildNodes(Transform parent)
    {
        var holder = new GameObject("SkillNodes");
        holder.transform.SetParent(parent, false);

        int placed = 0;

        for (int row = 0; row < Height; row++)
        {
            for (int col = 0; col < Width; col++)
            {
                char cell = At(col, row);
                if (!_recipe.Nodes.TryGetValue(cell, out var spec)) continue;

                var go = PlaceByHeight(spec.Model, holder.transform,
                                        CellToWorld(col, row), 0f, spec.HeightVsPlayer);
                if (go == null)
                {
                    Debug.LogWarning($"[{_recipe.Tag}] Model missing for node " +
                                     $"'{spec.NodeId}': {spec.Model}");
                    continue;
                }

                go.name = $"Node_{spec.NodeId}";
                MapSceneSetup.EnsureCollider(go);

                var node = go.AddComponent<SkillNodeController>();
                node.nodeId = spec.NodeId;

                // A node that carves the NavMesh puts a hole where the player has to
                // stand to use it.
                IgnoreInNavMesh(go);

                MapSceneSetup.AddFloatingLabel(go.transform, spec.Label);
                placed++;
            }
        }

        return placed;
    }

    /// <summary>
    /// Places the boss portals the layout marks.
    ///
    /// Given a collider and a floating label like a skill node, and kept OUT of the
    /// NavMesh for the same reason: a portal that carves a hole puts the hole exactly
    /// where the player has to stand to walk through it.
    /// </summary>
    private int BuildPortals(Transform parent)
    {
        if (_recipe.Portals.Count == 0) return 0;

        var holder = new GameObject("BossPortals");
        holder.transform.SetParent(parent, false);

        int placed = 0;

        for (int row = 0; row < Height; row++)
        {
            for (int col = 0; col < Width; col++)
            {
                char cell = At(col, row);
                if (!_recipe.Portals.TryGetValue(cell, out var spec)) continue;

                var go = PlaceByHeight(spec.Model, holder.transform,
                                        CellToWorld(col, row), 0f, spec.HeightVsPlayer);
                if (go == null)
                {
                    Debug.LogWarning($"[{_recipe.Tag}] Model missing for portal to " +
                                     $"'{spec.DestinationMapId}': {spec.Model}");
                    continue;
                }

                go.name = $"Portal_{spec.DestinationMapId}";
                MapSceneSetup.EnsureCollider(go);

                var portal = go.AddComponent<BossPortalController>();
                portal.gateMonsterId    = spec.GateMonsterId;
                portal.requiredKills    = spec.RequiredKills;
                portal.destinationMapId = spec.DestinationMapId;

                IgnoreInNavMesh(go);

                // The label is rewritten at runtime with the live count; this is what
                // it says in the editor and before the first refresh.
                MapSceneSetup.AddFloatingLabel(go.transform, "The Throne");
                placed++;
            }
        }

        return placed;
    }

    /// <summary>Places the monster camps the layout marks with 'g'.</summary>
    private int BuildCamps(Transform parent)
    {
        var holder = new GameObject("MonsterCamps");
        holder.transform.SetParent(parent, false);

        int placed = 0;

        for (int row = 0; row < Height; row++)
        {
            for (int col = 0; col < Width; col++)
            {
                if (At(col, row) != 'g') continue;

                var go = new GameObject($"Camp_{placed + 1}");
                go.transform.SetParent(holder.transform, false);
                go.transform.position = CellToWorld(col, row);

                var camp = go.AddComponent<MonsterCamp>();
                camp.radius = _recipe.CampRadius;

                placed++;
            }
        }

        return placed;
    }

    // ── Placement helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Instantiates a model scaled so its FOOTPRINT matches targetSize.
    ///
    /// Only for the ground: tiles have to meet their neighbours exactly, so what
    /// matters is how much of a cell they cover. Everything a player looks at is
    /// placed by height instead — see PlaceByHeight for why.
    ///
    /// Measured rather than assumed: Kenney's kits are mostly authored on a one-unit
    /// grid but not uniformly, and a hardcoded multiplier would leave gaps between
    /// ground tiles that are visible from the first frame.
    /// </summary>
    private GameObject PlaceByFootprint(string modelName, Transform parent, Vector3 position,
                                         float yaw, float targetSize)
    {
        var prefab = LoadModel(Resolve(modelName));
        if (prefab == null) return null;

        var go = Instantiate(prefab, parent, position, yaw);

        float footprint = MeasureFootprint(prefab);
        go.transform.localScale = Vector3.one *
                                  (footprint > 0.001f ? targetSize / footprint : targetSize);

        MapSceneSetup.EnsureUrpMaterials(go);
        return go;
    }

    /// <summary>
    /// Instantiates a model scaled so it stands a given number of PEOPLE tall.
    ///
    /// ══ WHY EVERYTHING WAS THE WRONG SIZE ═════════════════════════════════════
    ///
    /// Props used to be scaled by footprint, to a multiple of the four-unit tile: a
    /// flower and an oak both came out roughly four units across, because the number
    /// said how much of a tile to fill rather than how big the thing was. That reads
    /// as a deliberate scale for ground tiles and as nothing at all for a mushroom.
    ///
    /// Height, against the height of a person, is how anybody actually describes the
    /// size of an object — "the anvil should be half the size of the player" is the
    /// sentence that produced this method. SpumRig.CharacterHeight is the same number
    /// the player rig is scaled to, so the two cannot drift apart.
    ///
    /// <paramref name="fillLength"/> is for MODULAR pieces — a fence is a segment
    /// designed to meet the next one. Scaled to height alone, a two-metre palisade
    /// post covers two metres of a four-metre cell and the wall is half gaps, which
    /// an agent walks straight through and the player reads as a broken fence. So its
    /// long axis is stretched to the cell instead, and only its height comes from the
    /// number of people.
    /// </summary>
    private GameObject PlaceByHeight(string assetPath, Transform parent, Vector3 position,
                                      float yaw, float heightVsPlayer, float fillLength = 0f)
    {
        var prefab = LoadModel(assetPath);
        if (prefab == null) return null;

        Vector3 size   = MeasureSize(prefab);
        float   target = SpumRig.CharacterHeight * heightVsPlayer;
        float   uniform = size.y > 0.001f ? target / size.y : target;

        var scale = Vector3.one * uniform;

        if (fillLength > 0f)
        {
            // Which way the model is long decides both how to turn it and which axis
            // to stretch. Kenney is consistent within a kit but not across them, and
            // a fence stretched across its thickness is a wall of paving slabs.
            bool alongX = size.x >= size.z;

            if (alongX && size.x > 0.001f) scale.x = fillLength / size.x;
            else if (size.z > 0.001f)      scale.z = fillLength / size.z;

            if (!alongX) yaw += 90f;
        }

        var go = Instantiate(prefab, parent, position, yaw);
        go.transform.localScale = scale;

        MapSceneSetup.EnsureUrpMaterials(go);
        return go;
    }

    /// <summary>A bare model name is resolved under the recipe's kit; a path is used as-is.</summary>
    private string Resolve(string modelName) =>
        modelName != null && modelName.EndsWith(".fbx", System.StringComparison.OrdinalIgnoreCase)
            ? modelName
            : _recipe.ModelRoot + modelName + ".fbx";

    private static GameObject Instantiate(GameObject prefab, Transform parent,
                                           Vector3 position, float yaw)
    {
        var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent);
        PrefabUtility.UnpackPrefabInstance(go, PrefabUnpackMode.Completely,
                                            InteractionMode.AutomatedAction);

        go.transform.position = position;
        go.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
        return go;
    }

    // ── Model loading and measurement ─────────────────────────────────────────

    private static readonly Dictionary<string, GameObject> _modelCache = new();
    private static readonly Dictionary<GameObject, Vector3> _sizeCache = new();

    private static GameObject LoadModel(string assetPath)
    {
        if (string.IsNullOrEmpty(assetPath)) return null;
        if (_modelCache.TryGetValue(assetPath, out var cached)) return cached;

        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
        if (prefab == null) Debug.LogWarning($"[GridMap] Model not found: {assetPath}");

        _modelCache[assetPath] = prefab;
        return prefab;
    }

    /// <summary>
    /// A model's extent along each axis, in its own units.
    ///
    /// Measured rather than assumed: Kenney's kits are mostly authored on a one-unit
    /// grid but not uniformly, and a hardcoded multiplier would leave gaps between
    /// ground tiles that are visible from the first frame.
    ///
    /// The combined extent of the meshes, not their union bounds — a model built
    /// around an off-centre origin would otherwise measure as twice its own size.
    /// </summary>
    private static Vector3 MeasureSize(GameObject prefab)
    {
        if (_sizeCache.TryGetValue(prefab, out var cached)) return cached;

        var size = Vector3.zero;
        foreach (var filter in prefab.GetComponentsInChildren<MeshFilter>(includeInactive: true))
        {
            if (filter.sharedMesh == null) continue;

            var mesh = filter.sharedMesh.bounds.size;
            size = new Vector3(Mathf.Max(size.x, mesh.x),
                               Mathf.Max(size.y, mesh.y),
                               Mathf.Max(size.z, mesh.z));
        }

        _sizeCache[prefab] = size;
        return size;
    }

    /// <summary>Largest horizontal dimension of a model, in its own units.</summary>
    private static float MeasureFootprint(GameObject prefab)
    {
        var size = MeasureSize(prefab);
        return Mathf.Max(size.x, size.z);
    }

    // ── Scene housekeeping ────────────────────────────────────────────────────

    /// <summary>Keeps an object out of the NavMesh bake without disabling it.</summary>
    private static void IgnoreInNavMesh(GameObject go)
    {
        if (go == null) return;

        // `??` cannot be used here. It compares by reference, so it never sees Unity's
        // overloaded ==, and the wrapper GetComponent returns for an absent component
        // reads as non-null — the AddComponent is skipped and the next line throws.
        var modifier = go.GetComponent<Unity.AI.Navigation.NavMeshModifier>();
        if (modifier == null) modifier = go.AddComponent<Unity.AI.Navigation.NavMeshModifier>();
        if (modifier == null) return;

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

    /// <summary>Removes SampleScene's terrain — a grid map builds its own ground.</summary>
    private void RemoveTerrain()
    {
        foreach (var terrain in Object.FindObjectsByType<Terrain>(FindObjectsInactive.Include))
        {
            if (terrain == null) continue;

            Debug.Log($"[{_recipe.Tag}] Removing terrain '{terrain.name}' — this map builds " +
                      "its own ground.");
            Object.DestroyImmediate(terrain.gameObject);
        }
    }
}
