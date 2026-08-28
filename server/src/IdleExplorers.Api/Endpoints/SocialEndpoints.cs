using System.Linq;
using IdleExplorers.Api.Services;
using IdleExplorers.Api.Auth;
using IdleExplorers.Api.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace IdleExplorers.Api.Endpoints;

/// <summary>
/// Other people: where they are, who they are, and who they are standing with.
///
/// ══ WHY ONE ROUND TRIP DOES BOTH ══════════════════════════════════════════════
///
/// Reporting a position and reading everybody else's are the same request. They
/// happen at the same rate, about the same map, and splitting them would double the
/// traffic to learn the same thing a moment later.
///
/// It also removes a class of bug: a client that reported and forgot to read, or read
/// and forgot to report, would be invisible to everyone or blind to everyone, and
/// both look like "multiplayer is broken" from the inside.
///
/// ══ WHAT IS TRUSTED HERE ══════════════════════════════════════════════════════
///
/// The coordinates, and only because nothing depends on them. Position is
/// presentation in this game: no loot, no rate, no combat advantage turns on where a
/// character stands, so a client that lies about it gains nothing and the server
/// spends nothing checking. They are clamped to a believable range, which protects
/// other clients' arithmetic rather than the economy.
///
/// Everything ELSE in the answer -- name, level, class -- is read from the database,
/// never from the reporter. A client can misplace itself; it cannot award itself a
/// level in somebody else's party list.
/// </summary>
public static class SocialEndpoints
{
    /// <summary>
    /// The longest thing anybody can say.
    ///
    /// Enforced HERE, not in the input field. A client can put anything in that field
    /// and it is shown to other people; a cap the client applies protects nobody from
    /// a client that has removed it.
    /// </summary>
    public const int MaxSayLength = 140;

    /// <summary>
    /// How far back a presence poll looks for chat.
    ///
    /// Comfortably longer than the poll interval, so nothing said between two polls is
    /// missed -- and short enough that somebody arriving does not get a wall of
    /// backlog. Lines older than this are still in the table; they are simply not new.
    /// </summary>
    public const double ChatWindowSeconds = 12d;

    public static void Map(WebApplication app)
    {
        MapPresence(app);
        MapParty(app);
    }

    // ── Who is here ───────────────────────────────────────────────────────────

