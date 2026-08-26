using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>
/// Checks on the hand-authored maps, read straight out of the recipe files.
///
/// A map is an ASCII picture and a table of node ids, and both are typed by hand. The
/// picture can go ragged, the spawn can end up in the water, and a node id can be a
/// typo away from a node that resolves to nothing at runtime and does nothing when it
/// is clicked — with no error anywhere, because a SkillNodeController with an unknown
/// id is a warning in a console nobody is reading during a playtest.
///
/// All of it is text, so none of it needs Unity.
/// </summary>
internal static class MapChecks
{
    /// <summary>Recipe file, the map id in zone_data.json, and the human name.</summary>
    private static readonly (string File, string MapId, string Name)[] Maps =
    {
        ("GoblinCampSetup.cs", "goblin_camp",   "Goblin Camp"),
        ("HollowMapSetup.cs",  "fading_hollow", "Hollow of the Fading Light"),
    };

    private const float TileSize = 4f;

    public static void Run(Action<bool, string> check, string repoRoot)
    {
        foreach (var map in Maps)
        {
            string path = Path.Combine(repoRoot, "Assets", "Editor", map.File);
            if (!File.Exists(path))
            {
                check(false, $"{map.Name}: cannot find {path}");
                continue;
            }

            string source = File.ReadAllText(path);

            var rows = ReadLayout(source);
            Layout(check, map.Name, rows);
            NodeIds(check, repoRoot, map.MapId, map.Name, source, rows);
        }
    }

    // ── The picture ───────────────────────────────────────────────────────────

    private static List<string> ReadLayout(string source)
    {
        var rows = new List<string>();

        var match = Regex.Match(source,
            @"private static readonly string\[\] Layout\s*=\s*\{(.*?)\};",
            RegexOptions.Singleline);
        if (!match.Success) return rows;

        foreach (Match row in Regex.Matches(match.Groups[1].Value, "\"([^\"]*)\""))
            rows.Add(row.Groups[1].Value);

        return rows;
    }

    private static void Layout(Action<bool, string> check, string name, List<string> rows)
    {
        check(rows.Count > 0, $"{name}: the Layout array is where the check expects it");
        if (rows.Count == 0) return;

        int width = rows[0].Length;

        bool ragged = false;
        foreach (var row in rows) if (row.Length != width) ragged = true;
        check(!ragged, $"{name}: every row is the same width");

        // Stop rather than index into short rows. A ragged grid is already a failure,
        // and a check that crashes reports it worse than one that returns.
        if (ragged) return;

        // Exactly one spawn, and it must be walkable ground: a spawn on a cliff or in
        // the water is a spawn with no NavMesh under it.
        int spawns = 0, spawnRow = -1, spawnCol = -1;
        for (int r = 0; r < rows.Count; r++)
            for (int c = 0; c < width; c++)
                if (rows[r][c] == '@') { spawns++; spawnRow = r; spawnCol = c; }

        check(spawns == 1, $"{name}: exactly one spawn marker (found {spawns})");
        if (spawns != 1) return;

        // The world position the builder derives, reproduced from the same formula.
        float spawnX = (spawnCol - (width - 1) / 2f) * TileSize;
        float spawnZ = ((rows.Count - 1) / 2f - spawnRow) * TileSize;

        float halfWidth = width * TileSize / 2f;
        float halfDepth = rows.Count * TileSize / 2f;

        check(Math.Abs(spawnX) < halfWidth && Math.Abs(spawnZ) < halfDepth,
              $"{name}: the spawn ({spawnX}, {spawnZ}) is inside the map " +
              $"(±{halfWidth}, ±{halfDepth})");

        // The bug this exercise started from: the player was at x = 337.5 on a map 128
        // units wide. Anything of that order is not a rounding error.
        check(Math.Abs(spawnX) < halfWidth - TileSize && Math.Abs(spawnZ) < halfDepth - TileSize,
              $"{name}: the spawn is well inside the walls, not against the edge");

        // Camps must be on ground too, and there must be some.
        int camps = 0, campsBoxedIn = 0;
        for (int r = 0; r < rows.Count; r++)
            for (int c = 0; c < width; c++)
            {
                if (rows[r][c] != 'g') continue;
                camps++;

                // A camp's own cell is walkable by construction, but its neighbours
                // decide whether monsters have anywhere to appear.
                int walkable = 0;
                for (int dr = -1; dr <= 1; dr++)
                    for (int dc = -1; dc <= 1; dc++)
                    {
                        int nr = r + dr, nc = c + dc;
                        if (nr < 0 || nc < 0 || nr >= rows.Count || nc >= width) continue;
                        char cell = rows[nr][nc];
                        if (cell != 'C' && cell != '~') walkable++;
                    }

                if (walkable < 5) campsBoxedIn++;
            }

        check(camps >= 3, $"{name}: at least three monster camps (found {camps})");
        check(campsBoxedIn == 0,
              $"{name}: every camp has room around it ({campsBoxedIn} boxed in)");

        // The whole edge must be wall, or the NavMesh runs off the end of the ground
        // and the player can walk into nothing.
        bool sealedEdge = true;
        for (int c = 0; c < width; c++)
            if (rows[0][c] != 'C' || rows[rows.Count - 1][c] != 'C') sealedEdge = false;
        for (int r = 0; r < rows.Count; r++)
            if (rows[r][0] != 'C' || rows[r][width - 1] != 'C') sealedEdge = false;

        check(sealedEdge, $"{name}: the map is walled all the way round");
    }

