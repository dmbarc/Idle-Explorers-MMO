using System;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using IdleExplorers.Api.Infrastructure;
using Xunit;

namespace IdleExplorers.Api.Tests;

/// <summary>
/// Turning a feature off without shipping a build.
///
/// ══ THE TWO CLAIMS WORTH TESTING ══════════════════════════════════════════════
///
///   1. A flag nobody has inserted means ON. Otherwise adding a flag check to a
///      handler silently disables the feature the moment it deploys, before anybody
///      has had a chance to insert the row -- and the deploy looks fine.
///
///   2. The SERVER refuses, not the client. The copy in the bootstrap payload exists
///      so a disabled feature is hidden rather than visible-and-broken; a client that
///      ignores it must still be refused.
///
/// The second is the one that matters. A flag enforced only in the UI is a suggestion.
/// </summary>
[Collection("api")]
public class FeatureFlagTests(ApiFixture api) : IAsyncLifetime
{
    private static void RequireDatabase() => Skip.IfNot(ApiFixture.DatabaseReachable, ApiFixture.SkipReason);

    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>
    /// Every test here leaves the table as it found it.
    ///
    /// The flags are process-wide state shared with every other test in the
    /// collection, so a test that turned the boss off and crashed would fail the
    /// encounter suite with a message about a 503 -- which is a morning wasted.
    /// </summary>
    public async Task DisposeAsync()
    {
        if (!ApiFixture.DatabaseReachable) return;

        await using var db = await api.OpenDatabaseAsync();
        await using var command = db.CreateCommand();

        command.CommandText = "delete from feature_flag;";
        await command.ExecuteNonQueryAsync();

        api.Flags.Forget();
    }

    // ══ DEFAULTS ══════════════════════════════════════════════════════════════

    [SkippableFact]
    public async Task AFlagNobodyHasInsertedIsOn()
    {
        RequireDatabase();

        await ClearAsync();

        Assert.True(await api.Flags.IsEnabledAsync(FeatureFlags.GoblinKing));
        Assert.True(await api.Flags.IsEnabledAsync(FeatureFlags.Minigames));

        // Including a flag nobody has ever heard of, which is the same claim: absence
        // is "nobody turned this off".
        Assert.True(await api.Flags.IsEnabledAsync("a_flag_that_does_not_exist"));
    }

    [SkippableFact]
    public async Task EveryKnownFlagAppearsInTheBootstrapPayload()
    {
        RequireDatabase();

        await ClearAsync();

        await using var player = await api.NewPlayerAsync();

        JsonElement account = await Bootstrap(player);
        JsonElement flags   = account.GetProperty("flags");

        // A stable shape whether or not anybody has inserted a row, so the client can
        // rely on a flag being present rather than treating missing as a third state.
        foreach (string known in FeatureFlags.Known)
        {
            JsonElement row = flags.EnumerateArray()
                                   .First(f => f.GetProperty("flag").GetString() == known);

            Assert.True(row.GetProperty("enabled").GetBoolean());
        }
    }

    [SkippableFact]
    public async Task TheClientIsToldWhenSomethingIsOff()
    {
        RequireDatabase();

        await SetAsync(FeatureFlags.Minigames, enabled: false);

        await using var player = await api.NewPlayerAsync();

        JsonElement flags = (await Bootstrap(player)).GetProperty("flags");

        JsonElement row = flags.EnumerateArray()
                               .First(f => f.GetProperty("flag").GetString() == FeatureFlags.Minigames);

        Assert.False(row.GetProperty("enabled").GetBoolean());
    }

    // ══ ENFORCEMENT ═══════════════════════════════════════════════════════════

    [SkippableFact]
    public async Task ADisabledMinigameIsRefusedByTheServer()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await OwnershipTests.CreateCharacter(player, "Tapper");

        // On: accepted.
        (await Report(player, character)).EnsureSuccessStatusCode();

        await SetAsync(FeatureFlags.Minigames, enabled: false);

        var refused = await Report(player, character);

        // 503, not 403: nothing is wrong with this player or this request, and a
        // client treating it as permanent would need a restart to notice the flag
        // coming back.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);

        await SetAsync(FeatureFlags.Minigames, enabled: true);

