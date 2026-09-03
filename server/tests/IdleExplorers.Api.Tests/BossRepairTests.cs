#nullable enable

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using IdleExplorers.Rules;
using Xunit;

namespace IdleExplorers.Api.Tests;

/// <summary>
/// The five ways the King was broken in a playtest, each pinned by a test.
///
/// ══ WHY THESE ARE TOGETHER ════════════════════════════════════════════════════
///
/// Because they were one bug wearing five costumes. A live encounter row that nothing
/// ever closed meant the server believed players were fighting when they were not,
/// and every symptom followed from that: the boss "was not there" for the second
/// person through the door, walking out did nothing, dying locked you out, and the
/// only way to leave the arena was to lose.
///
/// The tests below are written against the symptoms rather than the cause, on
/// purpose. A test named after a cause stops meaning anything the day somebody fixes
/// the cause differently.
/// </summary>
[Collection("api")]
public class BossRepairTests(ApiFixture api)
{
    private static void RequireDatabase() => Skip.IfNot(ApiFixture.DatabaseReachable, ApiFixture.SkipReason);

    // ══ THE LOCKOUT ═══════════════════════════════════════════════════════════

    /// <summary>
    /// WALKING BACK IN IS A REJOIN, NOT A REFUSAL.
    ///
    /// This is the one that made the boss look absent. Nothing closed an encounter
    /// except killing the King or the enrage clock running out, so a player who died,
    /// alt-tabbed, or walked out had a live row with their name on it -- and the next
    /// engage was answered 409. From inside the client that is an arena with no boss:
    /// BossController never starts, so there are no telegraphs, no health bar and
    /// nothing to hit.
    /// </summary>
    [SkippableFact]
    public async Task EngagingTwiceRejoinsTheSameFight()
    {
        RequireDatabase();

        var player = await api.NewPlayerAsync();
        Guid character = await Ready(player, "Returner");

        JsonElement first = await EngageOk(player, character);

        api.Clock.Advance(TimeSpan.FromSeconds(45));

        JsonElement again = await EngageOk(player, character);

        Assert.Equal(first.GetProperty("encounterId").GetGuid(),
                     again.GetProperty("encounterId").GetGuid());

        // Still one fight, not two.
        Assert.Equal(1L, await LiveEncounters(character));
    }

    /// <summary>
    /// A REJOIN DOES NOT REFREEZE THE SNAPSHOT.
    ///
    /// Which is the reason the rejoin above is safe rather than an exploit. If it
    /// refroze, the loop would be: engage, see the King is a phase ahead, walk out,
    /// put the good sword on, walk back in with a bigger damage ceiling.
    ///
    /// Proven by actually equipping a weapon between the two engages and asserting the
    /// echoed DPS did not move.
    /// </summary>
    [SkippableFact]
    public async Task ARejoinKeepsTheOriginalSnapshot()
    {
        RequireDatabase();

        var player = await api.NewPlayerAsync();
        Guid character = await Ready(player, "Swapper");

        JsonElement first = await EngageOk(player, character);

        double before = first.GetProperty("frozenDps").GetDouble();

        await GiveAndEquip(player, character, "goblin_spear", "mainhand");

        JsonElement again = await EngageOk(player, character);

        Assert.Equal(before, again.GetProperty("frozenDps").GetDouble(), precision: 6);

        // ══ AND THE WEAPON REALLY WOULD HAVE CHANGED IT ═══════════════════════
        //
        // Without this the test passes when the equip silently failed, which is
        // exactly what it did on the first attempt: it reached for iron_sword, whose
        // levelReq is ten, on a level one character. Nothing changed, the numbers
        // matched, and the assertion above was green against a character still holding
        // the same nothing in both hands.
        //
        // So the sword is proven to matter by starting a FRESH fight with it on. If
        // this line does not move, the one above is not testing anything.
        await Flee(player, character);

        JsonElement fresh = await EngageOk(player, character);

        Assert.True(fresh.GetProperty("frozenDps").GetDouble() > before,
                    "the weapon made no difference, so the assertion above proved nothing");
    }

