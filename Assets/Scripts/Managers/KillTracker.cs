using System.Collections.Generic;
using IdleExplorers.Rules;
using UnityEngine;

/// <summary>
/// Counting what the character has killed.
///
/// ══ THE SUBSCRIBER THAT WAS NEVER THERE ═══════════════════════════════════════
///
/// GameEvents.OnMonsterKilled has fired from MonsterController since it was written
/// and has never had a single listener. Nothing in the game knew how many goblins
/// anyone had killed — which is why the boss portal could not exist.
///
/// ══ WHY KILLS SEEN HERE ARE ALWAYS ACTIVE ═════════════════════════════════════
///
/// This only runs while the game is open and a monster is dying on screen, so every
/// kill it observes is one the player was present for. AFK kills never come through
/// here: they come from settlement, as a number, and are credited to the other half
/// of the tally.
///
/// That is the whole distinction the boss gate rests on, and it falls out of WHERE
/// the two numbers come from rather than from anyone deciding it per kill.
///
/// ══ THE SERVER STILL DECIDES ══════════════════════════════════════════════════
///
/// This is a mirror. It exists so the portal can show "412 / 1000" without a round
/// trip, and so the client can grey out a door it knows is shut. Whether the portal
/// actually opens is answered by the server's kill_counter, and if the two disagree
/// the server wins — see BossPortalController, which asks before it lets anyone
/// through.
/// </summary>
public class KillTracker : MonoBehaviour
{
    private void OnEnable()  => GameEvents.OnMonsterKilled += Credit;
    private void OnDisable() => GameEvents.OnMonsterKilled -= Credit;

    /// <summary>One kill, watched, on the current character.</summary>
    private void Credit(string monsterId)
    {
        var character = CharacterManager.Current;
        if (character == null || string.IsNullOrEmpty(monsterId)) return;

        KillCount row = RowFor(character, monsterId);
        row.activeKills = Saturating(row.activeKills, 1L);

        GameEvents.OnKillCountChanged?.Invoke(monsterId, row.activeKills);
    }

    /// <summary>
    /// Kills a settlement credited while nobody was watching.
    ///
    /// Called with the figures the server returned rather than computed here, so the
    /// mirror stays a mirror. Computing them locally would be a second implementation
    /// of the thing the server exists to own.
    /// </summary>
    public static void CreditSettled(string monsterId, long active, long afk)
    {
        var character = CharacterManager.Current;
        if (character == null || string.IsNullOrEmpty(monsterId)) return;

        KillCount row = RowFor(character, monsterId);

        row.activeKills = Saturating(row.activeKills, active);
        row.afkKills    = Saturating(row.afkKills,    afk);

        GameEvents.OnKillCountChanged?.Invoke(monsterId, row.activeKills);
    }

    // ── Reading ───────────────────────────────────────────────────────────────

    public static long ActiveKills(string monsterId) => Find(monsterId)?.activeKills ?? 0L;
    public static long AfkKills(string monsterId)    => Find(monsterId)?.afkKills    ?? 0L;

    /// <summary>
    /// Whether a gate needing this many ACTIVE kills looks open from here.
    ///
    /// "Looks" is doing real work in that sentence. The server decides; this is what
    /// the portal draws while it waits to be told.
    /// </summary>
    public static bool GateLooksOpen(string monsterId, long required) =>
        ActiveKills(monsterId) >= required;

    public static long Remaining(string monsterId, long required) =>
        System.Math.Max(0L, required - ActiveKills(monsterId));

    // ── Internals ─────────────────────────────────────────────────────────────

    private static KillCount Find(string monsterId)
    {
        var kills = CharacterManager.Current?.kills;
        if (kills == null || string.IsNullOrEmpty(monsterId)) return null;

        foreach (var row in kills)
            if (row != null && row.monsterId == monsterId) return row;

        return null;
    }

    private static KillCount RowFor(CharacterData character, string monsterId)
    {
        character.kills ??= new List<KillCount>();

        foreach (var row in character.kills)
            if (row != null && row.monsterId == monsterId) return row;

        var created = new KillCount { monsterId = monsterId };
        character.kills.Add(created);

        return created;
    }

    /// <summary>
    /// Addition that stops at the ceiling rather than wrapping.
    ///
    /// A count that wrapped negative would close a gate the player had already opened,
    /// and no amount of further play would reopen it.
    /// </summary>
    private static long Saturating(long current, long add)
    {
        if (current < 0L) current = 0L;
        if (add     < 0L) add     = 0L;

        return current > long.MaxValue - add ? long.MaxValue : current + add;
    }
}
