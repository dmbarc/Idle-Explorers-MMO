using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace IdleExplorersTests;

/// <summary>
/// Cosmetics that draw nothing.
///
/// ══ WHAT THIS EXISTS TO CATCH ═════════════════════════════════════════════════
///
/// Eleven cosmetic items shipped with an equipSlot and an EMPTY equipSpriteAddress,
/// so equipping one saved correctly, showed correctly in the equipment panel, and
/// changed nothing at all on the character. Four of them cost relic coins.
///
/// Nothing in the project could see it. The item parsed, the slot existed, the code
/// that draws it ran and found nothing to draw. It is the same shape as every other
/// bug in this file's neighbourhood: STORING IS NOT SHOWING.
///
/// ══ WHY THE PATHS ARE CHECKED AGAINST DISK ════════════════════════════════════
///
/// Because an address is a string, and a wrong string fails exactly the way an empty
/// one does — Resources.Load returns null, silently, forever. A typo in
/// "Addons/Legacy/0_Unit/0_Sprite/2_Cloth/Cloth_6" is invisible until somebody
/// equips the thing and notices they look the same.
///
/// ══ WHY THE AURAS ARE CHECKED FOR LOOPING ═════════════════════════════════════
///
/// An aura is worn for hours. A one-shot particle effect plays for two seconds and
/// then the player has paid for nothing — and it LOOKS like it worked, once, which is
/// worse than never appearing. emberlight_aura shipped pointing at "VFX/aura_ember"
/// while the resolver prefixes "VFX/" itself, so the lookup was for
/// "VFX/VFX/aura_ember" and the only aura in the game had never once appeared.
/// </summary>
internal static class CosmeticChecks
{
    /// <summary>The slots whose whole purpose is to be looked at.</summary>
    private static readonly string[] CosmeticSlots = { "shirt", "cape", "tabard", "aura" };

    public static void Run(Action<bool, string> check, string repoRoot)
    {
        Console.WriteLine("Cosmetics");

        string itemJson = Path.Combine(repoRoot, "Assets", "StreamingAssets", "item_data.json");

        if (!File.Exists(itemJson))
        {
            check(false, $"item_data.json exists at {itemJson}");
            return;
        }

        List<Item> items = ReadItems(File.ReadAllText(itemJson));

        check(items.Count > 50, $"item_data.json parsed ({items.Count} items)");

        var cosmetics = items.Where(i => CosmeticSlots.Contains(i.Slot)).ToList();

        check(cosmetics.Count >= 10,
              $"the cosmetic slots hold something to check ({cosmetics.Count} items)");

        Dictionary<string, string> vfx = ReadVfxLibrary(repoRoot);

        check(vfx.Count >= 5, $"VFXLibrarySetup's effect table parsed ({vfx.Count} effects)");

        var tree  = new AssetIndex(repoRoot);
        var icons = ReadIconNames(repoRoot);

        check(tree.ResourcePaths.Count > 100, $"Resources folders indexed ({tree.ResourcePaths.Count} assets)");
        check(tree.Pngs.Count          > 100, $"png assets indexed ({tree.Pngs.Count} files)");
        check(icons.Count              > 20,  $"IconLibrarySetup's item table parsed ({icons.Count} rows)");

        var claimed = new Dictionary<string, string>();

        foreach (Item item in cosmetics)
        {
            // ── It must point at something ────────────────────────────────────
            if (string.IsNullOrWhiteSpace(item.SpriteAddress))
            {
                check(false, $"'{item.Id}' ({item.Slot}) has an equipSpriteAddress — " +
                             "without one it equips and draws nothing");
                continue;
            }

            // ── And nothing else may point at the same thing ──────────────────
            //
            // Two paid cosmetics that turn out to be the same art is the quiet way a
            // shop stops being worth buying from.
            if (claimed.TryGetValue(item.SpriteAddress, out string owner))
                check(false, $"'{item.Id}' draws the same art as '{owner}' " +
                             $"('{item.SpriteAddress}')");
            else
                claimed[item.SpriteAddress] = item.Id;

            if (item.Slot == "aura") CheckAura(check, item, vfx, tree);
            else                     CheckSprite(check, item, tree.ResourcePaths);

            // ── And it must be recognisable in the bag ────────────────────────
            //
            // A premium item drawing a generated grey square is what this half is
            // for. Either the item names its own icon or the library maps it.
            if (!string.IsNullOrWhiteSpace(item.IconAddress)) continue;

            if (!icons.TryGetValue(item.Id, out string iconName))
            {
                check(false, $"'{item.Id}' has an icon — no iconAddress and no row in " +
                             "IconLibrarySetup.ItemIconNames, so it draws a placeholder");
                continue;
            }

            check(tree.IsSingleSprite(iconName),
                  $"'{item.Id}' icon '{iconName}' is a single-sprite asset that exists " +
                  "(multi-sprite atlases and particle textures do not bind)");
        }
    }

