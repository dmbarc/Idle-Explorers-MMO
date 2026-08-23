using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Builds Assets/Resources/AudioLibrary.asset from the audio packs already in the
/// project.
///
/// No download is needed: the Kenney "Game Assets All-in-1" bundle already installed
/// here ships sixteen CC0 audio packs, and the four that matter for this game are RPG
/// Audio (leather, coins, metal, chopping, footsteps), Interface Sounds (clicks,
/// confirmations, errors), Impact Sounds (hits by material and weight) and Music
/// Loops. Everything below is drawn from those. CC0 means no attribution is required
/// and commercial use is fine.
///
/// Mapping is by FILE NAME PREFIX, so an id can claim a whole numbered family in one
/// line — "impactMetal_light_" picks up all five variants, and AudioManager plays a
/// different one each time. That is what stops a fight sounding like a metronome.
///
/// Menu: Idle Explorers → Rebuild Audio Library
/// </summary>
public static class AudioSetup
{
    private const string ASSET_PATH = "Assets/Resources/AudioLibrary.asset";

    /// <summary>
    /// soundId → clip name prefixes, and a volume trim.
    ///
    /// The trims are not decoration. Kenney's packs are normalised individually, so an
    /// interface click and a heavy metal impact arrive at wildly different loudness;
    /// without these the UI would be inaudible under combat, or combat deafening.
    /// </summary>
    private static readonly (string Id, string[] Prefixes, float Volume)[] SoundMap =
    {
        // ── Interface ─────────────────────────────────────────────────────────
        (Sfx.Click,   new[] { "click_00" },              0.55f),
        (Sfx.Hover,   new[] { "rollover" },              0.35f),
        (Sfx.Open,    new[] { "maximize_00" },           0.50f),
        (Sfx.Close,   new[] { "minimize_00" },           0.50f),
        (Sfx.Error,   new[] { "error_00" },              0.55f),
        (Sfx.Confirm, new[] { "confirmation_00" },       0.60f),
        (Sfx.Toggle,  new[] { "switch" },                0.50f),

        // ── Combat ────────────────────────────────────────────────────────────
        // Light metal for the player's swings, heavier bodies for taking one, so the
        // two are distinguishable without looking at the health bar.
        (Sfx.Hit,          new[] { "impactMetal_light_" },  0.55f),
        (Sfx.PlayerHurt,   new[] { "impactPunch_medium_" }, 0.70f),
        (Sfx.MonsterDeath, new[] { "impactMining_" },       0.60f),
        (Sfx.PlayerDeath,  new[] { "impactBell_heavy_" },   0.70f),
        (Sfx.AbilityCast,  new[] { "forceField_" },         0.50f),

        // ── Progression ───────────────────────────────────────────────────────
        (Sfx.LevelUp, new[] { "jingles-pizzicato_0" }, 0.55f),
        (Sfx.SkillUp, new[] { "pluck_00" },            0.55f),

        // ── Items ─────────────────────────────────────────────────────────────
        (Sfx.Pickup,   new[] { "handleSmallLeather", "dropLeather" }, 0.60f),
        (Sfx.Coins,    new[] { "handleCoins" },                       0.60f),
        (Sfx.Equip,    new[] { "cloth", "clothBelt" },                0.60f),
        (Sfx.Unequip,  new[] { "beltHandle" },                        0.55f),
        (Sfx.Break,    new[] { "impactGlass_heavy_" },                0.75f),
        (Sfx.Repair,   new[] { "metalLatch", "metalClick" },          0.60f),
        (Sfx.Purchase, new[] { "confirmation_00" },                   0.70f),

        // ── Skilling ──────────────────────────────────────────────────────────
        (Sfx.Mine,    new[] { "impactMining_" },      0.50f),
        (Sfx.Chop,    new[] { "chop" },               0.55f),
        (Sfx.Smith,   new[] { "metalPot" },           0.55f),
        (Sfx.Craft,   new[] { "handleSmallLeather" }, 0.50f),
        (Sfx.SetProc, new[] { "impactBell_heavy_" },  0.65f),
    };

    /// <summary>musicId → exact track name from Kenney's Music Loops.</summary>
    private static readonly (string Id, string[] Names, float Volume)[] MusicMap =
    {
        // Wandering and unhurried, which is what a menu wants.
        (Bgm.Menu, new[] { "Wacky Waiting" },  0.75f),

        // Long, low-key and loopable. An idle game is left running for hours, so the
        // in-game track must not be the one with a hook.
        (Bgm.Game, new[] { "Infinite Descent" }, 0.65f),
    };

    /// <summary>Folders searched, in order. First match wins for a given prefix.</summary>
    private static readonly string[] SearchRoots =
    {
        "Assets/Kenney Game Assets All-in-1 3.7.0/Audio",
        "Assets/Imports",
    };

    [MenuItem("Idle Explorers/Rebuild Audio Library")]
    public static void Rebuild() => Rebuild(showDialog: true);

