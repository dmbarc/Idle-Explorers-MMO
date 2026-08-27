using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Npgsql;
using Xunit;

namespace IdleExplorers.Api.Tests;

/// <summary>
/// Attacking the server on purpose.
///
/// ══ WHY THIS IS A TEST PROJECT AND NOT AN EXERCISE ════════════════════════════
///
/// A security review is a thing somebody did once. These run on every commit, for
/// the life of the project, and they fail the build — which is the only way an
/// anti-cheat property survives a year of feature work by people who were not in the
/// room when it was decided.
///
/// The acceptance criterion is not "the attack fails". It is: **every exploit either
/// fails, or produces exactly the legitimate result.** A duplicate craft that
/// succeeds and makes one item is a pass. A duplicate craft that is refused is a
/// pass. A duplicate craft that makes two items is the bug.
///
/// ══ WHAT IS NOT YET COVERED ═══════════════════════════════════════════════════
///
/// Boss engagement, loot claims and store receipts have no endpoints yet, so they
/// have no tests here rather than tests that pass vacuously. A test asserting that a
/// route which does not exist returns 404 proves nothing about the route that will.
/// They land with the endpoints.
/// </summary>
[Collection("api")]
public class AbuseTests(ApiFixture api)
{
    private static void RequireDatabase() => Skip.IfNot(ApiFixture.DatabaseReachable, ApiFixture.SkipReason);

    private const string TinRock = "tin_rock_1";

    // ══ Give myself things ════════════════════════════════════════════════════

    /// <summary>
    /// There is no endpoint that accepts a balance, so the attack is not "try to
    /// forge one" — it is "confirm no write path exists". Every route that mutates
    /// currency does so as a consequence of something the server decided.
    /// </summary>
    [SkippableFact]
    public async Task ThereIsNoRouteThatAcceptsACurrencyAmount()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await OwnershipTests.CreateCharacter(player, "Cheat");

        string[] guesses =
        [
            "/account/wallet", "/account/coins", "/account/balance",
            $"/character/{character}/wallet", $"/character/{character}/currency",
            "/wallet", "/wallet/credit", "/admin/grant",
        ];

        foreach (string path in guesses)
        {
            var response = await OwnershipTests.Post(player, path,
                new { currency = "relic_coins", amount = 1_000_000, balance = 1_000_000 });

            Assert.True(response.StatusCode is HttpStatusCode.NotFound
                                            or HttpStatusCode.MethodNotAllowed,
                        $"'{path}' answered {(int)response.StatusCode}");
        }

