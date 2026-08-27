using System;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Npgsql;
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

        command.CommandText = "select count(*) from telemetry_event where character_id = $1;";
        command.Parameters.AddWithValue(character);

        Assert.Equal(0L, Convert.ToInt64(await command.ExecuteScalarAsync()));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Task<System.Net.Http.HttpResponseMessage> Send(Player player, object batch) =>
        OwnershipTests.Post(player, "/telemetry/", batch);

    private static async Task<long> CountEvents(NpgsqlConnection db, Guid accountId)
    {
        await using var command = db.CreateCommand();

        command.CommandText = "select count(*) from telemetry_event where account_id = $1;";
        command.Parameters.AddWithValue(accountId);

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
