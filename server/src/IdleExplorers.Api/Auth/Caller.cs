using System.Security.Claims;
using IdleExplorers.Api.Infrastructure;

namespace IdleExplorers.Api.Auth;

/// <summary>
/// The account and character a request is allowed to touch.
///
/// ══ WHY OWNERSHIP IS CHECKED HERE AND NOT IN EACH HANDLER ═════════════════════
///
/// "Act on another character's id" is one line of difference from acting on your own,
/// and it is the easiest attack in the game to attempt: change a GUID in a request
/// body. Leaving each endpoint to remember the check means the endpoint written at
/// four in the afternoon three months from now is the hole.
///
/// So a handler never receives a character id. It receives a character it has already
/// been proven to own, or the request has already failed.
/// </summary>
public sealed class Caller(Db db)
{
    // ══ WHY THIS CACHES ═══════════════════════════════════════════════════════
    //
    // Scoped, so one instance serves one request -- and one request asks these
    // questions more than once. The idempotency middleware resolves the account to
    // scope its key, then the endpoint resolves the same account again, then asks
    // about ownership. Each of those was a separate connection.
    //
    // A settle opened SEVEN connections that way, six of them avoidable, and at three
    // hundred concurrent players that starved the pool: Npgsql defaults to a hundred,
    // requests queued behind each other waiting for one, and the failures arrived as
    // 500s and a p99 of 2.3 seconds. The load simulation found it; nothing smaller
    // would have.
    //
    // Caching per REQUEST rather than longer is the safe boundary: an account banned
    // mid-request is still served for that request and refused on the next one, which
    // is the same window any other approach would leave.

    private Guid? _accountId;
    private bool  _accountResolved;

    private readonly HashSet<Guid> _owned = [];
    private readonly HashSet<Guid> _notOwned = [];

    /// <summary>
    /// Finds the account for a validated token, creating one on first sight.
    ///
    /// Supabase Auth owns the credential; this owns the game account. They are created
    /// at different moments -- a user exists the instant they confirm an email, and an
    /// account exists the first time they ask the game for anything -- so the first
    /// authenticated request is where the two meet.
    /// </summary>
    public async Task<Guid?> AccountIdAsync(ClaimsPrincipal? principal,
                                            CancellationToken cancellation = default)
    {
        if (_accountResolved) return _accountId;

        Guid? userId = principal.SupabaseUserId();
        if (userId is null) return null;

        await using var connection = await db.OpenAsync(cancellation);

        // ══ CREATED AND FUNDED IN ONE STATEMENT ═══════════════════════════════
        //
        // ON CONFLICT DO NOTHING rather than check-then-insert: two requests arriving
        // together on a brand new account would both see nothing and both insert, and
        // one of them would fail on the primary key having already done its work.
        //
        // The welcome grant hangs off that same conflict clause, which is what makes
        // it exactly-once without a lock or a flag: `returning id` yields a row ONLY
        // for the statement that actually inserted, so a second request on the same
        // account funds nothing. An existing account passing through here — which is
        // every request the game makes — reaches the CTE with no rows and does nothing.
        //
        // ══ WHY THE LEDGER ROW IS IN THE SAME STATEMENT ═══════════════════════
        //
        // Because sum(delta) = balance is the invariant the scenario tests assert, and
        // this runs with no transaction around it. Two statements could half-apply and
        // leave a balance nobody can explain — which is precisely the state the ledger
        // exists to make impossible.
        await connection.ExecuteAsync(
            """
            with created as (
                insert into account (id, display_name)
                values ($1, $2)
                on conflict (id) do nothing
                returning id
            ),
            funded as (
                insert into wallet (account_id, currency, balance)
                select id, $3, $4 from created
                on conflict (account_id, currency) do nothing
                returning account_id
            )
            insert into wallet_ledger (account_id, currency, delta, reason)
            select account_id, $3, $4, $5 from funded;
            """,
            null,
            userId.Value,
            DefaultDisplayName(userId.Value),
            IdleExplorers.Rules.Currency.RelicCoins,
            IdleExplorers.Rules.Currency.WelcomeRelicCoins,
            IdleExplorers.Rules.Currency.WelcomeReason);

        // Read back rather than trusting the insert: the row may predate this request,
        // and a banned account must not be handed out just because it exists.
        _accountId = await connection.ScalarAsync<Guid?>(
            "select id from account where id = $1 and banned_at is null;",
            null,
            userId.Value);

        _accountResolved = true;
        return _accountId;
    }

    /// <summary>
    /// Confirms this account owns this character, and that it is not deleted.
    ///
    /// Returns false rather than throwing, so the caller decides between 403 and 404 --
    /// and both answers are the same shape on purpose. Telling an attacker apart
    /// "that character does not exist" from "that character is not yours" hands them a
    /// way to enumerate other players.
    /// </summary>
    public async Task<bool> OwnsCharacterAsync(Guid accountId, Guid characterId,
                                               CancellationToken cancellation = default)
    {
        if (_owned.Contains(characterId))    return true;
        if (_notOwned.Contains(characterId)) return false;

        await using var connection = await db.OpenAsync(cancellation);

        long found = await connection.ScalarAsync<long>(
            """
            select count(*) from character
             where id = $1 and account_id = $2 and deleted_at is null;
            """,
            null,
            characterId,
            accountId);

        // A character DELETED mid-request stays owned for the rest of it, which is
        // correct: the delete and whatever else is in flight are both the owner's, and
        // the next request sees the deletion.
        (found == 1 ? _owned : _notOwned).Add(characterId);

        return found == 1;
    }

    /// <summary>
    /// A name for an account nobody has named yet.
    ///
    /// Not the email, and not any part of it: the display name is shown to other
    /// players in chat, and defaulting to a fragment of somebody's email address
    /// publishes it to everyone they ever talk to.
    /// </summary>
    private static string DefaultDisplayName(Guid userId) =>
        $"Explorer-{userId.ToString("N")[..6]}";
}
