using IdleExplorers.Api.Infrastructure;
using Npgsql;

namespace IdleExplorers.Api.Background;

/// <summary>
/// Deletes guest accounts that have gone quiet.
///
/// ══ WHY A SWEEP AND NOT A DISCONNECT HANDLER ══════════════════════════════════
///
/// "Delete the account when they leave the page" is the obvious design and it cannot be
/// built. `beforeunload` does not fire when a phone backgrounds a tab, when the browser
/// is killed, when the device sleeps, or when the network simply stops -- and those are
/// the majority of departures, not the edge cases. A server that waits to be told
/// goodbye accumulates rows forever from everyone who never said it.
///
/// So nothing announces a departure. Silence is the signal: an account with no heartbeat
/// and no session activity inside the window is gone, whether it said so or not. The
/// same rule handles a closed tab, a dead battery and a tunnel.
///
/// ══ WHY DELETING ONE ROW IS ENOUGH ════════════════════════════════════════════
///
/// account.id references auth.users(id) ON DELETE CASCADE, and everything the player
/// owns hangs off account or character with the same rule -- 28 foreign keys of it. So
/// removing the auth user removes the character, its inventory, its equipment, its bank,
/// its talents, its party membership and its presence, in one statement, with no list of
/// tables here to fall out of date the next time one is added.
///
/// What deliberately survives is the ledger: wallet_ledger, item_ledger, telemetry_event
/// and security_event reference the account with ON DELETE SET NULL, so an economy audit
/// trail is not erased by an account going away. That is the existing design and this
/// does not change it.
///
/// ══ WHY IT RUNS HERE AND NOT IN THE DATABASE ══════════════════════════════════
///
/// pg_cron would also work, and would need enabling in a dashboard by hand -- a step
/// that lives nowhere in this repository and would be invisible to whoever next sets the
/// project up. This runs wherever the API runs, deploys with it, and is reviewable in
/// the same tree as every other rule.
/// </summary>
public sealed class GuestSweeper(Db db, ILogger<GuestSweeper> logger) : BackgroundService
{
    /// <summary>How long a guest may be silent before it is collected.</summary>
    public static readonly TimeSpan IdleWindow = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How long a brand new guest is left alone regardless.
    ///
    /// Sign-in and the first API call are not the same instant: the client gets a token,
    /// then asks the game for something. A guest swept in that gap would be deleted
    /// mid-handshake and see an authentication failure on a token that was valid.
    /// </summary>
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(5);

    /// <summary>How often to look.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    // ══ THE STATEMENT ═════════════════════════════════════════════════════════
    //
    // Two liveness signals, not one. A character's heartbeat covers someone playing;
    // account_session covers someone who signed in and is sitting in a menu without a
    // character yet. Either one inside the window keeps the guest.
    //
    // is_anonymous is the gate, so this can never touch a real account. It is a column
    // Supabase Auth maintains, rather than a flag of ours that could drift out of step
    // with what the token actually says.
    //
    // ══ WHY THERE IS A CAP ════════════════════════════════════════════════════
    //
    // This is an unattended DELETE against auth.users, which holds every real account
    // as well as the guests. The predicate is what keeps it off them; the cap is what
    // limits the damage if the predicate is ever wrong. A bug that deletes five hundred
    // rows is a bad afternoon. The same bug without a cap is the end of the project.
    //
    // It is also not a throughput limit worth worrying about: five hundred guests every
    // five minutes is far more than a portfolio link will ever produce, and a genuine
    // backlog simply drains over a few passes.
    private const int MaxPerPass = 500;

    private const string Sql = """
        with doomed as (
            select u.id
            from auth.users u
            where u.is_anonymous
              and u.created_at < now() - @grace
              and not exists (
                  select 1 from character c
                  where c.account_id = u.id
                    and c.last_heartbeat_at is not null
                    and c.last_heartbeat_at > now() - @idle
              )
              and not exists (
                  select 1 from account_session s
                  where s.account_id = u.id
                    and s.last_seen_at > now() - @idle
              )
            limit @cap
        )
        delete from auth.users u
        using doomed d
        where u.id = d.id
        """;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Guest sweep running every {Interval}, collecting guests idle over {Idle} " +
            "with a {Grace} grace period.",
            Interval, IdleWindow, Grace);

        // A first pass on startup, then on the interval. A deploy is exactly when a
        // backlog of guests from before the restart is waiting.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Never let a bad pass kill the loop. A sweep that stops running is a
                // table that grows without bound, and nothing else would report it.
                logger.LogError(ex, "Guest sweep failed; retrying next interval.");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Counts anonymous and real accounts, for the log line.
    /// </summary>
    /// <remarks>
    /// The real-account number is the one worth printing. This statement deletes from
    /// the table that holds every account in the project, and the only assurance that
    /// it is not touching them is a predicate. A count that stays put across sweeps is
    /// that assurance, visible, on an unattended job nobody is watching.
    /// </remarks>
    private const string CountSql = """
        select
            count(*) filter (where is_anonymous)     as guests,
            count(*) filter (where not is_anonymous) as real_accounts
        from auth.users
        """;

    /// <summary>Runs one pass. Returns how many guests were removed.</summary>
    public async Task<int> SweepAsync(CancellationToken cancellation = default)
    {
        await using var connection = await db.OpenAsync(cancellation);

        long guestsBefore = 0;
        long realBefore = 0;
        await using (var counting = new NpgsqlCommand(CountSql, connection))
        await using (var reader = await counting.ExecuteReaderAsync(cancellation))
        {
            if (await reader.ReadAsync(cancellation))
            {
                guestsBefore = reader.GetInt64(0);
                realBefore = reader.GetInt64(1);
            }
        }

        await using var command = new NpgsqlCommand(Sql, connection);

        command.Parameters.AddWithValue("idle", IdleWindow);
        command.Parameters.AddWithValue("grace", Grace);
        command.Parameters.AddWithValue("cap", MaxPerPass);

        int removed = await command.ExecuteNonQueryAsync(cancellation);

        logger.LogInformation(
            "Guest sweep: {Removed} removed, {Guests} guest account(s) before the pass, " +
            "{Real} real account(s) untouched.",
            removed, guestsBefore, realBefore);

        return removed;
    }
}
