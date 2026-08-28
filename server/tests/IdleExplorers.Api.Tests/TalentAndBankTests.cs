using System;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace IdleExplorers.Api.Tests;

/// <summary>
/// The two things that were Critical-tier and still lived on the client.
///
/// Talent points are stats, stats are damage per second, and damage per second decides
/// both farming speed and whether the enrage timer is beaten -- so a client that could
/// grant itself points could grant itself the whole game without touching a coin. The
/// bank is items.
///
/// Neither is exotic. They are tested here because they were MISSED, and a gap found
/// once is worth a test that finds it again.
/// </summary>
[Collection("api")]
public class TalentAndBankTests(ApiFixture api)
{
    private static void RequireDatabase() => Skip.IfNot(ApiFixture.DatabaseReachable, ApiFixture.SkipReason);

    // ══ TALENTS ═══════════════════════════════════════════════════════════════

    [SkippableFact]
    public async Task ANewCharacterHasNoPointsAndNoWayToGetThem()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await OwnershipTests.CreateCharacter(player, "Fresh");

        JsonElement sheet = await Talents(player, character);

        Assert.Equal(1, sheet.GetProperty("level").GetInt32());
        Assert.Equal(0, sheet.GetProperty("total").GetInt32());
        Assert.Equal(0, sheet.GetProperty("available").GetInt32());

