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
                                 IGameClock clock,
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
                        insert into character (account_id, name, class_id, appearance)
                        values ($1, $2, $3, $4::jsonb)
                        returning id;
                        """,
                        tx, accountId.Value, name, classId,
                        System.Text.Json.JsonSerializer.Serialize(
                            request.Appearance ?? new SpumSaveData(),
                            AppearanceJson));
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

                // The funnel starts here, and only the server can say it started. In
                // the same transaction as the insert, so a rolled-back creation cannot
                // leave a character in the funnel that no table has ever heard of.
                await TelemetryEndpoints.RecordAsync(
                    connection, tx, accountId.Value, characterId,
                    TelemetryEvents.CharacterCreated, await clock.NowAsync(http.RequestAborted),
                    ("classId", classId));

                // characterId, not id. The client deserialises this with JsonUtility,
                // which matches on field NAME and has no way to be told otherwise --
                // no attribute, no resolver. A mismatch is not an error there: the
                // field is simply left at its default, so the caller receives a
                // character whose id is the empty string and treats the creation as
                // having failed. See the note on the roster in AccountEndpoints.
                return Results.Ok(new
                {
                    characterId,
                    name,
                    classId,
                    xp          = 0L,
                    level       = 1,
                    appearance  = request.Appearance,
                    lastMapId   = "",
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

            string appearanceJson = await connection.ScalarAsync<string>(
                "select appearance::text from character where id = $1;",
                null, characterId) ?? "{}";

            string lastMapId = await connection.ScalarAsync<string>(
                "select last_map_id from character where id = $1;",
                null, characterId) ?? "";

            float lastX = await connection.ScalarAsync<float>(
                "select last_x from character where id = $1;", null, characterId);

            float lastZ = await connection.ScalarAsync<float>(
                "select last_z from character where id = $1;", null, characterId);

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
                characterId,
                xp,
                level = Levelling.CharacterLevel(xp),
                appearance = AppearanceOf(appearanceJson),
                lastMapId,
                lastX,
                lastZ,
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

            return Results.Ok(new { characterId, deleted = true });
        });

        // ── What they look like ───────────────────────────────────────────────
        //
        // Stored, never interpreted. This is the one thing the server holds that it
        // has no opinion about: it exists here so a face survives a character select
        // and follows the player to another machine, not because anybody could cheat
        // by having nicer hair.
        group.MapPut("/{characterId:guid}/appearance", async (
            HttpContext http, Caller caller, Db db, Guid characterId,
            [FromBody] AppearanceRequest request) =>
        {
            Guid? accountId = await caller.AccountIdAsync(http.User, http.RequestAborted);
            if (accountId is null) return Results.Unauthorized();

            if (!await caller.OwnsCharacterAsync(accountId.Value, characterId, http.RequestAborted))
                return NotYours();

            await using var connection = await db.OpenAsync(http.RequestAborted);

            await connection.ExecuteAsync(
                "update character set appearance = $2::jsonb where id = $1;",
                null, characterId,
                System.Text.Json.JsonSerializer.Serialize(
                    request.Appearance ?? new SpumSaveData(), AppearanceJson));

            return Results.Ok(new { characterId, appearance = request.Appearance });
        });

        // ── Where they are ────────────────────────────────────────────────────
        //
        // ══ WHY THE MAP IS CHECKED AND THE HAIRSTYLE IS NOT ═════════════
        //
        // Because the map decides what a character can gather and fight when they
        // next log in. An unvalidated map id is a client choosing to wake up in a zone
        // it has not unlocked, which is a progression skip rather than a cosmetic one.
        group.MapPut("/{characterId:guid}/location", async (
            HttpContext http, Caller caller, Db db, ContentCache content, Guid characterId,
            [FromBody] LocationRequest request) =>
        {
            Guid? accountId = await caller.AccountIdAsync(http.User, http.RequestAborted);
            if (accountId is null) return Results.Unauthorized();

            if (!await caller.OwnsCharacterAsync(accountId.Value, characterId, http.RequestAborted))
                return NotYours();

            string mapId = (request.MapId ?? "").Trim();

            // GetMap, not GetZone. A zone is a REGION -- verdant_wilds -- and the maps
            // inside it are what a character stands in: goblin_camp is a map of the
            // verdant_wilds zone. Validating against zones refused every real location,
            // including the starting map, which the paired test caught only because it
            // asserts a good value is ACCEPTED as well as a bad one refused.
            if (content.Catalogue.GetMap(mapId) is null)
            {
                return Results.Problem(
                    title:      "unknown map",
                    detail:     $"There is no map '{mapId}'.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            await using var connection = await db.OpenAsync(http.RequestAborted);

            // Clamped with the same rule presence uses. Position is presentation and
            // is not validated -- this is about keeping a broken float out of a column
            // other people's arithmetic will read, not about cheating.
            float x = IdleExplorers.Rules.Presence.Clamp(request.X);
            float z = IdleExplorers.Rules.Presence.Clamp(request.Z);

            await connection.ExecuteAsync(
                "update character set last_map_id = $2, last_x = $3, last_z = $4 where id = $1;",
                null, characterId, mapId, x, z);

            return Results.Ok(new { characterId, lastMapId = mapId, lastX = x, lastZ = z });
        });
    }

    /// <summary>
    /// Appearance as an object rather than a string of JSON.
    ///
    /// Returned parsed so the client reads a nested object, which is what JsonUtility
    /// can deserialise. Handed back as text it would need a second parse the client
    /// has no reason to know about.
    /// </summary>
    internal static SpumSaveData AppearanceOf(string json)
    {
        try
        {
            return System.Text.Json.JsonSerializer
                       .Deserialize<SpumSaveData>(json, AppearanceJson)
                   ?? new SpumSaveData();
        }
        catch (System.Text.Json.JsonException)
        {
            // A row written by an older shape. A default face beats a 500.
            return new SpumSaveData();
        }
    }

    /// <summary>
    /// Fields, because SpumSaveData is made of them.
    ///
    /// The global options do this too, but this serialiser is called directly rather
    /// than through the pipeline -- and System.Text.Json silently writes {} for a
    /// type of pure fields without it, which would store an empty face and look like
    /// the bug this column was added to fix.
    /// </summary>
    private static readonly System.Text.Json.JsonSerializerOptions AppearanceJson =
        new(System.Text.Json.JsonSerializerDefaults.Web)
        {
            IncludeFields = true,

            // SpumSaveData carries a computed IsEmpty. Without this it is written into
            // the stored document as though it were data, which is noise in the column
            // and a field the client would have to be told to ignore.
            IgnoreReadOnlyProperties = true,
        };

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

    public sealed record CreateRequest(string? Name, string? ClassId,
                                       SpumSaveData? Appearance);

    /// <summary>What the character looks like. Stored as given; the server reads no field of it.</summary>
    public sealed record AppearanceRequest(SpumSaveData? Appearance);

    /// <summary>Where the character is. Written on map change and on leaving the world.</summary>
    public sealed record LocationRequest(string? MapId, float X, float Z);
}
