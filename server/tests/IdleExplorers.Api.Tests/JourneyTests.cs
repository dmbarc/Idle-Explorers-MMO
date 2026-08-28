using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Npgsql;
using Xunit;

namespace IdleExplorers.Api.Tests;

/// <summary>
/// One player, from nothing to the portal, in one test.
///
/// ══ WHY A SINGLE LONG TEST ════════════════════════════════════════════════════
///
/// Everything here is covered in pieces elsewhere. What a narrative adds is the
/// joins: ore mined in step three has to be the ore smelted in step five, and the
/// bars smelted there have to be the bars the chestplate consumes. Unit tests each
/// pass with a different idea of what an inventory is; this one cannot.
///
/// It also reads as a specification. Somebody wanting to know what the server does
/// can read this file and find out, in order, without knowing where anything lives.
///
/// ══ WHERE IT STOPS ════════════════════════════════════════════════════════════
///
/// At the portal. Engaging the Goblin King, claiming its loot and buying relic coins
/// have no endpoints yet; the journey extends when they do rather than pretending
/// now.
/// </summary>
[Collection("api")]
public class JourneyTests(ApiFixture api)
{
    /// <summary>
    /// The gate, read rather than restated.
    ///
    /// These used to say 999 and 1000. When the gate moved to the shared rules tree
    /// and changed, four tests failed -- correctly, but for the wrong reason: they
    /// were asserting a NUMBER when the property under test is "one short is sealed
    /// and exactly enough is open".
    ///
    /// Written against the constant, they now test the rule and survive a rebalance.
    /// </summary>
    private const long Gate = IdleExplorers.Rules.BossGate.RequiredActiveKills;

    private static void RequireDatabase() => Skip.IfNot(ApiFixture.DatabaseReachable, ApiFixture.SkipReason);

    [SkippableFact]
    public async Task FromAnEmptyAccountToAnOpenPortal()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        await using var db     = await api.OpenDatabaseAsync();

        // ── 1. An account exists the first time it is asked for ───────────────
        using (var account = await GetJson(player, "/account/"))
        {
            Assert.Equal(player.UserId, account.RootElement.GetProperty("accountId").GetGuid());
            Assert.Empty(account.RootElement.GetProperty("characters").EnumerateArray());
        }

        // ── 2. A character ────────────────────────────────────────────────────
        Guid character = await OwnershipTests.CreateCharacter(player, "Bricta");

        // ── 3. Eight hours at the tin rock, unattended ────────────────────────
        (await OwnershipTests.Post(player, $"/activity/{character}", new { nodeId = "tin_rock_1" }))
            .EnsureSuccessStatusCode();

        api.Clock.Advance(TimeSpan.FromHours(8));

        long oreMined;

        using (var settled = await PostJson(player, $"/activity/{character}/settle"))
        {
            oreMined = OwnershipTests.StackQuantity(settled.RootElement, "items", "tin_ore");

            // 3 seconds an action at 0.6 offline: 720 an hour, 5,760 in eight.
            Assert.Equal(5_760L, oreMined);
            Assert.Equal(0L, settled.RootElement.GetProperty("supervisedActions").GetInt64());
        }

        Assert.Equal(oreMined, await Quantity(db, character, "tin_ore"));

        // ── 4. Enough mining experience to smelt ──────────────────────────────
        //
        // Read rather than assumed: the chestplate has a level requirement and the
        // server enforces it, so the journey has to have actually earned it.
        Assert.True(await SkillLevel(db, character, "mining") > 1,
                    "eight hours of mining did not raise the mining level");

        // ── 5. Smelt it into bars ─────────────────────────────────────────────
        (await OwnershipTests.Post(player, $"/activity/{character}/craft", new { recipeId = "smelt_tin_bar" }))
            .EnsureSuccessStatusCode();

        // Long enough to run the ore out. The point of the assertion below is that it
        // stops at the ORE rather than at the clock.
        api.Clock.Advance(TimeSpan.FromHours(24));

