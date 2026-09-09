using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

/// <summary>
/// Checks against the serialized assets themselves — the prefab and scene YAML.
///
/// Both bugs this round were invisible to every check the project had, because both
/// lived in serialized data rather than in code:
///
///   * The map scenes saved with ZERO cameras, and the game showed "Display 1 — No
///     cameras rendering" on entering either one.
///   * The player rig's hair renderer is VisibleInsideMask while the only SpriteMask
///     has no sprite, so hair drew nothing, anywhere, ever.
///
/// A compile check cannot see either. Reading the YAML can.
/// </summary>
internal static class AssetChecks
{
    // ── Rig masking ───────────────────────────────────────────────────────────

    /// <summary>
    /// A SpriteRenderer may not be VisibleInsideMask when no SpriteMask has a sprite.
    ///
    /// SpriteMaskInteraction: 0 = None, 1 = VisibleInsideMask, 2 = VisibleOutsideMask.
    /// A renderer set to 1 with nothing masking it renders nothing at all — no error,
    /// no warning, sprite correctly assigned, enabled true. It is the third way this
    /// rig has found to draw nothing silently.
    /// </summary>
    public static void RigMasking(Action<bool, string> check, string repoRoot)
    {
        string path = Path.Combine(repoRoot, "Assets", "Prefabs", "PlayerCharacter.prefab");

        if (!File.Exists(path))
        {
            check(false, $"PlayerCharacter.prefab exists at {path} " +
                         "(run 'Idle Explorers -> Setup Everything')");
            return;
        }

        string yaml = File.ReadAllText(path);

        int maskedRenderers = Regex.Matches(yaml, @"^\s*m_MaskInteraction:\s*[12]\s*$",
                                             RegexOptions.Multiline).Count;

        // Every SpriteMask block, and whether it carries a sprite. A mask with a real
        // sprite is a deliberate authoring choice and makes masking meaningful.
        var maskBlocks = SplitBlocks(yaml, "SpriteMask:");
        int masksWithSprite = 0;
        foreach (var block in maskBlocks)
            if (!Regex.IsMatch(block, @"^\s*m_Sprite:\s*\{fileID:\s*0\}\s*$", RegexOptions.Multiline))
                masksWithSprite++;

        check(masksWithSprite > 0 || maskedRenderers == 0,
              $"no renderer is mask-gated while every SpriteMask is empty " +
              $"(masked renderers: {maskedRenderers}, masks with a sprite: {masksWithSprite}) " +
              "— this is exactly what made hair invisible");

        // The generated prefab should carry no inert masks at all; PlayerPrefabSetup
        // strips them. Stated separately so a failure says which half is wrong.
        check(maskBlocks.Count == masksWithSprite,
              $"the prefab carries no empty SpriteMask " +
              $"({maskBlocks.Count - masksWithSprite} empty of {maskBlocks.Count})");
    }

    // ── Map cameras ───────────────────────────────────────────────────────────

    /// <summary>
    /// Every map scene needs exactly one camera, tagged MainCamera, with a listener.
    ///
    /// Unity YAML tags components by class id: !u!20 Camera, !u!81 AudioListener.
    /// Counting those is unambiguous in a way that grepping for "Main Camera" is not —
    /// the name is decoration, the class id is the component.
    /// </summary>
    public static void MapCameras(Action<bool, string> check, string repoRoot)
    {
        string scenes = Path.Combine(repoRoot, "Assets", "Scenes");

        if (!Directory.Exists(scenes))
        {
            check(false, $"scene folder exists at {scenes}");
            return;
        }

        var maps = Directory.GetFiles(scenes, "Map_*.unity");
        check(maps.Length > 0, "there is at least one map scene to check");

        foreach (var path in maps)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            string yaml = File.ReadAllText(path);

            int cameras   = Regex.Matches(yaml, @"^--- !u!20 &", RegexOptions.Multiline).Count;
            int listeners = Regex.Matches(yaml, @"^--- !u!81 &", RegexOptions.Multiline).Count;
            int tagged    = Regex.Matches(yaml, @"^\s*m_TagString:\s*MainCamera\s*$",
                                           RegexOptions.Multiline).Count;

            check(cameras == 1,   $"{name} has exactly one Camera (found {cameras})");
            check(listeners >= 1, $"{name} has an AudioListener (found {listeners})");
            check(tagged == 1,    $"{name} has exactly one object tagged MainCamera (found {tagged})");
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Every YAML document whose body starts with the given component header, up to the
    /// next document separator.
    /// </summary>
    private static List<string> SplitBlocks(string yaml, string header)
    {
        var blocks = new List<string>();
        var docs   = Regex.Split(yaml, @"^--- ", RegexOptions.Multiline);

        foreach (var doc in docs)
        {
            int newline = doc.IndexOf('\n');
            if (newline < 0) continue;

            string body = doc.Substring(newline + 1);
            if (body.TrimStart().StartsWith(header, StringComparison.Ordinal))
                blocks.Add(body);
        }

        return blocks;
    }
}
