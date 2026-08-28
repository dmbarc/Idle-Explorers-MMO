using System;
using System.IO;
using System.Text.RegularExpressions;

namespace IdleExplorersTests;

/// <summary>
/// The claims about the shared monster population that no compiler checks.
///
/// ══ WHY SOURCE CHECKS ═════════════════════════════════════════════════════════
///
/// Because most of what matters here is the ABSENCE of something, or a wire between
/// two components that compiles perfectly whether or not anybody strung it.
///
/// This project has lost three components to exactly that: Session.SignedInChanged
/// had no subscribers, BossPortalController.TryEnterAsync had no caller, and
/// BossHealthBar was never instantiated. Each looked finished and was unreachable,
/// and each cost a playtest to discover.
///
/// A shared population has four such wires — the poll hands it over, something
/// attaches the sync, the local spawner stands down, and a hit is reported — and any
/// one of them missing produces a game that looks exactly like the old one.
/// </summary>
internal static class SharedWorldChecks
{
    internal static void Run(Action<bool, string> check, string root)
    {
        Console.WriteLine("Shared world");

        string sync       = Read(root, "Assets/Scripts/PlayerScripts/MonsterSync.cs");
        string spawner    = Read(root, "Assets/Scripts/PlayerScripts/MonsterSpawner.cs");
        string controller = Read(root, "Assets/Scripts/PlayerScripts/MonsterController.cs");
        string presence   = Read(root, "Assets/Scripts/Backend/PresenceSync.cs");
        string characters = Read(root, "Assets/Scripts/Managers/CharacterManager.cs");
        string zones      = Read(root, "Assets/Scripts/Managers/ZoneManager.cs");

        check(sync.Length > 0, "MonsterSync.cs is where it is expected to be");

        if (sync.Length == 0) return;

        // ── The four wires ────────────────────────────────────────────────────

        check(Strip(characters).Contains("MonsterSync.Attach"),
              "something attaches MonsterSync — a sync nobody constructs draws nothing");

        check(Strip(presence).Contains("MonsterSync.Adopt"),
              "the presence poll hands the population to MonsterSync");

        check(Strip(controller).Contains("MonsterSync.ReportDamage"),
              "a hit on a shared monster is reported — otherwise everybody watches one " +
              "player fail to hurt it");

        check(Strip(zones).Contains("MonsterSync.Clear"),
              "leaving a map forgets its population, or the next map is never spawned");

        // ── The local spawner stands down ─────────────────────────────────────
        //
        // Sliced to Update, which is the method the spawn path runs through. A
        // file-wide match would pass on the comment explaining the rule, which is
        // precisely backwards — and this project has been fooled by proximity four
        // times.
        string update = Body(Strip(spawner), "void Update()");

        check(update.Length > 0, "MonsterSpawner.Update is where it is expected to be");
        check(Regex.IsMatch(update, @"ServerOwnsPopulation\s*\)\s*return"),
              "MonsterSpawner gives up when the server owns the population — two " +
              "populations in one field is the bug it was meant to fix, doubled");

        // ── Only our own kills drop and count ─────────────────────────────────
        //
        // A shared monster can fall because somebody else landed the last blow, and
        // the news arrives on a poll. Dropping loot for it would put items on the
        // ground this player never earned, and counting it would make the boss
        // portal's label climb from other people's fighting.
        string fallOver = Body(Strip(controller), "private void FallOver(");

        check(fallOver.Length > 0, "MonsterController.FallOver is where it is expected to be");
        check(Regex.IsMatch(fallOver, @"if\s*\(\s*ourKill\s*\)"),
              "loot and the kill count are gated on it having been OUR kill");

        check(Regex.IsMatch(fallOver, @"if\s*\(\s*ourKill\s*\)[^}]*DropLoot"),
              "DropLoot is inside that gate, not beside it");

        // ── And the server is the one that decides health ─────────────────────
        string adopt = Body(Strip(controller), "public void AdoptServerState(");

        check(adopt.Length > 0, "MonsterController.AdoptServerState is where it is expected to be");
        check(!adopt.Contains("DamageNumber"),
              "being TOLD a monster's health does not pop a damage number — the swing " +
              "that predicted it already did, and somebody else's hit is not ours to draw");
        check(Regex.IsMatch(adopt, @"FallOver\(\s*ourKill:\s*false\s*\)"),
              "a monster somebody else killed still falls over here");

        // ── The rules are shared, not duplicated ──────────────────────────────
        string rules = Read(root, "Assets/Scripts/Rules/Combat/Population.cs");

        check(rules.Length > 0, "Population.cs is in the SHARED rules tree, not in one host");
        check(rules.Contains("namespace IdleExplorers.Rules"),
              "Population is in the shared namespace, so both sides compile the same one");
        check(!rules.Contains("UnityEngine"),
              "Population has no engine dependency — the shared tree is compiled by the " +
              "server too, and one using directive breaks that quietly");
    }

    /// <summary>
    /// Source with its comments removed.
    ///
    /// These files argue at length about what they do not do, so a check that could
    /// not tell prose from code would pass on the comment explaining why the code is
    /// absent.
    /// </summary>
    private static string Strip(string source)
    {
        source = Regex.Replace(source, @"/\*.*?\*/", "", RegexOptions.Singleline);
        source = Regex.Replace(source, @"^\s*///.*$", "", RegexOptions.Multiline);
        source = Regex.Replace(source, @"^\s*//.*$",  "", RegexOptions.Multiline);

        return source;
    }

    /// <summary>
    /// The body of one method, by brace matching from its signature.
    ///
    /// A fixed-size window is not good enough and this project has the scars: a
    /// 600-character window once reached into the block above and passed on a
    /// neighbour's guard.
    /// </summary>
    private static string Body(string source, string signature)
    {
        int at = source.IndexOf(signature, StringComparison.Ordinal);
        if (at < 0) return "";

        int open = source.IndexOf('{', at);
        if (open < 0) return "";

        int depth = 0;

        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0)
                return source.Substring(open, i - open + 1);
        }

        return "";
    }

    private static string Read(string root, string relative)
    {
        string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

        return File.Exists(path) ? File.ReadAllText(path) : "";
    }
}