        using (var settled = await PostJson(player, $"/activity/{character}/settle"))
        {
            Assert.True(settled.RootElement.GetProperty("ranOutOfInputs").GetBoolean(),
                        "smelting did not report running out of ore");
        }

        long bars = await Quantity(db, character, "tin_bar");
        long oreLeft = await Quantity(db, character, "tin_ore");

        // ══ NOTHING WAS CONJURED, AND NOTHING VANISHED ════════════════════════
        //
        // Two ore per bar. Every ore mined is either still in the bag or accounted
        // for by a bar. This is the assertion the whole economy rests on.
        Assert.Equal(oreMined, oreLeft + bars * 2L);
        Assert.True(bars > 0, "no bars were smelted");

        // ── 6. The ledger explains all of it ──────────────────────────────────
        Assert.Equal(oreLeft, await LedgerTotal(db, character, "tin_ore"));
        Assert.Equal(bars,    await LedgerTotal(db, character, "tin_bar"));

        // ── 7. The portal is sealed, and says by how much ─────────────────────
        using (var gate = await GetJson(player, $"/boss/{character}/gate"))
        {
            Assert.False(gate.RootElement.GetProperty("open").GetBoolean());
            Assert.Equal(Gate, gate.RootElement.GetProperty("remaining").GetInt64());
        }

        // ── 8. Goblins, with somebody watching ────────────────────────────────
        (await OwnershipTests.Post(player, $"/activity/{character}/fight", new { monsterId = "goblin" }))
            .EnsureSuccessStatusCode();

        // Heartbeats, so the kills count as active. Without them a player could grind
        // the gate open in their sleep, which is the one thing it must not allow.
        //
        // Six of them, thirty seconds apart, because a bare-handed level-one character
        // is genuinely slow: base damage averages 2 a swing every 3 seconds, so a
        // 40-health goblin takes about a minute. Three minutes of watched time is two
        // or three kills, and that is the honest figure rather than a convenient one.
        // The client beats every twenty seconds for the same reason -- one beat covers
        // sixty, so a gap wider than that stops being supervised.
        for (int beat = 0; beat < 6; beat++)
        {
            (await OwnershipTests.Post(player, $"/activity/{character}/beat", new { }))
                .EnsureSuccessStatusCode();

            api.Clock.Advance(TimeSpan.FromSeconds(30));
        }

        using (var settled = await PostJson(player, $"/activity/{character}/settle"))
        {
            Assert.True(settled.RootElement.GetProperty("supervisedActions").GetInt64() > 0,
                        "a watched window credited no active kills");
        }

        // Coins from the goblins went to the wallet, not into the bag beside the bars.
        Assert.Equal(0L, await Quantity(db, character, "coins"));
        Assert.True(await WalletBalance(db, player.UserId, "coins") > 0);

        // ── 9. The thousandth kill ────────────────────────────────────────────
        //
        // Set directly. Settling a genuine thousand would mean forty minutes of
        // simulated combat per run, and what this step tests is the GATE.
        await SetActiveKills(db, character, Gate - 1);

        Assert.False((await ReadGate(player, character)).GetProperty("open").GetBoolean());

        await SetActiveKills(db, character, Gate);

        using (var gate = await GetJson(player, $"/boss/{character}/gate"))
        {
            Assert.True(gate.RootElement.GetProperty("open").GetBoolean());
            Assert.Equal(0L, gate.RootElement.GetProperty("remaining").GetInt64());
        }

        (await OwnershipTests.Post(player, $"/boss/{character}/unlock", new { }))
            .EnsureSuccessStatusCode();

        // ── 10. The books balance ─────────────────────────────────────────────
        await AssertLedgerBalances(db, player.UserId);

