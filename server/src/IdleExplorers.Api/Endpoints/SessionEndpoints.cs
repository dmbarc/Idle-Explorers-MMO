using IdleExplorers.Api.Auth;
using IdleExplorers.Api.Infrastructure;

namespace IdleExplorers.Api.Endpoints;

/// <summary>
/// One account, one client.
///
/// ══ WHAT THIS STOPS ═══════════════════════════════════════════════════════════
///
/// Signing into the same account in two browsers and playing the SAME CHARACTER in
/// both. Two clients each believing they owned the state, each settling, each
/// setting activities, each writing positions over one another.
///
/// Nothing duplicated — settlement holds a row lock and the timestamp is the
/// bookkeeping, so the second settle in an instant pays nothing. But it is a race
/// nobody should have to reason about, and it is the shape every duplication exploit
/// begins with.
///
/// ══ THE NEWEST CLIENT WINS ════════════════════════════════════════════════════
///
/// Rather than refusing the second sign-in. Refusing sounds stricter and behaves
/// worse: a closed browser, a crashed tab or a slept laptop each leave a claim
/// behind, and a player locked out of their own account by a tab they cannot reach
/// has no way through except waiting.
///
/// Displacement has neither problem. Whoever is at the keyboard gets in, and the
/// stale client stops being able to act — it finds out the next time it speaks,
/// which for a polling client is within a couple of seconds.
/// </summary>
public static class SessionEndpoints
{
    /// <summary>The header a client echoes its claim in.</summary>
    public const string HeaderName = "X-Idle-Session";

    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/session").RequireAuthorization();

        // ── Claim the account ─────────────────────────────────────────────────
        //
        // Called once, when a client starts playing. The id it returns is what every
        // later request carries.
        group.MapPost("/", async (HttpContext http, Caller caller, Db db, IGameClock clock) =>
        {
            Guid? accountId = await caller.AccountIdAsync(http.User, http.RequestAborted);
            if (accountId is null) return Results.Unauthorized();

            Guid sessionId = Guid.NewGuid();

            DateTimeOffset now = await clock.NowAsync(http.RequestAborted);

            await using var connection = await db.OpenAsync(http.RequestAborted);

            // Whoever asks last holds it. The previous client's id stops matching and
            // its next request is refused.
            await connection.ExecuteAsync(
                """
                insert into account_session (account_id, session_id, claimed_at, last_seen_at)
                values ($1, $2, $3, $3)
                on conflict (account_id) do update
                   set session_id = excluded.session_id,
                       claimed_at = excluded.claimed_at,
                       last_seen_at = excluded.last_seen_at;
                """,
                null, accountId.Value, sessionId, now);

            return Results.Ok(new { sessionId, claimedAt = now });
        });

        // ── Give it up ────────────────────────────────────────────────────────
        //
        // Signing out releases the claim, so the next sign-in anywhere is a clean one
        // rather than a displacement. Best effort: a client that never gets here is
        // displaced by the next sign-in anyway, which is the whole point of the
        // newest-wins rule.
        group.MapDelete("/", async (HttpContext http, Caller caller, Db db) =>
        {
            Guid? accountId = await caller.AccountIdAsync(http.User, http.RequestAborted);
            if (accountId is null) return Results.Unauthorized();

            await using var connection = await db.OpenAsync(http.RequestAborted);

            // Only if it is still theirs. A client that was displaced and then signed
            // out must not take the new client's claim with it on the way.
            await connection.ExecuteAsync(
                "delete from account_session where account_id = $1 and session_id = $2;",
                null, accountId.Value, SessionOf(http) ?? Guid.Empty);

            return Results.Ok(new { released = true });
        });
    }

    /// <summary>The claim this request carries, or null when it carries none.</summary>
    public static Guid? SessionOf(HttpContext http) =>
        http.Request.Headers.TryGetValue(HeaderName, out var value) &&
        Guid.TryParse(value.ToString(), out Guid parsed)
            ? parsed
            : null;
}
