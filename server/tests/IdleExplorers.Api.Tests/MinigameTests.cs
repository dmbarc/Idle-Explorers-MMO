using System;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Npgsql;
using Xunit;

namespace IdleExplorers.Api.Tests;

/// <summary>
/// The minigame over HTTP, and what a perfect liar gets.
///
/// The honest premise: a scripted client CAN claim perfect on every action, and no
/// timing game prevents that. What these assert is the ceiling — a cheating client
/// receives exactly the design cap and not one ore more, and cannot use the
/// minigame to make time run faster.
/// </summary>
[Collection("api")]
public class MinigameApiTests(ApiFixture api)
{
    private static void RequireDatabase() => Skip.IfNot(ApiFixture.DatabaseReachable, ApiFixture.SkipReason);

    private const string TinRock = "tin_rock_1";

    // ── Playing it ────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task PlayingWellPaysABonus()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await OwnershipTests.CreateCharacter(player, "Rhythmic");

        await StartMining(player, character);
        api.Clock.Advance(TimeSpan.FromMinutes(10));

        JsonElement result = await Report(player, character, Repeat("perfect", 60));

        long bonus = result.GetProperty("bonusActions").GetInt64();

        Assert.True(bonus > 0, "a perfect run paid nothing");

        // The bonus ore is really in the bag, through the same stacking rules.
        await using var db = await api.OpenDatabaseAsync();

        long settled = OwnershipTests.StackQuantity(
            result.GetProperty("settled"), "items", "tin_ore");
        long extra = OwnershipTests.StackQuantity(
            result.GetProperty("bonus"), "items", "tin_ore");