    /// <summary>
    /// AND THE CLOCK DOES NOT RESTART EITHER.
    ///
    /// The other half of the same exploit: if walking out and back in reset the enrage
    /// deadline, the timer -- which is the entire fail condition of this encounter --
    /// would be optional.
    /// </summary>
    [SkippableFact]
    public async Task ARejoinDoesNotResetTheEnrageClock()
    {
        RequireDatabase();

        var player = await api.NewPlayerAsync();
        Guid character = await Ready(player, "Stalling");

        await EngageOk(player, character);

        api.Clock.Advance(TimeSpan.FromSeconds(90));

        JsonElement again = await EngageOk(player, character);

        Assert.True(again.GetProperty("elapsedSeconds").GetDouble() >= 89d,
                    "the fight's own clock was reported as though it had just begun");
    }

    // ══ THE WAY OUT ═══════════════════════════════════════════════════════════

    /// <summary>
    /// LEAVING CLOSES THE FIGHT, AND THEN YOU CAN COME BACK.
    ///
    /// The exit had to do more than move the player: an exit that left the row behind
    /// would have fixed the arena and kept the lockout.
    /// </summary>
    [SkippableFact]
    public async Task FleeingEndsASoloFight()
    {
        RequireDatabase();

        var player = await api.NewPlayerAsync();
        Guid character = await Ready(player, "Coward");

        await EngageOk(player, character);

        JsonElement left = await Flee(player, character);

        Assert.True(left.GetProperty("left").GetBoolean());
        Assert.True(left.GetProperty("ended").GetBoolean(), "the last fighter out did not close the fight");

        Assert.Equal(0L, await LiveEncounters(character));

        // And a fresh attempt is a fresh fight rather than a 409.
        JsonElement fresh = await EngageOk(player, character);

        Assert.Equal(0d, fresh.GetProperty("elapsedSeconds").GetDouble(), precision: 1);
    }

    /// <summary>
    /// ONE PERSON LEAVING DOES NOT END EVERYBODY'S FIGHT.
    ///
    /// The reason this is not just a call to resolve. resolve ends the ENCOUNTER, and
    /// three people who are still swinging must not have it ended under them by the
    /// fourth deciding they have had enough.
    /// </summary>
    [SkippableFact]
    public async Task OneLeavingDoesNotEndTheGroupsFight()
    {
        RequireDatabase();

        var (aP, a, bP, b) = await AGroupOfTwo("Bail");

        JsonElement fight = await EngageOk(aP, a);
        await EngageOk(bP, b);

        Guid encounter = fight.GetProperty("encounterId").GetGuid();

        JsonElement left = await Flee(bP, b);

        Assert.True(left.GetProperty("left").GetBoolean());
        Assert.False(left.GetProperty("ended").GetBoolean(), "one of two leaving ended the whole fight");

        Assert.Equal(1L, await Participants(encounter));
        Assert.Equal(1L, await LiveEncounters(a));

        // The last one out does close it.
        Assert.True((await Flee(aP, a)).GetProperty("ended").GetBoolean());
        Assert.Equal(0L, await LiveEncounters(a));
    }

    /// <summary>
    /// Leaving something you are not in is not an error.
    ///
    /// A door that can fail is a door players get trapped behind, and the client calls
    /// this on every departure from the arena including the ones where no fight ever
    /// started.
    /// </summary>
    [SkippableFact]
    public async Task FleeingNothingIsHarmless()
    {
        RequireDatabase();

        var player = await api.NewPlayerAsync();
        Guid character = await Ready(player, "Wanderer");

        JsonElement left = await Flee(player, character);

        Assert.False(left.GetProperty("left").GetBoolean());
        Assert.False(left.GetProperty("ended").GetBoolean());
    }

    // ══ EVERYBODY SEES THE SAME FIGHT ═════════════════════════════════════════

