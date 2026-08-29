using System;
using System.Linq;
using System.IO;
using System.Text.RegularExpressions;

namespace IdleExplorersTests;

/// <summary>
/// The things that have to be true before strangers play it.
///
/// ══ WHY THESE ARE GROUPED ═════════════════════════════════════════════════════
///
/// Not because they are related in the code — they are a splash screen, an
/// instrumentation loop, a currency grant and a feature flag — but because they are
/// all *release* properties. Each one is invisible in normal development and only
/// matters the moment somebody who is not the author opens the game.
///
/// The telemetry checks are the sharpest. The ingest endpoint was built, tested,
/// given a flag, a batch cap, a payload cap, an allow-list and a security row for
/// anything outside it — and nothing in the game called it, for the whole of its
/// existence. That is the fourth time a finished component in this project turned out
/// to be unreachable, and the only reliable defence is asking who calls it.
/// </summary>
internal static class PlaytestReadinessChecks
{
    internal static void Run(Action<bool, string> check, string root)
    {
        Console.WriteLine("Playtest readiness");

        Telemetry(check, root);
        Splash(check, root);
        BossLoot(check, root);
        Currency(check, root);
    }

    // ── The client actually reports ───────────────────────────────────────────

    private static void Telemetry(Action<bool, string> check, string root)
    {
        string sync = Read(root, "Assets/Scripts/Backend/TelemetrySync.cs");

        check(sync.Length > 0, "TelemetrySync.cs is where it is expected to be");
        if (sync.Length == 0) return;

        string code = Strip(sync);

        // ══ THE ERROR HOOK IS THE WHOLE POINT ═════════════════════════════════
        //
        // A WebGL player who hits a null reference sees a game that stops responding.
        // They do not open the console. Without this the exception sits in their
        // browser and nobody ever reads it.
        // ══ THE SUBSCRIPTION, NOT THE MENTION ═════════════════════════════════
        //
        // Matching "Application.logMessageReceived" anywhere passes on the UNSUBSCRIBE
        // in OnDisable, which is present whether or not anything ever subscribed.
        // Removing the += left this green, which is the proximity trap this project
        // has now walked into five times.
        check(Regex.IsMatch(code, @"Application\.logMessageReceived\s*\+="),
              "TelemetrySync SUBSCRIBES to Unity's log stream — an exception a player " +
              "hits is otherwise only ever seen by the player");

        check(Regex.IsMatch(code, @"Application\.logMessageReceived\s*-="),
              "and unsubscribes, or a reused managers object reports every error twice");

        check(Regex.IsMatch(code, @"LogType\.Exception"),
              "exceptions specifically are captured, not just Debug.LogError");

        check(code.Contains("client_error") || code.Contains("ClientError"),
              "they are reported as client_error, which is the name the server accepts");

        // Muted after a few, or one broken Update fills the table from a single frame.
        check(Regex.IsMatch(code, @"MaxPerMessage"),
              "a repeating exception is rate-limited per message");

        // ══ AND SOMETHING CONSTRUCTS IT ═══════════════════════════════════════
        string characters = Strip(Read(root, "Assets/Scripts/Managers/CharacterManager.cs"));

        check(characters.Contains("TelemetrySync.Attach"),
              "something attaches TelemetrySync — the ingest endpoint spent its whole " +
              "life with no caller, which is what this check exists to notice");

        check(characters.Contains("Telemetry.SessionStart"),
              "a session is reported when one starts, so the funnel has a denominator");

        string zones = Strip(Read(root, "Assets/Scripts/Managers/ZoneManager.cs"));

        check(zones.Contains("Telemetry.MapEnter"), "entering a map is reported");

        string ui = Strip(Read(root, "Assets/Scripts/UI/Core/UIManager.cs"));

        check(ui.Contains("Telemetry.ScreenOpen"), "opening a screen is reported");

        // ══ AND IT CANNOT FEED ITSELF ═════════════════════════════════════════
        //
        // A failure inside the reporter that logged an ERROR would be caught by its own
        // hook, queued, sent, fail again, and log again. The catch must not raise the
        // thing it handles.
        string flush = Body(code, "private async Awaitable FlushAsync()");

        check(flush.Length > 0, "TelemetrySync.FlushAsync is where it is expected to be");
        check(!flush.Contains("Debug.LogError"),
              "the reporter never logs an ERROR — its own hook would catch it, queue it, " +
              "and try to send it, which is instrumentation feeding itself");
    }

    // ── Every player is told what they are walking into ───────────────────────

