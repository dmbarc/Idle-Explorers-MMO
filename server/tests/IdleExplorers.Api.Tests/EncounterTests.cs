using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using IdleExplorers.Rules;
using Xunit;

namespace IdleExplorers.Api.Tests;

/// <summary>
/// The boss fight, attacked deliberately.
///
/// ══ WHAT THESE ARE ACTUALLY TESTING ═══════════════════════════════════════════
///
/// One inequality:
///
///     cumulative damage  ≤  frozen DPS × (now − started_at + tolerance)
///
/// Most of what follows is an attempt to get around it -- a million actions at once,
/// a batch replayed, gear swapped mid-fight, two encounters at a time, resolving
/// twice. Each has to fail, and each has to fail by producing exactly the legitimate
/// result rather than by erroring in a way that also breaks honest play.
///
/// The fights here are won by ADVANCING THE CLOCK, which is the point: the only way
/// to do more damage is for more time to have passed.
/// </summary>
[Collection("api")]
public class EncounterTests(ApiFixture api)
{
    private static void RequireDatabase() => Skip.IfNot(ApiFixture.DatabaseReachable, ApiFixture.SkipReason);

    // ── Engaging ──────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task ThePortalMustBeOpenBeforeTheFightStarts()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await OwnershipTests.CreateCharacter(player, "Impatient");

        var refused = await Engage(player, character);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

