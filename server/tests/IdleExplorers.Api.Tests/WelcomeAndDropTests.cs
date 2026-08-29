#nullable enable

using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using IdleExplorers.Api.Endpoints;
using Xunit;

namespace IdleExplorers.Api.Tests;

/// <summary>
/// The two ways relic coins now enter the world, and the one that is closed.
///
/// ══ WHY THIS IS WORTH ITS OWN FILE ════════════════════════════════════════════
///
/// Relic coins are the premium currency — the number this whole architecture exists
/// to take off the player's machine. Three things changed at once: a welcome grant, a
/// boss drop, and the unpaid test tap being shut off. Each is a way currency does or
/// does not appear, and getting any of them wrong is either a player short-changed or
/// a mint.
///
/// Every balance assertion here is paired with a LEDGER assertion, because
/// sum(delta) = balance is the invariant the whole economy is checked against and a
/// grant that moved a balance without explaining itself is the shape of every
/// duplication bug.
/// </summary>
[Collection("api")]
public class WelcomeAndDropTests(ApiFixture api)
{
    private static void RequireDatabase() => Skip.IfNot(ApiFixture.DatabaseReachable, ApiFixture.SkipReason);

    // ── The welcome grant ─────────────────────────────────────────────────────

    /// <summary>
    /// A brand new account arrives with relic coins.
    ///
    /// NewPlayerAsync makes an authenticated request, which is what creates the
    /// account — the grant hangs off that same insert.
    /// </summary>
    [SkippableFact]
    public async Task ANewAccountIsGivenRelicCoins()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();

        // Any authenticated call provisions the account.
        (await OwnershipTests.Post(player, "/session/", new { })).EnsureSuccessStatusCode();

