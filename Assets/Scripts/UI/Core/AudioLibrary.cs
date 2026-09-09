using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Maps sound ids to clips drawn from the installed audio packs.
///
/// Same shape and the same reasoning as IconLibrary: the audio lives outside any
/// Resources folder, and copying hundreds of .ogg files into one to make
/// Resources.Load work would double them in the build. An editor pass
/// (Idle Explorers → Rebuild Audio Library) records direct asset references here, and
/// this single asset goes in Resources.
///
/// Each id holds a LIST of clips rather than one. Most game sounds fire in bursts —
/// footsteps, hits, coins — and a single sample repeated forty times a minute is the
/// difference between a game that has sound and a game you mute.
/// </summary>
[CreateAssetMenu(fileName = "AudioLibrary", menuName = "IdleExplorers/AudioLibrary")]
public class AudioLibrary : ScriptableObject
{
    [Serializable]
    public class Entry
    {
        public string           id;
        public List<AudioClip>  clips = new();

        [Range(0f, 2f)]
        public float            volume = 1f;
    }

    public List<Entry> sounds = new();
    public List<Entry> music  = new();

    private Dictionary<string, Entry> _soundLookup;
    private Dictionary<string, Entry> _musicLookup;

    public Entry GetSound(string id)
    {
        _soundLookup ??= Build(sounds);
        return id != null && _soundLookup.TryGetValue(id, out var e) ? e : null;
    }

    public Entry GetMusic(string id)
    {
        _musicLookup ??= Build(music);
        return id != null && _musicLookup.TryGetValue(id, out var e) ? e : null;
    }

    /// <summary>One clip from an id's set, chosen at random. Null when unmapped.</summary>
    public AudioClip PickSound(string id)
    {
        var entry = GetSound(id);
        return Pick(entry);
    }

    public AudioClip PickMusic(string id) => Pick(GetMusic(id));

    private static AudioClip Pick(Entry entry)
    {
        if (entry?.clips == null || entry.clips.Count == 0) return null;
        if (entry.clips.Count == 1) return entry.clips[0];
        return entry.clips[UnityEngine.Random.Range(0, entry.clips.Count)];
    }

    private static Dictionary<string, Entry> Build(List<Entry> entries)
    {
        var map = new Dictionary<string, Entry>();
        if (entries == null) return map;

        foreach (var e in entries)
            if (e != null && !string.IsNullOrEmpty(e.id) && e.clips != null && e.clips.Count > 0)
                map[e.id] = e;

        return map;
    }

    /// <summary>Drops cached lookups so an editor rebuild takes effect without a restart.</summary>
    public void InvalidateCache()
    {
        _soundLookup = null;
        _musicLookup = null;
    }
}

/// <summary>
/// Every sound the game asks for, in one place.
///
/// Strings rather than an enum so a sound can be added to the library without a code
/// change, but declared as constants so a typo at a call site is a compile error
/// rather than a silence nobody notices. AudioSetup validates that every id here has
/// at least one clip.
/// </summary>
public static class Sfx
{
    // UI
    public const string Click        = "ui_click";
    public const string Hover        = "ui_hover";
    public const string Open         = "ui_open";
    public const string Close        = "ui_close";
    public const string Error        = "ui_error";
    public const string Confirm      = "ui_confirm";
    public const string Toggle       = "ui_toggle";

    // World and combat
    public const string Hit          = "hit";
    public const string PlayerHurt   = "player_hurt";
    public const string MonsterDeath = "monster_death";
    public const string PlayerDeath  = "player_death";
    public const string AbilityCast  = "ability_cast";

    // Progression
    public const string LevelUp      = "level_up";
    public const string SkillUp      = "skill_up";

    // Items
    public const string Pickup       = "pickup";
    public const string Coins        = "coins";
    public const string Equip        = "equip";
    public const string Unequip      = "unequip";
    public const string Break        = "gear_break";
    public const string Repair       = "repair";
    public const string Purchase     = "purchase";

    // Skilling
    public const string Mine         = "mine";
    public const string Chop         = "chop";
    public const string Smith        = "smith";
    public const string Craft        = "craft";
    public const string SetProc      = "set_proc";

    /// <summary>Every id above, for the editor pass to check coverage against.</summary>
    public static readonly string[] All =
    {
        Click, Hover, Open, Close, Error, Confirm, Toggle,
        Hit, PlayerHurt, MonsterDeath, PlayerDeath, AbilityCast,
        LevelUp, SkillUp,
        Pickup, Coins, Equip, Unequip, Break, Repair, Purchase,
        Mine, Chop, Smith, Craft, SetProc,
    };
}

/// <summary>Music track ids.</summary>
public static class Bgm
{
    public const string Menu = "menu";
    public const string Game = "game";

    public static readonly string[] All = { Menu, Game };
}