        Assert.Equal(settled + extra, await Held(db, character, "tin_ore"));
        Assert.Equal(bonus, extra);
    }

    [SkippableFact]
    public async Task MissingEverythingPaysNothingExtra()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await OwnershipTests.CreateCharacter(player, "Clumsy");

        await StartMining(player, character);
        api.Clock.Advance(TimeSpan.FromMinutes(10));

        JsonElement result = await Report(player, character, Repeat("miss", 60));

        Assert.Equal(0L, result.GetProperty("bonusActions").GetInt64());
    }

    /// <summary>
    /// Playing badly must not be worse than not playing. This is an idle game: the
    /// baseline is leaving, and a minigame that can lose you ore is a minigame that
    /// punishes engagement.
    /// </summary>
    [SkippableFact]
    public async Task PlayingBadlyIsNeverWorseThanNotPlaying()
    {
        RequireDatabase();

        await using var quiet   = await api.NewPlayerAsync();
        await using var clumsy  = await api.NewPlayerAsync();

        Guid a = await OwnershipTests.CreateCharacter(quiet,  "Quiet");
        Guid b = await OwnershipTests.CreateCharacter(clumsy, "Clumsy");

        await StartMining(quiet, a);
        await StartMining(clumsy, b);

        api.Clock.Advance(TimeSpan.FromMinutes(10));

        // One settles plainly; the other reports sixty misses.
        var plain = await OwnershipTests.Post(quiet, $"/activity/{a}/settle", new { });
        plain.EnsureSuccessStatusCode();

        using var plainBody = JsonDocument.Parse(await plain.Content.ReadAsStringAsync());
        long quietOre = OwnershipTests.StackQuantity(plainBody.RootElement, "items", "tin_ore");

        JsonElement missed = await Report(clumsy, b, Repeat("miss", 60));
        long clumsyOre = OwnershipTests.StackQuantity(missed.GetProperty("settled"), "items", "tin_ore");

        Assert.Equal(quietOre, clumsyOre);
    }

    // ══ The ceiling ═══════════════════════════════════════════════════════════

    /// <summary>
    /// ══ THE ONE THAT MATTERS ══════════════════════════════════════════════════
    ///
    /// A scripted client claiming perfect on everything gets the design cap. Sixty
    /// per cent over the active rate, which is roughly what being present already
    /// pays — enough that sitting at the rock beats leaving, not so much that
    /// leaving feels like a mistake.
    /// </summary>
    [SkippableFact]
    public async Task APerfectLiarGetsExactlyTheDesignCap()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await OwnershipTests.CreateCharacter(player, "Scripted");

        await StartMining(player, character);

        // Three minutes: 3 seconds an action at 0.6 offline is 36 actions.
        api.Clock.Advance(TimeSpan.FromMinutes(3));

        JsonElement result = await Report(player, character, Repeat("perfect", 60));

        long settled = result.GetProperty("settled").GetProperty("actions").GetInt64();
        long bonus   = result.GetProperty("bonusActions").GetInt64();

        Assert.Equal(36L, settled);

        // 36 actions at 1.6x is 57.6, of which 36 was already paid: 21 after flooring.
        Assert.Equal(21L, bonus);
    }

    /// <summary>
    /// More grades than the window produced buys nothing. The server's own action
    /// count is the ceiling; the report only says how well those actions went.
    /// </summary>
    [SkippableFact]
    public async Task ClaimingMoreGradesThanActionsBuysNothingExtra()
    {
        RequireDatabase();

        await using var honest = await api.NewPlayerAsync();
        await using var liar   = await api.NewPlayerAsync();

        Guid a = await OwnershipTests.CreateCharacter(honest, "Honest");
        Guid b = await OwnershipTests.CreateCharacter(liar,   "Liar");

        await StartMining(honest, a);
        await StartMining(liar,   b);

        // Ten seconds: three actions at the offline rate.
        api.Clock.Advance(TimeSpan.FromSeconds(10));

        long fair    = (await Report(honest, a, Repeat("perfect", 3))).GetProperty("bonusActions").GetInt64();
        long claimed = (await Report(liar,   b, Repeat("perfect", 60))).GetProperty("bonusActions").GetInt64();

        Assert.Equal(fair, claimed);
    }

    [SkippableFact]
    public async Task AnUnboundedReportIsRefused()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await OwnershipTests.CreateCharacter(player, "Flooder");

        await StartMining(player, character);
        api.Clock.Advance(TimeSpan.FromHours(1));

        var response = await OwnershipTests.Post(player, $"/activity/{character}/minigame",
                                                 new { grades = Repeat("perfect", 5000) });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// Reporting repeatedly against the same elapsed time earns once, because the
    /// second settle produced no actions to grade.
    /// </summary>
    [SkippableFact]
    public async Task ReportingTwiceForOneWindowPaysOnce()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await OwnershipTests.CreateCharacter(player, "Repeater");

        await StartMining(player, character);
        api.Clock.Advance(TimeSpan.FromMinutes(10));

        long first  = (await Report(player, character, Repeat("perfect", 60))).GetProperty("bonusActions").GetInt64();
        long second = (await Report(player, character, Repeat("perfect", 60))).GetProperty("bonusActions").GetInt64();
        long third  = (await Report(player, character, Repeat("perfect", 60))).GetProperty("bonusActions").GetInt64();

        Assert.True(first > 0L);
        Assert.Equal(0L, second);
        Assert.Equal(0L, third);
    }

    [SkippableFact]
    public async Task AnInventedGradeEarnsNothing()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await OwnershipTests.CreateCharacter(player, "Creative");

        await StartMining(player, character);
        api.Clock.Advance(TimeSpan.FromMinutes(10));

        JsonElement result = await Report(player, character, Repeat("transcendent", 60));

        Assert.Equal(0L, result.GetProperty("bonusActions").GetInt64());
    }

    [SkippableFact]
    public async Task OneAccountCannotReportForAnother()
    {
        RequireDatabase();

        await using var owner = await api.NewPlayerAsync();
        await using var thief = await api.NewPlayerAsync();

        Guid character = await OwnershipTests.CreateCharacter(owner, "Victim");
        await StartMining(owner, character);

        var response = await OwnershipTests.Post(thief, $"/activity/{character}/minigame",
                                                 new { grades = Repeat("perfect", 10) });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── Crafting ──────────────────────────────────────────────────────────────

    /// <summary>
    /// A bonus craft still costs its materials. The reward is TIME — the player got
    /// further through the queue — and free output would make the anvil a printing
    /// press for anyone who can keep rhythm.
    /// </summary>
    [SkippableFact]
    public async Task ABonusCraftStillSpendsItsOre()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await OwnershipTests.CreateCharacter(player, "Drummer");

        await Give(character, "tin_ore", 400);

        (await OwnershipTests.Post(player, $"/activity/{character}/craft",
                                   new { recipeId = "smelt_tin_bar" })).EnsureSuccessStatusCode();

        api.Clock.Advance(TimeSpan.FromMinutes(5));

        JsonElement result = await Report(player, character, Repeat("perfect", 60));

        long bonus = result.GetProperty("bonusActions").GetInt64();
        Assert.True(bonus > 0L, "a perfect smelting run paid nothing");

        await using var db = await api.OpenDatabaseAsync();

        long bars = await Held(db, character, "tin_bar");
        long ore  = await Held(db, character, "tin_ore");

        // Two ore per bar, and every ore accounted for either as ore or as a bar.
        Assert.Equal(400L, ore + bars * 2L);
    }

    /// <summary>
    /// Running out of ore caps the bonus at what the materials allowed, rather than
    /// producing bars from nothing.
    /// </summary>
    [SkippableFact]
    public async Task ABonusCraftCannotExceedTheMaterials()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await OwnershipTests.CreateCharacter(player, "Frugal");

        // Exactly two ore: one bar, and nothing spare for a bonus.
        await Give(character, "tin_ore", 2);

        (await OwnershipTests.Post(player, $"/activity/{character}/craft",
                                   new { recipeId = "smelt_tin_bar" })).EnsureSuccessStatusCode();

        api.Clock.Advance(TimeSpan.FromMinutes(5));
        await Report(player, character, Repeat("perfect", 60));

        await using var db = await api.OpenDatabaseAsync();

        Assert.Equal(1L, await Held(db, character, "tin_bar"));
        Assert.Equal(0L, await Held(db, character, "tin_ore"));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string[] Repeat(string grade, int count) =>
        Enumerable.Repeat(grade, count).ToArray();

    private static async Task StartMining(Player player, Guid character) =>
        (await OwnershipTests.Post(player, $"/activity/{character}", new { nodeId = TinRock }))
            .EnsureSuccessStatusCode();

    private static async Task<JsonElement> Report(Player player, Guid character, string[] grades)
    {
        var response = await OwnershipTests.Post(player, $"/activity/{character}/minigame",
                                                 new { grades });
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private async Task Give(Guid character, string itemId, long quantity)
    {
        await using var db = await api.OpenDatabaseAsync();
        await using var command = db.CreateCommand();

        command.CommandText = """
            insert into inventory_slot (character_id, slot_index, item_id, quantity)
            values ($1, 0, $2, $3)
            on conflict (character_id, slot_index) do update
               set item_id = excluded.item_id, quantity = excluded.quantity;
            """;
        command.Parameters.AddWithValue(character);
        command.Parameters.AddWithValue(itemId);
        command.Parameters.AddWithValue(quantity);

        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> Held(NpgsqlConnection db, Guid character, string itemId)
    {
        await using var command = db.CreateCommand();

        command.CommandText = """
            select coalesce(sum(quantity), 0) from inventory_slot
             where character_id = $1 and item_id = $2;
            """;
        command.Parameters.AddWithValue(character);
        command.Parameters.AddWithValue(itemId);

        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}
