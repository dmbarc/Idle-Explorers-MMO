using System.Globalization;

namespace IdleExplorers.Tools;

/// <summary>
/// Operator commands. Things a person runs, not things a player triggers.
///
/// ══ WHY NOT ENDPOINTS ═════════════════════════════════════════════════════════
///
/// Exporting the whole telemetry table and printing another account's character sheet
/// are both exactly what an attacker would want, and neither has a player-facing use.
/// Behind HTTP they would need an authorisation model built for an audience of one,
/// and they would then exist forever as an admin surface pointed at the internet.
///
/// As a console command they need no authorisation model at all: reaching them means
/// already holding the database connection string, which is the credential that
/// matters. Nothing here is reachable from the game.
/// </summary>
public static class Program
{
    /// <summary>Where the database is, when nothing says otherwise. The local stack.</summary>
    public const string LocalConnection =
        "Host=127.0.0.1;Port=54322;Database=postgres;Username=postgres;Password=postgres";

    public static async Task<int> Main(string[] args)
    {
        var options = Options.Parse(args);

        try
        {
            return options.Command switch
            {
                "export"  => await Export.RunAsync(options),
                "inspect" => await Inspect.RunAsync(options),
                _         => Usage(),
            };
        }
        catch (Exception e)
        {
            // The message, not the stack. Every failure here is a connection string, a
            // missing character or a directory that does not exist, and a stack trace
            // buries all three under forty frames of Npgsql.
            Console.Error.WriteLine($"error: {e.Message}");

            return 1;
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine(
            """
            Idle Explorers operator tools.

              export   [--out DIR] [--since ISO8601] [--limit N]
                       [--after-gameplay ID] [--after-security ID]
                       Writes telemetry as NDJSON, one object per line.

              inspect  --character NAME-OR-ID
                       Prints a character sheet, the last ledger rows, and any
                       invariant violations.

            Both read the database from --db, or IDLE_EXPLORERS_DB, or the local stack.
            """);

        return 2;
    }
}

/// <summary>
/// The command line.
///
/// Hand-parsed rather than via a package. Six flags do not justify a dependency in a
/// tool whose whole purpose is to be runnable from a laptop with nothing installed.
/// </summary>
public sealed class Options
{
    public string  Command   { get; private init; } = "";
    public string  Out       { get; private init; } = ".";
    public string  Character { get; private init; } = "";
    /// <summary>
    /// Where to resume each stream.
    ///
    /// ══ WHY THERE ARE TWO ═════════════════════════════════════════════════════
    ///
    /// telemetry_event and security_event have INDEPENDENT bigserial sequences. One
    /// shared cursor therefore means one of the streams is always wrong: after a run
    /// that reaches gameplay row 400,000 and security row 300, resuming both from
    /// 400,000 skips every security row ever written, and resuming both from 300
    /// re-exports four hundred thousand gameplay rows.
    ///
    /// The first version of this took a single --after and had exactly that bug. Two
    /// flags, and the export prints the pair to pass next time.
    /// </summary>
    public long    AfterGameplay { get; private init; }
    public long    AfterSecurity { get; private init; }
    public int     Limit     { get; private init; } = 1_000_000;
    public DateTimeOffset? Since { get; private init; }

    /// <summary>
    /// Where the database is.
    ///
    /// Same precedence the API uses -- flag, then environment, then the local stack --
    /// so a connection string that works for one works for the other. A tool that
    /// needed its own configuration would be one people point at the wrong database.
    /// </summary>
    public string Connection { get; private init; } = "";

    public static Options Parse(string[] args)
    {
        string command = args.Length > 0 && !args[0].StartsWith('-') ? args[0] : "";

        string  outDir = ".", character = "", db = "";
        long    afterGameplay = 0L, afterSecurity = 0L;
        int     limit  = 1_000_000;
        DateTimeOffset? since = null;

        for (int i = 0; i < args.Length; i++)
        {
            string flag  = args[i];
            string value = i + 1 < args.Length ? args[i + 1] : "";

            switch (flag)
            {
                case "--out":       outDir    = value; i++; break;
                case "--character": character = value; i++; break;
                case "--db":        db        = value; i++; break;
                case "--after-gameplay":
                    afterGameplay = long.TryParse(value, out long g) ? g : 0L; i++; break;

                case "--after-security":
                    afterSecurity = long.TryParse(value, out long c) ? c : 0L; i++; break;

                // Named rather than silently ignored. Somebody scripting an incremental
                // export from the obvious flag name would otherwise re-export the whole
                // table nightly and never find out.
                case "--after":
                    throw new ArgumentException(
                        "--after is ambiguous: the two streams have separate id sequences. " +
                        "Use --after-gameplay and --after-security, which the previous " +
                        "export printed.");
                case "--limit":     limit     = int.TryParse(value, out int n) ? n : limit; i++; break;

                case "--since":
                    since = DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                                                    DateTimeStyles.AdjustToUniversal, out var parsed)
                        ? parsed : null;

                    if (since is null && value.Length > 0)
                        throw new ArgumentException($"--since '{value}' is not a date I can read.");

                    i++;
                    break;
            }
        }

        if (db.Length == 0)
            db = Environment.GetEnvironmentVariable("IDLE_EXPLORERS_DB") ?? Program.LocalConnection;

        return new Options
        {
            Command    = command,
            Out        = outDir,
            Character  = character,
            AfterGameplay = afterGameplay,
            AfterSecurity = afterSecurity,
            Limit      = limit,
            Since      = since,
            Connection = db,
        };
    }
}
