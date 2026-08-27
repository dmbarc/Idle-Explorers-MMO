using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Npgsql;
using Xunit;

namespace IdleExplorers.Api.Tests;

/// <summary>
/// Time turning into ore, over real HTTP, against a real database.
///
/// ══ HOW TIME MOVES HERE ═══════════════════════════════════════════════════════
///
/// Not by waiting, and not by editing last_settled_at. The first version of these
/// tests faked an elapsed hour by pushing that column into the past, and the trigger
/// refused — correctly, since settling a window and then winding the clock back is
/// the cheapest duplication exploit there is, and a test helper is not exempt.
///
/// So the server's clock is injected and these advance it FORWARD, which is the
/// direction time goes in production. The trigger stays armed for the whole run
/// rather than being worked around.
///
/// ══ WHAT IS NEVER SENT ════════════════════════════════════════════════════════
///
/// A reward, a quantity, a duration, or a timestamp. Every test here asks the server
/// what happened and checks the answer against what the rules say should have.
/// </summary>
[Collection("api")]
public class SettlementTests(ApiFixture api)
{
    private static void RequireDatabase() => Skip.IfNot(ApiFixture.DatabaseReachable, ApiFixture.SkipReason);

    /// <summary>A tin rock in the goblin camp, as zone_data.json declares it.</summary>
    private const string TinRock = "tin_rock_1";

    // ── Doing something ───────────────────────────────────────────────────────

    [SkippableFact]
    public async Task ACharacterCanBeSetToMine()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await OwnershipTests.CreateCharacter(player, "Bricta");

