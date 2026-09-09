#nullable enable

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace IdleExplorers.Api.Tests;

/// <summary>
/// Four people, one King.
///
/// ══ WHAT A SHARED FIGHT MUST NOT BECOME ═══════════════════════════════════════
///
/// A shared damage ALLOWANCE.
///
/// The whole anti-cheat of this encounter is one line: cumulative damage cannot
/// exceed frozen DPS multiplied by elapsed time, and both terms belong to the server.
/// Sharing a health pool is the point of group content. Sharing the ceiling would
/// mean four honest players' budgets could be spent by one dishonest one — a group
/// fight as a damage multiplier for whoever is cheating.
///
/// So the pool is on the encounter and the ceiling is on the participant, and the
/// test that matters is the one that proves a fighter cannot spend more than their
/// own.
///
/// ══ AND WHAT IT MUST BECOME ═══════════════════════════════════════════════════
///
/// Loot for everybody who swung. A group that kills the King together and hands the
/// drops to whoever pressed resolve is a group nobody joins twice.
/// </summary>
[Collection("api")]
public class GroupEncounterTests(ApiFixture api)
{
    private static void RequireDatabase() => Skip.IfNot(ApiFixture.DatabaseReachable, ApiFixture.SkipReason);

    /// <summary>
    /// Two people in a group fight ONE boss.
    ///
    /// The second engage joins rather than starting a second King with its own health
    /// bar standing in the same room.
    /// </summary>
    [SkippableFact]
    public async Task AGroupSharesOneFight()
    {
        RequireDatabase();

        var (aP, a, bP, b) = await AGroupOfTwo("Together");

        JsonElement first  = await EngageOk(aP, a);
        JsonElement second = await EngageOk(bP, b);

        Assert.Equal(first.GetProperty("encounterId").GetGuid(),
                     second.GetProperty("encounterId").GetGuid());

        // Scoped to THESE two, not the whole table. Other tests in this suite leave
        // their own fights running, and a global count was asserting something about
        // the test run rather than about a group.
        Assert.Equal(1L, await LiveEncountersFor(a, b));
    }

    /// <summary>
    /// Both of them chip away at the same health.
    ///
    /// The pool moves by each fighter's DELTA, so a batch from one of them does not
    /// overwrite what the other did between their two requests.
    /// </summary>
    [SkippableFact]
    public async Task BothContributeToOnePool()
    {
        RequireDatabase();

        var (aP, a, bP, b) = await AGroupOfTwo("Chip");

        await EngageOk(aP, a);
        await EngageOk(bP, b);

        api.Clock.Advance(TimeSpan.FromSeconds(30));

        var swingsA = new Swings();
        var swingsB = new Swings();

        await Act(aP, a, swingsA, 8);

        long afterA = await PoolOf(a);

        await Act(bP, b, swingsB, 8);

        long afterBoth = await PoolOf(a);

        Assert.True(afterA > 0L,          "the first fighter contributed nothing");
        Assert.True(afterBoth > afterA,   "the second fighter's damage did not reach the pool");
    }

    /// <summary>
    /// ONE FIGHTER CANNOT SPEND THE GROUP'S ALLOWANCE.
    ///
    /// The test this file exists for. Both engage; only one of them swings, and swings
    /// far more than their own clock allows. Their contribution is clamped to THEIR
    /// ceiling — not to the sum of everybody's.
    ///
    /// If the ceiling were read off the encounter total, the greedy fighter would be
    /// allowed roughly twice as much here, and four times as much in a full group.
    /// </summary>
    [SkippableFact]
    public async Task OneFighterCannotSpendTheGroupsCeiling()
    {
        RequireDatabase();

        var (aP, a, bP, b) = await AGroupOfTwo("Greedy");

        JsonElement fight = await EngageOk(aP, a);
        await EngageOk(bP, b);

        double dps = fight.GetProperty("frozenDps").GetDouble();

        Assert.True(dps > 0d);

        const double Window = 20d;

        api.Clock.Advance(TimeSpan.FromSeconds(Window));

        // Far more swings than twenty seconds can justify, from one fighter.
        //
        // Sent as repeated MAX-SIZE batches with the clock held still, because a single
        // 500-action request is refused outright by MaxActionsPerRequest -- which is a
        // different defence, and not the one under test. The burst that matters is the
        // one that looks legal request by request.
        var swings = new Swings();

        bool clamped = false;

        for (int burst = 0; burst < 20; burst++)
        {
            JsonElement said = await Act(aP, a, swings,
                                         IdleExplorers.Rules.BossEncounter.MaxActionsPerRequest);

            clamped |= said.GetProperty("clamped").GetBoolean();
        }

        Assert.True(clamped, "a sustained burst inside twenty seconds was never clamped");

        long mine = await ContributionOf(a);

        // Their own budget, with the tolerance the rules allow. The point is the
        // ceiling is theirs alone: a second fighter standing idle beside them must
        // not widen it.
        double allowed = IdleExplorers.Rules.BossEncounter.DamageCeiling(dps, Window);

        Assert.True(mine <= (long)Math.Ceiling(allowed),
                    $"contributed {mine}, own ceiling was {allowed:F0}");

        // And the other fighter, who did nothing, contributed nothing.
        Assert.Equal(0L, await ContributionOf(b));
    }