        (await Report(player, character)).EnsureSuccessStatusCode();
    }

    [SkippableFact]
    public async Task ADisabledBossCannotBeEngaged()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await OwnershipTests.CreateCharacter(player, "Hopeful");

        await OpenThePortal(character);

        (await OwnershipTests.Post(player, $"/boss/{character}/unlock", new { }))
            .EnsureSuccessStatusCode();

        await SetAsync(FeatureFlags.GoblinKing, enabled: false);

        var refused = await OwnershipTests.Post(player, $"/encounter/{character}",
                                                new { monsterId = "goblin_king" });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);

        await using var db = await api.OpenDatabaseAsync();
        await using var command = db.CreateCommand();

        // Refused BEFORE the database was touched, so a disabled boss leaves no
        // half-started encounter to trip the one-live-fight index later.
        command.CommandText = "select count(*) from encounter where character_id = $1;";
        command.Parameters.AddWithValue(character);

        Assert.Equal(0L, Convert.ToInt64(await command.ExecuteScalarAsync()));
    }

    [SkippableFact]
    public async Task DisabledTelemetryIngestSucceedsQuietlyRatherThanErroring()
    {
        RequireDatabase();

        await SetAsync(FeatureFlags.TelemetryIngest, enabled: false);

        await using var player = await api.NewPlayerAsync();

        var response = await OwnershipTests.Post(player, "/telemetry/", new
        {
            events = new[] { new { @event = "session_start", characterId = "", payload = new object[0] } },
        });

        // ══ WHY THIS ONE IS NOT A REFUSAL ═════════════════════════════════════
        //
        // Telemetry is not something a player asked for. A 503 here would make a
        // client show an error, or worse, retry -- turning a valve into a storm.
        response.EnsureSuccessStatusCode();

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(0, body.RootElement.GetProperty("accepted").GetInt32());
    }

    [SkippableFact]
    public async Task TurningOneFlagOffLeavesTheOthersAlone()
    {
        RequireDatabase();

        await SetAsync(FeatureFlags.Shop, enabled: false);

        // The point of per-feature flags rather than a maintenance mode: the thing
        // that misbehaves goes off, and the rest of the game keeps running.
        Assert.False(await api.Flags.IsEnabledAsync(FeatureFlags.Shop));
        Assert.True(await api.Flags.IsEnabledAsync(FeatureFlags.GoblinKing));
        Assert.True(await api.Flags.IsEnabledAsync(FeatureFlags.Minigames));
    }

    // ── Poking the table ──────────────────────────────────────────────────────

    /// <summary>
    /// Writes a flag and drops the cache, so a test does not wait ten seconds.
    ///
    /// Forgetting the cache rather than shortening it, because the cache duration is
    /// a production decision and a test that needs it changed is a test measuring the
    /// wrong thing.
    /// </summary>
    private async Task SetAsync(string flag, bool enabled)
    {
        await using var db = await api.OpenDatabaseAsync();
        await using var command = db.CreateCommand();

        command.CommandText = """
            insert into feature_flag (flag, enabled) values ($1, $2)
            on conflict (flag) do update set enabled = excluded.enabled, updated_at = now();
            """;
        command.Parameters.AddWithValue(flag);
        command.Parameters.AddWithValue(enabled);

        await command.ExecuteNonQueryAsync();

        api.Flags.Forget();
    }

    private async Task ClearAsync()
    {
        await using var db = await api.OpenDatabaseAsync();
        await using var command = db.CreateCommand();

        command.CommandText = "delete from feature_flag;";
        await command.ExecuteNonQueryAsync();

        api.Flags.Forget();
    }

    private async Task OpenThePortal(Guid character)
    {
        await using var db = await api.OpenDatabaseAsync();
        await using var command = db.CreateCommand();

        command.CommandText = """
            insert into kill_counter (character_id, monster_id, active_kills)
            values ($1, 'goblin', 1000)
            on conflict (character_id, monster_id) do update set active_kills = 1000;
            """;
        command.Parameters.AddWithValue(character);

        await command.ExecuteNonQueryAsync();
    }

    private static Task<System.Net.Http.HttpResponseMessage> Report(Player player, Guid character) =>
        OwnershipTests.Post(player, $"/activity/{character}/minigame",
                            new { grades = new[] { "good" } });

    private static async Task<JsonElement> Bootstrap(Player player)
    {
        var response = await player.Client.GetAsync("/account/");
        response.EnsureSuccessStatusCode();

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }
}