    /// <summary>
    /// A JOINER IS TOLD ABOUT THE FIGHT THAT IS RUNNING, NOT A NEW ONE.
    ///
    /// The seed IS the attack timeline -- every telegraph the client draws is generated
    /// from it. A joiner used to be handed the seed generated for their own request, so
    /// two people stood in one arena dodging two different fights: the cone on one
    /// screen was not the cone on the other, and neither matched the boss.
    ///
    /// Asserted through the CASTS rather than the seed itself, because the seed is an
    /// implementation detail and the schedule is the thing a player experiences.
    /// </summary>
    [SkippableFact]
    public async Task EverybodyInTheFightSeesTheSameTimeline()
    {
        RequireDatabase();

        var (aP, a, bP, b) = await AGroupOfTwo("SameFight");

        JsonElement first = await EngageOk(aP, a);

        api.Clock.Advance(TimeSpan.FromSeconds(20));

        JsonElement second = await EngageOk(bP, b);

        string[] one = CastSignature(first);
        string[] two = CastSignature(second);

        Assert.NotEmpty(one);
        Assert.Equal(one, two);
    }

    /// <summary>
    /// AND SHARES THE ENRAGE CLOCK.
    ///
    /// A latecomer with a fresh five minutes would be fighting a different boss from
    /// the people who let them in -- and worse, walking out and back in would be how
    /// you beat the timer.
    /// </summary>
    [SkippableFact]
    public async Task AJoinerInheritsTheFightsClock()
    {
        RequireDatabase();

        var (aP, a, bP, b) = await AGroupOfTwo("Clock");

        await EngageOk(aP, a);

        api.Clock.Advance(TimeSpan.FromSeconds(75));

        JsonElement late = await EngageOk(bP, b);

        Assert.True(late.GetProperty("elapsedSeconds").GetDouble() >= 74d,
                    "a joiner was told the fight had just started");
    }

    // ══ THE GROUP GOES IN TOGETHER ════════════════════════════════════════════

    /// <summary>
    /// A CALL REACHES THE OTHER MEMBERS.
    ///
    /// And reaches them on the presence poll, which is the point: it has to arrive at
    /// somebody who is not looking at the group panel, because that is exactly who is
    /// being called.
    /// </summary>
    [SkippableFact]
    public async Task ACallReachesTheRestOfTheGroup()
    {
        RequireDatabase();

        var (aP, a, bP, b) = await AGroupOfTwo("Calling");

        (await OwnershipTests.Post(aP, $"/party/{a}/call",
                                   new { mapId = "goblin_throne", monsterId = "goblin_king" }))
            .EnsureSuccessStatusCode();

        JsonElement seen = await Body(await OwnershipTests.Post(bP, $"/presence/{b}",
                                                                new { mapId = "goblin_camp", x = 0f, z = 0f }));

        JsonElement call = seen.GetProperty("party").GetProperty("call");

        Assert.Equal("goblin_throne", call.GetProperty("mapId").GetString());
        Assert.False(call.GetProperty("travelNow").GetBoolean(), "the countdown had already finished");

        Assert.True(call.GetProperty("secondsLeft").GetDouble() > 0d);
    }

    /// <summary>
    /// A SECOND PRESS DOES NOT RESTART THE COUNTDOWN.
    ///
    /// Two members standing in the portal both press it, and a countdown that restarts
    /// on every press is a countdown that never reaches zero.
    /// </summary>
    [SkippableFact]
    public async Task PressingTheCallTwiceDoesNotRestartIt()
    {
        RequireDatabase();

        var (aP, a, bP, b) = await AGroupOfTwo("Twice");

        (await OwnershipTests.Post(aP, $"/party/{a}/call", new { mapId = "goblin_throne" }))
            .EnsureSuccessStatusCode();

        api.Clock.Advance(TimeSpan.FromSeconds(5));

        JsonElement after = await Body(
            await OwnershipTests.Post(bP, $"/party/{b}/call", new { mapId = "goblin_throne" }));

        double left = after.GetProperty("call").GetProperty("secondsLeft").GetDouble();

        Assert.True(left <= ThroneCall.CountdownSeconds - 4d,
                    $"the second press restarted the count ({left:F1}s left of " +
                    $"{ThroneCall.CountdownSeconds})");
    }