        var response = await OwnershipTests.Post(player, $"/activity/{character}", new { nodeId = TinRock });
        response.EnsureSuccessStatusCode();

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("gather", body.RootElement.GetProperty("kind").GetString());
        Assert.Equal(TinRock,  body.RootElement.GetProperty("nodeId").GetString());
    }

    /// <summary>
    /// The node is looked up in content. A client that invents one gets nothing —
    /// which matters because everything about a node IS its rates.
    /// </summary>
    [SkippableFact]
    public async Task AnInventedNodeIsRefused()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await OwnershipTests.CreateCharacter(player, "Bricta");

        var response = await OwnershipTests.Post(player, $"/activity/{character}",
                                                 new { nodeId = "infinite_diamond_rock" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [SkippableFact]
    public async Task OneAccountCannotSetAnotherAccountsActivity()
    {
        RequireDatabase();

        await using var owner = await api.NewPlayerAsync();
        await using var thief = await api.NewPlayerAsync();

        Guid character = await OwnershipTests.CreateCharacter(owner, "Bricta");

        var response = await OwnershipTests.Post(thief, $"/activity/{character}", new { nodeId = TinRock });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── Earning ───────────────────────────────────────────────────────────────

    /// <summary>
    /// An hour away from the keyboard, paid at the offline rate.
    ///
    /// The figure is not hard-coded here — it is recomputed from the node the server
    /// actually loaded, so a content edit changes both sides at once and this keeps
    /// testing the mechanism rather than a number somebody typed twice.
    /// </summary>
    [SkippableFact]
    public async Task AnHourOfflineProducesTheOfflineRate()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await OwnershipTests.CreateCharacter(player, "Bricta");

        await StartMining(player, character);
        Advance(TimeSpan.FromHours(1));

        var outcome = await Settle(player, character);

        (float seconds, float active, float afk) = await NodeRates(character);
        long expected = (long)(3600d / (seconds / (active * afk)));

        Assert.Equal(expected, outcome.GetProperty("actions").GetInt64());
        Assert.Equal(expected, outcome.GetProperty("items").GetProperty("tin_ore").GetInt64());

        // Nobody was watching, so nothing counts toward the boss gate.
        Assert.Equal(0L, outcome.GetProperty("supervisedActions").GetInt64());
    }

    /// <summary>
    /// ══ THE INVARIANT THE WHOLE DESIGN RESTS ON ═══════════════════════════════
    ///
    /// Settling often must pay exactly what settling once pays. Otherwise a chatty
    /// client earns differently from a quiet one, and the difference is something a
    /// player can tune.
    /// </summary>
    [SkippableFact]
    public async Task SettlingTenTimesPaysWhatSettlingOncePays()
    {
        RequireDatabase();

        await using var patient = await api.NewPlayerAsync();
        await using var chatty  = await api.NewPlayerAsync();

        Guid a = await OwnershipTests.CreateCharacter(patient, "Patient");
        Guid b = await OwnershipTests.CreateCharacter(chatty,  "Chatty");

        await StartMining(patient, a);
        await StartMining(chatty,  b);

        // One shared hour. The chatty one settles every six minutes of it; the
        // patient one waits and settles at the end. Same window, same world.
        long piecemeal = 0;

        for (int i = 0; i < 10; i++)
        {
            Advance(TimeSpan.FromMinutes(6));
            piecemeal += (await Settle(chatty, b)).GetProperty("actions").GetInt64();
        }

        long once = (await Settle(patient, a)).GetProperty("actions").GetInt64();

        Assert.Equal(once, piecemeal);
    }

    /// <summary>
    /// Settling twice in a row pays the second time nothing. The timestamp is the
    /// bookkeeping — no idempotency key required for this to hold.
    /// </summary>
    [SkippableFact]
    public async Task SettlingTwiceImmediatelyPaysOnce()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await OwnershipTests.CreateCharacter(player, "Bricta");

        await StartMining(player, character);
        Advance(TimeSpan.FromHours(1));

        long first  = (await Settle(player, character)).GetProperty("actions").GetInt64();
        long second = (await Settle(player, character)).GetProperty("actions").GetInt64();

        Assert.True(first > 0);
        Assert.Equal(0L, second);
    }

    /// <summary>
    /// A heartbeat buys the ACTIVE rate for the window it covers, and nothing else
    /// does. The client never states how long it played.
    /// </summary>
    [SkippableFact]
    public async Task AHeartbeatEarnsTheActiveRate()
    {
        RequireDatabase();

        await using var watched = await api.NewPlayerAsync();
        await using var away    = await api.NewPlayerAsync();

        Guid a = await OwnershipTests.CreateCharacter(watched, "Watched");
        Guid b = await OwnershipTests.CreateCharacter(away,    "Away");

        await StartMining(watched, a);
        await StartMining(away,    b);

        // One beat, then thirty seconds of elapsed time -- inside the cover window.
        await OwnershipTests.Post(watched, $"/activity/{a}/beat", new { });

        Advance(TimeSpan.FromSeconds(30));
        Advance(TimeSpan.FromSeconds(30));

        long supervised   = (await Settle(watched, a)).GetProperty("actions").GetInt64();
        long unsupervised = (await Settle(away,    b)).GetProperty("actions").GetInt64();

        Assert.True(supervised > unsupervised,
                    $"watched earned {supervised}, away earned {unsupervised}");
    }

    /// <summary>
    /// The cap exists so a fortnight away does not hand out quantities that
    /// trivialise the game.
    /// </summary>
    [SkippableFact]
    public async Task AFortnightAwayPaysOneDay()
    {
        RequireDatabase();

        await using var week = await api.NewPlayerAsync();
        await using var day  = await api.NewPlayerAsync();

        Guid a = await OwnershipTests.CreateCharacter(week, "Fortnight");
        Guid b = await OwnershipTests.CreateCharacter(day,  "OneDay");

        // The fortnight character starts thirteen days earlier, so when both settle
        // together one has been away fourteen days and the other exactly one.
        await StartMining(week, a);
        Advance(TimeSpan.FromDays(13));

        await StartMining(day, b);
        Advance(TimeSpan.FromDays(1));

        long fortnight = (await Settle(week, a)).GetProperty("actions").GetInt64();
        long oneDay    = (await Settle(day,  b)).GetProperty("actions").GetInt64();

        Assert.Equal(oneDay, fortnight);
    }

    /// <summary>
    /// The bag fills and gathering stops at the bag, not at the rate. Experience is
    /// still paid — the character swung the pickaxe, and losing the xp too would
    /// punish them twice for one storage mistake.
    /// </summary>
    [SkippableFact]
    public async Task AFullBagCapsTheOreButNotTheExperience()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await OwnershipTests.CreateCharacter(player, "Hoarder");

        await StartMining(player, character);
        await FillBagWithJunk(character);

        Advance(TimeSpan.FromHours(4));
        var outcome = await Settle(player, character);

        Assert.True(outcome.GetProperty("stoppedForRoom").GetBoolean());
        Assert.True(outcome.GetProperty("lostToFullInventory").GetInt64() > 0);
        Assert.True(outcome.GetProperty("xpGained").GetInt64() > 0);
    }

    // ── The ledger ────────────────────────────────────────────────────────────

    /// <summary>
    /// Every item that appears has a ledger row saying where it came from. The
    /// inventory answers "what do they have"; the ledger answers "why".
    /// </summary>
    [SkippableFact]
    public async Task EveryGatheredItemIsExplainedByTheLedger()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await OwnershipTests.CreateCharacter(player, "Bricta");

        await StartMining(player, character);
        Advance(TimeSpan.FromHours(2));

        long granted = (await Settle(player, character))
            .GetProperty("items").GetProperty("tin_ore").GetInt64();

        await using var db = await api.OpenDatabaseAsync();

        long held   = await Scalar(db,
            "select coalesce(sum(quantity),0) from inventory_slot where character_id = $1 and item_id = 'tin_ore';",
            character);

        long ledger = await Scalar(db,
            "select coalesce(sum(delta),0) from item_ledger where character_id = $1 and item_id = 'tin_ore';",
            character);

        Assert.Equal(granted, held);
        Assert.Equal(held, ledger);
    }

    /// <summary>
    /// Experience is stored once, as xp. Level is derived — never a second column
    /// that can drift from the first.
    /// </summary>
    [SkippableFact]
    public async Task ExperienceLandsOnTheSkillAndAQuarterReachesTheCharacter()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await OwnershipTests.CreateCharacter(player, "Bricta");

        await StartMining(player, character);
        Advance(TimeSpan.FromHours(3));

        long xpGained = (await Settle(player, character)).GetProperty("xpGained").GetInt64();
        Assert.True(xpGained > 0);

        await using var db = await api.OpenDatabaseAsync();

        long skillXp = await Scalar(db,
            "select xp from character_skill where character_id = $1 and skill_id = 'mining';", character);
        long charXp  = await Scalar(db,
            "select xp from character where id = $1;", character);

        Assert.Equal(xpGained, skillXp);
        Assert.Equal(xpGained / 4, charXp);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task StartMining(Player player, Guid character)
    {
        var response = await OwnershipTests.Post(player, $"/activity/{character}", new { nodeId = TinRock });
        response.EnsureSuccessStatusCode();
    }

    private static async Task<JsonElement> Settle(Player player, Guid character)
    {
        var response = await OwnershipTests.Post(player, $"/activity/{character}/settle", new { });
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    /// <summary>
    /// Moves the world forward.
    ///
    /// Forward, never backward. An earlier version of these tests faked an elapsed
    /// hour by pushing last_settled_at into the past and the trigger refused it —
    /// see ApiFixture.Clock for why that refusal was correct and worth keeping.
    /// </summary>
    private void Advance(TimeSpan by) => api.Clock.Advance(by);

    private async Task<(float seconds, float active, float afk)> NodeRates(Guid character)
    {
        await using var db = await api.OpenDatabaseAsync();
        await using var command = db.CreateCommand();

        command.CommandText = """
            select seconds_per_action, active_rate_multi, afk_rate_multi
              from activity where character_id = $1;
            """;
        command.Parameters.AddWithValue(character);

        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();

        return (reader.GetFloat(0), reader.GetFloat(1), reader.GetFloat(2));
    }

    /// <summary>Fills every slot with something that is not what is being mined.</summary>
    private async Task FillBagWithJunk(Guid character)
    {
        await using var db = await api.OpenDatabaseAsync();

        for (int slot = 0; slot < 30; slot++)
        {
            await using var command = db.CreateCommand();

            command.CommandText = """
                insert into inventory_slot (character_id, slot_index, item_id, quantity)
                values ($1, $2, 'bones', 1)
                on conflict (character_id, slot_index) do nothing;
                """;
            command.Parameters.AddWithValue(character);
            command.Parameters.AddWithValue(slot);

            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task<long> Scalar(NpgsqlConnection db, string sql, Guid id)
    {
        await using var command = db.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue(id);

        object result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? 0L : Convert.ToInt64(result);
    }
}
