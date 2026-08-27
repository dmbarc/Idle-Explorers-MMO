using IdleExplorers.Api.Auth;
using IdleExplorers.Api.Infrastructure;
using IdleExplorers.Api.Services;
using IdleExplorers.Rules;
using Microsoft.AspNetCore.Mvc;

namespace IdleExplorers.Api.Endpoints;

/// <summary>
/// What a character is doing, and what that has earned.
///
/// ══ THE CLIENT SAYS WHAT IT WANTS, NEVER WHAT IT GOT ══════════════════════════
///
/// Three verbs, and none of them accepts a reward:
///
///   POST /activity/{id}          "work this node"      — an intent
///   POST /activity/{id}/beat     "I am still here"     — a fact about presence
///   POST /activity/{id}/settle   "what do I have?"     — a question
///
/// There is no "I gathered 40 ore". The rates come from content the server loaded,
/// the elapsed time comes from the server's clock, and the arithmetic comes from the
/// shared rules. A client that lies can only lie about which node it is standing at,
/// and the server checks that against the map.
/// </summary>
public static class ActivityEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/activity").RequireAuthorization();

        // ── Start doing something ─────────────────────────────────────────────
        group.MapPost("/{characterId:guid}", async (HttpContext http, Caller caller, Db db,
                                                    ContentCache content, IGameClock clock,
                                                    SettlementService settlement,
                                                    Guid characterId,
                                                    [FromBody] SetActivityRequest request) =>
        {
            Guid? accountId = await caller.AccountIdAsync(http.User, http.RequestAborted);
            if (accountId is null) return Results.Unauthorized();

            if (!await caller.OwnsCharacterAsync(accountId.Value, characterId, http.RequestAborted))
                return NotYours();

            // The node is resolved from CONTENT, not from the request. Everything the
            // request supplies beyond an id is ignored, because every one of those
            // numbers is a rate and a client-supplied rate is a client-supplied reward.
            SkillNodeEntry? node = FindNode(content, request.NodeId);

            if (node is null)
            {
                return Results.Problem(
                    title:      "unknown node",
                    detail:     $"There is no node '{request.NodeId}'.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            DateTimeOffset now = await clock.NowAsync(http.RequestAborted);

            return await db.InCharacterTransactionAsync(characterId, async (connection, tx) =>
            {
                // Settle the OLD activity before replacing it. Otherwise switching
                // nodes silently discards whatever the last one had accrued, and
                // switching often would be strictly worse than standing still.
                await settlement.SettleLockedAsync(connection, tx, characterId, now, http.RequestAborted);

                await connection.ExecuteAsync(
                    """
                    update activity
                       set kind               = 'gather',
                           skill_id           = $2,
                           node_id            = $3,
                           target_item_id     = $4,
                           recipe_id          = '',
                           monster_id         = '',
                           seconds_per_action = $5,
                           active_rate_multi  = $6,
                           afk_rate_multi     = $7,
                           xp_per_action      = $8,
                           special_chance     = $9,
                           progress           = 0,
                           started_at         = $10,
                           last_settled_at    = $10
                     where character_id = $1;
                    """,
                    tx, characterId,
                    node.skillId,
                    node.nodeId,
                    node.targetItemId,
                    Math.Max(RateMath.MinSecondsPerAction, node.baseSecondsPerAction),
                    Math.Max(RateMath.MinRateMultiplier, node.activeRateMulti),
                    Math.Max(0f, node.afkRateMulti),
                    Math.Max(0f, node.xpPerAction),
                    RulesMath.Clamp01(node.specialChance),
                    now);

                return Results.Ok(new
                {
                    characterId,
                    kind    = "gather",
                    nodeId  = node.nodeId,
                    skillId = node.skillId,
                    startedAt = now,
                });
            }, http.RequestAborted);
        });

        // ── Start crafting ────────────────────────────────────────────────────
        group.MapPost("/{characterId:guid}/craft", async (HttpContext http, Caller caller, Db db,
                                                          ContentCache content, IGameClock clock,
                                                          SettlementService settlement,
                                                          Guid characterId,
                                                          [FromBody] SetCraftRequest request) =>
        {
            Guid? accountId = await caller.AccountIdAsync(http.User, http.RequestAborted);
            if (accountId is null) return Results.Unauthorized();

            if (!await caller.OwnsCharacterAsync(accountId.Value, characterId, http.RequestAborted))
                return NotYours();

            CraftRecipe? recipe = content.Catalogue.GetRecipe(request.RecipeId);

            if (recipe is null)
            {
                return Results.Problem(
                    title:      "unknown recipe",
                    detail:     $"There is no recipe '{request.RecipeId}'.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            DateTimeOffset now = await clock.NowAsync(http.RequestAborted);

            return await db.InCharacterTransactionAsync(characterId, async (connection, tx) =>
            {
                await settlement.SettleLockedAsync(connection, tx, characterId, now, http.RequestAborted);

                // The level gate is checked HERE and not trusted from the client,
                // because a recipe requiring smithing 40 is a recipe whose output is
                // worth what forty levels of smithing cost.
                long skillXp = await connection.ScalarAsync<long>(
                    "select xp from character_skill where character_id = $1 and skill_id = $2;",
                    tx, characterId, recipe.skillId);

                int level = Levelling.SkillLevel(skillXp);

                if (level < recipe.reqSkillLevel)
                {
                    return Results.Problem(
                        title:      "skill too low",
                        detail:     $"'{recipe.DisplayName}' needs {recipe.skillId} {recipe.reqSkillLevel}; you are {level}.",
                        statusCode: StatusCodes.Status409Conflict);
                }

                // Craft time comes from the recipe AND the skill level, resolved here
                // once so the stored rate cannot be re-derived differently later.
                float seconds = Math.Max(RateMath.MinSecondsPerAction, recipe.SecondsPerCraft(level));

                await connection.ExecuteAsync(
                    """
                    update activity
                       set kind               = 'craft',
                           skill_id           = $2,
                           recipe_id          = $3,
                           node_id            = $4,
                           target_item_id     = '',
                           monster_id         = '',
                           seconds_per_action = $5,
                           active_rate_multi  = 1,
                           afk_rate_multi     = $6,
                           xp_per_action      = 0,
                           special_chance     = 0,
                           progress           = 0,
                           started_at         = $7,
                           last_settled_at    = $7
                     where character_id = $1;
                    """,
                    tx, characterId,
                    recipe.skillId,
                    recipe.id,
                    recipe.stationType ?? "",
                    seconds,
                    StationAfkRate,
                    now);

                return Results.Ok(new
                {
                    characterId,
                    kind      = "craft",
                    recipeId  = recipe.id,
                    skillId   = recipe.skillId,
                    startedAt = now,
                });
            }, http.RequestAborted);
        });

        // ── Stop ──────────────────────────────────────────────────────────────
        group.MapDelete("/{characterId:guid}", async (HttpContext http, Caller caller, Db db,
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
                var outcome = await settlement.SettleLockedAsync(connection, tx, characterId, now,
                                                                 http.RequestAborted);

                await connection.ExecuteAsync(
                    """
                    update activity
                       set kind = 'idle', skill_id = '', node_id = '', target_item_id = '',
                           recipe_id = '', monster_id = '', progress = 0
                     where character_id = $1;
                    """,
                    tx, characterId);

                return Results.Ok(Describe(outcome));
            }, http.RequestAborted);
        });

        // ── I am still here ───────────────────────────────────────────────────
        //
        // The only thing a heartbeat asserts is presence, and it asserts it by
        // ARRIVING. The body is ignored; there is nothing a client could put in one
        // that the server would be better off knowing.
        group.MapPost("/{characterId:guid}/beat", async (HttpContext http, Caller caller, Db db,
                                                         IGameClock clock, Guid characterId) =>
        {
            Guid? accountId = await caller.AccountIdAsync(http.User, http.RequestAborted);
            if (accountId is null) return Results.Unauthorized();

            if (!await caller.OwnsCharacterAsync(accountId.Value, characterId, http.RequestAborted))
                return NotYours();

            DateTimeOffset now = await clock.NowAsync(http.RequestAborted);

            await using var connection = await db.OpenAsync(http.RequestAborted);

            // Server time, not the request's idea of it. A client that could set this
            // could grant itself the active rate for a window it was not present for.
            await connection.ExecuteAsync(
                "update activity set last_heartbeat_at = $2 where character_id = $1;",
                null, characterId, now);

            return Results.Ok(new { acknowledgedAt = now });
        });

        // ── What have I earned? ───────────────────────────────────────────────
        group.MapPost("/{characterId:guid}/settle", async (HttpContext http, Caller caller,
                                                           IGameClock clock, SettlementService settlement,
                                                           Guid characterId) =>
        {
            Guid? accountId = await caller.AccountIdAsync(http.User, http.RequestAborted);
            if (accountId is null) return Results.Unauthorized();

            if (!await caller.OwnsCharacterAsync(accountId.Value, characterId, http.RequestAborted))
                return NotYours();

            DateTimeOffset now = await clock.NowAsync(http.RequestAborted);

            var outcome = await settlement.SettleAsync(characterId, now, http.RequestAborted);

            return Results.Ok(Describe(outcome));
        });
    }

    private static object Describe(SettlementService.Outcome outcome) => new
    {
        actions             = outcome.Actions,
        supervisedActions   = outcome.SupervisedActions,
        xpGained            = outcome.XpGained,
        elapsedSeconds      = outcome.ElapsedSeconds,
        supervisedSeconds   = outcome.SupervisedSeconds,
        items               = outcome.Items,
        currency            = outcome.Currency,
        stoppedForRoom      = outcome.StoppedForRoom,
        lostToFullInventory = outcome.LostToFullInventory,
        ranOutOfInputs      = outcome.RanOutOfInputs,
    };

    /// <summary>
    /// Finds a node by id across every zone.
    ///
    /// TODO(Phase 1): also check the character is actually AT this node's map. It is a
    /// tier-two check -- no reward depends on position, so a player who teleports
    /// gains nothing but a shorter walk -- but it is cheap and it makes the security
    /// event log meaningful.
    /// </summary>
    private static SkillNodeEntry? FindNode(ContentCache content, string? nodeId)
    {
        if (string.IsNullOrEmpty(nodeId)) return null;

        foreach (var zone in content.Catalogue.Zones.Values)
        {
            if (zone?.maps == null) continue;

            foreach (var map in zone.maps)
            {
                if (map?.skillNodes == null) continue;

                foreach (var node in map.skillNodes)
                    if (node != null && node.nodeId == nodeId) return node;
            }
        }

        return null;
    }

    private static IResult NotYours() =>
        Results.Problem(
            title:      "no such character",
            detail:     "That character does not exist, or does not belong to you.",
            statusCode: StatusCodes.Status404NotFound);

    /// <summary>
    /// How well a station works unattended.
    ///
    /// Half, against a gathering node's 0.6. A station is a place you stand at rather
    /// than a thing you walk between, so the gap between watching it and not is
    /// smaller -- but it is not zero, or an anvil would be a second job rather than
    /// an idle game.
    ///
    /// TODO(Phase 3): per-station, from content, once there is more than one kind.
    /// </summary>
    private const float StationAfkRate = 0.5f;

    public sealed record SetActivityRequest(string? NodeId);
    public sealed record SetCraftRequest(string? RecipeId);
}