        Assert.Equal(0L, await WalletBalance(player.UserId, "relic_coins"));
        Assert.Equal(0L, await WalletBalance(player.UserId, "coins"));
    }

    [SkippableFact]
    public async Task ThereIsNoRouteThatAcceptsAnItemGrant()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await OwnershipTests.CreateCharacter(player, "Cheat");

        string[] guesses =
        [
            $"/character/{character}/inventory", $"/character/{character}/items",
            $"/character/{character}/give", "/inventory/add",
        ];

        foreach (string path in guesses)
        {
            var response = await OwnershipTests.Post(player, path,
                new { itemId = "goblin_destroyer", quantity = 99 });

            Assert.True(response.StatusCode is HttpStatusCode.NotFound
                                            or HttpStatusCode.MethodNotAllowed,
                        $"'{path}' answered {(int)response.StatusCode}");
        }

        Assert.Equal(0L, await ItemCount(character));
    }

    /// <summary>
    /// Extra fields in a legitimate request. Model binding is a real attack surface:
    /// a DTO that happened to carry a Quantity would bind it, and nothing would look
    /// wrong.
    /// </summary>
    [SkippableFact]
    public async Task ExtraFieldsInARealRequestAreIgnored()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await OwnershipTests.CreateCharacter(player, "Cheat");

        var response = await OwnershipTests.Post(player, $"/activity/{character}", new
        {
            nodeId           = TinRock,
            // Everything below is invented by the attacker.
            secondsPerAction = 0.0001,
            activeRateMulti  = 10000,
            afkRateMulti     = 10000,
            xpPerAction      = 999999,
            specialChance    = 1.0,
            quantity         = 1_000_000,
        });

        response.EnsureSuccessStatusCode();

        // The stored rates are the node's, not the request's.
        await using var db = await api.OpenDatabaseAsync();
        await using var command = db.CreateCommand();

        command.CommandText = """
            select seconds_per_action, active_rate_multi, afk_rate_multi, xp_per_action, special_chance
              from activity where character_id = $1;
            """;
        command.Parameters.AddWithValue(character);

        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();

        Assert.Equal(3f,    reader.GetFloat(0), 2);   // the tin rock's authored figures
        Assert.Equal(1f,    reader.GetFloat(1), 2);
        Assert.Equal(0.6f,  reader.GetFloat(2), 2);
        Assert.Equal(14f,   reader.GetFloat(3), 2);
        Assert.Equal(0f,    reader.GetFloat(4), 2);
    }

    // ══ Claim the same time twice ═════════════════════════════════════════════

    [SkippableFact]
    public async Task ClaimingTheSameWindowTwiceYieldsNothingTheSecondTime()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await OwnershipTests.CreateCharacter(player, "Cheat");

        await StartMining(player, character);
        api.Clock.Advance(TimeSpan.FromHours(4));

        long first  = await SettleActions(player, character);
        long second = await SettleActions(player, character);
        long third  = await SettleActions(player, character);

        Assert.True(first > 0);
        Assert.Equal(0L, second);
        Assert.Equal(0L, third);
    }

    /// <summary>
    /// A timestamp in the request body. There is nowhere for it to go, and this
    /// confirms it: the payout is identical whether or not it is sent.
    /// </summary>
    [SkippableFact]
    public async Task AClientSuppliedTimestampChangesNothing()
    {
        RequireDatabase();

        await using var honest = await api.NewPlayerAsync();
        await using var liar   = await api.NewPlayerAsync();

        Guid a = await OwnershipTests.CreateCharacter(honest, "Honest");
        Guid b = await OwnershipTests.CreateCharacter(liar,   "Liar");

        await StartMining(honest, a);
        await StartMining(liar,   b);

        api.Clock.Advance(TimeSpan.FromHours(1));

        long paid = await SettleActions(honest, a);

        var forged = await OwnershipTests.Post(liar, $"/activity/{b}/settle", new
        {
            now             = DateTimeOffset.UtcNow.AddYears(5),
            elapsedSeconds  = 999_999_999,
            lastSettledAt   = DateTimeOffset.UtcNow.AddYears(-5),
        });
        forged.EnsureSuccessStatusCode();

        using var body = JsonDocument.Parse(await forged.Content.ReadAsStringAsync());

        Assert.Equal(paid, body.RootElement.GetProperty("actions").GetInt64());
    }

    /// <summary>
    /// Heartbeats are the one thing a client can genuinely influence, and the ceiling
    /// on what that buys is the active rate — about 1.6x, never more. It cannot beat
    /// real time.
    /// </summary>
    [SkippableFact]
    public async Task SpammingHeartbeatsCannotBeatTheActiveRate()
    {
        RequireDatabase();

        await using var spammer = await api.NewPlayerAsync();
        Guid character = await OwnershipTests.CreateCharacter(spammer, "Spammer");

        await StartMining(spammer, character);

        // A hundred beats in no time at all.
        for (int i = 0; i < 100; i++)
            await OwnershipTests.Post(spammer, $"/activity/{character}/beat", new { });

        api.Clock.Advance(TimeSpan.FromHours(1));

        long actions = await SettleActions(spammer, character);

        // The tin rock at the active rate is 3600/3 = 1200 an hour. Not one more.
        Assert.True(actions <= 1200L, $"a heartbeat spammer earned {actions} in an hour");
    }

    // ══ Act as somebody else ══════════════════════════════════════════════════

    [SkippableFact]
    public async Task SettlingAnotherPlayersCharacterIsRefused()
    {
        RequireDatabase();

        await using var owner = await api.NewPlayerAsync();
        await using var thief = await api.NewPlayerAsync();

        Guid character = await OwnershipTests.CreateCharacter(owner, "Victim");
        await StartMining(owner, character);

        api.Clock.Advance(TimeSpan.FromHours(1));

        var stolen = await OwnershipTests.Post(thief, $"/activity/{character}/settle", new { });
        Assert.Equal(HttpStatusCode.NotFound, stolen.StatusCode);

        // And the window is untouched, so the owner still gets all of it.
        Assert.True(await SettleActions(owner, character) > 0);
    }

    [SkippableFact]
    public async Task CraftingOnAnotherPlayersCharacterIsRefused()
    {
        RequireDatabase();

        await using var owner = await api.NewPlayerAsync();
        await using var thief = await api.NewPlayerAsync();

        Guid character = await OwnershipTests.CreateCharacter(owner, "Victim");

        var response = await OwnershipTests.Post(thief, $"/activity/{character}/craft",
                                                 new { recipeId = "smelt_tin_bar" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ══ Races ═════════════════════════════════════════════════════════════════

    /// <summary>
    /// ══ THE RACE THAT MATTERS MOST ════════════════════════════════════════════
    ///
    /// Twenty settlements of one window, all in flight together, all with DIFFERENT
    /// idempotency keys — so the middleware cannot help. Only the row lock and the
    /// timestamp can, and this is what proves they do.
    ///
    /// Different keys is the whole point. Sending the same key twenty times tests
    /// idempotency; sending twenty different ones tests the thing underneath it.
    /// </summary>
    [SkippableFact]
    public async Task TwentySimultaneousSettlementsPayTheWindowOnce()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await OwnershipTests.CreateCharacter(player, "Racer");

        await StartMining(player, character);
        api.Clock.Advance(TimeSpan.FromHours(1));

        var attempts = Enumerable.Range(0, 20)
            .Select(_ => OwnershipTests.Post(player, $"/activity/{character}/settle", new { }))
            .ToArray();

        HttpResponseMessage[] responses = await Task.WhenAll(attempts);

        long total = 0;

        foreach (var response in responses)
        {
            if (!response.IsSuccessStatusCode) continue;

            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            total += body.RootElement.GetProperty("actions").GetInt64();

            response.Dispose();
        }

        // One hour at the tin rock's offline rate: 3600 / (3 / 0.6) = 720.
        Assert.Equal(720L, total);
        Assert.Equal(720L, await ItemCount(character));
    }

    /// <summary>
    /// Two sessions on one account, both live, both working. Last writer wins on the
    /// row; neither duplicates. This is the "two devices" case, and it is ordinary
    /// rather than an attack — but it is where duplication would show up first.
    /// </summary>
    [SkippableFact]
    public async Task TwoSessionsOnOneAccountCannotBothClaimTheWindow()
    {
        RequireDatabase();

        await using var laptop = await api.NewPlayerAsync();
        Guid character = await OwnershipTests.CreateCharacter(laptop, "Shared");

        // A second client holding a token for the same account -- exactly what
        // signing in on a phone produces.
        using var phone = api.CreateClient();
        phone.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", ApiFixture.TokenFor(laptop.UserId));

        await StartMining(laptop, character);
        api.Clock.Advance(TimeSpan.FromHours(1));

        using var fromPhone = new HttpRequestMessage(HttpMethod.Post, $"/activity/{character}/settle");
        fromPhone.Headers.Add("Idempotency-Key", Player.NewKey());

        var both = await Task.WhenAll(
            OwnershipTests.Post(laptop, $"/activity/{character}/settle", new { }),
            phone.SendAsync(fromPhone));

        long total = 0;

        foreach (var response in both)
        {
            if (!response.IsSuccessStatusCode) continue;

            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            total += body.RootElement.GetProperty("actions").GetInt64();
        }

        Assert.Equal(720L, total);
        Assert.Equal(720L, await ItemCount(character));
    }

    /// <summary>
    /// Racing crafts. Materials must be spent exactly once, and the output must match
    /// what was spent — the classic double-spend, in the place it actually costs
    /// something.
    /// </summary>
    [SkippableFact]
    public async Task RacingCraftsCannotSpendTheSameOreTwice()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await OwnershipTests.CreateCharacter(player, "Smith");

        // Exactly enough tin ore for ten bars, and not one more.
        await GiveOre(character, "tin_ore", 20);

        (await OwnershipTests.Post(player, $"/activity/{character}/craft",
                                   new { recipeId = "smelt_tin_bar" })).EnsureSuccessStatusCode();

        api.Clock.Advance(TimeSpan.FromHours(1));

        var attempts = Enumerable.Range(0, 15)
            .Select(_ => OwnershipTests.Post(player, $"/activity/{character}/settle", new { }))
            .ToArray();

        foreach (var response in await Task.WhenAll(attempts)) response.Dispose();

        await using var db = await api.OpenDatabaseAsync();

        long oreLeft = await Quantity(db, character, "tin_ore");
        long bars     = await Quantity(db, character, "tin_bar");

        // Twenty ore, two per bar: ten bars and nothing left, however many requests
        // arrived at once.
        Assert.Equal(10L, bars);
        Assert.Equal(0L,  oreLeft);
    }

    /// <summary>
    /// A craft with nothing to craft from produces nothing, rather than producing
    /// from nothing. The campfire once conjured cooked shrimp this way.
    /// </summary>
    [SkippableFact]
    public async Task CraftingWithNoMaterialsProducesNothing()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await OwnershipTests.CreateCharacter(player, "Pauper");

        (await OwnershipTests.Post(player, $"/activity/{character}/craft",
                                   new { recipeId = "smelt_tin_bar" })).EnsureSuccessStatusCode();

        api.Clock.Advance(TimeSpan.FromHours(8));
        await SettleActions(player, character);

        await using var db = await api.OpenDatabaseAsync();

        Assert.Equal(0L, await Quantity(db, character, "tin_bar"));
    }

    /// <summary>
    /// Crafting something the character has not earned the right to make.
    /// </summary>
    [SkippableFact]
    public async Task ARecipeAboveTheSkillLevelIsRefused()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await OwnershipTests.CreateCharacter(player, "Novice");

        // The tin chestplate needs smithing 6; a new character is 1.
        var response = await OwnershipTests.Post(player, $"/activity/{character}/craft",
                                                 new { recipeId = "smith_tin_platebody" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    // ══ The ledger has to survive all of it ═══════════════════════════════════

    /// <summary>
    /// After every attack above, the books still balance. This is the check that
    /// catches a duplication nobody thought to assert on directly.
    /// </summary>
    [SkippableFact]
    public async Task TheLedgerStillExplainsEveryItemAfterARace()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await OwnershipTests.CreateCharacter(player, "Auditor");

        await StartMining(player, character);
        api.Clock.Advance(TimeSpan.FromHours(3));

        var attempts = Enumerable.Range(0, 10)
            .Select(_ => OwnershipTests.Post(player, $"/activity/{character}/settle", new { }))
            .ToArray();

        foreach (var response in await Task.WhenAll(attempts)) response.Dispose();

        await using var db = await api.OpenDatabaseAsync();

        long held = await Quantity(db, character, "tin_ore");

        await using var command = db.CreateCommand();
        command.CommandText = """
            select coalesce(sum(delta), 0) from item_ledger
             where character_id = $1 and item_id = 'tin_ore';
            """;
        command.Parameters.AddWithValue(character);

        long ledgered = Convert.ToInt64(await command.ExecuteScalarAsync());

        Assert.Equal(held, ledgered);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task StartMining(Player player, Guid character) =>
        (await OwnershipTests.Post(player, $"/activity/{character}", new { nodeId = TinRock }))
            .EnsureSuccessStatusCode();

    private static async Task<long> SettleActions(Player player, Guid character)
    {
        var response = await OwnershipTests.Post(player, $"/activity/{character}/settle", new { });
        response.EnsureSuccessStatusCode();

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("actions").GetInt64();
    }

    private async Task<long> WalletBalance(Guid accountId, string currency)
    {
        await using var db = await api.OpenDatabaseAsync();
        await using var command = db.CreateCommand();

        command.CommandText = "select coalesce(sum(balance),0) from wallet where account_id = $1 and currency = $2;";
        command.Parameters.AddWithValue(accountId);
        command.Parameters.AddWithValue(currency);

        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private async Task<long> ItemCount(Guid character)
    {
        await using var db = await api.OpenDatabaseAsync();
        await using var command = db.CreateCommand();

        command.CommandText = "select coalesce(sum(quantity),0) from inventory_slot where character_id = $1;";
        command.Parameters.AddWithValue(character);

        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<long> Quantity(NpgsqlConnection db, Guid character, string itemId)
    {
        await using var command = db.CreateCommand();

        command.CommandText = """
            select coalesce(sum(quantity),0) from inventory_slot
             where character_id = $1 and item_id = $2;
            """;
        command.Parameters.AddWithValue(character);
        command.Parameters.AddWithValue(itemId);

        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    /// <summary>
    /// Puts materials in the bag directly.
    ///
    /// Through SQL rather than an endpoint, because there is no endpoint that grants
    /// items -- which is the point of the first two tests in this file.
    /// </summary>
    private async Task GiveOre(Guid character, string itemId, long quantity)
    {
        await using var db = await api.OpenDatabaseAsync();
        await using var command = db.CreateCommand();

        command.CommandText = """
            insert into inventory_slot (character_id, slot_index, item_id, quantity)
            values ($1, 0, $2, $3)
            on conflict (character_id, slot_index) do update set item_id = $2, quantity = $3;
            """;
        command.Parameters.AddWithValue(character);
        command.Parameters.AddWithValue(itemId);
        command.Parameters.AddWithValue(quantity);

        await command.ExecuteNonQueryAsync();
    }
}
