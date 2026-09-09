using System;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Npgsql;
using IdleExplorers.Rules;
using Xunit;

namespace IdleExplorers.Api.Tests;

/// <summary>
/// Measuring the game without letting the game lie about itself.
///
/// The distinction these tests exist to hold: a client may report what it DID -- it
/// opened a screen, it entered a map -- and may never report what it EARNED. The
/// server already knows what it granted, and a funnel built on claims measures how
/// players' clients behave rather than how players do.
/// </summary>
[Collection("api")]
public class TelemetryTests(ApiFixture api)
{
    private static void RequireDatabase() => Skip.IfNot(ApiFixture.DatabaseReachable, ApiFixture.SkipReason);

    [SkippableFact]
    public async Task AClientMayReportItsOwnBehaviour()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await OwnershipTests.CreateCharacter(player, "Chatty");

        var response = await Send(player, new
        {
            events = new[]
            {
                new { @event = "session_start", characterId = character.ToString(),
                      payload = new[] { new { k = "platform", v = "webgl" } } },
                new { @event = "map_enter", characterId = character.ToString(),
                      payload = new[] { new { k = "mapId", v = "goblin_camp" } } },
            },
        });

        response.EnsureSuccessStatusCode();

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(2, body.RootElement.GetProperty("accepted").GetInt32());
        Assert.Equal(0, body.RootElement.GetProperty("rejected").GetInt32());