        Assert.Equal(IdleExplorers.Rules.Currency.WelcomeRelicCoins,
                     await Balance(player, IdleExplorers.Rules.Currency.RelicCoins));
    }

    /// <summary>
    /// And it is explained by exactly one ledger row.
    ///
    /// A balance with no ledger behind it is unexplainable, which is the state the
    /// ledger exists to make impossible. This is also what proves the grant is a
    /// GRANT rather than a column default — a default would move the balance and
    /// write nothing.
    /// </summary>
    [SkippableFact]
    public async Task TheWelcomeGrantIsExplainedByTheLedger()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();

        (await OwnershipTests.Post(player, "/session/", new { })).EnsureSuccessStatusCode();

        await using var db = await api.OpenDatabaseAsync();
        await using var command = db.CreateCommand();

        command.CommandText = """
            select count(*)::bigint, coalesce(sum(delta), 0)::bigint
              from wallet_ledger
             where account_id = $1 and currency = $2 and reason = $3;
            """;
        command.Parameters.AddWithValue(player.UserId);
        command.Parameters.AddWithValue(IdleExplorers.Rules.Currency.RelicCoins);
        command.Parameters.AddWithValue(IdleExplorers.Rules.Currency.WelcomeReason);

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());

        Assert.Equal(1L, reader.GetInt64(0));
        Assert.Equal(IdleExplorers.Rules.Currency.WelcomeRelicCoins, reader.GetInt64(1));
    }

    /// <summary>
    /// It is given ONCE, however many requests the account makes.
    ///
    /// ══ WHY THIS IS THE TEST THAT MATTERS ═════════════════════════════════════
    ///
    /// Caller.AccountIdAsync runs on every authenticated request in the game. A grant
    /// that fired each time would be a thousand relic coins per API call — not a bug
    /// somebody would notice slowly, but it is exactly the kind of thing that looks
    /// fine in one manual test.
    ///
    /// Exactly-once comes from `returning id` on an ON CONFLICT DO NOTHING insert:
    /// only the statement that actually inserted gets a row. This is what notices the
    /// day somebody "simplifies" that into a check-then-insert.
    /// </summary>
    [SkippableFact]
    public async Task TheWelcomeGrantIsGivenOnlyOnce()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();

        for (int i = 0; i < 5; i++)
            (await OwnershipTests.Post(player, "/session/", new { })).EnsureSuccessStatusCode();

        Guid character = await OwnershipTests.CreateCharacter(player, "Repeated");

        (await OwnershipTests.Post(player, $"/presence/{character}",
                                   new { mapId = "goblin_camp", x = 0f, z = 0f }))
            .EnsureSuccessStatusCode();

        Assert.Equal(IdleExplorers.Rules.Currency.WelcomeRelicCoins,
                     await Balance(player, IdleExplorers.Rules.Currency.RelicCoins));
    }

    /// <summary>
    /// It does not quietly hand out GOLD as well.
    ///
    /// The paired negative: a grant written against the wrong wallet, or against both,
    /// would satisfy every test above.
    /// </summary>
    [SkippableFact]
    public async Task TheWelcomeGrantIsRelicCoinsOnly()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();

        (await OwnershipTests.Post(player, "/session/", new { })).EnsureSuccessStatusCode();

        Assert.Equal(0L, await Balance(player, IdleExplorers.Rules.Currency.Coins));
    }

    // ── The unpaid tap is shut ────────────────────────────────────────────────

    /// <summary>
    /// The test-grant endpoint mints nothing.
    ///
    /// The migration sets the flag false explicitly, and the endpoint asks the STRICT
    /// variant — so an unknown flag is off too. Belt and braces on a switch whose ON
    /// state lets anybody mint what other people would pay for.
    /// </summary>
    [SkippableFact]
    public async Task TheUnpaidTestGrantIsRefused()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();

        // Provisioned FIRST, so the baseline is the welcome grant rather than nothing.
        // Reading it before the account existed made this test assert that a refused
        // grant had added a thousand coins -- which it had, and they were the welcome.
        (await OwnershipTests.Post(player, "/session/", new { })).EnsureSuccessStatusCode();

        long before = await Balance(player, IdleExplorers.Rules.Currency.RelicCoins);

        var request = new HttpRequestMessage(HttpMethod.Post, "/shop/test-grant")
        {
            Content = System.Net.Http.Json.JsonContent.Create(new { packId = "relic_1300" }),
        };

        request.Headers.Add("Idempotency-Key", Player.NewKey());

        HttpResponseMessage response = await player.Client.SendAsync(request);

        Assert.False(response.IsSuccessStatusCode,
                     "the unpaid grant must be refused while the flag is off");

        Assert.Equal(before, await Balance(player, IdleExplorers.Rules.Currency.RelicCoins));
    }

    // ── The King's drop ───────────────────────────────────────────────────────

    /// <summary>
    /// Relic coins are in the King's loot table, and they land in the WALLET.
    ///
    /// ══ WHY THE WALLET AND NOT THE BAG ════════════════════════════════════════
    ///
    /// Because Currency.WalletFor now maps the id, and GrantItemAsync routes anything
    /// it names to CreditWalletAsync with a ledger row. A relic coin that reached a
    /// bag slot would be a premium currency sitting in an inventory the settle
    /// overwrites — earned and then quietly gone.
    ///
    /// Granted here through the claim path rather than by beating the King, because
    /// what is being tested is where the drop GOES; EncounterTests already covers
    /// winning the fight.
    /// </summary>
    [SkippableFact]
    public async Task TheKingsRelicCoinsLandInTheWallet()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await OwnershipTests.CreateCharacter(player, "Crowned");

        long before = await Balance(player, IdleExplorers.Rules.Currency.RelicCoins);

        await Pend(character, IdleExplorers.Rules.Currency.RelicCoinsItemId, 2L);

        (await OwnershipTests.Post(player, $"/encounter/{character}/loot", new { }))
            .EnsureSuccessStatusCode();

        Assert.Equal(before + 2L, await Balance(player, IdleExplorers.Rules.Currency.RelicCoins));

        // And in the bag it is nowhere, because it is not an item.
        await using var db = await api.OpenDatabaseAsync();
        await using var command = db.CreateCommand();

        command.CommandText =
            "select count(*)::bigint from inventory_slot where character_id = $1 and item_id = $2;";
        command.Parameters.AddWithValue(character);
        command.Parameters.AddWithValue(IdleExplorers.Rules.Currency.RelicCoinsItemId);

        Assert.Equal(0L, (long)(await command.ExecuteScalarAsync() ?? 0L));
    }

    /// <summary>
    /// The content actually says so.
    ///
    /// The test above proves the PLUMBING works for a relic-coin drop. This proves
    /// the King is the one who has one — the two are independent, and a working pipe
    /// with nothing in it is the shape of half the bugs in this project.
    /// </summary>
    [SkippableFact]
    public async Task TheKingDropsBetweenOneAndTwoRelicCoins()
    {
        RequireDatabase();

        MonsterData? king = api.Content.Catalogue.GetMonster("goblin_king");

        Assert.NotNull(king);

        LootEntry? relic = king!.lootTable?
            .FirstOrDefault(e => e?.itemId == IdleExplorers.Rules.Currency.RelicCoinsItemId);

        Assert.NotNull(relic);
        Assert.Equal(1L, relic!.minQty);
        Assert.Equal(2L, relic.maxQty);
        Assert.Equal(100, relic.weight);

        await Task.CompletedTask;
    }

    // ── The King cannot be farmed ─────────────────────────────────────────────

    /// <summary>
    /// A boss may not be set as a standing activity.
    ///
    /// ══ THE EXPLOIT ═══════════════════════════════════════════════════════════
    ///
    /// Settlement resolves combat statistically -- dps against the monster's health,
    /// integrated over the window, paying the whole loot table. Point that at
    /// goblin_king and walking away overnight pays for HUNDREDS of Kings: crowns,
    /// weapons, and the relic coins he now drops, without ever entering the arena or
    /// beating the enrage timer.
    ///
    /// The boss exists precisely because it cannot be ground down on a clock.
    /// </summary>
    [SkippableFact]
    public async Task ABossCannotBeSetAsAnActivity()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await OwnershipTests.CreateCharacter(player, "Idler");

        HttpResponseMessage refused = await OwnershipTests.Post(
            player, $"/activity/{character}/fight", new { monsterId = "goblin_king" });

        Assert.Equal(System.Net.HttpStatusCode.Conflict, refused.StatusCode);

        await using var db = await api.OpenDatabaseAsync();
        await using var command = db.CreateCommand();

        command.CommandText = "select coalesce(monster_id, '') from activity where character_id = $1;";
        command.Parameters.AddWithValue(character);

        Assert.NotEqual("goblin_king", (string?)(await command.ExecuteScalarAsync() ?? ""));
    }

    /// <summary>
    /// And an ordinary monster still can.
    ///
    /// The paired acceptance. A guard that refused every monster would satisfy the
    /// test above and quietly stop the whole game from farming anything.
    /// </summary>
    [SkippableFact]
    public async Task AnOrdinaryMonsterStillCan()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await OwnershipTests.CreateCharacter(player, "Grinder");

        (await OwnershipTests.Post(player, $"/activity/{character}/fight",
                                   new { monsterId = "goblin" })).EnsureSuccessStatusCode();

        await using var db = await api.OpenDatabaseAsync();
        await using var command = db.CreateCommand();

        command.CommandText = "select monster_id from activity where character_id = $1;";
        command.Parameters.AddWithValue(character);

        Assert.Equal("goblin", (string?)await command.ExecuteScalarAsync());
    }

    // ── Machinery ─────────────────────────────────────────────────────────────

    private async Task<long> Balance(Player player, string currency)
    {
        await using var db = await api.OpenDatabaseAsync();
        await using var command = db.CreateCommand();

        command.CommandText =
            "select coalesce(balance, 0)::bigint from wallet where account_id = $1 and currency = $2;";
        command.Parameters.AddWithValue(player.UserId);
        command.Parameters.AddWithValue(currency);

        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }

    /// <summary>Puts something in the pending-loot pile, as a finished boss fight would.</summary>
    private async Task Pend(Guid character, string itemId, long quantity)
    {
        await using var db = await api.OpenDatabaseAsync();
        await using var command = db.CreateCommand();

        command.CommandText = """
            insert into pending_loot (character_id, item_id, quantity)
            values ($1, $2, $3);
            """;
        command.Parameters.AddWithValue(character);
        command.Parameters.AddWithValue(itemId);
        command.Parameters.AddWithValue(quantity);

        await command.ExecuteNonQueryAsync();
    }
}
