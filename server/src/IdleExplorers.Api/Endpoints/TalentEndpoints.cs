using IdleExplorers.Api.Auth;
using IdleExplorers.Api.Infrastructure;
using IdleExplorers.Api.Services;
using IdleExplorers.Rules;
using Microsoft.AspNetCore.Mvc;

namespace IdleExplorers.Api.Endpoints;

/// <summary>
/// Talent points, which were Critical-tier and entirely client-side until now.
///
/// ══ WHY THIS MATTERS MORE THAN IT LOOKS ═══════════════════════════════════════
///
/// A talent point is a stat, a stat is damage-per-second, and damage-per-second is
/// both how fast a character farms and whether they beat the boss enrage timer. A
/// client that could grant itself points could grant itself the whole game -- quietly,
/// without ever touching a currency or an item.
///
/// It was the least conspicuous hole in the migration and one of the largest.
///
/// ══ WHY THE POINTS ARE NEVER STORED ═══════════════════════════════════════════
///
/// Available points are derived: level gives the total, the rows give the spend, and
/// the difference is what is left. Storing a balance alongside them would create the
/// same drift SaveManager.BackfillCharacterXP exists to repair -- two numbers that are
/// supposed to agree and eventually do not.
///
/// So there is nothing to grant, and no write path that could grant it.
/// </summary>
public static class TalentEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/talent").RequireAuthorization();

        // ── What is spent, and what is left ───────────────────────────────────
        group.MapGet("/{characterId:guid}", async Task<IResult> (HttpContext http, Caller caller, Db db,
                                                                 ContentCache content, Guid characterId) =>
        {
            Guid? accountId = await caller.AccountIdAsync(http.User, http.RequestAborted);
            if (accountId is null) return Results.Unauthorized();

            if (!await caller.OwnsCharacterAsync(accountId.Value, characterId, http.RequestAborted))
                return NotYours();

            await using var connection = await db.OpenAsync(http.RequestAborted);

            var sheet = await ReadAsync(connection, null, content, characterId, http.RequestAborted);

            return Results.Ok(Describe(sheet));
        });

        // ── Spend one ─────────────────────────────────────────────────────────
        group.MapPost("/{characterId:guid}", async Task<IResult> (HttpContext http, Caller caller, Db db,
                                                                  ContentCache content, Guid characterId,
                                                                  [FromBody] SpendRequest? request) =>
        {
            Guid? accountId = await caller.AccountIdAsync(http.User, http.RequestAborted);
            if (accountId is null) return Results.Unauthorized();

            if (!await caller.OwnsCharacterAsync(accountId.Value, characterId, http.RequestAborted))
                return NotYours();

            string nodeId = request?.NodeId ?? "";

            if (string.IsNullOrWhiteSpace(nodeId))
            {
                return Results.Problem(
                    title:      "no talent named",
                    detail:     "Say which node the point goes into.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            // The character lock, so two clicks arriving together cannot both see the
            // same available point and both spend it. The idempotency key stops a
            // RETRY; only the lock stops a RACE.
            return await db.InCharacterTransactionAsync<IResult>(characterId, async (connection, tx) =>
            {
                Sheet sheet = await ReadAsync(connection, tx, content, characterId, http.RequestAborted);

                TalentNode? node = null;

                foreach (var candidate in sheet.Nodes)
                    if (candidate.id == nodeId) { node = candidate; break; }

                if (node is null)
                {
                    // Named separately from "you cannot afford it", because this one is
                    // a client bug or a probe rather than a player decision.
                    return Results.Problem(
                        title:      "no such talent",
                        detail:     $"'{nodeId}' is not in any tree this character has.",
                        statusCode: StatusCodes.Status404NotFound);
                }

                // ══ THE SAME FUNCTION THE CLIENT ASKED ════════════════════════
                //
                // Not a re-implementation of it. A server that disagreed with the
                // client about tier requirements would produce a button that looks
                // available and fails with no explanation, which is worse than the
                // button not being there.
                if (!Talents.CanSpend(node, sheet.Level, sheet.Ranks, sheet.Nodes, out string reason))
                {
                    return Results.Problem(
                        title:      "cannot spend that",
                        detail:     reason,
                        statusCode: StatusCodes.Status409Conflict);
                }

                await connection.ExecuteAsync(
                    """
                    insert into talent (character_id, node_id, rank)
                    values ($1, $2, 1)
                    on conflict (character_id, node_id) do update set rank = talent.rank + 1;
                    """,
                    tx, characterId, nodeId);

                await TelemetryEndpoints.RecordAsync(
                    connection, tx, accountId.Value, characterId,
                    "talent_spend", DateTimeOffset.UtcNow,
                    ("node", nodeId),
                    ("rank", (Talents.RankOf(sheet.Ranks, nodeId) + 1).ToString()));

                Sheet after = await ReadAsync(connection, tx, content, characterId, http.RequestAborted);

                return Results.Ok(Describe(after));
            }, http.RequestAborted);
        });

        // ── Respec ────────────────────────────────────────────────────────────
        group.MapDelete("/{characterId:guid}", async Task<IResult> (HttpContext http, Caller caller, Db db,
                                                                    ContentCache content, Guid characterId) =>
        {
            Guid? accountId = await caller.AccountIdAsync(http.User, http.RequestAborted);
            if (accountId is null) return Results.Unauthorized();

            if (!await caller.OwnsCharacterAsync(accountId.Value, characterId, http.RequestAborted))
                return NotYours();

            return await db.InCharacterTransactionAsync<IResult>(characterId, async (connection, tx) =>
            {
                // Everything, in one statement. A partial respec -- one tree cleared and
                // another not -- is a state no rule here knows how to price, and it is
                // exactly what a loop that fails halfway would leave behind.
                await connection.ExecuteAsync(
                    "delete from talent where character_id = $1;", tx, characterId);

                await TelemetryEndpoints.RecordAsync(
                    connection, tx, accountId.Value, characterId,
                    "talent_respec", DateTimeOffset.UtcNow);

                Sheet after = await ReadAsync(connection, tx, content, characterId, http.RequestAborted);

                return Results.Ok(Describe(after));
            }, http.RequestAborted);
        });
    }

    // ── Reading ───────────────────────────────────────────────────────────────

    private sealed record Sheet(int Level, List<TalentRank> Ranks, List<TalentNode> Nodes);

    private static async Task<Sheet> ReadAsync(Npgsql.NpgsqlConnection connection,
                                               Npgsql.NpgsqlTransaction? tx,
                                               ContentCache content, Guid characterId,
                                               CancellationToken cancellation)
    {
        long xp = await connection.ScalarAsync<long>(
            "select xp from character where id = $1;", tx, characterId);

        string classId = await connection.ScalarAsync<string>(
            "select class_id from character where id = $1;", tx, characterId) ?? "";

        var ranks = new List<TalentRank>();

        await using (var command = connection.Sql(
            "select node_id, rank from talent where character_id = $1 order by node_id;",
            tx, characterId))
        {
            await using var reader = await command.ExecuteReaderAsync(cancellation);

            while (await reader.ReadAsync(cancellation))
                ranks.Add(new TalentRank { nodeId = reader.GetString(0), rank = reader.GetInt32(1) });
        }

        // ══ EVERY CLASS, NOT JUST THE PRIMARY ONE ═════════════════════════════
        //
        // This read character.class_id alone, so a second class existed on the client
        // and nowhere the server could see. Spending a point in its tree came back
        // "no such talent" -- which was true, and the reason it was true was here.
        //
        // The primary class is still included even if the join returns nothing, so a
        // character whose rows predate the backfill is never left with no tree at all.
        var classes = new List<ClassData>();
        var seen    = new HashSet<string>(StringComparer.Ordinal);

        await using (var command = connection.Sql(
            "select class_id from character_class where character_id = $1 order by added_at;",
            tx, characterId))
        {
            await using var reader = await command.ExecuteReaderAsync(cancellation);

            while (await reader.ReadAsync(cancellation))
            {
                string owned = reader.GetString(0);

                if (!seen.Add(owned)) continue;

                if (content.Catalogue.GetClass(owned) is { } tree) classes.Add(tree);
            }
        }

        if (!string.IsNullOrEmpty(classId) && seen.Add(classId) &&
            content.Catalogue.GetClass(classId) is { } resolved)
        {
            classes.Add(resolved);
        }

        return new Sheet(Levelling.CharacterLevel(xp), ranks, Talents.NodesOf(classes));
    }

    private static object Describe(Sheet sheet) => new
    {
        level     = sheet.Level,
        total     = Talents.TotalPoints(sheet.Level),
        spent     = Talents.SpentPoints(sheet.Ranks, sheet.Nodes),
        available = Talents.AvailablePoints(sheet.Level, sheet.Ranks, sheet.Nodes),

        // An array of pairs rather than an object keyed by node. JsonUtility on the
        // client cannot deserialise a dictionary and produces an empty one silently.
        ranks = sheet.Ranks
            .Select(rank => new { nodeId = rank.nodeId, rank = rank.rank })
            .ToArray(),
    };

    private static IResult NotYours() =>
        Results.Problem(
            title:      "no such character",
            detail:     "That character does not exist, or does not belong to you.",
            statusCode: StatusCodes.Status404NotFound);

    public sealed record SpendRequest(string? NodeId);
}
