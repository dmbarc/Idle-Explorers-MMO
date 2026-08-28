#nullable enable

using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace IdleExplorers.Api.Tests;

/// <summary>
/// Potions: bought with gold, drunk on the server, and felt in the damage.
///
/// ══ WHY A BUFF IS WORTH THIS MUCH TESTING ═════════════════════════════════════
///
/// Because it is a damage multiplier that a player buys, and every one of those has
/// the same three ways to go wrong:
///
///   · It is CREDITED but never PAID — the mystic gem's bug exactly. Six tests proved
///     seconds were written to a column and every one passed while a gem did nothing,
///     because the code that spends the column bailed before reading it. So the test
///     that matters here is not "is there a row" but "did the DPS move".
///   · It STACKS, and becomes a currency: fifty potions, fifty times the damage.
///   · It never ENDS, and a ten-minute draught is permanent.
///
/// ══ AND WHY THE PRICE IS CHECKED IN GOLD ══════════════════════════════════════
///
/// ShopProduct now carries two prices and the endpoint picks one. A gold product that
/// charged relic coins would take the premium currency for a farmed item; one that
/// charged nothing would be a free purchase button. Both are one field away.
/// </summary>
[Collection("api")]
public class BuffTests(ApiFixture api)
{
    private static void RequireDatabase() => Skip.IfNot(ApiFixture.DatabaseReachable, ApiFixture.SkipReason);

    /// <summary>The Draught of Fury: +25% damage for ten minutes, 2,500 gold.</summary>
    private const string Fury    = "draught_of_fury";
    private const string BuyFury = "buy_draught_of_fury";

    // ── Drinking ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Drinking one moves the number the whole economy is paid from.
    ///
    /// ══ WHY THE ASSERTION IS ON DPS AND NOT ON THE ROW ════════════════════════
    ///
    /// A row in character_buff is the intermediate write. The observable outcome is
    /// that the character hits harder, and those are not the same claim: the gem
    /// wrote its column perfectly for a whole phase while the settle bailed before
    /// reading it. Whatever else changes, this asserts the thing the player paid for.
    /// </summary>
    [SkippableFact]
    public async Task DrinkingAPotionRaisesTheDamageTheServerPays()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await Ready(player, "Thirsty");

        double before = await Dps(player, character);

        await Give(character, Fury, slot: 0);

        (await Drink(player, character, Fury)).EnsureSuccessStatusCode();

        double after = await Dps(player, character);

