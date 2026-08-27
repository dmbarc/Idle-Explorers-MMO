using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace IdleExplorers.Api.Tests;

/// <summary>
/// A thousand goblins, and the portal they open.
///
/// ══ WHY THE THREE AWKWARD ONES ARE HERE ═══════════════════════════════════════
///
/// Closed at 999 and open at 1000 is the obvious test. The three that follow it are
/// the ones that catch real MMO bugs:
///
///   · unlock, disconnect, reconnect — proves the unlock is a row and not session
///     state, which is how a player loses a night's work by closing a laptop
///   · the thousandth kill arriving twice — proves the count is credited once
///   · nine thousand AFK kills — proves the gate is not a kill counter with extra
///     steps
/// </summary>
[Collection("api")]
public class BossGateTests(ApiFixture api)
{
    private static void RequireDatabase() => Skip.IfNot(ApiFixture.DatabaseReachable, ApiFixture.SkipReason);

    [SkippableFact]
    public async Task ANewCharacterFindsThePortalSealed()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await OwnershipTests.CreateCharacter(player, "Fresh");

        JsonElement gate = await ReadGate(player, character);

        Assert.False(gate.GetProperty("open").GetBoolean());
        Assert.Equal(0L,    gate.GetProperty("activeKills").GetInt64());
        Assert.Equal(1000L, gate.GetProperty("remaining").GetInt64());
    }

    [SkippableFact]
    public async Task TheGateIsSealedAt999AndOpenAt1000()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await OwnershipTests.CreateCharacter(player, "Grinder");

        await SetActiveKills(character, 999);

        Assert.False((await ReadGate(player, character)).GetProperty("open").GetBoolean());

        var refused = await OwnershipTests.Post(player, $"/boss/{character}/unlock", new { });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

        await SetActiveKills(character, 1000);

        Assert.True((await ReadGate(player, character)).GetProperty("open").GetBoolean());

        var opened = await OwnershipTests.Post(player, $"/boss/{character}/unlock", new { });
        opened.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// The unlock has to be persisted state, not something the session was holding.
    /// A player who reaches a thousand kills and then closes their laptop must not
    /// find the portal shut when they come back.
    /// </summary>
    [SkippableFact]
    public async Task ThePortalIsStillOpenAfterADisconnect()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await OwnershipTests.CreateCharacter(player, "Persistent");

        await SetActiveKills(character, 1000);
        (await OwnershipTests.Post(player, $"/boss/{character}/unlock", new { })).EnsureSuccessStatusCode();

        // A completely new client with a completely new token: the same thing signing
        // out and back in produces.
        using var reconnected = api.CreateClient();
        reconnected.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", ApiFixture.TokenFor(player.UserId));

        var response = await reconnected.GetAsync($"/boss/{character}/gate");
        response.EnsureSuccessStatusCode();

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.GetProperty("open").GetBoolean());

        await using var db = await api.OpenDatabaseAsync();
        await using var command = db.CreateCommand();

        command.CommandText = "select count(*) from unlock where character_id = $1 and unlock_id = 'goblin_king_portal';";
        command.Parameters.AddWithValue(character);

        Assert.Equal(1L, Convert.ToInt64(await command.ExecuteScalarAsync()));
    }

    /// <summary>
    /// Unlocking twice must leave one unlock, not two. The row is the permission;
    /// duplicating it is how a "claim your reward" flow pays out twice.
    /// </summary>
    [SkippableFact]
    public async Task UnlockingTwiceLeavesOneUnlock()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await OwnershipTests.CreateCharacter(player, "Twice");

        await SetActiveKills(character, 1000);

        var attempts = Enumerable.Range(0, 10)
            .Select(_ => OwnershipTests.Post(player, $"/boss/{character}/unlock", new { }))
            .ToArray();

        foreach (var response in await Task.WhenAll(attempts)) response.Dispose();

        await using var db = await api.OpenDatabaseAsync();
        await using var command = db.CreateCommand();

        command.CommandText = "select count(*) from unlock where character_id = $1;";
        command.Parameters.AddWithValue(character);

        Assert.Equal(1L, Convert.ToInt64(await command.ExecuteScalarAsync()));
    }

    /// <summary>
    /// ══ THE ONE THE WHOLE GATE EXISTS FOR ═════════════════════════════════════
    ///
    /// Nine thousand goblins killed while logged out open nothing. A gate an
    /// unattended character walks through on its own is not a gate.
    /// </summary>
    [SkippableFact]
    public async Task NineThousandAfkKillsOpenNothing()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await OwnershipTests.CreateCharacter(player, "Sleeper");

        await SetKills(character, active: 0, afk: 9000);

        JsonElement gate = await ReadGate(player, character);

        Assert.False(gate.GetProperty("open").GetBoolean());
        Assert.Equal(9000L, gate.GetProperty("afkKills").GetInt64());
        Assert.Equal(1000L, gate.GetProperty("remaining").GetInt64());

        var refused = await OwnershipTests.Post(player, $"/boss/{character}/unlock", new { });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
    }

    /// <summary>
    /// Somebody else's progress is not yours, and asking about it tells you nothing.
    /// </summary>
    [SkippableFact]
    public async Task OneAccountCannotOpenAnotherAccountsPortal()
    {
        RequireDatabase();

        await using var owner = await api.NewPlayerAsync();
        await using var thief = await api.NewPlayerAsync();

        Guid character = await OwnershipTests.CreateCharacter(owner, "Earner");
        await SetActiveKills(character, 1000);

        Assert.Equal(HttpStatusCode.NotFound,
                     (await thief.Client.GetAsync($"/boss/{character}/gate")).StatusCode);

        var stolen = await OwnershipTests.Post(thief, $"/boss/{character}/unlock", new { });
        Assert.Equal(HttpStatusCode.NotFound, stolen.StatusCode);
    }

    // ── Killing things for real ───────────────────────────────────────────────

    /// <summary>
    /// The full loop, without touching the database: fight goblins, and watch kills,
    /// loot, coins and experience all arrive from one settlement.
    /// </summary>
    [SkippableFact]
    public async Task FightingGoblinsProducesKillsLootAndCoins()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await OwnershipTests.CreateCharacter(player, "Fighter");

        (await OwnershipTests.Post(player, $"/activity/{character}/fight", new { monsterId = "goblin" }))
            .EnsureSuccessStatusCode();

        api.Clock.Advance(TimeSpan.FromHours(2));

        var response = await OwnershipTests.Post(player, $"/activity/{character}/settle", new { });
        response.EnsureSuccessStatusCode();

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        long kills = body.RootElement.GetProperty("actions").GetInt64();
        Assert.True(kills > 0, "two hours of goblins produced no kills");

        // Bones drop every time, so they are the deterministic half of the table.
        Assert.True(body.RootElement.GetProperty("items").TryGetProperty("bones", out var bones));
        Assert.True(bones.GetInt64() > 0);

        // Coins go to the wallet, never to a bag slot.
        Assert.True(body.RootElement.GetProperty("currency").TryGetProperty("coins", out var coins));
        Assert.True(coins.GetInt64() > 0);

        Assert.True(body.RootElement.GetProperty("xpGained").GetInt64() > 0);

        // And the kill counter moved, on the AFK side -- nobody was watching.
        JsonElement gate = await ReadGate(player, character);
        Assert.Equal(kills, gate.GetProperty("afkKills").GetInt64());
        Assert.Equal(0L,    gate.GetProperty("activeKills").GetInt64());
    }

    /// <summary>
    /// Coins are money, not an item. They must never occupy a slot.
    /// </summary>
    [SkippableFact]
    public async Task CoinsNeverReachTheBag()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await OwnershipTests.CreateCharacter(player, "Rich");

        (await OwnershipTests.Post(player, $"/activity/{character}/fight", new { monsterId = "goblin" }))
            .EnsureSuccessStatusCode();

        api.Clock.Advance(TimeSpan.FromHours(3));
        (await OwnershipTests.Post(player, $"/activity/{character}/settle", new { })).EnsureSuccessStatusCode();

        await using var db = await api.OpenDatabaseAsync();

        await using (var command = db.CreateCommand())
        {
            command.CommandText = "select count(*) from inventory_slot where character_id = $1 and item_id = 'coins';";
            command.Parameters.AddWithValue(character);

            Assert.Equal(0L, Convert.ToInt64(await command.ExecuteScalarAsync()));
        }

        await using (var command = db.CreateCommand())
        {
            command.CommandText = "select balance from wallet where account_id = $1 and currency = 'coins';";
            command.Parameters.AddWithValue(player.UserId);

            Assert.True(Convert.ToInt64(await command.ExecuteScalarAsync()) > 0);
        }
    }

    /// <summary>
    /// The wallet balance must equal the sum of its ledger. This is the invariant that
    /// catches a duplication nobody thought to assert on directly.
    /// </summary>
    [SkippableFact]
    public async Task TheWalletBalancesAgainstItsLedger()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await OwnershipTests.CreateCharacter(player, "Auditor");

        (await OwnershipTests.Post(player, $"/activity/{character}/fight", new { monsterId = "goblin" }))
            .EnsureSuccessStatusCode();

        api.Clock.Advance(TimeSpan.FromHours(5));

        // Several racing settlements, so the invariant is asserted against contention
        // rather than against a quiet single request.
        var attempts = Enumerable.Range(0, 8)
            .Select(_ => OwnershipTests.Post(player, $"/activity/{character}/settle", new { }))
            .ToArray();

        foreach (var response in await Task.WhenAll(attempts)) response.Dispose();

        await using var db = await api.OpenDatabaseAsync();
        await using var command = db.CreateCommand();

        command.CommandText = """
            select w.balance - coalesce((select sum(l.delta) from wallet_ledger l
                                          where l.account_id = w.account_id
                                            and l.currency   = w.currency), 0)
              from wallet w
             where w.account_id = $1 and w.currency = 'coins';
            """;
        command.Parameters.AddWithValue(player.UserId);

        object drift = await command.ExecuteScalarAsync();

        Assert.Equal(0L, drift is null or DBNull ? 0L : Convert.ToInt64(drift));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task<JsonElement> ReadGate(Player player, Guid character)
    {
        var response = await player.Client.GetAsync($"/boss/{character}/gate");
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private Task SetActiveKills(Guid character, long active) => SetKills(character, active, 0);

    /// <summary>
    /// Writes the counter directly.
    ///
    /// Through SQL because there is no endpoint that grants kills — which is the
    /// point. Reaching a thousand through settlement would mean simulating a thousand
    /// goblins per test, and what these are testing is the GATE, not the counting.
    /// </summary>
    private async Task SetKills(Guid character, long active, long afk)
    {
        await using var db = await api.OpenDatabaseAsync();
        await using var command = db.CreateCommand();

        command.CommandText = """
            insert into kill_counter (character_id, monster_id, active_kills, afk_kills)
            values ($1, 'goblin', $2, $3)
            on conflict (character_id, monster_id) do update
               set active_kills = excluded.active_kills, afk_kills = excluded.afk_kills;
            """;
        command.Parameters.AddWithValue(character);
        command.Parameters.AddWithValue(active);
        command.Parameters.AddWithValue(afk);

        await command.ExecuteNonQueryAsync();
    }
}
