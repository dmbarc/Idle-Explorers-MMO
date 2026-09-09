using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The client's copy of what a potion did, for as long as it lasts.
///
/// ══ WHAT THIS IS AND IS NOT ═══════════════════════════════════════════════════
///
/// It is a MIRROR. The buff itself is a row in character_buff, written by the server
/// when the potion was drunk and read by the one function that assembles a
/// character's stats. Nothing here grants anything, and deleting this file would
/// change no number the server computes.
///
/// It exists so the client draws the same damage the server pays. Without it a
/// character with a Draught of Fury would swing for one number on screen and be
/// credited another, forever -- which is exactly the formula drift the shared rules
/// tree was built to make impossible, arriving through the back door as missing
/// state rather than as a second implementation.
///
/// ══ WHY IT EXPIRES ON ITS OWN ═════════════════════════════════════════════════
///
/// The server stores an absolute instant and filters on now(); this counts down. The
/// two can disagree by a second at the boundary, which costs nothing: the server's
/// answer is the one that pays, and a client that thinks a buff has one second left
/// when the server has already dropped it draws a number a fraction too high for one
/// swing.
///
/// What it must NOT do is keep a buff alive that the server has ended, so the
/// countdown is against unscaled real time and the expiry is checked on read rather
/// than swept on a timer.
/// </summary>
public static class BuffManager
{
    /// <summary>One buff, with the moment it stops mattering.</summary>
    private readonly struct Live
    {
        public readonly IdleExplorers.Rules.Buffs.Active Buff;
        public readonly float EndsAt;

        public Live(IdleExplorers.Rules.Buffs.Active buff, float endsAt)
        {
            Buff   = buff;
            EndsAt = endsAt;
        }
    }

    private static readonly Dictionary<string, Live> _live = new();

    /// <summary>Raised whenever a buff starts, is refreshed, or runs out.</summary>
    public static Action OnChanged;

    /// <summary>
    /// Records a buff the SERVER granted.
    ///
    /// Takes the magnitude and duration the server reported rather than the ones in
    /// the item file: the server clamps both, and a client that read the raw content
    /// would draw an unclamped number.
    /// </summary>
    public static void Adopt(string statId, float magnitude, double seconds, string label)
    {
        if (string.IsNullOrEmpty(statId) || magnitude <= 0f || seconds <= 0d) return;
        if (!IdleExplorers.Rules.Buffs.IsBuffable(statId)) return;

        // Keyed on the stat, so a second Draught of Fury replaces the first exactly as
        // the server's (character_id, stat_id) primary key does. Two mirrors of one
        // row would be the client stacking something the server does not.
        _live[statId] = new Live(
            new IdleExplorers.Rules.Buffs.Active
            {
                statId           = statId,
                magnitude        = IdleExplorers.Rules.Buffs.Clamp(magnitude),
                secondsRemaining = seconds,
                label            = label,
            },
            Time.unscaledTime + (float)seconds);

        OnChanged?.Invoke();
    }

    /// <summary>Forgets everything. Called when the character changes.</summary>
    public static void Clear()
    {
        if (_live.Count == 0) return;

        _live.Clear();
        OnChanged?.Invoke();
    }

    /// <summary>
    /// Everything still in force.
    ///
    /// ══ WHY THIS DOES NOT PRUNE ═══════════════════════════════════════════════
    ///
    /// It is called from inside StatsManager's recompute, and dropping an entry there
    /// would raise OnChanged, which marks the stat block dirty, in the middle of
    /// building it. Reading is a read; PruneExpired is what ends things, and it runs
    /// on the manager's own tick where nothing is half-built.
    ///
    /// Expired entries are skipped rather than returned, so a stale one can never be
    /// applied even if a prune has not happened yet.
    ///
    /// Allocates a list per call, which is fine: this is read when the stat block is
    /// rebuilt and when the HUD redraws, not per frame per monster.
    /// </summary>
    public static List<IdleExplorers.Rules.Buffs.Active> Active()
    {
        var live = new List<IdleExplorers.Rules.Buffs.Active>();

        foreach (var pair in _live)
        {
            float left = pair.Value.EndsAt - Time.unscaledTime;

            if (left <= 0f) continue;

            pair.Value.Buff.secondsRemaining = left;
            live.Add(pair.Value.Buff);
        }

        return live;
    }

    /// <summary>
    /// Ends anything whose time is up, and says so.
    ///
    /// ══ WHY SOMETHING HAS TO CALL THIS ════════════════════════════════════════
    ///
    /// Because expiry is the only change to a buff that no player action causes. If
    /// the only prune happened while stats were being rebuilt, a Draught of Fury would
    /// come off whenever the character next changed a piece of gear -- which is to
    /// say, at a moment unrelated to the ten minutes on the label.
    ///
    /// StatsManager ticks it: that is the component whose answer a buff changes, and
    /// it is already alive for exactly as long as the character is.
    ///
    /// Cheap in the common case -- no buffs, or none expired, allocates nothing.
    /// </summary>
    public static void PruneExpired()
    {
        if (_live.Count == 0) return;

        List<string> ended = null;

        foreach (var pair in _live)
            if (pair.Value.EndsAt - Time.unscaledTime <= 0f)
                (ended ??= new List<string>()).Add(pair.Key);

        if (ended == null) return;

        foreach (string statId in ended)
        {
            if (_live.TryGetValue(statId, out Live done))
                GameEvents.FireToast($"{done.Buff.label} wears off.", ChatTone.Info);

            _live.Remove(statId);
        }

        OnChanged?.Invoke();
    }

    /// <summary>True when anything is in force. Cheap enough for a HUD tick.</summary>
    public static bool Any => _live.Count > 0;

    /// <summary>
    /// Folds the live buffs into an assembled stat block.
    ///
    /// Through the SHARED Buffs.Apply, which is the whole point: the server calls the
    /// same function on the same fields, so the two cannot decide that +25% damage
    /// means different things.
    /// </summary>
    public static void Apply(StatBlock block) =>
        IdleExplorers.Rules.Buffs.Apply(block, Active());
}