    public static void Rebuild(bool showDialog)
    {
        const string resourcesDir = "Assets/Resources";
        if (!Directory.Exists(resourcesDir)) Directory.CreateDirectory(resourcesDir);

        var library = AssetDatabase.LoadAssetAtPath<AudioLibrary>(ASSET_PATH);
        bool isNew = library == null;
        if (isNew) library = ScriptableObject.CreateInstance<AudioLibrary>();

        library.sounds.Clear();
        library.music.Clear();

        var index   = BuildClipIndex();
        var missing = new List<string>();
        int mapped  = 0;
        int clips   = 0;

        foreach (var (id, prefixes, volume) in SoundMap)
        {
            var found = Collect(index, prefixes);
            if (found.Count == 0) { missing.Add(id); continue; }

            library.sounds.Add(new AudioLibrary.Entry { id = id, clips = found, volume = volume });
            mapped++;
            clips += found.Count;
        }

        foreach (var (id, names, volume) in MusicMap)
        {
            var found = Collect(index, names);
            if (found.Count == 0) { missing.Add($"music:{id}"); continue; }

            library.music.Add(new AudioLibrary.Entry { id = id, clips = found, volume = volume });
            clips += found.Count;
        }

        if (isNew) AssetDatabase.CreateAsset(library, ASSET_PATH);
        else       EditorUtility.SetDirty(library);

        library.InvalidateCache();
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Debug.Log($"[Audio] {mapped}/{SoundMap.Length} sound ids and {library.music.Count}/{MusicMap.Length} " +
                  $"music tracks mapped to {clips} clip(s) → {ASSET_PATH}");

        if (missing.Count > 0)
        {
            // Loud, because an unmapped sound is simply silence — the exact failure
            // mode that let this project ship an audio manager wired to nothing.
            Debug.LogWarning($"[Audio] {missing.Count} id(s) found no clips and will be silent: " +
                             string.Join(", ", missing) + "\n" +
                             "Check that the Kenney audio packs are still under " +
                             $"'{SearchRoots[0]}'.");
        }

        ReportUnmappedIds(library);

        if (showDialog)
            EditorUtility.DisplayDialog("Audio Library Rebuilt",
                $"{mapped}/{SoundMap.Length} sounds, {library.music.Count}/{MusicMap.Length} music tracks, " +
                $"{clips} clips total.\n\n" +
                "All from the Kenney CC0 packs already in the project — nothing to download, " +
                "no attribution required.\n\n" +
                (missing.Count > 0 ? $"{missing.Count} id(s) are silent — see the console." : "Every id has audio."),
                "OK");
    }

    /// <summary>
    /// Names a sound id the code asks for that the map above never mentions. Populate
    /// can only complain about mappings it HAS; an id nobody added is invisible to it.
    /// </summary>
    private static void ReportUnmappedIds(AudioLibrary library)
    {
        var gaps = new List<string>();

        foreach (var id in Sfx.All)
            if (library.GetSound(id) == null) gaps.Add(id);

        foreach (var id in Bgm.All)
            if (library.GetMusic(id) == null) gaps.Add($"music:{id}");

        if (gaps.Count == 0)
        {
            Debug.Log("[Audio] Every sound id the game uses has at least one clip.");
            return;
        }

        Debug.LogWarning($"[Audio] {gaps.Count} id(s) referenced in code have no entry: " +
                         string.Join(", ", gaps));
    }

    // ── Clip discovery ────────────────────────────────────────────────────────

    private static Dictionary<string, AudioClip> BuildClipIndex()
    {
        var index = new Dictionary<string, AudioClip>();

        foreach (var root in SearchRoots)
        {
            if (!Directory.Exists(root)) continue;

            foreach (var guid in AssetDatabase.FindAssets("t:AudioClip", new[] { root }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                string name = Path.GetFileNameWithoutExtension(path);

                // First root wins, so Kenney's packs take precedence over Imports.
                if (index.ContainsKey(name)) continue;

                var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
                if (clip != null) index[name] = clip;
            }
        }
        return index;
    }

    /// <summary>
    /// Every clip whose name starts with one of the prefixes, sorted so numbered
    /// families stay in order and a rebuild produces a stable asset.
    /// </summary>
    private static List<AudioClip> Collect(Dictionary<string, AudioClip> index, string[] prefixes)
    {
        var names = new List<string>();

        foreach (var name in index.Keys)
        {
            foreach (var prefix in prefixes)
            {
                if (!name.StartsWith(prefix, System.StringComparison.OrdinalIgnoreCase)) continue;
                names.Add(name);
                break;
            }
        }

        names.Sort(System.StringComparer.OrdinalIgnoreCase);

        var clips = new List<AudioClip>();
        foreach (var name in names) clips.Add(index[name]);

        // A dozen variants of a footstep is fine; a hundred is a pointless asset
        // reference list and a slower load for no audible benefit.
        const int MaxVariants = 8;
        if (clips.Count > MaxVariants) clips.RemoveRange(MaxVariants, clips.Count - MaxVariants);

        return clips;
    }
}
