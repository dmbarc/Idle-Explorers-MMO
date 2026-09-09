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
/// Buying time, and the shape that stops it being a currency printer.
///
/// ══ WHAT THE GEM MUST NOT BE ══════════════════════════════════════════════════
///
/// The client's version moved lastLogoutUnixTime backwards and let the ordinary
/// accrual run over the wider gap. Harmless where the save already belongs to the
/// player; a printer the moment a server believes it, because anybody who can push a
/// timestamp back can push it back as far and as often as they like.
///
/// So purchased time is a BALANCE — credited_seconds — that settlement drains, and
/// last_settled_at only ever moves forward. These tests are about that shape: the
/// seconds come from the server's catalogue, the item is really consumed, and one gem
/// pays once.
///
/// The column and the drain already existed. Nothing had ever added to it, so a gem
/// did nothing at all — which is exactly the kind of half-built path a test that only
/// asks "is it refused?" would never notice.
/// </summary>
[Collection("api")]
public class MysticGemTests(ApiFixture api)
{
    private static void RequireDatabase() => Skip.IfNot(ApiFixture.DatabaseReachable, ApiFixture.SkipReason);

    /// <summary>An hour, per item_data.json.</summary>
    private const string SmallGem = "mystic_gem";

    /// <summary>Seventy-two hours — the one that was reported doing nothing.</summary>
    private const string GiganticGem = "mystic_gem_gigantic";

    [SkippableFact]
    public async Task AGemCreditsTheTimeItIsWorth()
    {
        RequireDatabase();

        var player      = await api.NewPlayerAsync();
        Guid characterId = await OwnershipTests.CreateCharacter(player, "Gemmy");

        await GiveItem(characterId, GiganticGem, 1);

        HttpResponseMessage used = await Use(player, characterId, GiganticGem);

        Assert.Equal(HttpStatusCode.OK, used.StatusCode);

        JsonElement body = JsonDocument.Parse(await used.Content.ReadAsStringAsync()).RootElement;

        // 72 hours, read from the server's own catalogue rather than the request.
        Assert.Equal(259200L, body.GetProperty("grantedSeconds").GetInt64());
        Assert.Equal(259200L, await CreditedSeconds(characterId));
    }

    /// <summary>
    /// The gem leaves the bag.
    ///
    /// Paired with the credit above on purpose: a path that credits without consuming
    /// is one gem worth infinite time, and a path that consumes without crediting is
    /// the bug this endpoint was written to fix.
    /// </summary>
    [SkippableFact]
    public async Task TheGemIsConsumed()
    {
        RequireDatabase();

        var player      = await api.NewPlayerAsync();
        Guid characterId = await OwnershipTests.CreateCharacter(player, "Spendy");

        await GiveItem(characterId, SmallGem, 2);

        await Use(player, characterId, SmallGem);

        Assert.Equal(1L, await Quantity(characterId, SmallGem));
    }

    /// <summary>A gem nobody owns buys nothing.</summary>
    [SkippableFact]
    public async Task AGemYouDoNotHaveIsRefused()
    {
        RequireDatabase();

        var player      = await api.NewPlayerAsync();
        Guid characterId = await OwnershipTests.CreateCharacter(player, "Penniless");

        HttpResponseMessage refused = await Use(player, characterId, GiganticGem);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(0L, await CreditedSeconds(characterId));
    }

    /// <summary>
    /// An item that grants no time is refused, and stays in the bag.
    ///
    /// An endpoint that consumed whatever it was handed and shrugged would be the
    /// worst of both: the item gone and nothing bought.
    /// </summary>
    [SkippableFact]
    public async Task AnItemThatGrantsNoTimeIsRefusedAndKept()
    {
        RequireDatabase();

        var player      = await api.NewPlayerAsync();
        Guid characterId = await OwnershipTests.CreateCharacter(player, "Hoarder");

        await GiveItem(characterId, "tin_ore", 5);

        HttpResponseMessage refused = await Use(player, characterId, "tin_ore");

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(5L, await Quantity(characterId, "tin_ore"));
    }

    /// <summary>Somebody else's character is not yours to spend from.</summary>
    [SkippableFact]
    public async Task AnotherPlayersGemIsNotYours()
    {
        RequireDatabase();

        var owner    = await api.NewPlayerAsync();
        var stranger = await api.NewPlayerAsync();

        Guid characterId = await OwnershipTests.CreateCharacter(owner, "Mine2");

        await GiveItem(characterId, SmallGem, 1);

        HttpResponseMessage refused = await Use(stranger, characterId, SmallGem);

        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        Assert.Equal(1L, await Quantity(characterId, SmallGem));
    }

