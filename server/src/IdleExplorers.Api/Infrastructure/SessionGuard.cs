using IdleExplorers.Api.Auth;
using IdleExplorers.Api.Endpoints;

namespace IdleExplorers.Api.Infrastructure;

/// <summary>
/// Refuses a client that has been displaced by a newer one.
///
/// ══ WHY MIDDLEWARE AND NOT A CHECK IN EACH HANDLER ════════════════════════════
///
/// The same reasoning as idempotency, which sits beside this for the same reason: a
/// rule enforced by every handler remembering it is a rule the endpoint written in
/// six months will not have. Here it means a new mutating route is protected the day
/// it is written rather than the day somebody notices.
///
/// ══ WHY ONLY MUTATIONS ════════════════════════════════════════════════════════
///
/// A displaced client reading is harmless — it is looking at its own account, and the
/// answer it gets is the truth. What must not happen is two clients WRITING: two
/// settles, two activity changes, two positions fighting each other.
///
/// Letting reads through also means the stale tab can still show a coherent screen
/// while it tells the player they have been signed in elsewhere, rather than
/// collapsing into errors.
///
/// ══ WHY A MISSING HEADER IS ALLOWED ═══════════════════════════════════════════
///
/// Deliberately, and it is the one soft edge here.
///
/// A client that never claimed a session is not a second client — it is an older
/// build, or a tool, or the very first request of a sign-in that is about to claim
/// one. Refusing those would mean this could not be deployed without shipping the
/// matching client in the same instant, and the failure would be every request in the
/// game returning 409.
///
/// The guard is against a client holding the WRONG claim, which is precisely the
/// two-browser case: both of them have one, and only one of them is current.
/// </summary>
public sealed class SessionGuard(RequestDelegate next)
{
    /// <summary>What a displaced client is told. Read by the client to show a message.</summary>
    public const string DisplacedTitle = "signed in elsewhere";

    public async Task InvokeAsync(HttpContext context, Caller caller, Db db)
    {
        if (!Mutates(context.Request.Method))
        {
            await next(context);
            return;
        }

        Guid? claimed = SessionEndpoints.SessionOf(context);

        // No claim at all: an older client, a tool, or the sign-in about to make one.
        if (claimed is null)
        {
            await next(context);
            return;
        }

        Guid? accountId = await caller.AccountIdAsync(context.User, context.RequestAborted);

        // Unauthenticated, or an account that does not exist. Whatever is wrong, it is
        // not this middleware's to answer -- the endpoint returns 401 on its own.
        if (accountId is null)
        {
            await next(context);
            return;
        }

        await using var connection = await db.OpenAsync(context.RequestAborted);

        Guid? current = await connection.ScalarAsync<Guid?>(
            "select session_id from account_session where account_id = $1;",
            null, accountId.Value);

        // No row means nobody has claimed it -- an account playing on a client that
        // predates this. Not a displacement.
        if (current is null || current == claimed)
        {
            await next(context);
            return;
        }

        await Results.Problem(
            title:      DisplacedTitle,
            detail:     "This account is being played somewhere else. Only one at a time.",
            statusCode: StatusCodes.Status409Conflict)
            .ExecuteAsync(context);
    }

    /// <summary>
    /// Methods that change something.
    ///
    /// GET and HEAD are exempt -- see the note above about a displaced tab still being
    /// able to draw a coherent screen while it explains itself.
    /// </summary>
    private static bool Mutates(string method) =>
        !HttpMethods.IsGet(method) && !HttpMethods.IsHead(method) && !HttpMethods.IsOptions(method);
}