        Assert.True(after > before,
                    $"a Draught of Fury should raise DPS; it went {before} -> {after}");
    }

    /// <summary>
    /// And an undrunk one changes nothing.
    ///
    /// The paired negative. Without it the test above would pass just as well against
    /// a server that raised everybody's damage on any request at all.
    /// </summary>
    [SkippableFact]
    public async Task CarryingAPotionDoesNothing()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await Ready(player, "Patient");

        double before = await Dps(player, character);

        await Give(character, Fury, slot: 0, quantity: 10);

        double after = await Dps(player, character);

        Assert.Equal(before, after, 6);
    }

    /// <summary>
    /// A second Draught of Fury refreshes the first. It does not stack.
    ///
    /// ══ WHY THIS IS THE EXPLOIT TEST ══════════════════════════════════════════
    ///
    /// A stacking buff is a currency. Fifty potions would be fifty times the damage,
    /// and the only limit on how hard a character hits would be how much gold they
    /// had — which is a purchasable win condition against a boss whose fail state is a
    /// DPS check.
    ///
    /// The primary key on (character_id, stat_id) is what prevents it. This is the
    /// test that notices the day somebody adds an id column "so buffs can be listed".
    /// </summary>
    [SkippableFact]
    public async Task DrinkingTwoOfTheSameDoesNotStack()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await Ready(player, "Greedy");

        await Give(character, Fury, slot: 0, quantity: 5);

        (await Drink(player, character, Fury)).EnsureSuccessStatusCode();
        double once = await Dps(player, character);

        (await Drink(player, character, Fury)).EnsureSuccessStatusCode();
        double twice = await Dps(player, character);

        Assert.Equal(once, twice, 6);

        await using var db = await api.OpenDatabaseAsync();
        Assert.Equal(1L, await Rows(db, character));
    }

    /// <summary>
    /// Two DIFFERENT potions do both apply.
    ///
    /// The other half of the pair above: a table that refused a second row of any kind
    /// would satisfy "does not stack" perfectly and quietly make every potion after
    /// the first one useless.
    /// </summary>
    [SkippableFact]
    public async Task TwoDifferentPotionsBothApply()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await OwnershipTests.CreateCharacter(player, "Prepared", "warrior");

        await Give(character, Fury, slot: 0);
        await Give(character, "draught_of_swiftness", slot: 1);

        (await Drink(player, character, Fury)).EnsureSuccessStatusCode();
        (await Drink(player, character, "draught_of_swiftness")).EnsureSuccessStatusCode();

        await using var db = await api.OpenDatabaseAsync();
        Assert.Equal(2L, await Rows(db, character));
    }

    /// <summary>
    /// An expired buff pays nothing.
    ///
    /// Written by moving the expiry into the past rather than by waiting ten minutes.
    /// The filter is `expires_at > now()` in SQL, so this exercises the real condition
    /// — a buff that was applied in C# after being read would pass a test that waited
    /// and fail this one.
    /// </summary>
    [SkippableFact]
    public async Task AnExpiredBuffIsNotApplied()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await Ready(player, "Lapsed");

        double before = await Dps(player, character);

        await Give(character, Fury, slot: 0);
        (await Drink(player, character, Fury)).EnsureSuccessStatusCode();

        Assert.True(await Dps(player, character) > before, "the potion took hold in the first place");

        await Expire(character);

        Assert.Equal(before, await Dps(player, character), 6);
    }

    /// <summary>Something that is not a potion is not drinkable as one.</summary>
    [SkippableFact]
    public async Task SomethingThatIsNotAPotionIsRefused()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await OwnershipTests.CreateCharacter(player, "Confused", "warrior");

        await Give(character, "tin_ore", slot: 0, quantity: 5);

        var response = await Drink(player, character, "tin_ore");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>A potion you do not have cannot be drunk.</summary>
    [SkippableFact]
    public async Task APotionYouDoNotHaveCannotBeDrunk()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await OwnershipTests.CreateCharacter(player, "Empty", "warrior");

        var response = await Drink(player, character, Fury);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        await using var db = await api.OpenDatabaseAsync();
        Assert.Equal(0L, await Rows(db, character));
    }

    // ── Paying for one ────────────────────────────────────────────────────────

    /// <summary>
    /// A gold product takes gold, and takes the right amount of it.
    /// </summary>
    [SkippableFact]
    public async Task AGoldProductIsPaidForInGold()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await OwnershipTests.CreateCharacter(player, "Solvent", "warrior");

        await GrantWallet(player, IdleExplorers.Rules.Currency.Coins, 10_000L);

        HttpResponseMessage bought = await Buy(player, character, BuyFury);

        bought.EnsureSuccessStatusCode();

        using var body = JsonDocument.Parse(await bought.Content.ReadAsStringAsync());

        Assert.Equal(IdleExplorers.Rules.Currency.Coins,
                     body.RootElement.GetProperty("currency").GetString());

        Assert.Equal(7_500L, await Wallet(player, IdleExplorers.Rules.Currency.Coins));
    }

    /// <summary>
    /// And it does NOT quietly take relic coins.
    ///
    /// The failure this rules out is the expensive one: a player with a full premium
    /// balance and no gold buying a farmed item and only noticing afterwards.
    /// </summary>
    [SkippableFact]
    public async Task AGoldProductDoesNotTakeRelicCoins()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await OwnershipTests.CreateCharacter(player, "Rich", "warrior");

        await GrantWallet(player, IdleExplorers.Rules.Currency.RelicCoins, 99_999L);

        HttpResponseMessage response = await Buy(player, character, BuyFury);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("not enough gold", body.RootElement.GetProperty("title").GetString());

        // Untouched, to the coin.
        Assert.Equal(99_999L, await Wallet(player, IdleExplorers.Rules.Currency.RelicCoins));
    }

    // ── Machinery ─────────────────────────────────────────────────────────────

    private static Task<HttpResponseMessage> Drink(Player player, Guid character, string itemId) =>
        OwnershipTests.Post(player, $"/activity/{character}/use", new { itemId });

    private static Task<HttpResponseMessage> Buy(Player player, Guid character, string productId) =>
        OwnershipTests.Post(player, $"/shop/{character}/buy", new { productId });

    /// <summary>
    /// What the server thinks this character hits for, measured where it matters most.
    ///
    /// ══ WHY THIS GOES THROUGH THE BOSS ════════════════════════════════════════
    ///
    /// frozen_dps is written at engage by SettlementService.FreezeCombatAsync, which
    /// is the same function the farm integral calls — so it is the character's real
    /// damage, not a number computed for a test.
    ///
    /// It is also the exact number these potions exist to move: the King's fail
    /// condition is a DPS check against a server clock, and a Draught of Fury that
    /// did not reach frozen_dps would be a potion that helps everywhere except the
    /// one fight it was brewed for.
    ///
    /// The encounter is closed again afterwards so a character can be measured twice.
    /// </summary>
    private async Task<double> Dps(Player player, Guid character)
    {
        (await OwnershipTests.Post(player, $"/encounter/{character}",
                                   new { monsterId = "goblin_king" })).EnsureSuccessStatusCode();

        await using var db = await api.OpenDatabaseAsync();

        double dps;

        await using (var read = db.CreateCommand())
        {
            read.CommandText = """
                select p.frozen_dps
                  from encounter_participant p
                  join encounter e on e.id = p.encounter_id
                 where p.character_id = $1 and e.ended_at is null
                 order by p.joined_at desc
                 limit 1;
                """;
            read.Parameters.AddWithValue(character);

            object? result = await read.ExecuteScalarAsync();

            dps = result is null or DBNull ? 0d : Convert.ToDouble(result);
        }

        // Ended rather than left open: engage refuses a character already fighting,
        // and these tests measure the same character before and after a drink.
        await using (var close = db.CreateCommand())
        {
            // A verdict as well as an end. encounter_ends_with_a_verdict refuses a row
            // that has finished without saying how -- which is the constraint doing
            // exactly its job, since every later query would then have to guess.
            close.CommandText = """
                update encounter set ended_at = now(), won = false
                 where ended_at is null
                   and id in (select encounter_id from encounter_participant where character_id = $1);
                """;
            close.Parameters.AddWithValue(character);

            await close.ExecuteNonQueryAsync();
        }

        return dps;
    }

    /// <summary>A character with the portal open, so their damage can be measured.</summary>
    private async Task<Guid> Ready(Player player, string name)
    {
        Guid character = await OwnershipTests.CreateCharacter(player, name, "warrior");

        await using (var db = await api.OpenDatabaseAsync())
        await using (var command = db.CreateCommand())
        {
            // Straight to the unlock. What these tests are about is the POTION, and
            // grinding a thousand goblins per test would be testing the gate again.
            command.CommandText = """
                insert into kill_counter (character_id, monster_id, active_kills)
                values ($1, 'goblin', 1000)
                on conflict (character_id, monster_id) do update set active_kills = 1000;
                """;
            command.Parameters.AddWithValue(character);

            await command.ExecuteNonQueryAsync();
        }

        (await OwnershipTests.Post(player, $"/boss/{character}/unlock", new { }))
            .EnsureSuccessStatusCode();

        return character;
    }

    private async Task<long> Rows(Npgsql.NpgsqlConnection db, Guid character)
    {
        await using var command = db.CreateCommand();

        command.CommandText =
            "select count(*)::bigint from character_buff where character_id = $1 and expires_at > now();";
        command.Parameters.AddWithValue(character);

        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }

    /// <summary>Moves every buff's expiry into the past, without waiting for one.</summary>
    private async Task Expire(Guid character)
    {
        await using var db = await api.OpenDatabaseAsync();
        await using var command = db.CreateCommand();

        command.CommandText =
            "update character_buff set expires_at = now() - interval '1 second' where character_id = $1;";
        command.Parameters.AddWithValue(character);

        await command.ExecuteNonQueryAsync();
    }

    private async Task Give(Guid character, string itemId, int slot, long quantity = 1)
    {
        await using var db = await api.OpenDatabaseAsync();
        await using var command = db.CreateCommand();

        command.CommandText = """
            insert into inventory_slot (character_id, slot_index, item_id, quantity)
            values ($1, $2, $3, $4)
            on conflict (character_id, slot_index) do update
               set item_id = excluded.item_id, quantity = excluded.quantity;
            """;
        command.Parameters.AddWithValue(character);
        command.Parameters.AddWithValue(slot);
        command.Parameters.AddWithValue(itemId);
        command.Parameters.AddWithValue(quantity);

        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Puts money in a wallet, with the ledger row that has to accompany it.
    ///
    /// Both, always: sum(delta) = balance is the invariant the scenario tests assert,
    /// and a test fixture that broke it would fail an unrelated suite in a way nobody
    /// would trace back to here.
    /// </summary>
    private async Task GrantWallet(Player player, string currency, long amount)
    {
        await using var db = await api.OpenDatabaseAsync();

        await using (var command = db.CreateCommand())
        {
            command.CommandText = """
                insert into wallet (account_id, currency, balance)
                values ($1, $2, $3)
                on conflict (account_id, currency) do update set balance = excluded.balance;
                """;
            command.Parameters.AddWithValue(player.UserId);
            command.Parameters.AddWithValue(currency);
            command.Parameters.AddWithValue(amount);

            await command.ExecuteNonQueryAsync();
        }

        await using (var ledger = db.CreateCommand())
        {
            ledger.CommandText = """
                insert into wallet_ledger (account_id, currency, delta, reason)
                values ($1, $2, $3, 'test_grant');
                """;
            ledger.Parameters.AddWithValue(player.UserId);
            ledger.Parameters.AddWithValue(currency);
            ledger.Parameters.AddWithValue(amount);

            await ledger.ExecuteNonQueryAsync();
        }
    }

    private async Task<long> Wallet(Player player, string currency)
    {
        await using var db = await api.OpenDatabaseAsync();
        await using var command = db.CreateCommand();

        command.CommandText =
            "select coalesce(balance, 0)::bigint from wallet where account_id = $1 and currency = $2;";
        command.Parameters.AddWithValue(player.UserId);
        command.Parameters.AddWithValue(currency);

        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }
}