    /// <summary>
    /// last_settled_at never goes backwards, whatever is bought.
    ///
    /// The invariant the whole design rests on. If a gem could move it, every other
    /// protection in settlement is decoration.
    /// </summary>
    [SkippableFact]
    public async Task TimeNeverMovesBackwards()
    {
        RequireDatabase();

        var player      = await api.NewPlayerAsync();
        Guid characterId = await OwnershipTests.CreateCharacter(player, "Chrono");

        await GiveItem(characterId, GiganticGem, 1);

        DateTime before = await LastSettledAt(characterId);

        await Use(player, characterId, GiganticGem);

        Assert.True(await LastSettledAt(characterId) >= before);
    }

    /// <summary>
    /// THE GEM ACTUALLY PAYS.
    ///
    /// ══ THE TEST THAT WAS MISSING ═══════════════════════════════════════
    ///
    /// Everything above proved the seconds were CREDITED, and every one of them
    /// passed while a gem did nothing whatsoever in the game.
    ///
    /// Using one settles first and then credits, so the settle that follows runs with
    /// an elapsed of very nearly zero -- and the guard read elapsed alone and returned
    /// Nothing before ever looking at the column. The seventy-two hours went in and
    /// stayed there.
    ///
    /// Crediting is not paying. This asks for the payment.
    /// </summary>
    [SkippableFact]
    public async Task AGemActuallyPaysOut()
    {
        RequireDatabase();

        var player      = await api.NewPlayerAsync();
        Guid characterId = await OwnershipTests.CreateCharacter(player, "Cashout");

        // Something to be paid FOR. An idle character earns nothing however much time
        // it is handed, which would make this pass for the wrong reason.
        await SetGathering(player, characterId);

        await GiveItem(characterId, GiganticGem, 1);

        long before = await SkillXp(characterId);

        await Use(player, characterId, GiganticGem);

        // The settle the client runs straight afterwards, with no wall clock between.
        HttpResponseMessage settled = await OwnershipTests.Post(
            player, $"/activity/{characterId}/settle", new { });

        settled.EnsureSuccessStatusCode();

        Assert.True(await SkillXp(characterId) > before,
                    "seventy-two hours were credited and never paid");

        Assert.Equal(0L, await CreditedSeconds(characterId));
    }

    // ── Machinery ─────────────────────────────────────────────────────────────

    /// <summary>Puts the character to work, so there is something for time to buy.</summary>
    private static async Task SetGathering(Player player, Guid characterId)
    {
        (await OwnershipTests.Post(player, $"/activity/{characterId}",
                                   new { nodeId = "tin_rock_1" })).EnsureSuccessStatusCode();
    }

    private async Task<long> SkillXp(Guid characterId)
    {
        await using var connection = await api.OpenDatabaseAsync();
        await using var command = connection.CreateCommand();

        command.CommandText =
            "select coalesce(sum(xp), 0)::bigint from character_skill where character_id = $1;";
        command.Parameters.AddWithValue(characterId);

        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }


    private static async Task<HttpResponseMessage> Use(Player player, Guid characterId, string itemId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/activity/{characterId}/use")
        {
            Content = JsonContent.Create(new { itemId }),
        };

        request.Headers.Add("Idempotency-Key", Player.NewKey());

        return await player.Client.SendAsync(request);
    }

    private async Task GiveItem(Guid characterId, string itemId, long quantity)
    {
        await using var connection = await api.OpenDatabaseAsync();
        await using var command = connection.CreateCommand();

        command.CommandText =
            """
            insert into inventory_slot (character_id, slot_index, item_id, quantity)
            values ($1, 0, $2, $3)
            on conflict (character_id, slot_index)
              do update set item_id = excluded.item_id, quantity = excluded.quantity;
            """;

        command.Parameters.AddWithValue(characterId);
        command.Parameters.AddWithValue(itemId);
        command.Parameters.AddWithValue(quantity);

        await command.ExecuteNonQueryAsync();
    }

    private async Task<long> Quantity(Guid characterId, string itemId)
    {
        await using var connection = await api.OpenDatabaseAsync();
        await using var command = connection.CreateCommand();

        command.CommandText =
            "select coalesce(sum(quantity), 0)::bigint from inventory_slot " +
            "where character_id = $1 and item_id = $2;";

        command.Parameters.AddWithValue(characterId);
        command.Parameters.AddWithValue(itemId);

        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }

    private async Task<long> CreditedSeconds(Guid characterId)
    {
        await using var connection = await api.OpenDatabaseAsync();
        await using var command = connection.CreateCommand();

        command.CommandText = "select credited_seconds from activity where character_id = $1;";
        command.Parameters.AddWithValue(characterId);

        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }

    private async Task<DateTime> LastSettledAt(Guid characterId)
    {
        await using var connection = await api.OpenDatabaseAsync();
        await using var command = connection.CreateCommand();

        command.CommandText = "select last_settled_at from activity where character_id = $1;";
        command.Parameters.AddWithValue(characterId);

        return (DateTime)(await command.ExecuteScalarAsync())!;
    }
}
