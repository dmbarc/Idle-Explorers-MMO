using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// "Goblin Camp" — a palisade in a clearing, and the goblins who built it.
///
/// ══ WHAT THIS REPLACES ════════════════════════════════════════════════════════
///
/// The Goblin Camp used to be seven skill nodes arranged on a 7.5-unit circle around
/// a point on SampleScene's terrain. SampleScene is a debug scene: a 2000-unit
/// heightmap with one baked patch you could stand on. It was never a place, and the
/// zone description — "the goblins have been here for centuries, nobody dared ask
/// them to leave" — described somewhere that did not exist.
///
/// This is that somewhere: a fenced camp with tents and cook fires, stolen crop rows
/// to the south, a rock face to the north-east that the mining nodes are cut into, and
/// a river along the east edge to fish from. The node IDS are unchanged, so
/// zone_data.json, every skill rate and every saved activity carry straight over.
///
/// All of the machinery lives in GridMapSetup. This file is only the map.
///
/// Menu: Idle Explorers → Build Goblin Camp
/// </summary>
public static class GoblinCampSetup
{
    private const string Nature =
        "Assets/Kenney Game Assets All-in-1 3.7.0/3D assets/Nature Kit/Models/FBX format/";

    private const string Survival =
        "Assets/Kenney Game Assets All-in-1 3.7.0/3D assets/Survival Kit/Models/FBX format/";

    // ── The map ───────────────────────────────────────────────────────────────
    //
    //   C cliff (wall)     . grass          ~ water (no ground: unwalkable)
    //   T tree             R rock           B bush        F flowers    M mushrooms
    //   f palisade         t tent           s stump       c crop row   S idol
    //   @ player spawn     1-7 skill nodes  g monster camp
    //
    // The block of 'f' from row 6 to row 15 is the palisade, with its gate on the
    // north wall. The player spawns inside it, within a few paces of the campfire,
    // the anvil and the chest — the three things a returning player wants first.
    //
    // North is row 0. Every row must be the same length.
    private static readonly string[] Layout =
    {
        "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC",
        "C....TTTTT...............RRRR..C",
        "C...TTTTTTT..............RR1RR.C",
        "C..TTT3TTTT..............RRRRR.C",
        "C...TTTTTT....g.........RR2RR..C",
        "C....TTT.................RRR..XC",
        "C.........ffff..ffff...........C",
        "C.........f........f....~~~~...C",
        "C..g......f...t....f...~~~~~~..C",
        "C.........f.5......f..~~~~~~~..C",
        "C.........f........f.4~~~~~~~..C",
        "C.........f..@..t..f..~~~~~~~..C",
        "C....s....f.7...6..f...~~~~~~..C",
        "C.........f..t.....f....~~~~...C",
        "C..B......f........f...........C",
        "C.........ffffffffff...........C",
        "C....ccc........ccc......g.....C",
        "C....ccc........ccc............C",
        "C....ccc........ccc.......B....C",
        "C......s.........s.............C",
        "C..FFF...............MM........C",
        "C.FFFFF..............MMM.......C",
        "C..FFF......S.........M........C",
        "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC",
    };

    /// <summary>
    /// Which skill node each digit places. These ids come from zone_data.json and are
    /// the same seven the terrain version used — the map is new, the content is not.
    ///
    /// The last number on each row is a HEIGHT, as a multiple of the height of a
    /// person, so an anvil is knee-high on a blacksmith rather than looming over one.
    /// </summary>
    private static readonly Dictionary<char, GridMapSetup.NodeSpec> Nodes = new()
    {
        ['1'] = new("tin_rock_1",    "Tin Rock",    Nature   + "rock_largeC.fbx",     0.85f),
        ['2'] = new("copper_rock_1", "Copper Rock", Nature   + "rock_largeA.fbx",     0.85f),
        ['3'] = new("normal_tree_1", "Tree",        Nature   + "tree_oak.fbx",        3.00f),
        ['4'] = new("shrimp_pool_1", "Shrimp Pool", Nature   + "log.fbx",             0.35f),
        ['5'] = new("campfire_1",    "Campfire",    Nature   + "campfire_logs.fbx",   0.40f),
        ['6'] = new("anvil_1",       "Anvil",       Survival + "workbench-anvil.fbx", 0.50f),
        ['7'] = new("bank_chest_1",  "Bank Chest",  Survival + "chest.fbx",           0.55f),
    };

    /// <summary>
    /// The way to the Goblin King, marked 'X'.
    ///
    /// Placed in the north-east past the rocks rather than beside the spawn, so that
    /// finding it is a walk. A boss door in the first three seconds of a new game
    /// reads as content the player has already missed.
    /// </summary>
    private static readonly Dictionary<char, GridMapSetup.PortalSpec> Portals = new()
    {
        ['X'] = new(IdleExplorers.Backend.BossGate.Monster,
                    IdleExplorers.Backend.BossGate.RequiredActiveKills,
                    "goblin_throne",
                    Nature + "stone_tallG.fbx",
                    2.20f),
    };

