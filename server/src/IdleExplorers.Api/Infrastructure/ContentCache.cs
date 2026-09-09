using IdleExplorers.Content;
using IdleExplorers.Rules;

namespace IdleExplorers.Api.Infrastructure;

/// <summary>
/// The authored game, read once at startup and held for the life of the process.
///
/// ══ WHY IN MEMORY AND NOT IN THE DATABASE ═════════════════════════════════════
///
/// Content is read on nearly every request — a settlement needs the node's rates, a
/// craft needs the recipe's inputs, a kill needs the monster's loot table — and it
/// changes only when somebody edits a JSON file and redeploys. Querying it per
/// request would be the single largest source of database load in the system, in
/// exchange for freshness nothing needs.
///
/// The trade is that changing content requires a deploy. For an idle game with one
/// author that is the correct side of the trade; the feature_flag table exists for
/// the things that genuinely have to change without one.
///
/// ══ WHY IT REFUSES TO START ═══════════════════════════════════════════════════
///
/// A server that boots with an unreadable catalogue serves a game with no items, no
/// monsters and no recipes, and every one of those failures is silent — a recipe
/// that does not exist simply cannot be crafted. Throwing at startup turns a deploy
/// that would have looked successful into one that visibly did not happen.
/// </summary>
public sealed class ContentCache
{
    private ContentFiles.Import? _import;

    public GameContent Catalogue =>
        _import?.Catalogue ?? throw new InvalidOperationException("Content has not been loaded.");

    /// <summary>
    /// Hash of the twelve files, handed to the client at login.
    ///
    /// A client whose own copy hashes differently is told to re-download rather than
    /// quietly disagreeing with the server about what a chestplate costs.
    /// </summary>
    public string Version => _import?.Version ?? "";

    public void Load()
    {
        string directory = ContentDirectory();
        var import = ContentFiles.LoadFrom(directory);

        if (import.Failures.Count > 0)
        {
            throw new InvalidOperationException(
                $"Content could not be read from '{directory}':{Environment.NewLine}  " +
                string.Join($"{Environment.NewLine}  ", import.Failures));
        }

        // Dangling references are refused as hard as unreadable files. Every one of
        // them is silent in game: a recipe naming a missing item produces nothing and
        // says nothing, which from inside the game is indistinguishable from bad luck.
        if (import.Problems.Count > 0)
        {
            throw new InvalidOperationException(
                $"Content in '{directory}' is not internally consistent:{Environment.NewLine}  " +
                string.Join($"{Environment.NewLine}  ", import.Problems));
        }

        _import = import;
    }

    /// <summary>
    /// Where the twelve files are.
    ///
    /// Configurable, because the container copies them next to the binary while a
    /// developer runs against the ones in the Unity project — and pointing the server
    /// at a stale copy of the content is a whole class of confusing afternoon.
    /// </summary>
    private static string ContentDirectory()
    {
        string? configured = Environment.GetEnvironmentVariable("IDLE_EXPLORERS_CONTENT");
        if (!string.IsNullOrWhiteSpace(configured)) return configured;

        string beside = Path.Combine(AppContext.BaseDirectory, "content");
        if (Directory.Exists(beside)) return beside;

        // Walking up to the Unity project is a development convenience only. In a
        // container there is nothing above the binary, so this finds nothing and the
        // failure above names the directory it looked in.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, "Assets", "StreamingAssets");
            if (Directory.Exists(candidate)) return candidate;

            dir = dir.Parent;
        }

        return beside;
    }
}
