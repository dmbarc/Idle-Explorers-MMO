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
        Guid? userId = principal.SupabaseUserId();
        if (userId is null) return null;

        await using var connection = await db.OpenAsync(cancellation);

        // ON CONFLICT DO NOTHING rather than check-then-insert: two requests arriving
        // together on a brand new account would both see nothing and both insert, and
        // one of them would fail on the primary key having already done its work.
        await connection.ExecuteAsync(
            """
            insert into account (id, display_name)
            values ($1, $2)
            on conflict (id) do nothing;
            """,
            null,
            userId.Value,
            DefaultDisplayName(userId.Value));

        // Read back rather than trusting the insert: the row may predate this request,
        // and a banned account must not be handed out just because it exists.
        return await connection.ScalarAsync<Guid?>(
            "select id from account where id = $1 and banned_at is null;",
            null,
            userId.Value);
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
        await using var connection = await db.OpenAsync(cancellation);

        long found = await connection.ScalarAsync<long>(
            """
            select count(*) from character
             where id = $1 and account_id = $2 and deleted_at is null;
            """,
            null,
            characterId,
            accountId);

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
