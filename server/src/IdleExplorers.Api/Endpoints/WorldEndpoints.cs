using IdleExplorers.Api.Auth;
using IdleExplorers.Api.Infrastructure;
using IdleExplorers.Api.Services;
using IdleExplorers.Rules;
using Microsoft.AspNetCore.Mvc;

namespace IdleExplorers.Api.Endpoints;

/// <summary>
/// Hitting the things standing in a map.
///
/// ══ WHY THIS IS NOT THE BOSS ENDPOINT ═════════════════════════════════════════
///
/// The boss fight validates every action against a frozen snapshot, a sequence
/// number and a cooldown, because the fight's outcome IS the reward: beating the
/// enrage timer is what grants the loot, so the timeline has to be unforgeable.
///
/// An ordinary goblin grants nothing. Loot and experience come from settlement,
/// which integrates each player's own time against server-owned rates — so two
/// people fighting the same goblin are both paid for the time they spent, and
/// killing one faster than is possible earns exactly the same as killing it slowly.
///
/// So this endpoint is about keeping the WORLD coherent rather than the economy
/// honest. Its whole job is that a monster dies once, for everybody, at roughly the
/// right moment.
///
/// ══ WHAT IS STILL CAPPED, AND WHY ═════════════════════════════════════════════
///
/// Damage, at dps × elapsed with three seconds of slack — the boss's own ceiling.
/// Not because a fast kill is worth anything, but because without it one client can
/// delete a map's entire population in a frame, and everybody else's screen empties.
/// That is griefing rather than cheating, and it is worth one multiplication to
/// prevent.
///
/// The dps comes from FreezeCombatAsync, which is the same function the farm integral
/// and the boss both use. A second way of asking "how hard does this character hit"
/// is the drift the shared rules tree exists to design out.
/// </summary>
public static class WorldEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/world").RequireAuthorization();

        // ── Hit something ─────────────────────────────────────────────────────
        group.MapPost("/{characterId:guid}/strike", async Task<IResult> (
            HttpContext http, Caller caller, Db db, IGameClock clock,
            SettlementService settlement, Guid characterId,
            [FromBody] StrikeRequest request) =>
        {
            Guid? accountId = await caller.AccountIdAsync(http.User, http.RequestAborted);
            if (accountId is null) return Results.Unauthorized();

            if (!await caller.OwnsCharacterAsync(accountId.Value, characterId, http.RequestAborted))
            {
                return Results.Problem(
                    title:      "no such character",
                    detail:     "That character does not exist, or does not belong to you.",
                    statusCode: StatusCodes.Status404NotFound);
            }

            if (request.MonsterId == Guid.Empty)
            {
                return Results.Problem(
                    title:      "no target",
                    detail:     "A strike has to say what it hit.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            DateTimeOffset now = await clock.NowAsync(http.RequestAborted);

            return await db.InCharacterTransactionAsync(characterId, async (connection, tx) =>
            {
                // ══ HOW HARD THIS CHARACTER HITS, FROM THE SERVER ═════════════
                //
                // Read here rather than sent, and read through the same function the
                // farm integral and the boss use. The request carries how LONG it has
                // been swinging; the server decides what that is worth.
                var frozen = await settlement.FreezeCombatAsync(connection, tx, characterId,
                                                                http.RequestAborted);

                double elapsed = Math.Max(0d, Math.Min(request.Seconds, MaxWindowSeconds));
                double ceiling = Population.DamageCeiling(frozen.Dps, elapsed);

                double? remaining = await PopulationService.StrikeAsync(
                    connection, tx, request.MonsterId, characterId,
                    request.Damage, ceiling, now, http.RequestAborted);

                // Null means there was no such LIVE monster: somebody else killed it
                // between this client's swing and this request, or it has already
                // respawned as a different row. Not an error — it is the normal
                // outcome of two people fighting the same goblin, and the client
                // simply picks another target.
                if (remaining is null)
                {
                    return Results.Ok(new
                    {
                        monsterId = request.MonsterId,
                        health    = 0d,
                        alive     = false,
                        hit       = false,
                    });
                }

                return Results.Ok(new
                {
                    monsterId = request.MonsterId,
                    health    = remaining.Value,
                    alive     = remaining.Value > 0d,
                    hit       = true,
                });
            }, http.RequestAborted);
        });
    }

    /// <summary>
    /// The longest window one strike report may claim.
    ///
    /// A client that had not reported for ten minutes and then claimed all of it at
    /// once would be handed a ceiling ten minutes wide — which is the ceiling not
    /// being one. Capped at a few seconds, which is longer than the reporting
    /// interval and shorter than anything worth exploiting.
    /// </summary>
    private const double MaxWindowSeconds = 5d;

    /// <summary>
    /// One report: what was hit, for how much, over how long.
    ///
    /// The damage is the client's PREDICTION, drawn from the same shared rules the
    /// server computes with — so in the ordinary case the ceiling is never reached
    /// and the number is accepted as sent. It exists so the health bar on screen and
    /// the health in the table move together.
    /// </summary>
    public sealed record StrikeRequest(Guid MonsterId, double Damage, double Seconds);
}
