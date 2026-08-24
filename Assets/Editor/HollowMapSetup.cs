using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// "Hollow of the Fading Light" — a bowl of green ringed by rock, going dark.
///
/// All of the machinery lives in GridMapSetup. This file is the map: a picture of the
/// layout, a palette of models, the seven skill nodes and the light that falls on it.
/// Nothing here does anything; it only describes somewhere.
///
/// Menu: Idle Explorers → Build Hollow Map
/// </summary>
public static class HollowMapSetup
{
    private const string Nature =
        "Assets/Kenney Game Assets All-in-1 3.7.0/3D assets/Nature Kit/Models/FBX format/";

    private const string Survival =
        "Assets/Kenney Game Assets All-in-1 3.7.0/3D assets/Survival Kit/Models/FBX format/";

    // ── The map ───────────────────────────────────────────────────────────────
    //
    //   C cliff (wall)        . grass            P stone path
    //   T tree                R rock             B bush
    //   F flowers             M mushrooms        ~ water (no ground: unwalkable)
    //   @ player spawn        1-7 skill nodes    S statue/landmark
    //   g monster camp
    //
    // North is row 0. Every row must be the same length.
    private static readonly string[] Layout =
    {
        "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC",
        "C.....TTT...........RRR........C",
        "C...TTTTTT.........RRRRRR......C",
        "C..TTTTTTTT........RR1RRR......C",
        "C...TTT3TT.........gRRRRR......C",
        "C....TTTT............RRR.......C",
        "C..g..TT......B.......R........C",
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
        "C......MM.......g....~~~~~~....C",
        "C.......M.............~~~~.....C",
        "C.........B....................C",
        "C..............................C",
        "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC",
    };

    /// <summary>
    /// Which skill node each digit places. The ids must match this map's skillNodes in
    /// zone_data.json, or the node resolves to nothing at runtime and logs a warning
    /// naming itself.
    ///
    /// The last number on each row is a HEIGHT, as a multiple of the height of a
    /// person. The anvil at 0.5 is the playtest note that started this: it used to
    /// stand at twice the player's height, because the number here meant "how much of
    /// a four-unit floor tile to fill" and nobody had told the artwork how big a
    /// person was.
    /// </summary>
    private static readonly Dictionary<char, GridMapSetup.NodeSpec> Nodes = new()
    {
        ['1'] = new("hollow_tin_rock",    "Tin Rock",    Nature   + "rock_largeC.fbx",      0.85f),
        ['2'] = new("hollow_copper_rock", "Copper Rock", Nature   + "rock_largeA.fbx",      0.85f),
        ['3'] = new("hollow_tree",        "Ancient Oak", Nature   + "tree_oak.fbx",         3.20f),
        ['4'] = new("hollow_fishing",     "Still Water", Nature   + "log.fbx",              0.35f),
        ['5'] = new("hollow_campfire",    "Campfire",    Nature   + "campfire_stones.fbx",  0.40f),
        ['6'] = new("hollow_anvil",       "Anvil",       Survival + "workbench-anvil.fbx",  0.50f),
        ['7'] = new("hollow_bank",        "Bank Chest",  Survival + "chest.fbx",            0.55f),
    };

    /// <summary>
    /// Scatter sets. One model is chosen per cell from the seeded random, so a forest
    /// is varied without any of it being typed out.
    /// </summary>
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

        ['S'] = new(new[]
        {
            "statue_obelisk", "statue_column", "statue_head", "statue_ring",
        }, 2.2f, blocks: true),
    };

    /// <summary>
    /// The light the map is named for. A low sun the colour of late afternoon, cold
    /// blue in the shadows, and fog close enough that the cliffs fade before they end
    /// — the hollow should feel like somewhere the day is leaving.
    /// </summary>
    private static readonly MapSceneSetup.MapMood Mood = new()
    {
        Name         = "fading light",
        SunColor     = new Color(1.00f, 0.80f, 0.60f),
        SunIntensity = 0.90f,
        SunAngles    = new Vector3(22f, 210f, 0f),

        AmbientSky     = new Color(0.20f, 0.24f, 0.34f),
        AmbientEquator = new Color(0.13f, 0.15f, 0.19f),
        AmbientGround  = new Color(0.06f, 0.07f, 0.07f),

        FogColor = new Color(0.13f, 0.15f, 0.21f),
        FogStart = 35f,
        FogEnd   = 165f,
    };

    private static GridMapSetup.Recipe Recipe() => new()
    {
        Tag         = "Hollow",
        DisplayName = "Hollow of the Fading Light",
        ScenePath   = "Assets/Scenes/Map_FadingHollow.unity",
        Layout      = Layout,
        TileSize    = 4f,
        Seed        = 20260823,
        CampRadius  = 9f,
        ModelRoot   = Nature,
        GroundModel = "ground_grass",
        PathModel   = "path_stone",
        WaterModel  = "ground_riverTile",
        CliffModel  = "cliff_block_rock",
        Nodes       = Nodes,
        Scatter     = Scatter,
        Mood        = Mood,
    };

    [MenuItem("Idle Explorers/Build Hollow Map")]
    public static void BuildMenu()
    {
        if (!EditorUtility.DisplayDialog("Build Hollow Map",
                $"This assembles Map_FadingHollow.unity from Kenney Nature Kit tiles " +
                $"({Layout[0].Length}x{Layout.Length} cells at 4 units) and bakes its NavMesh.\n\n" +
                "SampleScene and the Goblin Camp are left alone.",
                "Build it", "Cancel"))
            return;

        Execute(showDialog: true);
    }

    public static void Execute(bool showDialog) => GridMapSetup.Build(Recipe(), showDialog);
}