    /// <summary>
    /// A late arrival earns from when they ARRIVED, not from when the fight began.
    ///
    /// Otherwise walking in at the four-minute mark of a five-minute enrage would hand
    /// somebody four minutes of allowance to spend in one batch.
    /// </summary>
    [SkippableFact]
    public async Task ALateArrivalDoesNotInheritTheFightsAge()
    {
        RequireDatabase();

        var (aP, a, bP, b) = await AGroupOfTwo("Latecomer");

        await EngageOk(aP, a);

        // A long time passes before the second fighter walks in.
        api.Clock.Advance(TimeSpan.FromSeconds(120));

        JsonElement late = await EngageOk(bP, b);

        double dps = late.GetProperty("frozenDps").GetDouble();

        // Barely any time as a participant.
        api.Clock.Advance(TimeSpan.FromSeconds(5));

        var swings = new Swings();

        for (int burst = 0; burst < 20; burst++)
            await Act(bP, b, swings, IdleExplorers.Rules.BossEncounter.MaxActionsPerRequest);

        long mine = await ContributionOf(b);

        double allowedForFive = IdleExplorers.Rules.BossEncounter.DamageCeiling(dps, 5d);

        Assert.True(mine <= (long)Math.Ceiling(allowedForFive),
                    $"a late arrival contributed {mine} against a five-second ceiling of {allowedForFive:F0}");
    }

    /// <summary>
    /// Everybody who fought gets paid.
    ///
    /// Read from the participants rather than from the party, because the party is
    /// what it is NOW and the fight is what it WAS.
    ///
    /// ══ WHAT "PAID" MEANS FOR A GROUP ═════════════════════════════════════════
    ///
    /// Experience, in full, to everybody -- splitting it would make a group strictly
    /// worse than soloing for anybody who could manage it.
    ///
    /// Items, ONCE, put up for a roll. This test used to assert that the second
    /// fighter had loot waiting, and that assertion was the old bug written down: the
    /// fight rolled its whole table separately for each of them, so four people
    /// killing the King produced four Goblin Spears and the rarest thing in the game
    /// was the one everybody had by the second clear.
    /// </summary>
    [SkippableFact]
    public async Task EverybodyWhoFoughtIsRewarded()
    {
        RequireDatabase();

        var (aP, a, bP, b) = await AGroupOfTwo("Sharing");

        JsonElement fight = await EngageOk(aP, a);
        await EngageOk(bP, b);

        long   maxHp = fight.GetProperty("bossMaxHp").GetInt64();
        double dps   = fight.GetProperty("frozenDps").GetDouble();

        var swingsA = new Swings();
        var swingsB = new Swings();

        long beforeB = await XpOf(b);

        for (int step = 0; step < 400; step++)
        {
            JsonElement said = await Act(aP, a, swingsA, 8);

            await Act(bP, b, swingsB, 8);

            if (said.GetProperty("dead").GetBoolean()) break;

            api.Clock.Advance(TimeSpan.FromSeconds(Math.Max(1d, maxHp / dps / 20d)));
        }

        JsonElement done = await Resolve(aP, a);

        Assert.True(done.GetProperty("won").GetBoolean(), "the group did not manage to kill it");

        // The one who never pressed resolve has experience paid...
        Assert.True(await XpOf(b) > beforeB, "the second fighter got no experience");

        // ...and a share of the argument, rather than a private copy of the drops.
        Assert.True(await OpenRolls(a) > 0L, "a group kill offered nothing to roll for");

        Assert.Equal(0L, await PendingCount(a));
        Assert.Equal(0L, await PendingCount(b));
    }

    /// <summary>
    /// Solo is the same code path with one participant.
    ///
    /// Stated as its own test because the refactor that introduced participants could
    /// have made group content work and quietly broken the ordinary case — which is
    /// the more common one and the one nobody would think to re-check.
    /// </summary>
    [SkippableFact]
    public async Task AloneStillWorks()
    {
        RequireDatabase();

        var player    = await api.NewPlayerAsync();
        Guid character = await Ready(player, "Hermit");

        JsonElement fight = await EngageOk(player, character);

        Assert.True(fight.GetProperty("bossMaxHp").GetInt64() > 0L);
        Assert.Equal(1L, await ParticipantCount(fight.GetProperty("encounterId").GetGuid()));
    }