        // ── 11. And the character sheet agrees with all of it ─────────────────
        using (var sheet = await GetJson(player, $"/character/{character}"))
        {
            long sheetBars = 0;

            foreach (var slot in sheet.RootElement.GetProperty("inventory").EnumerateArray())
                if (slot.GetProperty("itemId").GetString() == "tin_bar")
                    sheetBars += slot.GetProperty("quantity").GetInt64();

            Assert.Equal(bars, sheetBars);

            // Level is derived from xp on read, never stored -- so it cannot drift.
            Assert.True(sheet.RootElement.GetProperty("level").GetInt32() >= 1);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task<JsonDocument> GetJson(Player player, string path)
    {
        var response = await player.Client.GetAsync(path);
        response.EnsureSuccessStatusCode();

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static async Task<JsonDocument> PostJson(Player player, string path)
    {
        var response = await OwnershipTests.Post(player, path, new { });
        response.EnsureSuccessStatusCode();

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static async Task<JsonElement> ReadGate(Player player, Guid character)
    {
        using var document = await GetJson(player, $"/boss/{character}/gate");
        return document.RootElement.Clone();
    }

    private static async Task<long> Quantity(NpgsqlConnection db, Guid character, string itemId) =>
        await Scalar(db, """
            select coalesce(sum(quantity), 0) from inventory_slot
             where character_id = $1 and item_id = $2;
            """, character, itemId);

    private static async Task<long> LedgerTotal(NpgsqlConnection db, Guid character, string itemId) =>
        await Scalar(db, """
            select coalesce(sum(delta), 0) from item_ledger
             where character_id = $1 and item_id = $2;
            """, character, itemId);

    private static async Task<long> WalletBalance(NpgsqlConnection db, Guid account, string currency) =>
        await Scalar(db, "select coalesce(sum(balance), 0) from wallet where account_id = $1 and currency = $2;",
                     account, currency);

    private static async Task<int> SkillLevel(NpgsqlConnection db, Guid character, string skillId)
    {
        long xp = await Scalar(db,
            "select coalesce(xp, 0) from character_skill where character_id = $1 and skill_id = $2;",
            character, skillId);

        return IdleExplorers.Rules.Levelling.SkillLevel(xp);
    }

    /// <summary>
    /// Every wallet balance equals the sum of its own ledger.
    ///
    /// Run at the end of the journey rather than after each step, because the point is
    /// that nothing along the way broke it -- and a duplication that cancels out
    /// within one step would still show here.
    /// </summary>
    private static async Task AssertLedgerBalances(NpgsqlConnection db, Guid account)
    {
        await using var command = db.CreateCommand();

        command.CommandText = """
            select w.currency,
                   w.balance,
                   coalesce((select sum(l.delta) from wallet_ledger l
                              where l.account_id = w.account_id and l.currency = w.currency), 0)
              from wallet w
             where w.account_id = $1;
            """;
        command.Parameters.AddWithValue(account);

        await using var reader = await command.ExecuteReaderAsync();

        int checked_ = 0;

        while (await reader.ReadAsync())
        {
            string currency = reader.GetString(0);
            long   balance  = reader.GetInt64(1);
            long   ledger   = Convert.ToInt64(reader.GetValue(2));

            Assert.True(balance == ledger,
                        $"{currency}: balance {balance} but the ledger sums to {ledger}");
            checked_++;
        }

        Assert.True(checked_ > 0, "the journey ended with no wallet to balance");
    }

    private static async Task SetActiveKills(NpgsqlConnection db, Guid character, long kills)
    {
        await using var command = db.CreateCommand();

        command.CommandText = """
            insert into kill_counter (character_id, monster_id, active_kills)
            values ($1, 'goblin', $2)
            on conflict (character_id, monster_id) do update set active_kills = excluded.active_kills;
            """;
        command.Parameters.AddWithValue(character);
        command.Parameters.AddWithValue(kills);

        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> Scalar(NpgsqlConnection db, string sql, params object[] parameters)
    {
        await using var command = db.CreateCommand();
        command.CommandText = sql;

        foreach (object value in parameters) command.Parameters.AddWithValue(value);

        object result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? 0L : Convert.ToInt64(result);
    }
}