    private static readonly Dictionary<char, GridMapSetup.ScatterSpec> Scatter = new()
    {
        ['T'] = new(new[]
        {
            "tree_oak", "tree_default", "tree_tall", "tree_detailed", "tree_fat",
            "tree_pineRoundA", "tree_pineTallB", "tree_simple", "tree_thin", "tree_blocks",
        }, 3.0f, blocks: true),

        ['R'] = new(new[]
        {
            "rock_largeA", "rock_largeB", "rock_largeC", "rock_largeD",
            "rock_tallA", "rock_tallC", "rock_tallF", "stone_largeA", "stone_largeD",
        }, 0.9f, blocks: true),

        ['B'] = new(new[]
        {
            "plant_bush", "plant_bushDetailed", "plant_bushLarge",
            "plant_bushSmall", "plant_bushTriangle",
        }, 0.5f, blocks: false),

        ['F'] = new(new[]
        {
            "flower_redA", "flower_redB", "flower_purpleA", "flower_purpleC",
            "flower_yellowA", "flower_yellowB", "grass", "grass_large",
        }, 0.22f, blocks: false),

        ['M'] = new(new[]
        {
            "mushroom_red", "mushroom_redGroup", "mushroom_tan", "mushroom_tanTall",
        }, 0.18f, blocks: false),

        // The palisade. AlignToRun is what makes it a wall: with a random yaw per cell
        // it is a heap of planks lying at every angle, which is what the same fence
        // models look like anywhere else in this file.
        ['f'] = new(new[]
        {
            "fence_simpleHigh", "fence_planks", "fence_simple",
        }, 1.15f, blocks: true, alignToRun: true),

        // Head height and a bit. A tent you cannot see over is a tent that hides the
        // fight happening behind it.
        ['t'] = new(new[]
        {
            "tent_detailedOpen", "tent_detailedClosed", "tent_smallOpen", "tent_smallClosed",
        }, 1.10f, blocks: true),

        ['s'] = new(new[]
        {
            "stump_old", "stump_oldTall", "stump_round", "stump_square", "stump_squareDetailed",
        }, 0.35f, blocks: false),

        // Waist-high and walkable. Goblins grow their own; the rows are the reason
        // the camp is here rather than somewhere else.
        ['c'] = new(new[]
        {
            "crops_wheatStageB", "crops_cornStageC", "crops_cornStageD",
            "crops_leafsStageB", "crops_dirtRow",
        }, 0.55f, blocks: false),

        ['S'] = new(new[]
        {
            "statue_head", "statue_block",
        }, 1.80f, blocks: true),
    };

    /// <summary>
    /// Overcast noon, not the blazing white the terrain version inherited. Enough sun
    /// to cast a direction and read a shape, a cool ambient so the greens stay green
    /// instead of washing to cyan, and fog far enough out that the camp is clear and
    /// the treeline is not.
    /// </summary>
    private static readonly MapSceneSetup.MapMood Mood = new()
    {
        Name         = "overcast",
        SunColor     = new Color(1.00f, 0.96f, 0.86f),
        SunIntensity = 1.05f,
        SunAngles    = new Vector3(48f, 145f, 0f),

        AmbientSky     = new Color(0.30f, 0.33f, 0.36f),
        AmbientEquator = new Color(0.20f, 0.21f, 0.21f),
        AmbientGround  = new Color(0.10f, 0.10f, 0.09f),

        FogColor = new Color(0.28f, 0.31f, 0.32f),
        FogStart = 55f,
        FogEnd   = 210f,
    };

    private static GridMapSetup.Recipe Recipe() => new()
    {
        Tag         = "GoblinCamp",
        DisplayName = "Goblin Camp",
        ScenePath   = "Assets/Scenes/Map_GoblinCamp.unity",
        Layout      = Layout,
        TileSize    = 4f,
        Seed        = 20260824,
        CampRadius  = 9f,
        ModelRoot   = Nature,
        GroundModel = "ground_grass",
        PathModel   = "path_wood",

        // Stone rather than the Hollow's rock, so two maps ringed by the same kit do
        // not read as the same place with the middle swapped out.
        CliffModel  = "cliff_block_stone",

        Nodes   = Nodes,
        Portals = Portals,
        Scatter = Scatter,
        Mood    = Mood,
    };

    [MenuItem("Idle Explorers/Build Goblin Camp")]
    public static void BuildMenu()
    {
        if (!EditorUtility.DisplayDialog("Build Goblin Camp",
                $"This assembles Map_GoblinCamp.unity from Kenney Nature Kit tiles " +
                $"({Layout[0].Length}x{Layout.Length} cells at 4 units) and bakes its NavMesh.\n\n" +
                "SampleScene and the Hollow are left alone.",
                "Build it", "Cancel"))
            return;

        Execute(showDialog: true);
    }

    public static void Execute(bool showDialog) => GridMapSetup.Build(Recipe(), showDialog);
}