    /// <summary>An aura is a VFX id, not a path. It resolves through VFXLibrary.</summary>
    private static void CheckAura(Action<bool, string> check, Item item,
                                  Dictionary<string, string> vfx, AssetIndex tree)
    {
        // The mistake this catches by name: a path-shaped address on a slot that takes
        // an id. AbilityVFX prefixes "VFX/" itself, so "VFX/aura_ember" is a lookup for
        // "VFX/VFX/aura_ember" and resolves to nothing.
        check(!item.SpriteAddress.Contains('/'),
              $"'{item.Id}' aura address '{item.SpriteAddress}' is a bare VFX id, not a path " +
              "— AbilityVFX prefixes 'VFX/' itself");

        if (!vfx.TryGetValue(item.SpriteAddress, out string prefabName))
        {
            check(false, $"'{item.Id}' aura '{item.SpriteAddress}' is in " +
                         "VFXLibrarySetup.EffectPrefabs");
            return;
        }

        string prefab = tree.Prefab(prefabName);

        if (prefab == null)
        {
            check(false, $"'{item.Id}' aura prefab '{prefabName}' exists on disk");
            return;
        }

        string yaml = File.ReadAllText(prefab);

        int loops = Regex.Matches(yaml, @"^\s*looping:\s*1\s*$", RegexOptions.Multiline).Count;
        int once  = Regex.Matches(yaml, @"^\s*looping:\s*0\s*$", RegexOptions.Multiline).Count;

        check(loops > 0 && once == 0,
              $"'{item.Id}' aura prefab '{prefabName}' loops ({loops} looping, {once} one-shot) " +
              "— an aura is worn for hours and a one-shot plays once and stops");
    }

    /// <summary>Everything else is a Resources-relative path to a sprite sheet.</summary>
    private static void CheckSprite(Action<bool, string> check, Item item,
                                    HashSet<string> resources)
    {
        // SpriteLoader accepts "path#SubSprite"; only the path half is on disk.
        string path = item.SpriteAddress.Split('#')[0];

        check(resources.Contains(path.Replace('\\', '/')),
              $"'{item.Id}' art '{path}' exists under a Resources folder — " +
              "Resources.Load returns null for a wrong path exactly as it does for none");
    }

    // ── Reading the sources ───────────────────────────────────────────────────

    private sealed class Item
    {
        public string Id = "";
        public string Slot = "";
        public string SpriteAddress = "";
        public string IconAddress = "";
    }

    /// <summary>
    /// Pulls the four fields out of item_data.json without a JSON library.
    ///
    /// The file is one object per item at a fixed indent, which is what makes this
    /// safe. It stays deliberately dumb: a parser here that disagreed with Unity's
    /// would be a check testing itself.
    /// </summary>
    private static List<Item> ReadItems(string json)
    {
        var items = new List<Item>();
        Item current = null;

        foreach (string raw in json.Split('\n'))
        {
            string line = raw.Trim();

            Match id = Regex.Match(line, "^\"id\":\\s*\"([^\"]*)\"");
            if (id.Success)
            {
                current = new Item { Id = id.Groups[1].Value };
                items.Add(current);
                continue;
            }

            if (current == null) continue;

            Match slot = Regex.Match(line, "^\"equipSlot\":\\s*\"([^\"]*)\"");
            if (slot.Success) { current.Slot = slot.Groups[1].Value; continue; }

            Match sprite = Regex.Match(line, "^\"equipSpriteAddress\":\\s*\"([^\"]*)\"");
            if (sprite.Success) { current.SpriteAddress = sprite.Groups[1].Value; continue; }

            Match icon = Regex.Match(line, "^\"iconAddress\":\\s*\"([^\"]*)\"");
            if (icon.Success) current.IconAddress = icon.Groups[1].Value;
        }

        return items;
    }

    /// <summary>effect id → prefab name, from the editor table that builds the library.</summary>
    private static Dictionary<string, string> ReadVfxLibrary(string repoRoot)
    {
        string path = Path.Combine(repoRoot, "Assets", "Editor", "VFXLibrarySetup.cs");
        var map = new Dictionary<string, string>();

        if (!File.Exists(path)) return map;

        foreach (Match m in Regex.Matches(File.ReadAllText(path),
                                          "\\{\\s*\"([A-Za-z0-9_]+)\"\\s*,\\s*\"([^\"]+)\"\\s*\\}"))
            map[m.Groups[1].Value] = m.Groups[2].Value;

        return map;
    }