    private static void MapPresence(WebApplication app)
    {
        var group = app.MapGroup("/presence").RequireAuthorization();

        group.MapPost("/{characterId:guid}", async Task<IResult> (
            HttpContext http, Caller caller, Db db, IGameClock clock,
            PopulationService population, Guid characterId,
            [FromBody] PresenceRequest request) =>
        {
            Guid? accountId = await caller.AccountIdAsync(http.User, http.RequestAborted);
            if (accountId is null) return Results.Unauthorized();

            if (!await caller.OwnsCharacterAsync(accountId.Value, characterId, http.RequestAborted))
                return NotYours();

            string mapId = (request.MapId ?? "").Trim();

            if (string.IsNullOrEmpty(mapId))
            {
                return Results.Problem(
                    title:      "no map",
                    detail:     "A presence report has to say which map it is on.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            float x = IdleExplorers.Rules.Presence.Clamp(request.X);
            float z = IdleExplorers.Rules.Presence.Clamp(request.Z);

            DateTimeOffset now = await clock.NowAsync(http.RequestAborted);

            await using var connection = await db.OpenAsync(http.RequestAborted);

            await connection.ExecuteAsync(
                """
                insert into presence (character_id, account_id, map_id, x, z, updated_at)
                values ($1, $2, $3, $4, $5, $6)
                on conflict (character_id) do update
                   set map_id = excluded.map_id, x = excluded.x, z = excluded.z,
                       updated_at = excluded.updated_at;
                """,
                null, characterId, accountId.Value, mapId, x, z, now);

            // ══ SAYING SOMETHING ═════════════════════════════════════════════
            //
            // Trimmed and capped here rather than trusted. A client can put anything
            // in this field and it is shown to other people, so the length limit is
            // the server's -- a client-side cap protects nobody from a client that
            // does not have one.
            string said = (request.Say ?? "").Trim();

            if (said.Length > MaxSayLength) said = said[..MaxSayLength];

            if (said.Length > 0)
            {
                await connection.ExecuteAsync(
                    """
                    insert into chat_line (character_id, map_id, body, said_at)
                    values ($1, $2, $3, $4);
                    """,
                    null, characterId, mapId, said, now);
            }

            // ══ PRESENCE IS WHAT REMEMBERS WHERE YOU WERE ══════════════════════
            //
            // The character's last position is written HERE, from the poll that
            // already knows it, rather than by the map-entry code.
            //
            // Entry cannot do it. It runs the instant a scene finishes loading, when
            // the rig is standing on the spawn point -- so saving there wrote the
            // spawn point over the real position and then "restored" it. That bug
            // survived two attempts because both of them were in the wrong place.
            //
            // Here it is a side effect of something that happens every two seconds
            // for as long as somebody is standing there, so the last write before
            // they leave is where they actually were.
            await connection.ExecuteAsync(
                "update character set last_map_id = $2, last_x = $3, last_z = $4 where id = $1;",
                null, characterId, mapId, x, z);

            var others = new List<object>();

            // Everybody else on this map whose row is recent enough to believe. The
            // cutoff is a timestamp rather than a "disconnect" event, which is what
            // makes a closed laptop behave correctly without one.
            DateTimeOffset since = now.AddSeconds(-IdleExplorers.Rules.Presence.StaleAfterSeconds);

            await using (var command = connection.Sql(
                """
                select p.character_id, p.x, p.z,
                       c.name, c.class_id, c.xp, c.appearance::text,
                       m.party_id
                from presence p
                join character c on c.id = p.character_id
                left join party_member m on m.character_id = p.character_id
                where p.map_id = $1
                  and p.updated_at >= $2
                  and p.character_id <> $3
                  and c.deleted_at is null
                order by c.name
                limit 64;
                """,
                null, mapId, since, characterId))
            {
                await using var reader = await command.ExecuteReaderAsync(http.RequestAborted);

                while (await reader.ReadAsync(http.RequestAborted))
                {
                    long xp = reader.GetInt64(5);

                    others.Add(new
                    {
                        characterId = reader.GetGuid(0),
                        x           = reader.GetFloat(1),
                        z           = reader.GetFloat(2),
                        name        = reader.GetString(3),
                        classId     = reader.GetString(4),

                        // Derived from xp, never stored and never reported. See Levelling.
                        level       = IdleExplorers.Rules.Levelling.CharacterLevel(xp),
                        appearance  = CharacterEndpoints.AppearanceOf(reader.GetString(6)),
                        partyId     = reader.IsDBNull(7) ? (Guid?)null : reader.GetGuid(7),
                    });
                }
            }

            // ══ WHAT WAS SAID HERE RECENTLY ═══════════════════════════════════
            //
            // Everything from the last few seconds, INCLUDING the caller's own line.
            // Their own bubble is drawn from the same answer everybody else's is, so
            // there is one code path and no way for their view of a conversation to
            // drift from the view other people have of it.
            var chat = new List<object>();

            DateTimeOffset chatSince = now.AddSeconds(-ChatWindowSeconds);

            await using (var command = connection.Sql(
                """
                select c.id, c.character_id, ch.name, c.body, c.said_at
                from chat_line c
                join character ch on ch.id = c.character_id
                where c.map_id = $1 and c.said_at >= $2
                order by c.id
                limit 64;
                """,
                null, mapId, chatSince))
            {
                await using var reader = await command.ExecuteReaderAsync(http.RequestAborted);

                while (await reader.ReadAsync(http.RequestAborted))
                {
                    chat.Add(new
                    {
                        id          = reader.GetInt64(0),
                        characterId = reader.GetGuid(1),
                        name        = reader.GetString(2),
                        body        = reader.GetString(3),
                        saidAt      = reader.GetFieldValue<DateTimeOffset>(4),
                    });
                }
            }

            // ══ AND THE MONSTERS STANDING IN IT ══════════════════════════════
            //
            // On the poll that is already happening rather than on a poll of its own.
            // Position, speech and the world are one question -- "what is around me" --
            // and asking it three times a second in three requests would be three times
            // the round trips for one answer.
            //
            // Corpses come back too, with how long they have been one, so the client
            // can fade a body rather than blink it out from under a player who is
            // still swinging at it.
            var monsters = await population.RefreshAsync(connection, mapId, now, http.RequestAborted);

            return Results.Ok(new
            {
                mapId,
                others = others.ToArray(),
                chat   = chat.ToArray(),

                monsters = monsters.Select(m => new
                {
                    id          = m.Id,
                    monsterId   = m.MonsterId,
                    x           = m.X,
                    z           = m.Z,
                    health      = m.Health,
                    maxHealth   = m.MaxHealth,
                    secondsDead = m.SecondsDead,
                }).ToArray(),
            });
        });
    }

    // ── Standing together ─────────────────────────────────────────────────────

    private static void MapParty(WebApplication app)
    {
        var group = app.MapGroup("/party").RequireAuthorization();

        // What group am I in, if any.
        group.MapGet("/{characterId:guid}", async Task<IResult> (
            HttpContext http, Caller caller, Db db, Guid characterId) =>
        {
            Guid? accountId = await caller.AccountIdAsync(http.User, http.RequestAborted);
            if (accountId is null) return Results.Unauthorized();

            if (!await caller.OwnsCharacterAsync(accountId.Value, characterId, http.RequestAborted))
                return NotYours();

            await using var connection = await db.OpenAsync(http.RequestAborted);

            return Results.Ok(await DescribeAsync(connection, characterId, http.RequestAborted));
        });

        // Start one, or return the one already joined.
        group.MapPost("/{characterId:guid}", async Task<IResult> (
            HttpContext http, Caller caller, Db db, Guid characterId) =>
        {
            Guid? accountId = await caller.AccountIdAsync(http.User, http.RequestAborted);
            if (accountId is null) return Results.Unauthorized();

            if (!await caller.OwnsCharacterAsync(accountId.Value, characterId, http.RequestAborted))
                return NotYours();

            return await db.InCharacterTransactionAsync(characterId, async (connection, tx) =>
            {
                Guid? existing = await PartyOfAsync(connection, tx, characterId, http.RequestAborted);

                // Idempotent by nature rather than by key: pressing "form a group"
                // twice is one group, because the second press finds the first.
                if (existing is null)
                {
                    Guid partyId = await connection.ScalarAsync<Guid>(
                        "insert into party (leader_character_id) values ($1) returning id;",
                        tx, characterId);

                    await connection.ExecuteAsync(
                        "insert into party_member (party_id, character_id) values ($1, $2);",
                        tx, partyId, characterId);
                }

                return Results.Ok(await DescribeAsync(connection, characterId, http.RequestAborted, tx));
            }, http.RequestAborted);
        });

        // Join somebody else's.
        group.MapPost("/{characterId:guid}/join/{leaderId:guid}", async Task<IResult> (
            HttpContext http, Caller caller, Db db, Guid characterId, Guid leaderId) =>
        {
            Guid? accountId = await caller.AccountIdAsync(http.User, http.RequestAborted);
            if (accountId is null) return Results.Unauthorized();

            if (!await caller.OwnsCharacterAsync(accountId.Value, characterId, http.RequestAborted))
                return NotYours();

            if (characterId == leaderId)
            {
                return Results.Problem(
                    title:      "already yours",
                    detail:     "You cannot join your own group.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            // ══ LOCKED ON THE PARTY, NOT THE JOINER ═══════════════════════════
            //
            // Two people accepting the last seat at once must not both get it. The
            // lock has to be the thing being contended -- the party -- so it is taken
            // against the LEADER's character row, which every join to this party
            // shares. Locking the joiner's own row would let four of them past the
            // count at the same instant.
            return await db.InCharacterTransactionAsync(leaderId, async (connection, tx) =>
            {
                Guid? partyId = await PartyOfAsync(connection, tx, leaderId, http.RequestAborted);

                if (partyId is null)
                {
                    return Results.Problem(
                        title:      "no such group",
                        detail:     "That player is not in a group.",
                        statusCode: StatusCodes.Status404NotFound);
                }

                Guid? mine = await PartyOfAsync(connection, tx, characterId, http.RequestAborted);

                if (mine == partyId)
                    return Results.Ok(await DescribeAsync(connection, characterId, http.RequestAborted, tx));

                if (mine is not null)
                {
                    return Results.Problem(
                        title:      "already grouped",
                        detail:     "Leave your current group first.",
                        statusCode: StatusCodes.Status409Conflict);
                }

                long members = await connection.ScalarAsync<long>(
                    "select count(*) from party_member where party_id = $1;",
                    tx, partyId.Value);

                if (!IdleExplorers.Rules.Party.HasRoom((int)members))
                {
                    return Results.Problem(
                        title:      "group is full",
                        detail:     $"A group holds {IdleExplorers.Rules.Party.MaxMembers}.",
                        statusCode: StatusCodes.Status409Conflict);
                }

                await connection.ExecuteAsync(
                    "insert into party_member (party_id, character_id) values ($1, $2);",
                    tx, partyId.Value, characterId);

                return Results.Ok(await DescribeAsync(connection, characterId, http.RequestAborted, tx));
            }, http.RequestAborted);
        });

        // Leave. Deleting the last member takes the party with it.
        group.MapDelete("/{characterId:guid}", async Task<IResult> (
            HttpContext http, Caller caller, Db db, Guid characterId) =>
        {
            Guid? accountId = await caller.AccountIdAsync(http.User, http.RequestAborted);
            if (accountId is null) return Results.Unauthorized();

            if (!await caller.OwnsCharacterAsync(accountId.Value, characterId, http.RequestAborted))
                return NotYours();

            return await db.InCharacterTransactionAsync(characterId, async (connection, tx) =>
            {
                Guid? partyId = await PartyOfAsync(connection, tx, characterId, http.RequestAborted);

                if (partyId is null) return Results.Ok(new { partyId = (Guid?)null, members = Array.Empty<object>() });

                await connection.ExecuteAsync(
                    "delete from party_member where character_id = $1;", tx, characterId);

                long left = await connection.ScalarAsync<long>(
                    "select count(*) from party_member where party_id = $1;", tx, partyId.Value);

                // An empty party is not a party. Left behind, it would sit in the table
                // for ever and its leader could never form another one, because the
                // leader column would still point at them.
                if (left == 0L)
                {
                    await connection.ExecuteAsync(
                        "delete from party where id = $1;", tx, partyId.Value);
                }
                else
                {
                    // The leader left and somebody is still here. Hand it to whoever
                    // has been in longest, rather than dissolving a group around the
                    // people still standing in it.
                    await connection.ExecuteAsync(
                        """
                        update party
                           set leader_character_id = (
                                 select character_id from party_member
                                  where party_id = $1 order by joined_at limit 1)
                         where id = $1 and leader_character_id = $2;
                        """,
                        tx, partyId.Value, characterId);
                }

                return Results.Ok(await DescribeAsync(connection, characterId, http.RequestAborted, tx));
            }, http.RequestAborted);
        });
    }

    // ── Machinery ─────────────────────────────────────────────────────────────

    private static async Task<Guid?> PartyOfAsync(Npgsql.NpgsqlConnection connection,
                                                  Npgsql.NpgsqlTransaction? tx,
                                                  Guid characterId,
                                                  CancellationToken cancellation) =>
        await connection.ScalarAsync<Guid?>(
            "select party_id from party_member where character_id = $1;", tx, characterId);

    /// <summary>
    /// The caller's group as they should see it.
    ///
    /// Names and levels come from the character table rather than from whoever last
    /// reported a position, so a party list cannot be made to show a level nobody has.
    /// </summary>
    private static async Task<object> DescribeAsync(Npgsql.NpgsqlConnection connection,
                                                    Guid characterId,
                                                    CancellationToken cancellation,
                                                    Npgsql.NpgsqlTransaction? tx = null)
    {
        Guid? partyId = await PartyOfAsync(connection, tx, characterId, cancellation);

        if (partyId is null)
            return new { partyId = (Guid?)null, leaderCharacterId = (Guid?)null, members = Array.Empty<object>() };

        Guid leader = await connection.ScalarAsync<Guid>(
            "select leader_character_id from party where id = $1;", tx, partyId.Value);

        var members = new List<object>();

        await using var command = connection.Sql(
            """
            select c.id, c.name, c.class_id, c.xp
              from party_member m
              join character c on c.id = m.character_id
             where m.party_id = $1
             order by m.joined_at;
            """,
            tx, partyId.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellation);

        while (await reader.ReadAsync(cancellation))
        {
            long xp = reader.GetInt64(3);

            members.Add(new
            {
                characterId = reader.GetGuid(0),
                name        = reader.GetString(1),
                classId     = reader.GetString(2),
                level       = IdleExplorers.Rules.Levelling.CharacterLevel(xp),
            });
        }

        return new
        {
            partyId           = (Guid?)partyId.Value,
            leaderCharacterId = (Guid?)leader,
            members           = members.ToArray(),
            maxMembers        = IdleExplorers.Rules.Party.MaxMembers,
        };
    }

    /// <summary>The same answer for "does not exist" and "not yours". See CharacterEndpoints.</summary>
    private static IResult NotYours() =>
        Results.Problem(
            title:      "no such character",
            detail:     "That character does not exist, or does not belong to you.",
            statusCode: StatusCodes.Status404NotFound);

    /// <summary>
    /// Where I am, and optionally what I just said.
    ///
    /// Say rides along with the position because they happen at the same rate about
    /// the same map -- a separate endpoint would double the traffic to deliver a
    /// sentence a moment later, and would let a client report one without the other.
    /// </summary>
    public sealed record PresenceRequest(string? MapId, float X, float Z, string? Say);
}