        await using var db = await api.OpenDatabaseAsync();
        Assert.Equal(2L, await CountEvents(db, player.UserId));
    }

    /// <summary>
    /// ══ THE ONE THAT MATTERS ══════════════════════════════════════════════════
    ///
    /// A client claiming a reward. It is refused, and the attempt is itself recorded
    /// — because a client sending events it should not know about is either a bug
    /// worth finding or somebody seeing what the endpoint accepts.
    /// </summary>
    [SkippableFact]
    public async Task AClientMayNotReportWhatItEarned()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await OwnershipTests.CreateCharacter(player, "Fabricator");

        var response = await Send(player, new
        {
            events = new[]
            {
                new { @event = "boss_defeated", characterId = character.ToString(),
                      payload = new[] { new { k = "xp", v = "4000" } } },
                new { @event = "afk_claim", characterId = character.ToString(),
                      payload = new[] { new { k = "coins", v = "999999" } } },
            },
        });

        response.EnsureSuccessStatusCode();

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(0, body.RootElement.GetProperty("accepted").GetInt32());
        Assert.Equal(2, body.RootElement.GetProperty("rejected").GetInt32());

        await using var db = await api.OpenDatabaseAsync();

        Assert.Equal(0L, await CountEvents(db, player.UserId));
        Assert.Equal(2L, await CountSecurity(db, player.UserId, "telemetry_not_reportable"));

        // The count above excludes server-emitted events, so it would also read zero if
        // the exclusion were simply swallowing everything. This proves it is not: the
        // one row the server DID write is still there.
        Assert.Equal(1L, await CountNamed(db, player.UserId, TelemetryEvents.CharacterCreated));
    }

    /// <summary>
    /// Somebody else's character id in a report would poison their funnel with
    /// activity they never had.
    /// </summary>
    [SkippableFact]
    public async Task AnotherPlayersCharacterIsNotAttributed()
    {
        RequireDatabase();

        await using var owner    = await api.NewPlayerAsync();
        await using var reporter = await api.NewPlayerAsync();

        Guid victim = await OwnershipTests.CreateCharacter(owner, "Victim");

        var response = await Send(reporter, new
        {
            events = new[]
            {
                new { @event = "map_enter", characterId = victim.ToString(),
                      payload = new[] { new { k = "mapId", v = "goblin_camp" } } },
            },
        });

        response.EnsureSuccessStatusCode();

        await using var db = await api.OpenDatabaseAsync();

        // Recorded against the REPORTER, with no character attached: the event is
        // real, the attribution is not.
        await using var command = db.CreateCommand();

        command.CommandText = """
            select count(*) from telemetry_event
             where account_id = $1 and character_id is null;
            """;
        command.Parameters.AddWithValue(reporter.UserId);

        Assert.Equal(1L, Convert.ToInt64(await command.ExecuteScalarAsync()));
        Assert.Equal(0L, await CountEvents(db, owner.UserId));
    }

    [SkippableFact]
    public async Task AnUnboundedBatchIsRefused()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();

        var flood = new object[500];

        for (int i = 0; i < flood.Length; i++)
            flood[i] = new { @event = "screen_open", characterId = "", payload = new object[0] };

        var response = await Send(player, new { events = flood });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        await using var db = await api.OpenDatabaseAsync();
        Assert.Equal(0L, await CountEvents(db, player.UserId));
    }

    [SkippableFact]
    public async Task AnEmptyBatchIsFine()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();

        var response = await Send(player, new { events = new object[0] });
        response.EnsureSuccessStatusCode();
    }

    // ── What the server reports about itself ──────────────────────────────────

    /// <summary>
    /// The server emits what it granted, at the moment it grants it. That is the only
    /// moment the report can be true.
    /// </summary>
    [SkippableFact]
    public async Task TheServerRecordsWhatItPaidOut()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await OwnershipTests.CreateCharacter(player, "Miner");

        (await OwnershipTests.Post(player, $"/activity/{character}", new { nodeId = "tin_rock_1" }))
            .EnsureSuccessStatusCode();

        api.Clock.Advance(TimeSpan.FromHours(2));

        (await OwnershipTests.Post(player, $"/activity/{character}/settle", new { }))
            .EnsureSuccessStatusCode();

        await using var db = await api.OpenDatabaseAsync();
        await using var command = db.CreateCommand();

        command.CommandText = """
            select payload->>'kind', (payload->>'actions')::bigint
              from telemetry_event
             where character_id = $1 and event = 'settled'
             order by id desc limit 1;
            """;
        command.Parameters.AddWithValue(character);

        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync(), "the settlement recorded no telemetry");

        Assert.Equal("gather", reader.GetString(0));

        // Two hours at the tin rock's offline rate: 1,440.
        Assert.Equal(1440L, reader.GetInt64(1));
    }

    /// <summary>
    /// An idle character closing its window every few seconds would be most of the
    /// table, and none of those rows would answer a question anybody has.
    /// </summary>
    [SkippableFact]
    public async Task NothingIsRecordedForAWindowThatPaidNothing()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await OwnershipTests.CreateCharacter(player, "Idle");

        // Never told to do anything, so every settle is a no-op.
        for (int i = 0; i < 5; i++)
        {
            api.Clock.Advance(TimeSpan.FromSeconds(10));

            (await OwnershipTests.Post(player, $"/activity/{character}/settle", new { }))
                .EnsureSuccessStatusCode();
        }

        await using var db = await api.OpenDatabaseAsync();
        await using var command = db.CreateCommand();

        // Named, rather than "no rows at all". The character has a char_create row by
        // construction, and a test that counted every row would be asserting that
        // creating a character records nothing -- which is the opposite of the truth.
        command.CommandText = """
            select count(*) from telemetry_event where character_id = $1 and event = $2;
            """;

        command.Parameters.AddWithValue(character);
        command.Parameters.AddWithValue(TelemetryEvents.Settled);

        Assert.Equal(0L, Convert.ToInt64(await command.ExecuteScalarAsync()));
    }

    // ══ WHAT ONLY THE SERVER MAY SAY ══════════════════════════════════════════
    //
    // These are the funnel. Every one of them is a claim about state -- a character
    // exists, a portal opened, a minigame paid -- and a client that could assert any
    // of them could write itself into a cohort it never reached.

    [SkippableFact]
    public async Task CreatingACharacterStartsTheFunnel()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();

        await using (var empty = await api.OpenDatabaseAsync())
            Assert.Equal(0L, await CountNamed(empty, player.UserId, TelemetryEvents.CharacterCreated));

        Guid character = await OwnershipTests.CreateCharacter(player, "Founder");

        await using var db = await api.OpenDatabaseAsync();

        Assert.Equal(1L, await CountNamed(db, player.UserId, TelemetryEvents.CharacterCreated));

        // Attributed to the character, not just the account. The funnel counts distinct
        // characters, so an unattributed row would make every stage read one player.
        await using var command = db.CreateCommand();

        command.CommandText = """
            select payload ->> 'classId' from telemetry_event
             where character_id = $1 and event = $2;
            """;

        command.Parameters.AddWithValue(character);
        command.Parameters.AddWithValue(TelemetryEvents.CharacterCreated);

        object classId = await command.ExecuteScalarAsync();

        Assert.NotNull(classId);
    }

    [SkippableFact]
    public async Task ANameAlreadyTakenRecordsNoCreation()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();

        await OwnershipTests.CreateCharacter(player, "Twin");

        var refused = await OwnershipTests.Post(player, "/character/",
                                                new { name = "Twin", classId = "" });

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

        await using var db = await api.OpenDatabaseAsync();

        // One, not two. The event is written inside the creating transaction, so a
        // refusal rolls it back with the insert it describes -- a funnel counting
        // attempts rather than characters would over-report every stage.
        Assert.Equal(1L, await CountNamed(db, player.UserId, TelemetryEvents.CharacterCreated));
    }

    [SkippableFact]
    public async Task AMinigameReportIsRecordedEvenWhenItPaysNothing()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await OwnershipTests.CreateCharacter(player, "Tapper");

        // No activity, so there are no actions for the grades to attach to and the
        // bonus is zero. That is exactly the case worth recording: a client reporting
        // grades against nothing is either a UI bug or somebody probing the endpoint.
        var response = await OwnershipTests.Post(player, $"/activity/{character}/minigame",
                                                 new { grades = new[] { "perfect", "perfect", "miss" } });

        response.EnsureSuccessStatusCode();

        await using var db = await api.OpenDatabaseAsync();

        Assert.Equal(1L, await CountNamed(db, player.UserId, TelemetryEvents.MinigameGraded));

        await using var command = db.CreateCommand();

        command.CommandText = """
            select payload ->> 'graded', payload ->> 'actions',
                   payload ->> 'perfect', payload ->> 'bonusActions'
              from telemetry_event
             where character_id = $1 and event = $2;
            """;

        command.Parameters.AddWithValue(character);
        command.Parameters.AddWithValue(TelemetryEvents.MinigameGraded);

        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());

        Assert.Equal("3", reader.GetString(0));
        Assert.Equal("0", reader.GetString(1));
        Assert.Equal("2", reader.GetString(2));

        // The ratio is the anti-cheat signal: three grades against zero actions pays
        // nothing, and the row is what makes that visible in aggregate.
        Assert.Equal("0", reader.GetString(3));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Task<System.Net.Http.HttpResponseMessage> Send(Player player, object batch) =>
        OwnershipTests.Post(player, "/telemetry/", batch);

    /// <summary>
    /// Events the CLIENT got into the table.
    ///
    /// ══ WHY THE SERVER'S OWN ARE EXCLUDED ═════════════════════════════════════
    ///
    /// Creating a character emits char_create, settling a paying window emits settled,
    /// and opening the portal emits boss_unlock -- none of which any test here asked
    /// for. Counting every row would make each of these tests assert a total that
    /// changes whenever the server learns to record something new, and the failure
    /// would read as "the ingest endpoint accepted an extra event" when it did not.
    ///
    /// The excluded names come from the shared constants rather than string literals,
    /// so a new server-emitted event cannot quietly start counting as a client one.
    /// </summary>
    private static async Task<long> CountEvents(NpgsqlConnection db, Guid accountId)
    {
        await using var command = db.CreateCommand();

        command.CommandText = """
            select count(*) from telemetry_event
             where account_id = $1 and not (event = any($2));
            """;

        command.Parameters.AddWithValue(accountId);
        command.Parameters.AddWithValue(ServerEmitted);

        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    /// <summary>
    /// Everything only the server may write. Never accepted from a client -- see the
    /// allowlist in TelemetryEndpoints, which is the enforcement; this is the mirror
    /// the tests count against.
    /// </summary>
    private static readonly string[] ServerEmitted =
    {
        TelemetryEvents.CharacterCreated,
        TelemetryEvents.Settled,
        TelemetryEvents.BossUnlocked,
        TelemetryEvents.BossEngaged,
        TelemetryEvents.BossEnded,
        TelemetryEvents.MinigameGraded,
    };

    private static async Task<long> CountNamed(NpgsqlConnection db, Guid accountId, string eventName)
    {
        await using var command = db.CreateCommand();

        command.CommandText =
            "select count(*) from telemetry_event where account_id = $1 and event = $2;";

        command.Parameters.AddWithValue(accountId);
        command.Parameters.AddWithValue(eventName);

        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<long> CountSecurity(NpgsqlConnection db, Guid accountId, string kind)
    {
        await using var command = db.CreateCommand();

        command.CommandText = "select count(*) from security_event where account_id = $1 and kind = $2;";
        command.Parameters.AddWithValue(accountId);
        command.Parameters.AddWithValue(kind);

        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}