    /// <summary>item id → icon file name, from IconLibrarySetup's ItemIconNames only.</summary>
    private static Dictionary<string, string> ReadIconNames(string repoRoot)
    {
        string path = Path.Combine(repoRoot, "Assets", "Editor", "IconLibrarySetup.cs");
        var map = new Dictionary<string, string>();

        if (!File.Exists(path)) return map;

        string source = File.ReadAllText(path);

        // Only the ITEM table. Skills, abilities and classes use the same row shape,
        // and a skill id that happened to match an item id would otherwise satisfy a
        // check about items — the proximity mistake this project has made four times.
        int start = source.IndexOf("ItemIconNames", StringComparison.Ordinal);
        if (start < 0) return map;

        int open = source.IndexOf('{', start);
        if (open < 0) return map;

        int close = MatchingBrace(source, open);
        if (close < 0) return map;

        foreach (Match m in Regex.Matches(source.Substring(open, close - open),
                                          "\\{\\s*\"([A-Za-z0-9_]+)\"\\s*,\\s*\"([^\"]+)\"\\s*\\}"))
            map[m.Groups[1].Value] = m.Groups[2].Value;

        return map;
    }

    private static int MatchingBrace(string text, int open)
    {
        int depth = 0;

        for (int i = open; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}' && --depth == 0) return i;
        }

        return -1;
    }

    /// <summary>
    /// One walk of Assets, and every lookup this file needs answered from memory.
    ///
    /// ══ WHY IT IS ONE WALK ════════════════════════════════════════════════════
    ///
    /// The first version made three separate recursive enumerations and read every
    /// *.png.meta in the project in full — over a tree that includes a 1.2 GB art
    /// pack. It ran for five minutes and was killed. A check nobody will wait for is
    /// a check that gets commented out.
    ///
    /// So: one enumeration that records paths only, and file CONTENTS read for the
    /// dozen assets actually asked about.
    /// </summary>
    private sealed class AssetIndex
    {
        /// <summary>Load paths for everything under a folder named Resources.</summary>
        public readonly HashSet<string> ResourcePaths = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Base name → EVERY .png with that name.
        ///
        /// A list rather than one path, and the distinction is not academic: Kenney
        /// ships six files called campfire.png and exactly one of them is imported as
        /// a sprite. Keeping the first found reported a perfectly good icon as
        /// missing, which is the check being wrong rather than the data.
        ///
        /// IconLibrarySetup searches `"name" t:Sprite`, so a single sprite-imported
        /// file among any number of plain textures is a match.
        /// </summary>
        public readonly Dictionary<string, List<string>> Pngs = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Base name → path, for every .prefab in the project.</summary>
        private readonly Dictionary<string, string> _prefabs = new(StringComparer.OrdinalIgnoreCase);

        public AssetIndex(string repoRoot)
        {
            string assets = Path.Combine(repoRoot, "Assets");
            if (!Directory.Exists(assets)) return;

            foreach (string file in Directory.EnumerateFiles(assets, "*", SearchOption.AllDirectories))
            {
                if (file.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;

                string name = Path.GetFileNameWithoutExtension(file);

                if (file.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                {
                    if (!Pngs.TryGetValue(name, out var same)) Pngs[name] = same = new List<string>();
                    same.Add(file);
                }
                else if (file.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                    _prefabs.TryAdd(name, file);

                RecordResourcePath(file);
            }
        }

        /// <summary>
        /// Unity ships every folder named Resources anywhere under Assets, and the
        /// load path is what follows it, extension removed.
        /// </summary>
        private void RecordResourcePath(string file)
        {
            string normalised = file.Replace('\\', '/');

            int marker = normalised.LastIndexOf("/Resources/", StringComparison.OrdinalIgnoreCase);
            if (marker < 0) return;

            string relative = normalised.Substring(marker + "/Resources/".Length);
            int dot = relative.LastIndexOf('.');

            ResourcePaths.Add(dot < 0 ? relative : relative.Substring(0, dot));
        }

        public string Prefab(string name) => _prefabs.GetValueOrDefault(name);

        /// <summary>
        /// Whether a name binds as a Sprite in IconLibrarySetup's t:Sprite search.
        ///
        /// Two kinds of file silently fail it, and both dead ends are recorded in that
        /// file's comments because both were walked into: a particle texture
        /// (textureType is not 8) and a multi-sprite atlas (spriteMode 2), which holds
        /// named sub-sprites and no sprite of its own.
        /// </summary>
        public bool IsSingleSprite(string name)
        {
            if (!Pngs.TryGetValue(name, out var candidates)) return false;

            foreach (string png in candidates)
            {
                string meta = png + ".meta";
                if (!File.Exists(meta)) continue;

                string text = File.ReadAllText(meta);

                if (Regex.IsMatch(text, @"^\s*textureType:\s*8\s*$", RegexOptions.Multiline)
                 && Regex.IsMatch(text, @"^\s*spriteMode:\s*1\s*$",  RegexOptions.Multiline))
                    return true;
            }

            return false;
        }
    }
}