    // ── The node ids ──────────────────────────────────────────────────────────

    /// <summary>
    /// Every node the layout places must be declared for this map in zone_data.json,
    /// and every node declared there must be placed.
    ///
    /// Both halves matter and they fail differently. A placed id that zone_data does
    /// not know is a rock the player clicks and nothing happens. A declared id that
    /// the layout never places is a skill the map claims to offer and does not — and
    /// AFK accrual will happily go on crediting it, because the activity snapshot
    /// outlives the node.
    /// </summary>
    private static void NodeIds(Action<bool, string> check, string repoRoot, string mapId,
                                string name, string source, List<string> rows)
    {
        // ['1'] = new("tin_rock_1", "Tin Rock", Nature + "rock_largeC.fbx", 0.85f),
        var placed = new Dictionary<char, string>();
        foreach (Match m in Regex.Matches(source,
                     @"\['(.)'\]\s*=\s*new\(\s*""([^""]+)""\s*,"))
            placed[m.Groups[1].Value[0]] = m.Groups[2].Value;

        check(placed.Count > 0, $"{name}: the Nodes table is where the check expects it");
        if (placed.Count == 0) return;

        var declared = DeclaredNodeIds(repoRoot, mapId);
        check(declared.Count > 0, $"{name}: zone_data.json declares nodes for '{mapId}'");
        if (declared.Count == 0) return;

        // Only the characters that actually appear in the picture count as placed. A
        // table entry for a digit nobody drew is a node that does not exist.
        var used = new HashSet<string>();
        foreach (var row in rows)
            foreach (char c in row)
                if (placed.TryGetValue(c, out string nodeId)) used.Add(nodeId);

        foreach (string nodeId in used)
            check(declared.Contains(nodeId),
                  $"{name}: the layout places '{nodeId}', which zone_data.json declares");

        Reachability(check, name, rows, placed);

        foreach (string nodeId in declared)
            check(used.Contains(nodeId),
                  $"{name}: zone_data.json declares '{nodeId}', which the layout places");
    }

    /// <summary>
    /// Every node and every camp must be somewhere the player can actually walk to.
    ///
    /// The fishing spot used to be drawn in the middle of the water. Water cells get no
    /// ground tile — that hole is what makes water unwalkable — so the node's own cell
    /// was one square of land with a moat around it. It rendered, it was clickable, and
    /// the character walked to the shore and stopped.
    ///
    /// A flood fill from the spawn answers the question the picture cannot: not "is
    /// this cell land" but "can you get there". Four-way rather than eight, because an
    /// agent cannot squeeze through the corner where two diagonal water cells touch.
    /// </summary>
    private static void Reachability(Action<bool, string> check, string name,
                                     List<string> rows, Dictionary<char, string> nodes)
    {
        if (rows.Count == 0) return;

        int width = rows[0].Length, height = rows.Count;

        int spawnRow = -1, spawnCol = -1;
        for (int r = 0; r < height; r++)
            for (int c = 0; c < width; c++)
                if (rows[r][c] == '@') { spawnRow = r; spawnCol = c; }

        if (spawnRow < 0) return;

        var reached = new bool[height, width];
        var queue   = new Queue<(int Col, int Row)>();

        reached[spawnRow, spawnCol] = true;
        queue.Enqueue((spawnCol, spawnRow));

        var steps = new (int Col, int Row)[] { (1, 0), (-1, 0), (0, 1), (0, -1) };

        while (queue.Count > 0)
        {
            var (col, row) = queue.Dequeue();

            foreach (var step in steps)
            {
                int nc = col + step.Col, nr = row + step.Row;
                if (nc < 0 || nr < 0 || nc >= width || nr >= height) continue;
                if (reached[nr, nc]) continue;

                char cell = rows[nr][nc];
                if (cell == 'C' || cell == '~') continue;

                reached[nr, nc] = true;
                queue.Enqueue((nc, nr));
            }
        }

        for (int r = 0; r < height; r++)
            for (int c = 0; c < width; c++)
            {
                char cell = rows[r][c];

                if (nodes.TryGetValue(cell, out string nodeId))
                    check(reached[r, c], $"{name}: node '{nodeId}' can be walked to from the spawn");

                if (cell == 'g')
                    check(reached[r, c], $"{name}: the camp at column {c}, row {r} is connected " +
                                         "to the spawn");
            }
    }

    private static HashSet<string> DeclaredNodeIds(string repoRoot, string mapId)
    {
        var ids = new HashSet<string>();

        string path = Path.Combine(repoRoot, "Assets", "StreamingAssets", "zone_data.json");
        if (!File.Exists(path)) return ids;

        using var document = JsonDocument.Parse(File.ReadAllText(path));

        foreach (var zone in document.RootElement.EnumerateArray())
        {
            if (!zone.TryGetProperty("maps", out var maps)) continue;

            foreach (var map in maps.EnumerateArray())
            {
                if (!map.TryGetProperty("id", out var id) || id.GetString() != mapId) continue;
                if (!map.TryGetProperty("skillNodes", out var nodes)) continue;

                foreach (var node in nodes.EnumerateArray())
                    if (node.TryGetProperty("nodeId", out var nodeId))
                        ids.Add(nodeId.GetString());
            }
        }

        return ids;
    }
}