    /// <summary>The count finishing is what moves people, and the server says when.</summary>
    [SkippableFact]
    public async Task TheCountdownEventuallySaysGo()
    {
        RequireDatabase();

        var (aP, a, bP, b) = await AGroupOfTwo("Go");

        (await OwnershipTests.Post(aP, $"/party/{a}/call", new { mapId = "goblin_throne" }))
            .EnsureSuccessStatusCode();

        api.Clock.Advance(TimeSpan.FromSeconds(ThroneCall.CountdownSeconds + 1d));

        JsonElement party = await Body(await bP.Client.GetAsync($"/party/{b}"));

        Assert.True(party.GetProperty("call").GetProperty("travelNow").GetBoolean());
    }

    /// <summary>A stale call is not a call. Otherwise the portal countdown haunts the group.</summary>
    [SkippableFact]
    public async Task AnOldCallDisappears()
    {
        RequireDatabase();

        var (aP, a, _, _) = await AGroupOfTwo("Stale");

        (await OwnershipTests.Post(aP, $"/party/{a}/call", new { mapId = "goblin_throne" }))
            .EnsureSuccessStatusCode();

        api.Clock.Advance(TimeSpan.FromSeconds(ThroneCall.ExpiresAfterSeconds + 5d));

        JsonElement party = await Body(await aP.Client.GetAsync($"/party/{a}"));

        Assert.Equal(JsonValueKind.Null, party.GetProperty("call").ValueKind);
    }

