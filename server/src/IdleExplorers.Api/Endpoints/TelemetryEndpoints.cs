using System.Text.Json;
using IdleExplorers.Api.Auth;
using IdleExplorers.Api.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace IdleExplorers.Api.Endpoints;

/// <summary>
/// What players are doing, and what somebody is trying to do to the server.
///
/// ══ TWO STREAMS, NOT ONE ══════════════════════════════════════════════════════
///
/// Gameplay telemetry answers "what are players doing" -- which classes, how long
/// AFK, how many reach the boss. Security telemetry answers "is somebody trying to
/// break this" -- rejected actions, idempotency replays, impossible movement.
///
/// Mixed into one table neither is queryable: the security signal is a thousandth of
/// the volume and drowns, and every gameplay funnel has to remember to exclude it.
/// Two tables, two indexes, two questions.
///
/// ══ WHY THE CLIENT MAY ONLY REPORT ITS OWN BEHAVIOUR ══════════════════════════
///
/// A client can say "I opened the character sheet". It cannot say "I earned 4,000
/// experience" -- the server already knows what it granted, and accepting a claim
/// about a reward would put the client back in charge of the thing this whole
/// architecture removed.
///
/// So events arriving here are filtered against a list of things a client is allowed
/// to have an opinion about. Anything else is dropped and counted as a security
/// event, because a client sending events it should not know about is either a bug
/// or a probe.
///
/// ══ DELIBERATELY COARSE ═══════════════════════════════════════════════════════
///
/// No per-gather-tick events and no per-attack events. That is the difference
/// between a stream somebody reads and a stream nobody can afford to keep.
/// </summary>
public static class TelemetryEndpoints
{
    /// <summary>
    /// Events a client is trusted to report, because none of them is a reward.
    ///
    /// Everything else the server emits itself, at the moment it decides the thing
    /// being reported -- which is the only moment the report can be true.
    /// </summary>
    private static readonly HashSet<string> ClientReportable = new(StringComparer.Ordinal)
    {
        "session_start", "session_end",
        "screen_open", "screen_close",
        "map_enter",
        "tutorial_step",
        "minigame_result",
        "client_error",
        "setting_changed",
    };

    /// <summary>
    /// Most events one request may carry.
    ///
    /// Batched because a client that posted per event would spend more time on
    /// telemetry than on the game; capped because an uncapped batch is a way to fill
    /// a database from a laptop.
    /// </summary>
    public const int MaxBatch = 64;

    /// <summary>Longest a payload may be once serialised.</summary>
    public const int MaxPayloadBytes = 4096;

    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/telemetry").RequireAuthorization();

        group.MapPost("/", async (HttpContext http, Caller caller, Db db, IGameClock clock,
                                  [FromBody] TelemetryBatch batch) =>
        {
            Guid? accountId = await caller.AccountIdAsync(http.User, http.RequestAborted);
            if (accountId is null) return Results.Unauthorized();

            var events = batch?.Events ?? [];

            if (events.Length == 0) return Results.Ok(new { accepted = 0, rejected = 0 });

            if (events.Length > MaxBatch)
            {
                return Results.Problem(
                    title:      "batch too large",
                    detail:     $"At most {MaxBatch} events per request.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            DateTimeOffset now = await clock.NowAsync(http.RequestAborted);

            await using var connection = await db.OpenAsync(http.RequestAborted);

            int accepted = 0, rejected = 0;

            foreach (var reported in events)
            {
                if (reported is null) continue;

                string name = reported.Event ?? "";

                if (!ClientReportable.Contains(name))
                {
                    // Counted rather than silently dropped. A client reporting
                    // "boss_defeated" is either a bug worth finding or somebody
                    // seeing what the endpoint accepts, and both are worth a row.
                    await RecordSecurityAsync(connection, accountId.Value,
                                              "telemetry_not_reportable",
                                              $"{{\"event\":{JsonSerializer.Serialize(name)}}}",
                                              now, http.RequestAborted);
                    rejected++;
                    continue;
                }

                string payload = Payload(reported);

                if (payload.Length > MaxPayloadBytes) { rejected++; continue; }

                // The character is verified, not trusted. A client reporting activity
                // on somebody else's character would otherwise poison their funnel.
                Guid? characterId = null;

                if (Guid.TryParse(reported.CharacterId, out Guid claimed) &&
                    await caller.OwnsCharacterAsync(accountId.Value, claimed, http.RequestAborted))
                {
                    characterId = claimed;
                }

                await connection.ExecuteAsync(
                    """
                    insert into telemetry_event (account_id, character_id, event, payload, occurred_at)
                    values ($1, $2, $3, $4::jsonb, $5);
                    """,
                    null, accountId.Value, characterId, name, payload, now);

                accepted++;
            }

            return Results.Ok(new { accepted, rejected });
        });
    }

    /// <summary>
    /// Turns the reported key/value pairs into a JSON object.
    ///
    /// An ARRAY of pairs on the wire rather than an object, because JsonUtility on the
    /// client cannot serialise a Dictionary -- the same constraint that shaped every
    /// other response. Stored as jsonb, because unlike an idempotency response this
    /// one genuinely is queried.
    /// </summary>
    private static string Payload(ReportedEvent reported)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var pair in reported.Payload ?? [])
        {
            if (pair is null || string.IsNullOrEmpty(pair.K)) continue;

            fields[pair.K] = pair.V ?? "";
        }

        return JsonSerializer.Serialize(fields);
    }

    /// <summary>
    /// Records something suspicious.
    ///
    /// Public, because the interesting security events are raised by the endpoints
    /// that refuse things rather than by anything here.
    /// </summary>
    public static async Task RecordSecurityAsync(Npgsql.NpgsqlConnection connection,
                                                 Guid accountId, string kind, string detail,
                                                 DateTimeOffset now,
                                                 CancellationToken cancellation)
    {
        await connection.ExecuteAsync(
            """
            insert into security_event (account_id, kind, detail, occurred_at)
            values ($1, $2, $3::jsonb, $4);
            """,
            null, accountId, kind, detail, now);
    }

    public sealed record TelemetryBatch(ReportedEvent[]? Events);

    public sealed record ReportedEvent(string? Event, string? CharacterId, KeyValue[]? Payload);

    /// <summary>One field. An array of these rather than an object -- see Payload.</summary>
    public sealed record KeyValue(string? K, string? V);
}
