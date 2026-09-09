using System;
using System.Collections.Generic;
using System.IO;

/// <summary>
/// The shared rules tree must stay compilable in two hosts that share nothing.
///
/// Assets/Scripts/Rules is linked into BOTH Unity and the ASP.NET Core game server.
/// That is what makes it impossible for the server's damage number and the client's
/// damage number to disagree -- there is one implementation, not two that have to be
/// kept in step by hand.
///
/// The arrangement survives exactly as long as nothing in there references something
/// only one host has. A single `using UnityEngine;` breaks the server build, and it
/// is a very easy line to add by reflex while working in the editor. So the boundary
/// is asserted rather than remembered.
///
/// Two further rules, both about determinism rather than portability:
///
///   * No clock. A rules function that reads DateTime.Now cannot be tested by
///     advancing time, and on the server it would be reading the wrong clock anyway
///     -- elapsed time is an argument, always, because the database owns "now".
///
///   * No unseeded randomness. Anything the server decides must be re-derivable from
///     an audit log, which means CounterRandom seeded and indexed, not System.Random
///     and not UnityEngine.Random.
/// </summary>
internal static class RulesPurity
{
    private const string RulesDir = "Assets/Scripts/Rules";

    /// <summary>Substring, and what it would break if it appeared.</summary>
    private static readonly (string Needle, string Why)[] Banned =
    {
        ("UnityEngine",   "the game server has no engine to link against"),
        ("DateTime.Now",  "elapsed time is an argument; the server's database owns the clock"),
        ("DateTime.UtcNow", "elapsed time is an argument; the server's database owns the clock"),
        ("new Random(",   "use CounterRandom, so a roll can be re-derived from its seed and index"),
        ("System.Random", "use CounterRandom, so a roll can be re-derived from its seed and index"),
    };

    public static void Run(Action<bool, string> check, string repoRoot)
    {
        string dir = Path.Combine(repoRoot, RulesDir);

        if (!Directory.Exists(dir))
        {
            check(false, $"the shared rules tree exists at {RulesDir}");
            return;
        }

        var files = Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories);

        // A glob that silently matches nothing would make every assertion below pass
        // while proving nothing at all.
        check(files.Length > 0, $"{RulesDir} contains source to check (found {files.Length})");

        foreach (string file in files)
        {
            string name = Path.GetFileName(file);

            foreach (var (needle, why) in Banned)
            {
                var hits = OffendingLines(file, needle);
                check(hits.Count == 0,
                      hits.Count == 0
                          ? $"{name} is free of '{needle}'"
                          : $"{name} must not use '{needle}' -- {why} (line {hits[0]})");
            }
        }
    }

    /// <summary>
    /// Line numbers where the needle appears in actual code.
    ///
    /// Comments are stripped first, and deliberately so: these files EXPLAIN why they
    /// avoid UnityEngine and System.Random, and a check that could not tell prose from
    /// code would forbid documenting the rule it enforces.
    /// </summary>
    private static List<int> OffendingLines(string file, string needle)
    {
        var hits = new List<int>();
        string[] lines = File.ReadAllLines(file);

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];

            int comment = line.IndexOf("//", StringComparison.Ordinal);
            if (comment >= 0) line = line.Substring(0, comment);

            if (line.IndexOf(needle, StringComparison.Ordinal) >= 0) hits.Add(i + 1);
        }

        return hits;
    }
}