    /// <summary>Somebody on their own has nobody to call, and is told so rather than silently ignored.</summary>
    [SkippableFact]
    public async Task CallingWithNoGroupIsRefused()
    {
        RequireDatabase();

        var player = await api.NewPlayerAsync();
        Guid character = await OwnershipTests.CreateCharacter(player, "Lonely");

        var response = await OwnershipTests.Post(player, $"/party/{character}/call",
                                                 new { mapId = "goblin_throne" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    // ══ WHO GETS THE CROWN ════════════════════════════════════════════════════

    /// <summary>
    /// A GROUP KILL PRODUCES ROLLS, NOT A COPY EACH.
    ///
    /// The fight used to roll the whole drop table separately for every fighter, so
    /// four people killing the King produced four of everything and the rarest item in
    /// the game was the one everybody had by the second clear.
    /// </summary>
    [SkippableFact]
    public async Task AGroupKillIsRolledForRatherThanCopied()
    {
        RequireDatabase();

        var (aP, a, bP, b) = await AGroupOfTwo("Rolling");

        Guid encounter = await KillTheKing(aP, a, bP, b);

        Assert.True(await Rolls(encounter) > 0L, "a group kill offered nothing to roll for");

        // Nothing has been handed out yet: a contested drop is not loot until it is won.
        Assert.Equal(0L, await Pending(a));
        Assert.Equal(0L, await Pending(b));

        // Experience is NOT contested, and both of them have it.
        Assert.True(await Xp(a) > 0L);
        Assert.True(await Xp(b) > 0L);
    }

    /// <summary>
    /// NEED BEATS GREED, WHATEVER THE DICE SAID.
    ///
    /// Proven end to end rather than only in the rules: the whole point is that the
    /// endpoint uses the shared decision, and a server that rolled its own comparison
    /// beside it is exactly the kind of second implementation this codebase keeps
    /// removing.
    /// </summary>
    [SkippableFact]
    public async Task NeedBeatsGreed()
    {
        RequireDatabase();

        var (aP, a, bP, b) = await AGroupOfTwo("NeedGreed");

        Guid encounter = await KillTheKing(aP, a, bP, b);

        Guid roll = await FirstRoll(encounter);

        Skip.If(roll == Guid.Empty, "the King dropped nothing this run");

        // ══ THE ONE WHO NEEDS IS THE ONE WHO ROLLED WORSE ═════════════════════
        //
        // Chosen rather than assumed, and it is what makes this a test of the RULE
        // instead of a test of the dice. The first version had the earlier joiner
        // press need -- and the earlier joiner also wins ties and often wins on the
        // roll, so it stayed green with need and greed collapsed into a single band,
        // which is precisely the thing it claims to prove.
        //
        // The rolls are derivable, so who is worse off is knowable before anybody
        // presses anything.
        long seed = await Seed(encounter);

        int rollForA = LootRoll.RollFor(seed, 0, 0);
        int rollForB = LootRoll.RollFor(seed, 0, 1);

        Skip.If(rollForA == rollForB, "the two rolled the same, so neither is worse off");

        bool aIsWorse = rollForA < rollForB;

        Guid needy  = aIsWorse ? a : b;
        Guid greedy = aIsWorse ? b : a;

        Player needyPlayer  = aIsWorse ? aP : bP;
        Player greedyPlayer = aIsWorse ? bP : aP;

        await Answer(greedyPlayer, greedy, roll, LootRoll.Greed);
        await Answer(needyPlayer,  needy,  roll, LootRoll.Need);

        Assert.Equal(needy, await Winner(roll));
        Assert.True(await Pending(needy) > 0L, "the winner was not paid");
        Assert.Equal(0L, await Pending(greedy));
    }

    /// <summary>
    /// EVERYBODY PASSING IS A REAL ANSWER.
    ///
    /// Settled with no winner rather than left open for ever. An item nobody wanted is
    /// destroyed, which is the honest reading of two people pressing pass -- and an
    /// unsettled roll would sit in the group's window on every poll from now on.
    /// </summary>
    [SkippableFact]
    public async Task EverybodyPassingSettlesWithNobody()
    {
        RequireDatabase();

        var (aP, a, bP, b) = await AGroupOfTwo("Passing");

        Guid encounter = await KillTheKing(aP, a, bP, b);

        Guid roll = await FirstRoll(encounter);

        Skip.If(roll == Guid.Empty, "the King dropped nothing this run");

        await Answer(aP, a, roll, LootRoll.Pass);
        await Answer(bP, b, roll, LootRoll.Pass);

        Assert.Equal(1L, await Settled(roll));
        Assert.Equal(Guid.Empty, await Winner(roll));

        Assert.Equal(0L, await Pending(a));
        Assert.Equal(0L, await Pending(b));
    }

    /// <summary>
    /// A SECOND ANSWER IS NOT AN ANSWER.
    ///
    /// Changing your mind after seeing what somebody else did is the one thing a
    /// need/greed window must never allow. Enforced by the primary key rather than by
    /// a check, so a new code path cannot forget it.
    /// </summary>
    [SkippableFact]
    public async Task YouCannotChangeYourMind()
    {
        RequireDatabase();

        var (aP, a, bP, b) = await AGroupOfTwo("Fickle");

        Guid encounter = await KillTheKing(aP, a, bP, b);

        Guid roll = await FirstRoll(encounter);

        Skip.If(roll == Guid.Empty, "the King dropped nothing this run");

        await Answer(aP, a, roll, LootRoll.Pass);

        JsonElement second = await Answer(aP, a, roll, LootRoll.Need);

        Assert.False(second.GetProperty("answered").GetBoolean(),
                     "a pass was upgraded to a need after the fact");

        await Answer(bP, b, roll, LootRoll.Greed);

        // The greed wins, because the need was never recorded.
        Assert.Equal(b, await Winner(roll));
    }

    /// <summary>
    /// SOMEBODY WHO DID NOT FIGHT IT CANNOT PUT A HAND UP.
    ///
    /// Checked on the server rather than left to the client not showing the window.
    /// Anybody with a roll id and an account could otherwise claim a crown they were
    /// nowhere near.
    /// </summary>
    [SkippableFact]
    public async Task AStrangerCannotRoll()
    {
        RequireDatabase();

        var (aP, a, bP, b) = await AGroupOfTwo("Outsider");

        Guid encounter = await KillTheKing(aP, a, bP, b);

        Guid roll = await FirstRoll(encounter);

        Skip.If(roll == Guid.Empty, "the King dropped nothing this run");

        var stranger = await api.NewPlayerAsync();
        Guid outsider = await OwnershipTests.CreateCharacter(stranger, "Passerby");

        var response = await OwnershipTests.Post(stranger, $"/encounter/{outsider}/rolls/{roll}",
                                                 new { choice = LootRoll.Need });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// A ROLL NOBODY ANSWERS SETTLES ITSELF.
    ///
    /// Somebody will close their browser mid-window, and a crown must not wait for
    /// them for ever. There is no background sweep in this API, so the settle happens
    /// on the next read -- which is what the group's own open window is doing every
    /// two seconds.
    /// </summary>
    [SkippableFact]
    public async Task AnUnansweredRollSettlesOnTheClock()
    {
        RequireDatabase();

        var (aP, a, bP, b) = await AGroupOfTwo("Silent");

        Guid encounter = await KillTheKing(aP, a, bP, b);

        Guid roll = await FirstRoll(encounter);

        Skip.If(roll == Guid.Empty, "the King dropped nothing this run");

        await Answer(aP, a, roll, LootRoll.Need);

        // The other one never says anything.
        Assert.Equal(0L, await Settled(roll));

        api.Clock.Advance(TimeSpan.FromSeconds(LootRoll.DecideSeconds + 5d));

        (await aP.Client.GetAsync($"/encounter/{a}/rolls")).EnsureSuccessStatusCode();

        Assert.Equal(1L, await Settled(roll));
        Assert.Equal(a, await Winner(roll));
    }

    /// <summary>Alone, nothing is contested: the drop goes straight into pending loot.</summary>
    [SkippableFact]
    public async Task SoloLootIsNotRolledFor()
    {
        RequireDatabase();

        var player = await api.NewPlayerAsync();
        Guid character = await Ready(player, "Soloist");

        Guid encounter = await KillTheKing(player, character, null, Guid.Empty);

        Assert.Equal(0L, await Rolls(encounter));
        Assert.True(await Pending(character) > 0L, "a solo kill paid nothing");
    }

    // ── Machinery ─────────────────────────────────────────────────────────────

    /// <summary>The atSeconds of every cast in every phase, as one comparable list.</summary>
    private static string[] CastSignature(JsonElement fight)
    {
        var signature = new List<string>();

        foreach (JsonElement phase in fight.GetProperty("phases").EnumerateArray())
        {
            foreach (JsonElement cast in phase.GetProperty("casts").EnumerateArray())
            {
                signature.Add($"{phase.GetProperty("index").GetInt32()}:" +
                              $"{cast.GetProperty("abilityId").GetString()}@" +
                              $"{cast.GetProperty("atSeconds").GetDouble():F3}");
            }
        }

        return signature.ToArray();
    }

    /// <summary>
    /// Beats the King, with one fighter or two, and returns the encounter.
    ///
    /// Deliberately the same loop the group tests already use rather than writing
    /// damage straight into the row: the loot behaviour under test only happens on the
    /// resolve path, and a fight arranged by hand would prove the resolve worked on a
    /// state no real fight produces.
    /// </summary>
    private async Task<Guid> KillTheKing(Player aP, Guid a, Player? bP, Guid b)
    {
        JsonElement fight = await EngageOk(aP, a);

        if (bP is not null) await EngageOk(bP, b);

        Guid   encounter = fight.GetProperty("encounterId").GetGuid();
        long   maxHp     = fight.GetProperty("bossMaxHp").GetInt64();
        double dps       = fight.GetProperty("frozenDps").GetDouble();

        var swingsA = new Swings();
        var swingsB = new Swings();

        for (int step = 0; step < 400; step++)
        {
            JsonElement said = await Act(aP, a, swingsA, 8);

            if (bP is not null) await Act(bP, b, swingsB, 8);

            if (said.GetProperty("dead").GetBoolean()) break;

            api.Clock.Advance(TimeSpan.FromSeconds(Math.Max(1d, maxHp / dps / 20d)));
        }

        JsonElement done = await Body(
            await OwnershipTests.Post(aP, $"/encounter/{a}/resolve", new { }));

        Assert.True(done.GetProperty("won").GetBoolean(), "the King survived the test");

        return encounter;
    }

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

        await Execute(
            """
            insert into kill_counter (character_id, monster_id, active_kills)
            values ($1, 'goblin', 100000)
            on conflict (character_id, monster_id) do update set active_kills = 100000;
            """, character);

        (await OwnershipTests.Post(player, $"/boss/{character}/unlock", new { }))
            .EnsureSuccessStatusCode();

        return character;
    }

    private async Task GiveAndEquip(Player player, Guid character, string itemId, string slotId)
    {
        await Execute(
            """
            insert into inventory_slot (character_id, slot_index, item_id, quantity)
            values ($1, 29, $2, 1)
            on conflict (character_id, slot_index) do update
               set item_id = excluded.item_id, quantity = excluded.quantity;
            """, character, itemId);

        // Not asserted: the point of the test is the DPS not moving, and a content
        // change that renamed the sword would otherwise fail it for the wrong reason.
        await OwnershipTests.Post(player, $"/equipment/{character}/equip",
                                  new { itemId, slotId });
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

    private static async Task<JsonElement> Flee(Player player, Guid character)
    {
        var response = await OwnershipTests.Post(player, $"/encounter/{character}/flee", new { });
        response.EnsureSuccessStatusCode();

        return await Body(response);
    }

    private static async Task<JsonElement> Answer(Player player, Guid character, Guid roll, string choice)
    {
        var response = await OwnershipTests.Post(player, $"/encounter/{character}/rolls/{roll}",
                                                 new { choice });
        response.EnsureSuccessStatusCode();

        return await Body(response);
    }

    private static async Task<JsonElement> Body(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private async Task Execute(string sql, params object[] args)
    {
        await using var db = await api.OpenDatabaseAsync();
        await using var command = db.CreateCommand();

        command.CommandText = sql;

        foreach (object arg in args) command.Parameters.AddWithValue(arg);

        await command.ExecuteNonQueryAsync();
    }

    private async Task<long> Scalar(string sql, params object[] args)
    {
        await using var db = await api.OpenDatabaseAsync();
        await using var command = db.CreateCommand();

        command.CommandText = sql;

        foreach (object arg in args) command.Parameters.AddWithValue(arg);

        return Convert.ToInt64(await command.ExecuteScalarAsync() ?? 0L);
    }

    private Task<long> LiveEncounters(Guid character) => Scalar(
        """
        select count(*) from encounter e
        join encounter_participant p on p.encounter_id = e.id
        where p.character_id = $1 and e.ended_at is null;
        """, character);

    private Task<long> Participants(Guid encounter) => Scalar(
        "select count(*) from encounter_participant where encounter_id = $1;", encounter);

    private Task<long> Rolls(Guid encounter) => Scalar(
        "select count(*) from loot_roll where encounter_id = $1;", encounter);

    private Task<long> Settled(Guid roll) => Scalar(
        "select count(*) from loot_roll where id = $1 and settled_at is not null;", roll);

    private Task<long> Pending(Guid character) => Scalar(
        "select count(*) from pending_loot where character_id = $1;", character);

    private Task<long> Xp(Guid character) => Scalar(
        "select xp from character where id = $1;", character);

    /// <summary>The fight's own seed, so the dice can be worked out in advance.</summary>
    private Task<long> Seed(Guid encounter) => Scalar(
        "select seed from encounter where id = $1;", encounter);

    private async Task<Guid> FirstRoll(Guid encounter)
    {
        await using var db = await api.OpenDatabaseAsync();
        await using var command = db.CreateCommand();

        command.CommandText = "select id from loot_roll where encounter_id = $1 order by roll_index limit 1;";
        command.Parameters.AddWithValue(encounter);

        return await command.ExecuteScalarAsync() is Guid id ? id : Guid.Empty;
    }

    private async Task<Guid> Winner(Guid roll)
    {
        await using var db = await api.OpenDatabaseAsync();
        await using var command = db.CreateCommand();

        command.CommandText = "select winner_id from loot_roll where id = $1;";
        command.Parameters.AddWithValue(roll);

        return await command.ExecuteScalarAsync() is Guid id ? id : Guid.Empty;
    }
}
