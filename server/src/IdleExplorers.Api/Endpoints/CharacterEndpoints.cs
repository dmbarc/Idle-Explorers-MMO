using IdleExplorers.Api.Auth;
using IdleExplorers.Api.Infrastructure;
using IdleExplorers.Rules;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace IdleExplorers.Api.Endpoints;

/// <summary>
/// Creating, reading and deleting characters.
///
/// Every route takes the character id in the path and proves ownership before doing
/// anything with it. "Act on another character's id" is a one-GUID attack and this is
/// where it stops — see Caller for why the check does not live in each handler.
/// </summary>
public static class CharacterEndpoints
{
    /// <summary>
    /// How many living characters one account may hold.
    ///
    /// A cap rather than none, because every character is a settlement row the
    /// background sweep visits and a slice of the bank nobody is using. Generous
    /// enough that no real player meets it, low enough that a script cannot make a
    /// hundred thousand.
    /// </summary>
    public const int MaxCharacters = 12;

    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/character").RequireAuthorization();

        // ── Create ────────────────────────────────────────────────────────────
        group.MapPost("/", async (HttpContext http, Caller caller, Db db, ContentCache content,
                                  [FromBody] CreateRequest request) =>
        {
            Guid? accountId = await caller.AccountIdAsync(http.User, http.RequestAborted);
            if (accountId is null) return Results.Unauthorized();

            string name = (request.Name ?? "").Trim();

            if (name.Length is < 1 or > 20)
            {
                return Results.Problem(
                    title:      "invalid name",
                    detail:     "Between 1 and 20 characters.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            string classId = request.ClassId ?? "";

            // Validated against content, so a client cannot invent a class and inherit
            // whatever a missing class resolves to.
            if (!string.IsNullOrEmpty(classId) && content.Catalogue.GetClass(classId) is null)
            {
                return Results.Problem(
                    title:      "unknown class",
                    detail:     $"There is no class '{classId}'.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            // The account lock makes the count-then-insert safe: two creations racing
            // would otherwise both see eleven characters and both proceed.
            return await db.InAccountTransactionAsync(accountId.Value, async (connection, tx) =>
            {
                long living = await connection.ScalarAsync<long>(
                    "select count(*) from character where account_id = $1 and deleted_at is null;",
                    tx, accountId.Value);

                if (living >= MaxCharacters)
                {
                    return Results.Problem(
                        title:      "too many characters",
                        detail:     $"An account may hold {MaxCharacters}.",
                        statusCode: StatusCodes.Status409Conflict);
                }

                Guid characterId;

                try
                {
                    characterId = await connection.ScalarAsync<Guid>(
                        """
                        insert into character (account_id, name, class_id)
                        values ($1, $2, $3)
                        returning id;
                        """,
                        tx, accountId.Value, name, classId);
                }
                catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
                {
                    return Results.Problem(
                        title:      "name taken",
                        detail:     $"You already have a character called '{name}'.",
                        statusCode: StatusCodes.Status409Conflict);
                }

                // Idle from birth, and the row exists from birth. Settlement reads it
                // on every state read, and a null activity would be a branch on every
                // one of those paths forever.
                await connection.ExecuteAsync(
                    "insert into activity (character_id) values ($1);",
                    tx, characterId);

                return Results.Ok(new
                {
                    id      = characterId,
                    name,
                    classId,
                    xp      = 0L,
                    level   = 1,
                });
            }, http.RequestAborted);
        });

        // ── Read ──────────────────────────────────────────────────────────────
        group.MapGet("/{characterId:guid}", async (HttpContext http, Caller caller, Db db,
                                                   Guid characterId) =>
        {
            Guid? accountId = await caller.AccountIdAsync(http.User, http.RequestAborted);
            if (accountId is null) return Results.Unauthorized();

            if (!await caller.OwnsCharacterAsync(accountId.Value, characterId, http.RequestAborted))
                return NotYours();

            await using var connection = await db.OpenAsync(http.RequestAborted);

            long xp = await connection.ScalarAsync<long>(
                "select xp from character where id = $1;", null, characterId);

            var skills = new List<object>();

            await using (var command = connection.Sql(
                "select skill_id, xp from character_skill where character_id = $1 order by skill_id;",
                null, characterId))
            {
                await using var reader = await command.ExecuteReaderAsync(http.RequestAborted);

                while (await reader.ReadAsync(http.RequestAborted))
                {
                    long skillXp = reader.GetInt64(1);

                    skills.Add(new
                    {
                        skillId = reader.GetString(0),
                        xp      = skillXp,
                        level   = Levelling.SkillLevel(skillXp),
                    });
                }
            }

            var inventory = new List<object>();

            await using (var command = connection.Sql(
                """
                select slot_index, item_id, quantity
                  from inventory_slot where character_id = $1 order by slot_index;
                """,
                null, characterId))
            {
                await using var reader = await command.ExecuteReaderAsync(http.RequestAborted);

                while (await reader.ReadAsync(http.RequestAborted))
                {
                    inventory.Add(new
                    {
                        slot     = reader.GetInt32(0),
                        itemId   = reader.GetString(1),
                        quantity = reader.GetInt64(2),
                    });
                }
            }

            var equipment = new List<object>();

            await using (var command = connection.Sql(
                """
                select slot_id, item_id, durability
                  from equipment where character_id = $1 order by slot_id;
                """,
                null, characterId))
            {
                await using var reader = await command.ExecuteReaderAsync(http.RequestAborted);

                while (await reader.ReadAsync(http.RequestAborted))
                {
                    equipment.Add(new
                    {
                        slotId     = reader.GetString(0),
                        itemId     = reader.GetString(1),
                        durability = reader.GetInt32(2),
                    });
                }
            }

            var kills = new List<object>();

            await using (var command = connection.Sql(
                """
                select monster_id, active_kills, afk_kills
                  from kill_counter where character_id = $1 order by monster_id;
                """,
                null, characterId))
            {
                await using var reader = await command.ExecuteReaderAsync(http.RequestAborted);

                while (await reader.ReadAsync(http.RequestAborted))
                {
                    kills.Add(new
                    {
                        monsterId   = reader.GetString(0),
                        activeKills = reader.GetInt64(1),
                        afkKills    = reader.GetInt64(2),
                    });
                }
            }

            return Results.Ok(new
            {
                id    = characterId,
                xp,
                level = Levelling.CharacterLevel(xp),
                skills,
                inventory,
                equipment,
                kills,
            });
        });

        // ── Delete ────────────────────────────────────────────────────────────
        group.MapDelete("/{characterId:guid}", async (HttpContext http, Caller caller, Db db,
                                                      Guid characterId) =>
        {
            Guid? accountId = await caller.AccountIdAsync(http.User, http.RequestAborted);
            if (accountId is null) return Results.Unauthorized();

            if (!await caller.OwnsCharacterAsync(accountId.Value, characterId, http.RequestAborted))
                return NotYours();

            await using var connection = await db.OpenAsync(http.RequestAborted);

            // Soft, so the name is freed but the ledger rows pointing at this character
            // keep meaning something. An audit trail that says "deleted character" is
            // worth more than one with a dangling id in it.
            await connection.ExecuteAsync(
                "update character set deleted_at = now() where id = $1 and deleted_at is null;",
                null, characterId);

            return Results.Ok(new { id = characterId, deleted = true });
        });
    }

    /// <summary>
    /// The same answer for "does not exist" and "not yours".
    ///
    /// Telling those apart hands an attacker a way to enumerate other players'
    /// characters one GUID at a time.
    /// </summary>
    private static IResult NotYours() =>
        Results.Problem(
            title:      "no such character",
            detail:     "That character does not exist, or does not belong to you.",
            statusCode: StatusCodes.Status404NotFound);

    public sealed record CreateRequest(string? Name, string? ClassId);
}
