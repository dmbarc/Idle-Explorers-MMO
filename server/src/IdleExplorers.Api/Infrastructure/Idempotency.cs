using System.Text;
using System.Text.Json;
using IdleExplorers.Api.Auth;
using Npgsql;

namespace IdleExplorers.Api.Infrastructure;

/// <summary>
/// One request, one effect, however many times it arrives.
///
/// ══ WHY THIS IS MIDDLEWARE AND NOT A HELPER ═══════════════════════════════════
///
/// WebGL tabs get suspended mid-request constantly — a phone locking, a laptop lid, a
/// background tab throttled — and the client retries. A retried craft that consumes
/// twice is a support ticket; a retried purchase that grants twice is worse in the
/// other direction.
///
/// A helper each handler calls is a rule enforced by memory. Middleware means the
/// endpoint somebody adds in a hurry is covered before they have thought about it,
/// and forgetting is a 400 rather than a duplicate.
///
/// ══ WHY THE KEY IS CLAIMED BEFORE THE WORK, NOT AFTER ═════════════════════════
///
/// The obvious shape — look for a stored response, run the handler, store the answer —
/// is wrong, and wrong in exactly the way this is supposed to prevent. Two copies of
/// one request arriving together BOTH find nothing stored, and both then execute. The
/// character lock serialises them, which means the second craft happens after the
/// first rather than instead of it. Two crafts, one request id.
///
/// So the unique constraint is used as a CLAIM, taken before the handler runs:
///
///   insert wins   → nobody else has this request, do the work
///   insert loses  → somebody has it. Either they finished, and their response is
///                   replayed, or they are still going, and this is told to retry.
///
/// ══ WHAT IT REPLAYS ═══════════════════════════════════════════════════════════
///
/// The stored RESPONSE, byte for byte. Not "success" — the original answer. A client
/// retrying a settle has to be told what it earned, not merely that it already
/// earned it, or the retry shows a player nothing and they assume it failed.
///
/// ══ WHAT IT DOES NOT DO ═══════════════════════════════════════════════════════
///
/// It does not make anything atomic. Two DIFFERENT requests racing is a lock problem,
/// handled by Db.InCharacterTransactionAsync. This only collapses repeats of the
/// SAME one.
/// </summary>
public sealed class IdempotencyMiddleware(RequestDelegate next)
{
    public const string HeaderName = "Idempotency-Key";

    /// <summary>A claim with no answer yet. Any real response is 2xx or higher.</summary>
    private const int InFlight = 0;

    /// <summary>
    /// How long a claim may sit unanswered before a retry may take it over.
    ///
    /// The window this closes: the process dies between claiming and answering, and
    /// that request id is poisoned forever — the player retries, is told "in
    /// progress" by a request that no longer exists, and can never get past it
    /// because the key is baked into their client's retry.
    ///
    /// Long enough that a slow settlement is never stolen from itself, short enough
    /// that a crash costs one retry.
    /// </summary>
    private static readonly TimeSpan ClaimTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Methods that change something. GET and HEAD are exempt because replaying a read
    /// is free, and requiring a key on them would be ceremony.
    /// </summary>
    private static bool Mutates(string method) =>
        method is "POST" or "PUT" or "PATCH" or "DELETE";

    public async Task InvokeAsync(HttpContext context, Db db, Caller caller)
    {
        if (!Mutates(context.Request.Method))
        {
            await next(context);
            return;
        }

        // Unauthenticated mutations do not exist in this API, and the key is stored
        // per account. Let it through to be rejected by authorization, which is a
        // clearer answer than "missing idempotency key" to someone not logged in.
        Guid? accountId = await caller.AccountIdAsync(context.User, context.RequestAborted);
        if (accountId is null)
        {
            await next(context);
            return;
        }

        if (!context.Request.Headers.TryGetValue(HeaderName, out var header) ||
            string.IsNullOrWhiteSpace(header))
        {
            await Problem(context, StatusCodes.Status400BadRequest,
                          "missing idempotency key",
                          $"Every mutating request must carry an {HeaderName} header.");
            return;
        }

        string requestId = header.ToString();

        if (requestId.Length > 200)
        {
            await Problem(context, StatusCodes.Status400BadRequest,
                          "idempotency key too long", "Keys are at most 200 characters.");
            return;
        }

        string endpoint = $"{context.Request.Method} {context.Request.Path}";

        Claim claim = await TryClaimAsync(db, accountId.Value, requestId, endpoint,
                                          context.RequestAborted);

        switch (claim.Outcome)
        {
            case ClaimOutcome.Won:
                break;

            case ClaimOutcome.AlreadyAnswered:
                context.Response.StatusCode  = claim.StatusCode;
                context.Response.ContentType = "application/json";
                context.Response.Headers["Idempotent-Replay"] = "true";

                await context.Response.WriteAsync(claim.Response!, context.RequestAborted);
                return;

            case ClaimOutcome.StillRunning:
                // 409 rather than 202: the client asked for a result and there is not
                // one yet. Retrying the same key is the correct thing for it to do,
                // and Retry-After says when without it having to guess.
                context.Response.Headers["Retry-After"] = "1";

                await Problem(context, StatusCodes.Status409Conflict,
                              "request in progress",
                              "An identical request is still being processed. Retry shortly.");
                return;

            case ClaimOutcome.DifferentEndpoint:
                // The same key on a different endpoint is a client bug, and replaying
                // a craft response to a purchase would be a worse one.
                await Problem(context, StatusCodes.Status409Conflict,
                              "idempotency key reused",
                              $"That key was already used for '{claim.Endpoint}'.");
                return;
        }

        // ── The claim is ours. Run it, and record what it answered ────────────
        Stream original = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;

        bool answered = false;

        try
        {
            await next(context);

            buffer.Position = 0;
            string body = await new StreamReader(buffer, Encoding.UTF8)
                .ReadToEndAsync(context.RequestAborted);

            buffer.Position = 0;
            await buffer.CopyToAsync(original, context.RequestAborted);

            if (context.Response.StatusCode is >= 200 and < 300)
            {
                await AnswerAsync(db, accountId.Value, requestId,
                                  context.Response.StatusCode, body, CancellationToken.None);
                answered = true;
            }
        }
        finally
        {
            context.Response.Body = original;

            // A claim with no answer is released rather than left standing. Only
            // SUCCESS is idempotent: a 500 from a transient database blip must stay
            // retryable, or one bad moment permanently poisons a request id the
            // player cannot see and cannot change.
            if (!answered)
                await ReleaseAsync(db, accountId.Value, requestId, CancellationToken.None);
        }
    }