        // There is no endpoint that grants a point, which is the design: available
        // points are DERIVED from level minus spend, so there is no balance to inflate.
        Assert.Equal(0, sheet.GetProperty("ranks").GetArrayLength());
    }

    [SkippableFact]
    public async Task PointsCannotBeSpentWithoutLevels()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await OwnershipTests.CreateCharacter(player, "Eager");

        string node = await FirstNodeAsync(character);

        Skip.If(node == null, "No class with a talent tree is authored.");

        var refused = await OwnershipTests.Post(player, $"/talent/{character}",
                                                new { nodeId = node });

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

        Assert.Equal(0, (await Talents(player, character)).GetProperty("ranks").GetArrayLength());
    }

    [SkippableFact]
    public async Task APointIsSpentOnceAndTheSpendIsDerivedNotStored()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await OwnershipTests.CreateCharacter(player, "Studious");

        string node = await FirstNodeAsync(character);
        Skip.If(node == null, "No class with a talent tree is authored.");

        await GiveLevels(character, 10);

        JsonElement before = await Talents(player, character);
        int available = before.GetProperty("available").GetInt32();

        Assert.True(available > 0);

        var response = await OwnershipTests.Post(player, $"/talent/{character}", new { nodeId = node });
        response.EnsureSuccessStatusCode();

        JsonElement after = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(1, after.GetProperty("ranks").GetArrayLength());
        Assert.True(after.GetProperty("spent").GetInt32() > 0);

        // total - spent, every time it is asked. Nothing stores a balance, so nothing
        // can drift out of step with the rows -- the failure BackfillCharacterXP exists
        // to repair.
        Assert.Equal(after.GetProperty("total").GetInt32() - after.GetProperty("spent").GetInt32(),
                     after.GetProperty("available").GetInt32());
    }

    [SkippableFact]
    public async Task AMadeUpTalentIsRefused()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await OwnershipTests.CreateCharacter(player, "Inventive");

        await GiveLevels(character, 50);

        var refused = await OwnershipTests.Post(player, $"/talent/{character}",
                                                new { nodeId = "become_invincible" });

        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
    }

    [SkippableFact]
    public async Task ARespecReturnsEveryPoint()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await OwnershipTests.CreateCharacter(player, "Fickle");

        string node = await FirstNodeAsync(character);
        Skip.If(node == null, "No class with a talent tree is authored.");

        await GiveLevels(character, 10);

        (await OwnershipTests.Post(player, $"/talent/{character}", new { nodeId = node }))
            .EnsureSuccessStatusCode();

        using var request = new System.Net.Http.HttpRequestMessage(
            System.Net.Http.HttpMethod.Delete, $"/talent/{character}");

        // A DELETE mutates, so the idempotency middleware wants a key like any other
        // write. Omitting it is a 400, which is the middleware being right.
        request.Headers.Add("Idempotency-Key", Player.NewKey());

        var wiped = await player.Client.SendAsync(request);
        wiped.EnsureSuccessStatusCode();

        JsonElement after = JsonDocument.Parse(await wiped.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(0, after.GetProperty("spent").GetInt32());
        Assert.Equal(0, after.GetProperty("ranks").GetArrayLength());
        Assert.Equal(after.GetProperty("total").GetInt32(), after.GetProperty("available").GetInt32());
    }

    [SkippableFact]
    public async Task NobodyCanSpendAnotherPlayersPoints()
    {
        RequireDatabase();

        await using var owner = await api.NewPlayerAsync();
        await using var thief = await api.NewPlayerAsync();

        Guid character = await OwnershipTests.CreateCharacter(owner, "Owner");
        await GiveLevels(character, 10);

        string node = await FirstNodeAsync(character);
        Skip.If(node == null, "No class with a talent tree is authored.");

        var refused = await OwnershipTests.Post(thief, $"/talent/{character}", new { nodeId = node });

        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);

        var read = await thief.Client.GetAsync($"/talent/{character}");
        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
    }

    /// <summary>
    /// The point of talents: they change what the SERVER computes.
    ///
    /// ══ WHY THIS IS THE TEST THAT MATTERS ═════════════════════════════════════
    ///
    /// Everything above proves points are stored and cannot be conjured. None of it
    /// proves they DO anything -- and the endpoints shipped first with the settlement
    /// service still ignoring the table, so a player could spend into a damage tree and
    /// farm at exactly the same rate.
    ///
    /// A talent that persists and has no effect is worse than no talent system, because
    /// it looks like one.
    /// </summary>
    [SkippableFact]
    public async Task ASpentPointChangesWhatTheServerPaysOut()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await OwnershipTests.CreateCharacter(player, "Sharpened");

        string node = await DamageNodeAsync(character);
        Skip.If(node == null, "No damage-percent talent is authored.");

        await GiveLevels(character, 30);

        // A measured hour of fighting, before.
        long before = await FightForAnHour(player, character);

        Skip.If(before <= 0L, "The character kills nothing, so there is nothing to improve.");

        (await OwnershipTests.Post(player, $"/talent/{character}", new { nodeId = node }))
            .EnsureSuccessStatusCode();

        long after = await FightForAnHour(player, character);

        Assert.True(after > before,
                    $"an attack-damage talent paid {after} kills against {before} without it");
    }

    /// <summary>
    /// One hour of goblins, settled, counted in kills.
    ///
    /// The clock moves FORWARD, as it does in production -- the trigger refuses a
    /// backwards last_settled_at, correctly.
    /// </summary>
    private async Task<long> FightForAnHour(Player player, Guid character)
    {
        (await OwnershipTests.Post(player, $"/activity/{character}/fight",
                                   new { monsterId = "goblin" })).EnsureSuccessStatusCode();

        api.Clock.Advance(TimeSpan.FromHours(1));

        var settled = await OwnershipTests.Post(player, $"/activity/{character}/settle", new { });
        settled.EnsureSuccessStatusCode();

        return Body(await settled.Content.ReadAsStringAsync()).GetProperty("actions").GetInt64();
    }

    /// <summary>A first-tier node that raises attack damage, or null if none is authored.</summary>
    private async Task<string> DamageNodeAsync(Guid character)
    {
        foreach (var entry in api.Content.Catalogue.Classes.Values)
        {
            if (entry?.talentTree == null) continue;

            foreach (var node in entry.talentTree)
            {
                if (node == null || node.tier != 0)                      continue;
                if (node.effectType != "attackDamagePercent")            continue;
                if (!string.IsNullOrEmpty(node.abilityId))               continue;
                if (node.effectValue <= 0f)                              continue;

                await SetClass(character, entry.id);
                return node.id;
            }
        }

        return null;
    }

    /// <summary>
    /// A SECOND class's talents can be spent in.
    ///
    /// ══ THE BUG THIS EXISTS FOR ════════════════════════════════════════
    ///
    /// Multi-classing lived entirely on the client. CharacterData carried a list of
    /// class ids; the server had one class_id column and built the talent trees from
    /// it alone.
    ///
    /// So unlocking a second class worked, its tree drew, and putting a point in it
    /// answered "no such talent" -- which was the server telling the truth about a
    /// class nobody had ever told it about.
    /// </summary>
    [SkippableFact]
    public async Task ASecondClassesTalentsCanBeSpent()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await OwnershipTests.CreateCharacter(player, "Versatile");

        // A node from a class this character does NOT start as.
        (string classId, string nodeId) = await ForeignNodeAsync(character);

        Skip.If(nodeId == null, "Fewer than two classes with talent trees are authored.");

        await GiveLevels(character, 20);

        // Before taking the class, the server has never heard of that tree.
        var refused = await OwnershipTests.Post(player, $"/talent/{character}", new { nodeId });

        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);

        // Take it.
        (await OwnershipTests.Post(player, $"/character/{character}/class", new { classId }))
            .EnsureSuccessStatusCode();

        // And now it is a tree this character has.
        var allowed = await OwnershipTests.Post(player, $"/talent/{character}", new { nodeId });

        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    /// <summary>
    /// A tier-0 node belonging to a class this character is NOT.
    ///
    /// Paired with the refusal above: without the "not" this would pass on the
    /// character's own tree and prove nothing about multi-classing.
    /// </summary>
    private async Task<(string ClassId, string NodeId)> ForeignNodeAsync(Guid character)
    {
        string mine = await PrimaryClassAsync(character);

        foreach (var entry in api.Content.Catalogue.Classes.Values)
        {
            if (entry?.talentTree == null || entry.id == mine) continue;

            foreach (var node in entry.talentTree)
            {
                if (node == null || node.tier != 0) continue;
                if (string.IsNullOrEmpty(node.id))  continue;

                return (entry.id, node.id);
            }
        }

        return (null, null);
    }

    private async Task<string> PrimaryClassAsync(Guid character)
    {
        await using var db = await api.OpenDatabaseAsync();
        await using var command = db.CreateCommand();

        command.CommandText = "select coalesce(class_id, '') from character where id = $1;";
        command.Parameters.AddWithValue(character);

        return (string)(await command.ExecuteScalarAsync() ?? "");
    }

    // ══ THE BANK ══════════════════════════════════════════════════════════════

    [SkippableFact]
    public async Task ItemsMoveIntoTheBankAndBackOut()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await OwnershipTests.CreateCharacter(player, "Saver");

        await GiveItems(character, "tin_ore", 500);

        var deposited = await OwnershipTests.Post(player, $"/bank/{character}/deposit",
                                                  new { itemId = "tin_ore", quantity = 300 });
        deposited.EnsureSuccessStatusCode();

        Assert.Equal(300L, Body(await deposited.Content.ReadAsStringAsync()).GetProperty("moved").GetInt64());

        Assert.Equal(300L, await BankHolds(player, "tin_ore"));
        Assert.Equal(200L, await BagHolds(character, "tin_ore"));

        var withdrawn = await OwnershipTests.Post(player, $"/bank/{character}/withdraw",
                                                  new { itemId = "tin_ore", quantity = 100 });
        withdrawn.EnsureSuccessStatusCode();

        Assert.Equal(200L, await BankHolds(player, "tin_ore"));
        Assert.Equal(300L, await BagHolds(character, "tin_ore"));
    }

    [SkippableFact]
    public async Task NothingIsConjuredAndNothingIsLost()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await OwnershipTests.CreateCharacter(player, "Careful");

        await GiveItems(character, "tin_ore", 1000);

        // Back and forth several times. The invariant is the only thing that matters:
        // whatever the split, the total is the total.
        //
        // The quantities are chosen so the bag never empties -- each cycle moves 400 in
        // and 400 back out. An earlier version deposited more than it withdrew and ran
        // the bag dry on the fourth pass, which failed as a 409 and looked like a bug
        // in the endpoint rather than in the test's arithmetic.
        for (int i = 0; i < 5; i++)
        {
            (await OwnershipTests.Post(player, $"/bank/{character}/deposit",
                                       new { itemId = "tin_ore", quantity = 400 })).EnsureSuccessStatusCode();

            (await OwnershipTests.Post(player, $"/bank/{character}/withdraw",
                                       new { itemId = "tin_ore", quantity = 400 })).EnsureSuccessStatusCode();
        }

        // And once more, left split across both, so the assertion below is about a
        // total that genuinely spans the two containers.
        (await OwnershipTests.Post(player, $"/bank/{character}/deposit",
                                   new { itemId = "tin_ore", quantity = 350 })).EnsureSuccessStatusCode();

        Assert.Equal(1000L, await BankHolds(player, "tin_ore") + await BagHolds(character, "tin_ore"));
    }

    [SkippableFact]
    public async Task DepositingMoreThanYouHaveIsRefused()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await OwnershipTests.CreateCharacter(player, "Optimist");

        await GiveItems(character, "tin_ore", 10);

        var refused = await OwnershipTests.Post(player, $"/bank/{character}/deposit",
                                                new { itemId = "tin_ore", quantity = 1_000_000 });

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

        // Nothing moved, and nothing was created on the way to being refused.
        Assert.Equal(0L,  await BankHolds(player, "tin_ore"));
        Assert.Equal(10L, await BagHolds(character, "tin_ore"));
    }

    [SkippableFact]
    public async Task DepositingSomethingYouDoNotHaveIsRefused()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await OwnershipTests.CreateCharacter(player, "Chancer");

        var refused = await OwnershipTests.Post(player, $"/bank/{character}/deposit",
                                                new { itemId = "kings_crown", quantity = 1 });

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(0L, await BankHolds(player, "kings_crown"));
    }

    [SkippableFact]
    public async Task TheBankIsSharedAcrossACharactersSiblings()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();

        Guid miner  = await OwnershipTests.CreateCharacter(player, "Miner");
        Guid smith  = await OwnershipTests.CreateCharacter(player, "Smith");

        await GiveItems(miner, "tin_ore", 400);

        (await OwnershipTests.Post(player, $"/bank/{miner}/deposit",
                                   new { itemId = "tin_ore", quantity = 400 })).EnsureSuccessStatusCode();

        // The whole reason the bank exists: what one character banks, another spends.
        (await OwnershipTests.Post(player, $"/bank/{smith}/withdraw",
                                   new { itemId = "tin_ore", quantity = 400 })).EnsureSuccessStatusCode();

        Assert.Equal(0L,   await BagHolds(miner, "tin_ore"));
        Assert.Equal(400L, await BagHolds(smith, "tin_ore"));
        Assert.Equal(0L,   await BankHolds(player, "tin_ore"));
    }

    [SkippableFact]
    public async Task NobodyCanBankFromAnotherPlayersCharacter()
    {
        RequireDatabase();

        await using var owner = await api.NewPlayerAsync();
        await using var thief = await api.NewPlayerAsync();

        Guid character = await OwnershipTests.CreateCharacter(owner, "Owner");
        await GiveItems(character, "tin_ore", 100);

        var refused = await OwnershipTests.Post(thief, $"/bank/{character}/deposit",
                                                new { itemId = "tin_ore", quantity = 100 });

        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        Assert.Equal(100L, await BagHolds(character, "tin_ore"));
    }

    [SkippableFact]
    public async Task AFullBagRefusesAWithdrawalRatherThanEatingIt()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await OwnershipTests.CreateCharacter(player, "Stuffed");

        await GiveItems(character, "tin_ore", 100);

        (await OwnershipTests.Post(player, $"/bank/{character}/deposit",
                                   new { itemId = "tin_ore", quantity = 100 })).EnsureSuccessStatusCode();

        // Every slot taken by something that cannot stack with the withdrawal.
        await using (var db = await api.OpenDatabaseAsync())
        await using (var command = db.CreateCommand())
        {
            command.CommandText = """
                insert into inventory_slot (character_id, slot_index, item_id, quantity)
                select $1, generate_series(0, 29), 'normal_logs', 1
                on conflict (character_id, slot_index) do update
                   set item_id = 'normal_logs', quantity = 1;
                """;
            command.Parameters.AddWithValue(character);

            await command.ExecuteNonQueryAsync();
        }

        var refused = await OwnershipTests.Post(player, $"/bank/{character}/withdraw",
                                                new { itemId = "tin_ore", quantity = 100 });

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

        // Still in the bank. A withdrawal into a full bag must never be the way an
        // item leaves the game.
        Assert.Equal(100L, await BankHolds(player, "tin_ore"));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static JsonElement Body(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static async Task<JsonElement> Talents(Player player, Guid character)
    {
        var response = await player.Client.GetAsync($"/talent/{character}");
        response.EnsureSuccessStatusCode();

        return Body(await response.Content.ReadAsStringAsync());
    }

    private async Task<long> BankHolds(Player player, string itemId)
    {
        var response = await player.Client.GetAsync("/bank/");
        response.EnsureSuccessStatusCode();

        JsonElement bank = Body(await response.Content.ReadAsStringAsync());

        long total = 0L;

        foreach (JsonElement slot in bank.GetProperty("slots").EnumerateArray())
            if (slot.GetProperty("itemId").GetString() == itemId)
                total += slot.GetProperty("quantity").GetInt64();

        return total;
    }

    private async Task<long> BagHolds(Guid character, string itemId)
    {
        await using var db = await api.OpenDatabaseAsync();
        await using var command = db.CreateCommand();

        command.CommandText = """
            select coalesce(sum(quantity), 0) from inventory_slot
             where character_id = $1 and item_id = $2;
            """;
        command.Parameters.AddWithValue(character);
        command.Parameters.AddWithValue(itemId);

        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    /// <summary>
    /// Puts items in the bag directly.
    ///
    /// Through SQL because there is no endpoint that grants items -- which is the
    /// point. Earning them through settlement would be testing settlement.
    /// </summary>
    private async Task GiveItems(Guid character, string itemId, long quantity)
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

    private async Task GiveLevels(Guid character, int level)
    {
        await using var db = await api.OpenDatabaseAsync();
        await using var command = db.CreateCommand();

        command.CommandText = "update character set xp = $2 where id = $1;";
        command.Parameters.AddWithValue(character);
        command.Parameters.AddWithValue(IdleExplorers.Rules.Levelling.CharacterXpFor(level));

        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// A node from whatever class this character ended up with, or null.
    ///
    /// Discovered rather than hard-coded, so retiring a talent retires the test's
    /// dependency on it rather than turning the whole file red.
    /// </summary>
    private async Task<string> FirstNodeAsync(Guid character)
    {
        await using var db = await api.OpenDatabaseAsync();
        await using var command = db.CreateCommand();

        command.CommandText = "select class_id from character where id = $1;";
        command.Parameters.AddWithValue(character);

        string classId = (await command.ExecuteScalarAsync()) as string ?? "";

        var catalogue = api.Content.Catalogue;

        // Any class with a tree. The character's own if it has one, otherwise the
        // first authored -- these tests are about the endpoint, not about which class
        // the fixture happens to create.
        var withTree = catalogue.GetClass(classId);

        if (withTree?.talentTree is { Length: > 0 })
            return FirstTierZero(withTree.talentTree);

        foreach (var entry in catalogue.Classes.Values)
            if (entry?.talentTree is { Length: > 0 })
            {
                await SetClass(character, entry.id);
                return FirstTierZero(entry.talentTree);
            }

        return null;
    }

    /// <summary>
    /// A node in the first tier, which is the only one spendable from zero.
    ///
    /// Picking any node would fail the tier requirement and test the wrong refusal.
    /// </summary>
    private static string FirstTierZero(TalentNode[] tree)
    {
        foreach (var node in tree)
            if (node != null && node.tier == 0 && !string.IsNullOrEmpty(node.id)) return node.id;

        return tree.Length > 0 ? tree[0]?.id : null;
    }

    private async Task SetClass(Guid character, string classId)
    {
        await using var db = await api.OpenDatabaseAsync();
        await using var command = db.CreateCommand();

        command.CommandText = "update character set class_id = $2 where id = $1;";
        command.Parameters.AddWithValue(character);
        command.Parameters.AddWithValue(classId);

        await command.ExecuteNonQueryAsync();
    }
}