        await using var db = await api.OpenDatabaseAsync();
        Assert.Equal(0L, await CountEncounters(db, character));
    }

    [SkippableFact]
    public async Task EngagingReturnsTheWholeTimelineUpFront()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await Ready(player, "Challenger");

        JsonElement fight = await EngageOk(player, character);

        Assert.True(fight.GetProperty("bossMaxHp").GetInt64() > 0L);
        Assert.Equal(fight.GetProperty("bossMaxHp").GetInt64(), fight.GetProperty("bossHp").GetInt64());

        JsonElement phases = fight.GetProperty("phases");

        Assert.Equal(3, phases.GetArrayLength());

        // ══ WHY THIS MATTERS ══════════════════════════════════════════════════
        //
        // The client draws every telegraph from this, with no further network calls.
        // An empty schedule is a boss that attacks with no wind-up, which is
        // unavoidable damage rather than a mechanic.
        foreach (JsonElement phase in phases.EnumerateArray())
        {
            JsonElement casts = phase.GetProperty("casts");

            Assert.True(casts.GetArrayLength() > 0);

            foreach (JsonElement cast in casts.EnumerateArray())
            {
                Assert.False(string.IsNullOrEmpty(cast.GetProperty("abilityId").GetString()));
                Assert.False(string.IsNullOrEmpty(cast.GetProperty("shape").GetString()));
                Assert.True(cast.GetProperty("telegraphSeconds").GetDouble() > 0d);
            }
        }
    }

    [SkippableFact]
    public async Task OnlyOneFightAtATime()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await Ready(player, "Doubler");

        await EngageOk(player, character);

        // The exploit: two encounters, one set of actions feeding both, two lots of
        // loot. Refused by a partial unique index rather than by a handler check,
        // because a handler check is a race unless it holds a lock.
        var second = await Engage(player, character);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        await using var db = await api.OpenDatabaseAsync();
        Assert.Equal(1L, await CountEncounters(db, character));
    }

    [SkippableFact]
    public async Task OnlyABossCanBeEngaged()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await Ready(player, "Confused");

        var refused = await OwnershipTests.Post(player, $"/encounter/{character}",
                                                new { monsterId = "goblin" });

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    }

    // ══ THE CEILING ═══════════════════════════════════════════════════════════

    [SkippableFact]
    public async Task AFloodOfActionsIsWorthTheTimeThatHasPassed()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await Ready(player, "Flooder");

        JsonElement fight = await EngageOk(player, character);

        double dps = fight.GetProperty("frozenDps").GetDouble();

        Assert.True(dps > 0d, "the character has to be able to hit something");

        // No time has passed. The most this can possibly be worth is the opening
        // tolerance, whatever the client sends.
        long total = 0L;
        var swings = new Swings();

        for (int burst = 0; burst < 20; burst++)
        {
            JsonElement said = await Act(player, character, swings, BossEncounter.MaxActionsPerRequest);

            total = fight.GetProperty("bossMaxHp").GetInt64() - said.GetProperty("bossHp").GetInt64();
        }

        double ceiling = BossEncounter.DamageCeiling(dps, elapsedSeconds: 0d);

        Assert.True(total <= (long)Math.Ceiling(ceiling),
                    $"640 instant actions were credited {total}, over the ceiling of {ceiling:F0}");

        // And it is not zero: an honest opening swing still lands.
        Assert.True(total > 0L);
    }

    [SkippableFact]
    public async Task TheOnlyWayToDoMoreDamageIsForTimeToPass()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await Ready(player, "Patient");

        JsonElement fight = await EngageOk(player, character);

        long maxHp    = fight.GetProperty("bossMaxHp").GetInt64();
        double dps    = fight.GetProperty("frozenDps").GetDouble();
        var swings = new Swings();

        await Act(player, character, swings, 30);

        long afterFirst = maxHp - (await Act(player, character, swings, 1))
                                   .GetProperty("bossHp").GetInt64();

        // Thirty seconds, and the same flood again.
        api.Clock.Advance(TimeSpan.FromSeconds(30));

        long afterWait = maxHp - (await Act(player, character, swings, 30))
                                   .GetProperty("bossHp").GetInt64();

        Assert.True(afterWait > afterFirst);

        // The gain is bounded by exactly the DPS the clock allows -- generously, since
        // the client may not have filled the whole window with swings.
        Assert.True(afterWait - afterFirst <= (long)Math.Ceiling(dps * 30d) + 1L);
    }

    [SkippableFact]
    public async Task AReplayedBatchAddsNothing()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await Ready(player, "Echo");

        JsonElement fight = await EngageOk(player, character);
        long maxHp = fight.GetProperty("bossMaxHp").GetInt64();

        var batch = new
        {
            actions = Enumerable.Range(1, 10)
                                .Select(i => new { sequence = i, abilityId = "" })
                                .ToArray(),
        };

        JsonElement first = await PostActions(player, character, batch);

        api.Clock.Advance(TimeSpan.FromSeconds(60));

        // Same sequence numbers. Every one is at or below the highest already accepted,
        // so every one is refused -- and the boss takes no damage from a resend.
        JsonElement replay = await PostActions(player, character, batch);

        Assert.Equal(0, replay.GetProperty("accepted").GetInt32());
        Assert.Equal(10, replay.GetProperty("rejected").GetInt32());

        Assert.Equal(maxHp - first.GetProperty("bossHp").GetInt64(),
                     maxHp - replay.GetProperty("bossHp").GetInt64());
    }

    [SkippableFact]
    public async Task AnUnboundedBatchIsRefused()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await Ready(player, "Firehose");

        await EngageOk(player, character);

        var flood = new
        {
            actions = Enumerable.Range(1, BossEncounter.MaxActionsPerRequest + 1)
                                .Select(i => new { sequence = i, abilityId = "" })
                                .ToArray(),
        };

        var refused = await OwnershipTests.Post(player, $"/encounter/{character}/actions", flood);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    }

    [SkippableFact]
    public async Task ActingWithoutAFightIsRefused()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await Ready(player, "Shadowboxer");

        var refused = await OwnershipTests.Post(player, $"/encounter/{character}/actions",
                                                new { actions = new[] { new { sequence = 1, abilityId = "" } } });

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
    }

    [SkippableFact]
    public async Task AnUnknownAbilityIsRejectedRatherThanTreatedAsAnAttack()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await Ready(player, "Inventive");

        await EngageOk(player, character);

        JsonElement said = await PostActions(player, character, new
        {
            actions = new[] { new { sequence = 1, abilityId = "delete_boss" } },
        });

        Assert.Equal(0, said.GetProperty("accepted").GetInt32());
        Assert.Equal(1, said.GetProperty("rejected").GetInt32());
    }

    // ══ RESOLVING ═════════════════════════════════════════════════════════════

    [SkippableFact]
    public async Task TheServerDecidesTheBossDied()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await Ready(player, "Victor");

        JsonElement result = await FightToAClear(player, character);

        Assert.True(result.GetProperty("won").GetBoolean());
        Assert.True(result.GetProperty("xpGained").GetInt64() > 0L);

        // Loot is EARNED, not received: it waits in pending_loot until claimed.
        Assert.True(result.GetProperty("pending").GetArrayLength() > 0);
    }

    [SkippableFact]
    public async Task ResolvingAFightThatWasNotWonPaysNothing()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await Ready(player, "Wiper");

        await EngageOk(player, character);

        var swings = new Swings();
        await Act(player, character, swings, 5);

        JsonElement result = await Resolve(player, character);

        Assert.False(result.GetProperty("won").GetBoolean());
        Assert.Equal(0L, result.GetProperty("xpGained").GetInt64());
        Assert.Equal(0,  result.GetProperty("pending").GetArrayLength());

        JsonElement waiting = await PendingLoot(player, character);
        Assert.Equal(0, waiting.GetProperty("pending").GetArrayLength());
    }

    [SkippableFact]
    public async Task AFightCannotBeResolvedTwice()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await Ready(player, "Greedy");

        JsonElement first = await FightToAClear(player, character);

        Assert.True(first.GetProperty("won").GetBoolean());

        // The obvious exploit: resolve the same win repeatedly for repeated loot.
        var second = await OwnershipTests.Post(player, $"/encounter/{character}/resolve", new { });

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        await using var db = await api.OpenDatabaseAsync();

        long crowns = await CountPending(db, character, "kings_crown");

        Assert.Equal(1L, crowns);
    }

    [SkippableFact]
    public async Task LootWaitsInsteadOfEvaporatingIntoAFullBag()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await Ready(player, "Hoarder");

        await FightToAClear(player, character);
        await FillTheBag(character);

        JsonElement claim = await Claim(player, character);

        // ══ THE POINT ═════════════════════════════════════════════════════════
        //
        // A boss kill that silently deleted the crown because slot thirty held four
        // logs is the worst thing this game could do to somebody's evening.
        Assert.True(claim.GetProperty("bagWasFull").GetBoolean());
        Assert.True(claim.GetProperty("stillWaiting").GetInt64() > 0L);

        JsonElement waiting = await PendingLoot(player, character);
        Assert.True(waiting.GetProperty("pending").GetArrayLength() > 0);
    }

    [SkippableFact]
    public async Task ClaimingTwiceGrantsOnce()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await Ready(player, "Doubleclick");

        await FightToAClear(player, character);

        await Claim(player, character);

        long afterFirst = await CrownsHeld(character);

        JsonElement again = await Claim(player, character);

        Assert.Equal(0, again.GetProperty("claimed").GetArrayLength());
        Assert.Equal(afterFirst, await CrownsHeld(character));
    }

    [SkippableFact]
    public async Task CoinsFromTheBossLandInTheWalletAndTheLedgerAgrees()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await Ready(player, "Banker");

        await FightToAClear(player, character);
        await Claim(player, character);

        await using var db = await api.OpenDatabaseAsync();
        await using var command = db.CreateCommand();

        // The invariant the whole ledger exists for, on the path most likely to break
        // it: a large one-off grant through a code path used nowhere else.
        command.CommandText = """
            select w.balance, coalesce(sum(l.delta), 0)
              from wallet w
              left join wallet_ledger l
                on l.account_id = w.account_id and l.currency = w.currency
             where w.account_id = $1 and w.currency = 'coins'
             group by w.balance;
            """;
        command.Parameters.AddWithValue(player.UserId);

        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());

        long balance = reader.GetInt64(0);
        long ledger  = (long)reader.GetDecimal(1);

        Assert.True(balance > 0L);
        Assert.Equal(balance, ledger);
    }

    // ══ OWNERSHIP AND GEAR ════════════════════════════════════════════════════

    [SkippableFact]
    public async Task NobodyCanActOnSomebodyElsesFight()
    {
        RequireDatabase();

        await using var owner  = await api.NewPlayerAsync();
        await using var thief  = await api.NewPlayerAsync();

        Guid character = await Ready(owner, "Owner");

        await EngageOk(owner, character);

        var refused = await OwnershipTests.Post(thief, $"/encounter/{character}/actions",
                                                new { actions = new[] { new { sequence = 1, abilityId = "" } } });

        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);

        var stolen = await OwnershipTests.Post(thief, $"/encounter/{character}/resolve", new { });
        Assert.Equal(HttpStatusCode.NotFound, stolen.StatusCode);

        var looted = await OwnershipTests.Post(thief, $"/encounter/{character}/loot", new { });
        Assert.Equal(HttpStatusCode.NotFound, looted.StatusCode);
    }

    [SkippableFact]
    public async Task TheSnapshotIsFrozenSoSwappingGearMidFightChangesNothing()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await Ready(player, "Swapper");

        JsonElement fight = await EngageOk(player, character);
        double frozen = fight.GetProperty("frozenDps").GetDouble();

        // Strip the character bare mid-fight. Under a live snapshot this would drop
        // their damage to nothing; under a frozen one it changes exactly nothing,
        // which is what stops the fight being a gear-swapping puzzle.
        await using (var db = await api.OpenDatabaseAsync())
        await using (var command = db.CreateCommand())
        {
            command.CommandText = "delete from equipment where character_id = $1;";
            command.Parameters.AddWithValue(character);

            await command.ExecuteNonQueryAsync();
        }

        api.Clock.Advance(TimeSpan.FromSeconds(60));

        var swings = new Swings();
        JsonElement said = await Act(player, character, swings, 20);

        long dealt = fight.GetProperty("bossMaxHp").GetInt64() - said.GetProperty("bossHp").GetInt64();

        Assert.True(dealt > 0L, "a frozen snapshot keeps hitting after the gear comes off");
        Assert.True(dealt <= (long)Math.Ceiling(BossEncounter.DamageCeiling(frozen, 60d)));
    }

    [SkippableFact]
    public async Task AnAbandonedFightDoesNotLockThePlayerOutForever()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await Ready(player, "Quitter");

        await EngageOk(player, character);

        // Walked away. The unique index does not care that nobody is playing it, so
        // without expiry this character could never fight the boss again.
        api.Clock.Advance(TimeSpan.FromSeconds(BossEncounter.MaxEnrageSeconds + 60d));

        JsonElement fresh = await EngageOk(player, character);

        Assert.Equal(fresh.GetProperty("bossMaxHp").GetInt64(), fresh.GetProperty("bossHp").GetInt64());

        await using var db = await api.OpenDatabaseAsync();
        await using var command = db.CreateCommand();

        command.CommandText = """
            select count(*) from encounter
             where character_id = $1 and ended_at is not null and won = false;
            """;
        command.Parameters.AddWithValue(character);

        Assert.Equal(1L, Convert.ToInt64(await command.ExecuteScalarAsync()));
    }

    [SkippableFact]
    public async Task TheFunnelSeesEveryStageOfTheFight()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await Ready(player, "Measured");

        await FightToAClear(player, character);

        await using var db = await api.OpenDatabaseAsync();

        Assert.Equal(1L, await CountEvent(db, character, TelemetryEvents.BossEngaged));
        Assert.Equal(1L, await CountEvent(db, character, TelemetryEvents.BossEnded));

        await using var command = db.CreateCommand();

        command.CommandText = """
            select payload ->> 'won' from telemetry_event
             where character_id = $1 and event = $2;
            """;
        command.Parameters.AddWithValue(character);
        command.Parameters.AddWithValue(TelemetryEvents.BossEnded);

        Assert.Equal("true", (string)await command.ExecuteScalarAsync());
    }

    // ── Getting to the fight ──────────────────────────────────────────────────

    /// <summary>A character with the portal open, ready to engage.</summary>
    private async Task<Guid> Ready(Player player, string name)
    {
        Guid character = await OwnershipTests.CreateCharacter(player, name);

        await using (var db = await api.OpenDatabaseAsync())
        await using (var command = db.CreateCommand())
        {
            // Straight to the unlock. What these tests are about is the FIGHT, and
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

    /// <summary>
    /// Beats the boss the only way it can be beaten: by letting enough time pass.
    ///
    /// The clock advances in steps and the client swings each step. That is exactly
    /// what an honest client does, and it is the only thing that works -- which is the
    /// property the whole file is about.
    /// </summary>
    private async Task<JsonElement> FightToAClear(Player player, Guid character)
    {
        JsonElement fight = await EngageOk(player, character);

        long   maxHp = fight.GetProperty("bossMaxHp").GetInt64();
        double dps   = fight.GetProperty("frozenDps").GetDouble();

        Assert.True(dps > 0d);

        var swings = new Swings();

        // Enough steps to chew through the boss at this DPS, bounded so a balance
        // change cannot turn this into an infinite loop.
        for (int step = 0; step < 400; step++)
        {
            JsonElement said = await Act(player, character, swings, 8);

            if (said.GetProperty("dead").GetBoolean()) break;

            api.Clock.Advance(TimeSpan.FromSeconds(Math.Max(1d, maxHp / dps / 20d)));
        }

        return await Resolve(player, character);
    }

    private Task<HttpResponseMessage> Engage(Player player, Guid character) =>
        OwnershipTests.Post(player, $"/encounter/{character}", new { monsterId = "goblin_king" });

    private async Task<JsonElement> EngageOk(Player player, Guid character)
    {
        var response = await Engage(player, character);
        response.EnsureSuccessStatusCode();

        return await Body(response);
    }

    /// <summary>
    /// The client's action counter.
    ///
    /// A tiny mutable object rather than a ref parameter, because an async method
    /// cannot take one -- and because the sequence genuinely is state that belongs to
    /// the client across requests, which is the thing being modelled.
    /// </summary>
    private sealed class Swings { public long Sequence; }

    private async Task<JsonElement> Act(Player player, Guid character, Swings swings, int count)
    {
        var actions = new List<object>(count);

        for (int i = 0; i < count; i++)
            actions.Add(new { sequence = ++swings.Sequence, abilityId = "" });

        return await PostActions(player, character, new { actions = actions.ToArray() });
    }

    private static async Task<JsonElement> PostActions(Player player, Guid character, object batch)
    {
        var response = await OwnershipTests.Post(player, $"/encounter/{character}/actions", batch);
        response.EnsureSuccessStatusCode();

        return await Body(response);
    }

    private static async Task<JsonElement> Resolve(Player player, Guid character)
    {
        var response = await OwnershipTests.Post(player, $"/encounter/{character}/resolve", new { });
        response.EnsureSuccessStatusCode();

        return await Body(response);
    }

    private static async Task<JsonElement> Claim(Player player, Guid character)
    {
        var response = await OwnershipTests.Post(player, $"/encounter/{character}/loot", new { });
        response.EnsureSuccessStatusCode();

        return await Body(response);
    }

    private static async Task<JsonElement> PendingLoot(Player player, Guid character)
    {
        var response = await player.Client.GetAsync($"/encounter/{character}/loot");
        response.EnsureSuccessStatusCode();

        return await Body(response);
    }

    private static async Task<JsonElement> Body(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    // ── Poking at the database ────────────────────────────────────────────────

    private async Task FillTheBag(Guid character)
    {
        await using var db = await api.OpenDatabaseAsync();
        await using var command = db.CreateCommand();

        // Every slot, each holding something the boss does not drop, so nothing can
        // stack into a partly-used slot.
        command.CommandText = """
            insert into inventory_slot (character_id, slot_index, item_id, quantity)
            select $1, generate_series(0, 29), 'normal_logs', 1
            on conflict (character_id, slot_index) do update
               set item_id = 'normal_logs', quantity = 1;
            """;
        command.Parameters.AddWithValue(character);

        await command.ExecuteNonQueryAsync();
    }

    private async Task<long> CrownsHeld(Guid character)
    {
        await using var db = await api.OpenDatabaseAsync();
        await using var command = db.CreateCommand();

        command.CommandText = """
            select coalesce(sum(quantity), 0) from inventory_slot
             where character_id = $1 and item_id = 'kings_crown';
            """;
        command.Parameters.AddWithValue(character);

        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<long> CountEncounters(Npgsql.NpgsqlConnection db, Guid character)
    {
        await using var command = db.CreateCommand();

        command.CommandText = "select count(*) from encounter where character_id = $1;";
        command.Parameters.AddWithValue(character);

        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<long> CountPending(Npgsql.NpgsqlConnection db, Guid character, string itemId)
    {
        await using var command = db.CreateCommand();

        command.CommandText = """
            select count(*) from pending_loot where character_id = $1 and item_id = $2;
            """;
        command.Parameters.AddWithValue(character);
        command.Parameters.AddWithValue(itemId);

        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<long> CountEvent(Npgsql.NpgsqlConnection db, Guid character, string eventName)
    {
        await using var command = db.CreateCommand();

        command.CommandText = """
            select count(*) from telemetry_event where character_id = $1 and event = $2;
            """;
        command.Parameters.AddWithValue(character);
        command.Parameters.AddWithValue(eventName);

        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}