    // ── The claim ─────────────────────────────────────────────────────────────

    private enum ClaimOutcome { Won, AlreadyAnswered, StillRunning, DifferentEndpoint }

    private readonly record struct Claim(
        ClaimOutcome Outcome,
        int          StatusCode = 0,
        string?      Response   = null,
        string?      Endpoint   = null);

    private static async Task<Claim> TryClaimAsync(Db db, Guid accountId, string requestId,
                                                   string endpoint, CancellationToken cancellation)
    {
        await using var connection = await db.OpenAsync(cancellation);

        // One statement, so the check and the claim cannot be separated by another
        // request. The DO UPDATE fires only for a claim that has timed out, which is
        // how a crashed request is taken over rather than blocking its own retry
        // forever. RETURNING tells us which branch happened.
        await using var command = connection.Sql(
            """
            insert into idempotency_record (account_id, request_id, endpoint, status_code, response)
            values ($1, $2, $3, 0, '{}')
            on conflict (account_id, request_id) do update
               set created_at = now(),
                   endpoint   = excluded.endpoint
             where idempotency_record.status_code = 0
               and idempotency_record.created_at < now() - $4::interval
            returning status_code, response, endpoint;
            """,
            null,
            accountId, requestId, endpoint, ClaimTimeout);

        await using (var reader = await command.ExecuteReaderAsync(cancellation))
        {
            // A row came back: either the insert or the timeout takeover. Both mean
            // the claim is ours.
            if (await reader.ReadAsync(cancellation)) return new Claim(ClaimOutcome.Won);
        }

        // No row: the conflict target existed and the WHERE excluded it. Find out why.
        await using var lookup = connection.Sql(
            """
            select status_code, response, endpoint
              from idempotency_record
             where account_id = $1 and request_id = $2;
            """,
            null, accountId, requestId);

        await using var existing = await lookup.ExecuteReaderAsync(cancellation);

        if (!await existing.ReadAsync(cancellation))
        {
            // It vanished between the two statements — a concurrent release. Treat it
            // as still running rather than racing again; the retry will win cleanly.
            return new Claim(ClaimOutcome.StillRunning);
        }

        int    status        = existing.GetInt32(0);
        string response      = existing.GetString(1);
        string storedEndpoint = existing.GetString(2);

        if (storedEndpoint != endpoint)
            return new Claim(ClaimOutcome.DifferentEndpoint, Endpoint: storedEndpoint);

        return status == InFlight
            ? new Claim(ClaimOutcome.StillRunning)
            : new Claim(ClaimOutcome.AlreadyAnswered, status, response, storedEndpoint);
    }

    private static async Task AnswerAsync(Db db, Guid accountId, string requestId,
                                          int statusCode, string body,
                                          CancellationToken cancellation)
    {
        await using var connection = await db.OpenAsync(cancellation);

        await connection.ExecuteAsync(
            """
            update idempotency_record
               set status_code = $3, response = $4
             where account_id = $1 and request_id = $2;
            """,
            null,
            accountId, requestId, statusCode,
            string.IsNullOrWhiteSpace(body) ? "{}" : body);
    }

    private static async Task ReleaseAsync(Db db, Guid accountId, string requestId,
                                           CancellationToken cancellation)
    {
        try
        {
            await using var connection = await db.OpenAsync(cancellation);

            await connection.ExecuteAsync(
                """
                delete from idempotency_record
                 where account_id = $1 and request_id = $2 and status_code = 0;
                """,
                null, accountId, requestId);
        }
        catch (Exception)
        {
            // The request already failed; failing to tidy up after it must not replace
            // the error the caller needs to see. The claim times out on its own.
        }
    }

    private static Task Problem(HttpContext context, int status, string title, string detail)
    {
        context.Response.StatusCode  = status;
        context.Response.ContentType = "application/problem+json";

        return context.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            title,
            status,
            detail,
        }), context.RequestAborted);
    }
}
