using IdleExplorers.Api.Auth;
using IdleExplorers.Api.Infrastructure;
using IdleExplorers.Api.Services;
using IdleExplorers.Rules;

namespace IdleExplorers.Api.Endpoints;

/// <summary>
/// The portal, and what it takes to open it.
///
/// ══ WHY THE GATE IS AN ENDPOINT AND NOT A CLIENT CHECK ════════════════════════
///
/// The client draws the portal and needs to know whether it is lit, so it reads this
/// too. But reading is not deciding: the number comes from kill_counter, which only
/// settlement writes, and the engage call re-checks it inside the same transaction
/// that starts the fight.
///
/// Checking twice is deliberate. The read is for the UI and can be stale by a second
/// without hurting anyone; the check at engage is the one that matters, and it holds
/// the character lock so a thousandth kill landing at the same instant cannot be
/// counted by one and missed by the other.
/// </summary>
public static class BossEndpoints
{
    /// <summary>
    /// Active goblin kills that open the Goblin King's portal.
    ///
    /// ACTIVE only. Not out of suspicion of idle play -- the whole game runs on it --
    /// but because a gate an unattended character walks through on its own is not a
    /// gate, and the thousandth goblin should be something the player did.
    /// </summary>
    public const long GoblinKingGate = 1000L;

    public const string GateMonster = "goblin";
    public const string GateUnlock  = "goblin_king_portal";

    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/boss").RequireAuthorization();

        // ── Is the portal open? ───────────────────────────────────────────────
        group.MapGet("/{characterId:guid}/gate", async (HttpContext http, Caller caller, Db db,
                                                        IGameClock clock, SettlementService settlement,
                                                        Guid characterId) =>
        {
            Guid? accountId = await caller.AccountIdAsync(http.User, http.RequestAborted);
            if (accountId is null) return Results.Unauthorized();

            if (!await caller.OwnsCharacterAsync(accountId.Value, characterId, http.RequestAborted))
                return NotYours();

            // Settled first, so the count includes the window still in flight. A
            // player who has just landed their thousandth kill should find the portal
            // open, not find it open after they next click something.
            DateTimeOffset now = await clock.NowAsync(http.RequestAborted);
            await settlement.SettleAsync(characterId, now, http.RequestAborted);

            await using var connection = await db.OpenAsync(http.RequestAborted);

            long active = await connection.ScalarAsync<long>(
                """
                select coalesce(active_kills, 0) from kill_counter
                 where character_id = $1 and monster_id = $2;
                """,
                null, characterId, GateMonster);

            long afk = await connection.ScalarAsync<long>(
                """
                select coalesce(afk_kills, 0) from kill_counter
                 where character_id = $1 and monster_id = $2;
                """,
                null, characterId, GateMonster);

            return Results.Ok(new
            {
                monsterId   = GateMonster,
                required    = GoblinKingGate,
                activeKills = active,
                // Shown because a portal reading 412 / 1000 beside somebody who has
                // killed nine thousand goblins in their sleep has to explain itself.
                afkKills    = afk,
                remaining   = Math.Max(0L, GoblinKingGate - active),
                open        = active >= GoblinKingGate,
            });
        });

        // ── Open it ───────────────────────────────────────────────────────────
        group.MapPost("/{characterId:guid}/unlock", async (HttpContext http, Caller caller, Db db,
                                                           IGameClock clock, SettlementService settlement,
                                                           Guid characterId) =>
        {
            Guid? accountId = await caller.AccountIdAsync(http.User, http.RequestAborted);
            if (accountId is null) return Results.Unauthorized();

            if (!await caller.OwnsCharacterAsync(accountId.Value, characterId, http.RequestAborted))
                return NotYours();

            DateTimeOffset now = await clock.NowAsync(http.RequestAborted);

            return await db.InCharacterTransactionAsync(characterId, async (connection, tx) =>
            {
                // Inside the lock, so the count cannot move between reading it and
                // acting on it.
                await settlement.SettleLockedAsync(connection, tx, characterId, now, http.RequestAborted);

                long active = await connection.ScalarAsync<long>(
                    """
                    select coalesce(active_kills, 0) from kill_counter
                     where character_id = $1 and monster_id = $2;
                    """,
                    tx, characterId, GateMonster);

                if (active < GoblinKingGate)
                {
                    return Results.Problem(
                        title:      "portal sealed",
                        detail:     $"{GoblinKingGate - active} more active goblin kills.",
                        statusCode: StatusCodes.Status409Conflict);
                }

                // Persisted, because the portal must still be open after a disconnect.
                // An unlock that lives in a session is an unlock a player loses by
                // closing a laptop.
                await connection.ExecuteAsync(
                    """
                    insert into unlock (character_id, unlock_id)
                    values ($1, $2)
                    on conflict (character_id, unlock_id) do nothing;
                    """,
                    tx, characterId, GateUnlock);

                return Results.Ok(new { unlockId = GateUnlock, activeKills = active, open = true });
            }, http.RequestAborted);
        });
    }

    private static IResult NotYours() =>
        Results.Problem(
            title:      "no such character",
            detail:     "That character does not exist, or does not belong to you.",
            statusCode: StatusCodes.Status404NotFound);
}