    // ── Machinery ─────────────────────────────────────────────────────────────

    private async Task<(Player, Guid, Player, Guid)> AGroupOfTwo(string prefix)
    {
        var aPlayer = await api.NewPlayerAsync();
        var bPlayer = await api.NewPlayerAsync();

        Guid a = await Ready(aPlayer, $"{prefix}A{Guid.NewGuid():N}"[..12]);
        Guid b = await Ready(bPlayer, $"{prefix}B{Guid.NewGuid():N}"[..12]);

        (await OwnershipTests.Post(aPlayer, $"/party/{a}", new { })).EnsureSuccessStatusCode();
        (await OwnershipTests.Post(bPlayer, $"/party/{b}/join/{a}", new { })).EnsureSuccessStatusCode();

        return (aPlayer, a, bPlayer, b);
    }

    private async Task<Guid> Ready(Player player, string name)
    {
        Guid character = await OwnershipTests.CreateCharacter(player, name);

        await using (var db = await api.OpenDatabaseAsync())
        await using (var command = db.CreateCommand())
        {
            command.CommandText = """
                insert into kill_counter (character_id, monster_id, active_kills)
                values ($1, 'goblin', 100000)
                on conflict (character_id, monster_id) do update set active_kills = 100000;
                """;
            command.Parameters.AddWithValue(character);

            await command.ExecuteNonQueryAsync();
        }

        (await OwnershipTests.Post(player, $"/boss/{character}/unlock", new { }))
            .EnsureSuccessStatusCode();

        return character;
    }

    private sealed class Swings { public long Sequence; }

    private static async Task<JsonElement> EngageOk(Player player, Guid character)
    {
        var response = await OwnershipTests.Post(player, $"/encounter/{character}",
                                                 new { monsterId = "goblin_king" });
        response.EnsureSuccessStatusCode();

        return await Body(response);
    }

    private static async Task<JsonElement> Act(Player player, Guid character, Swings swings, int count)
    {
        var actions = new List<object>(count);

        for (int i = 0; i < count; i++)
            actions.Add(new { sequence = ++swings.Sequence, abilityId = "" });

        var response = await OwnershipTests.Post(player, $"/encounter/{character}/actions",
                                                 new { actions = actions.ToArray() });
        response.EnsureSuccessStatusCode();

        return await Body(response);
    }

    private static async Task<JsonElement> Resolve(Player player, Guid character)
    {
        var response = await OwnershipTests.Post(player, $"/encounter/{character}/resolve", new { });
        response.EnsureSuccessStatusCode();

        return await Body(response);
    }

    private static async Task<JsonElement> Body(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private async Task<long> Scalar(string sql, params object[] args)
    {
        await using var db = await api.OpenDatabaseAsync();
        await using var command = db.CreateCommand();

        command.CommandText = sql;

        foreach (object arg in args) command.Parameters.AddWithValue(arg);

        return Convert.ToInt64(await command.ExecuteScalarAsync() ?? 0L);
    }

    /// <summary>The shared pool of the fight this character is in.</summary>
    private Task<long> PoolOf(Guid character) => Scalar(
        """
        select coalesce(e.damage_dealt, 0) from encounter e
        join encounter_participant p on p.encounter_id = e.id
        where p.character_id = $1 and e.ended_at is null;
        """, character);

    /// <summary>What THIS character put into it.</summary>
    private Task<long> ContributionOf(Guid character) => Scalar(
        """
        select coalesce(p.damage_dealt, 0) from encounter_participant p
        join encounter e on e.id = p.encounter_id
        where p.character_id = $1 and e.ended_at is null;
        """, character);

    private Task<long> ParticipantCount(Guid encounterId) => Scalar(
        "select count(*) from encounter_participant where encounter_id = $1;", encounterId);

    /// <summary>How many DISTINCT live fights these two are in. One, when grouped.</summary>
    private Task<long> LiveEncountersFor(Guid a, Guid b) => Scalar(
        """
        select count(distinct e.id) from encounter e
        join encounter_participant p on p.encounter_id = e.id
        where e.ended_at is null and p.character_id in ($1, $2);
        """, a, b);

    private Task<long> PendingCount(Guid character) => Scalar(
        "select count(*) from pending_loot where character_id = $1;", character);

    private Task<long> XpOf(Guid character) => Scalar(
        "select xp from character where id = $1;", character);

    /// <summary>Contested drops from the fight this character was in.</summary>
    private Task<long> OpenRolls(Guid character) => Scalar(
        """
        select count(*) from loot_roll r
        join encounter_participant p on p.encounter_id = r.encounter_id
        where p.character_id = $1;
        """, character);
}