    private static void Splash(Action<bool, string> check, string root)
    {
        string splash = Read(root, "Assets/Scripts/UI/Screens/SplashScreen.cs");

        check(splash.Length > 0, "SplashScreen.cs is where it is expected to be");
        if (splash.Length == 0) return;

        check(splash.Contains("early alpha"),
              "the splash says this is an early alpha");

        check(splash.Contains("danielmbarcus@gmail.com"),
              "the splash gives players somewhere to send a bug");

        string code = Strip(splash);

        // ══ IT HAS TO BE ACKNOWLEDGED, NOT FLASHED PAST ═══════════════════════
        //
        // The screen used to advance to Login on a timer. A notice nobody has to
        // dismiss is a notice that disappears while the page is still settling.
        check(!Regex.IsMatch(code, @"TransitionTo\([^)]*\)\s*;\s*\n\s*\}\s*\n\s*\}\s*\n?\s*$") ||
              code.Contains("Continue"),
              "the splash does not advance on its own");

        check(code.Contains("interactable = false"),
              "the continue button is dead until the content has loaded — pressing " +
              "through early reaches a character screen with no classes on it");
    }

    // ── Beating the King is VISIBLE ───────────────────────────────────────────

    /// <summary>
    /// Claiming boss loot re-reads what the server did with it.
    ///
    /// ══ STORING IS NOT SHOWING ════════════════════════════════════════════════
    ///
    /// The claim endpoint puts items in the bag and coins in the wallet, server-side,
    /// and answers with a list. OnItemPickedUp has no subscriber that changes any
    /// number — it is a notification. So without a pull the player kills the King,
    /// sees a toast, and looks at an unchanged inventory and an unchanged relic-coin
    /// counter.
    ///
    /// This is the third time in this project: relic coins granted and displayed as
    /// zero, equipment applied and the sprite left bare, a gem credited and never
    /// paid. Each time the write was perfect and nothing read it back.
    /// </summary>
    private static void BossLoot(Action<bool, string> check, string root)
    {
        string fight = Strip(Read(root, "Assets/Scripts/PlayerScripts/BossFight.cs"));

        check(fight.Length > 0, "BossFight.cs is where it is expected to be");
        if (fight.Length == 0) return;

        string claim = Body(fight, "public async Awaitable ClaimAsync()");

        check(claim.Length > 0, "BossFight.ClaimAsync is where it is expected to be");

        check(claim.Contains("PullAccountAsync"),
              "claiming boss loot re-reads the ACCOUNT, or the relic coins the King " +
              "dropped are credited and the counter still says what it said before");

        check(claim.Contains("PullCharacterAsync"),
              "and the CHARACTER, or the items are in the bag and not on the screen");
    }

    // ── The currency rules ────────────────────────────────────────────────────

    private static void Currency(Action<bool, string> check, string root)
    {
        string currency = Read(root, "Assets/Scripts/Rules/Economy/Currency.cs");

        check(currency.Length > 0, "Currency.cs is where it is expected to be");
        if (currency.Length == 0) return;

        check(Regex.IsMatch(currency, @"WelcomeRelicCoins\s*=\s*1000L"),
              "a new account is given 1000 relic coins");

        string walletFor = Body(Strip(currency), "public static string WalletFor(");

        check(walletFor.Contains("RelicCoinsItemId"),
              "relic coins are a WALLET currency — a drop of them must not land in a " +
              "bag slot the next settle overwrites");

        // ══ THE UNPAID TAP IS SHUT ════════════════════════════════════════════
        //
        // Checked against the migration rather than the live database, because that is
        // the thing that travels with a deploy. A `db push` is what turns it off, and
        // a migration that only seeds ON CONFLICT DO NOTHING cannot.
        string migrations = Path.Combine(root, "server", "supabase", "migrations");

        bool shutOff = Directory.Exists(migrations) &&
                       Directory.EnumerateFiles(migrations, "*.sql")
                                .Select(File.ReadAllText)
                                .Any(sql => Regex.IsMatch(
                                         sql,
                                         @"update\s+feature_flag[\s\S]*?set\s+enabled\s*=\s*false[\s\S]*?shop_test_grants",
                                         RegexOptions.IgnoreCase));

        check(shutOff,
              "a migration explicitly turns shop_test_grants OFF — the original seeds " +
              "it with ON CONFLICT DO NOTHING, which cannot turn a live one back off");
    }

    // ── Machinery ─────────────────────────────────────────────────────────────

    private static string Strip(string source)
    {
        source = Regex.Replace(source, @"/\*.*?\*/", "", RegexOptions.Singleline);
        source = Regex.Replace(source, @"^\s*///.*$", "", RegexOptions.Multiline);
        source = Regex.Replace(source, @"^\s*//.*$",  "", RegexOptions.Multiline);

        return source;
    }

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
