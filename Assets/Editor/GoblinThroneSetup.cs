using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// The Goblin King's arena, as a recipe.
///
/// ══ WHY IT IS SHAPED LIKE THIS ════════════════════════════════════════════════
///
/// Small, round and empty. Every one of those is a consequence of what the fight
/// does rather than a style choice:
///
///   · SMALL, because the charge reaches twenty units and a boss that can be kited
///     forever is a boss with no charge.
///   · ROUND, because the quake is a ring centred on the King. Corners would mean
///     one safe spot, learnable once, and then the mechanic is furniture.
///   · EMPTY, because a telegraph the player cannot see behind a rock is an ambush
///     wearing a telegraph's clothes. Scatter is confined to the perimeter.
///
/// It is also the first map with nothing to gather. A fishing spot in a boss arena
/// is a joke that stops being funny the second time somebody walks in.
/// </summary>
public static class GoblinThroneSetup
{
    private const string Nature   = "Assets/Kenney Game Assets All-in-1 3.7.0/3D assets/Nature Kit/Models/FBX format/";
    private const string Survival = "Assets/Kenney Game Assets All-in-1 3.7.0/3D assets/Survival Kit/Models/FBX format/";

    /// <summary>
    /// Twenty-one by twenty-one, which at four units a cell is eighty across.
    ///
    /// Four times the charge's reach, so crossing it is a decision rather than a
    /// formality, and the ring never covers the whole floor.
    ///
    /// 'K' is the King. '@' is where the player arrives — deliberately at the south
    /// edge rather than in the middle, so the fight begins with a walk toward
    /// something and the player has seen the arena before anything hits them.
    /// </summary>
    private static readonly string[] Layout =
    {
        "CCCCCCCCCCCCCCCCCCCCC",
        "CCCCCCC.......CCCCCCC",
        "CCCCC...........CCCCC",
        "CCCC.............CCCC",
        "CCC...............CCC",
        "CC.................CC",
        "CC.................CC",
        "C...................C",
        "C.........K.........C",
        "C...................C",
        "C...................C",
        "C...................C",
        "C...................C",
        "CC.................CC",
        "CC.................CC",
        "CCC...............CCC",
        "CCCC.............CCCC",
        "CCCCC...........CCCCC",
        "CCCCCC....@....CCCCCC",
        "CCCCCCC.......CCCCCCC",
        "CCCCCCCCCCCCCCCCCCCCC",
    };

    /// <summary>
    /// Nothing. An arena with a mining rock in it invites a player to stand still
    /// during a boss fight, and then to be annoyed that they died.
    /// </summary>
    private static readonly Dictionary<char, GridMapSetup.NodeSpec> Nodes = new();

    /// <summary>
    /// Sparse, and only what the perimeter can hide behind without hiding a telegraph.
    /// The floor stays clear on purpose — see the class comment.
    /// </summary>
    private static readonly Dictionary<char, GridMapSetup.ScatterSpec> Scatter = new()
    {
        ['C'] = new(new[] { "stone_smallA", "stone_smallB", "stone_tallC" },
                    heightVsPlayer: 0.55f, blocks: false),
    };

    /// <summary>
    /// Overcast and close. The camp is daylight; this should not be, or arriving
    /// feels like walking into the next field rather than into a boss room.
    /// </summary>
    private static readonly MapSceneSetup.MapMood Mood = new()
    {
        Name           = "overcast dusk",
        SunColor       = new Color(0.72f, 0.66f, 0.62f),
        SunIntensity   = 0.75f,

        // Low and behind, so the King is lit from the far side of the arena and the
        // player walks toward a silhouette.
        SunAngles      = new Vector3(32f, 200f, 0f),

        AmbientSky     = new Color(0.16f, 0.15f, 0.18f),
        AmbientEquator = new Color(0.14f, 0.13f, 0.14f),
        AmbientGround  = new Color(0.08f, 0.07f, 0.08f),

        FogColor = new Color(0.19f, 0.18f, 0.20f),

        // Tight, so the walls fade out and the arena reads as a room rather than as
        // a small piece of a big map.
        FogStart = 25f,
        FogEnd   = 95f,
    };

    private static GridMapSetup.Recipe Recipe() => new()
    {
        Tag         = "GoblinThrone",
        DisplayName = "The Goblin Throne",
        ScenePath   = "Assets/Scenes/Map_GoblinThrone.unity",
        Layout      = Layout,
        TileSize    = 4f,
        Seed        = 20260827,
        CampRadius  = 6f,
        ModelRoot   = Nature,

        // Dirt rather than grass. Nothing has grown here in a while, which is the
        // one piece of story the floor can tell on its own.
        GroundModel = "ground_pathRocks",
        PathModel   = "ground_pathRocks",
        CliffModel  = "cliff_blockCave_stone",

        Nodes   = Nodes,
        Scatter = Scatter,
        Mood    = Mood,
    };

    [MenuItem("Idle Explorers/Build Goblin Throne")]
    public static void Build()
    {
        GridMapSetup.Build(Recipe(), showDialog: false);
        PlaceTheKing();
    }

    /// <summary>
    /// Puts the King on his stump.
    ///
    /// Placed here rather than through MonsterSpawner, deliberately. The spawner
    /// pools, respawns and caps its monsters, and every one of those is wrong for a
    /// boss: it must be exactly one, it must not come back while the player is still
    /// in the room, and it must not count toward a population budget shared with
    /// goblins.
    /// </summary>
    private static void PlaceTheKing()
    {
        var scene = UnityEditor.SceneManagement.EditorSceneManager.OpenScene(
            "Assets/Scenes/Map_GoblinThrone.unity");

        (int col, int row) = FindMarker('K');
        if (col < 0)
        {
            Debug.LogError("[GoblinThrone] The layout has no 'K' — the King has nowhere to sit.");
            return;
        }

        Vector3 where = CellToWorld(col, row);

        // The stump he is so convinced of.
        var stump = LoadModel(Nature + "tree_thin_dark.fbx");
        if (stump != null)
        {
            var throne = Object.Instantiate(stump, where, Quaternion.identity);
            throne.name = "Throne";
            throne.transform.localScale = Vector3.one * 1.6f;
        }

        var king = new GameObject("GoblinKing");
        king.transform.position = where + Vector3.up * 0.5f;
        king.AddComponent<BossController>();

        Debug.Log($"[GoblinThrone] The King is placed at {where}. " +
                  "He is initialised from the map's defaultMonsterId at runtime.");

        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);
        UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene);
    }

    private static (int Col, int Row) FindMarker(char marker)
    {
        for (int row = 0; row < Layout.Length; row++)
            for (int col = 0; col < Layout[row].Length; col++)
                if (Layout[row][col] == marker) return (col, row);

        return (-1, -1);
    }

    /// <summary>
    /// Grid to world, matching GridMapSetup's own centring.
    ///
    /// Duplicated rather than exposed, because the alternative is making the map
    /// builder's private geometry public for one caller -- and this is four lines
    /// that a check would catch if they ever disagreed.
    /// </summary>
    private static Vector3 CellToWorld(int col, int row)
    {
        const float tile = 4f;

        float width  = Layout[0].Length * tile;
        float height = Layout.Length * tile;

        return new Vector3(col * tile - width * 0.5f + tile * 0.5f,
                           0f,
                           height * 0.5f - row * tile - tile * 0.5f);
    }

    private static GameObject LoadModel(string path) =>
        AssetDatabase.LoadAssetAtPath<GameObject>(path);
}
