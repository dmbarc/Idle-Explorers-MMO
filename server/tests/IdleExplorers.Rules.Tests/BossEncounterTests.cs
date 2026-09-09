using System;
using System.Collections.Generic;
using IdleExplorers.Rules;
using Xunit;

namespace IdleExplorers.Rules.Tests;

/// <summary>
/// The boss fight's arithmetic, and the one bound that makes it safe.
/// </summary>
public class BossEncounterTests
{
    // ══ THE CEILING ═══════════════════════════════════════════════════════════

    [Fact]
    public void TheCeilingIsDpsTimesElapsedPlusTheTolerance()
    {
        Assert.Equal(100d * (10d + BossEncounter.ToleranceSeconds),
                     BossEncounter.DamageCeiling(100d, 10d), 6);
    }

    /// <summary>
    /// The exploit the ceiling exists for: batch a million actions into one instant.
    /// </summary>
    [Fact]
    public void AMillionInstantActionsAreWorthAnInstant()
    {
        const double dps = 500d;

        double ceiling = BossEncounter.DamageCeiling(dps, elapsedSeconds: 0d);

        // Two seconds of DPS, which is the opening-swing tolerance and nothing more.
        Assert.Equal(dps * BossEncounter.ToleranceSeconds, ceiling, 6);

        // A million swings would be worth this much if nothing capped them. The gap is
        // the whole point of the file.
        double uncapped = 1_000_000d * BossEncounter.SwingDamage(dps, 1d, 1d, 0d, null);

        Assert.True(uncapped > ceiling * 1000d,
                    "the test is meaningless unless the uncapped total is enormously larger");
    }

    [Fact]
    public void TheCeilingGrowsOnlyWithTheClock()
    {
        double atTen    = BossEncounter.DamageCeiling(100d, 10d);
        double atTwenty = BossEncounter.DamageCeiling(100d, 20d);

        Assert.Equal(1000d, atTwenty - atTen, 6);
    }

    [Fact]
    public void TheToleranceDoesNotGrowWithTheFight()
    {
        // A tolerance expressed as a fraction would be worth thirty seconds of free
        // damage at the end of a five minute enrage. This one is worth two, always.
        double slackEarly = BossEncounter.DamageCeiling(100d, 1d)   - 100d * 1d;
        double slackLate  = BossEncounter.DamageCeiling(100d, 300d) - 100d * 300d;

        Assert.Equal(slackEarly, slackLate, 6);
    }

    [Fact]
    public void NegativeAndNonsenseInputsCannotMintDamage()
    {
        Assert.Equal(0d, BossEncounter.DamageCeiling(-100d, 10d), 6);
        Assert.True(BossEncounter.DamageCeiling(100d, -50d) >= 0d);
    }

    // ══ DAMAGE ════════════════════════════════════════════════════════════════

    [Fact]
    public void BossDamagePerSecondEqualsFarmingDamagePerSecond()
    {
        const double dps = 250d, swingSeconds = 2.4d;

        // No jitter, no armour, no ability: one swing is exactly one swing's worth.
        double swing = BossEncounter.SwingDamage(dps, swingSeconds, 1d, 0d, null);

        Assert.Equal(dps * swingSeconds, swing, 6);

        // Which is the property that keeps gear meaning the same thing in both places.
        Assert.Equal(dps, swing / swingSeconds, 6);
    }

    [Fact]
    public void AnAbilityMultipliesTheSwingAndIsBounded()
    {
        double plain  = BossEncounter.SwingDamage(100d, 1d, 1d, 0d, null);
        double heavy  = BossEncounter.SwingDamage(100d, 1d, 2.2d, 0d, null);
        double absurd = BossEncounter.SwingDamage(100d, 1d, 10_000d, 0d, null);

        Assert.Equal(plain * 2.2d, heavy, 6);

        // A content typo cannot end the fight in one swing, even though the ceiling
        // would also have caught it.
        Assert.Equal(plain * BossEncounter.MaxAbilityMultiplier, absurd, 6);
    }

    [Fact]
    public void ArmourSlowsAFightAndNeverStopsOne()
    {
        double naked = BossEncounter.Mitigate(100d, 0d);
        double armoured = BossEncounter.Mitigate(100d, BossEncounter.ArmourHalvingPoint);

        Assert.Equal(100d, naked, 6);
        Assert.Equal(50d,  armoured, 6);

        // The property flat subtraction does not have: however absurd the armour, the
        // boss is slow rather than immune -- so a content typo is a long fight and not
        // a wall with no feedback.
        Assert.True(BossEncounter.Mitigate(100d, 1_000_000d) > 0d);
    }

    [Fact]
    public void AZeroSwingIntervalCannotBecomeInfiniteDamage()
    {
        double zero = BossEncounter.SwingDamage(100d, 0d, 1d, 0d, null);
        double fast = BossEncounter.SwingDamage(100d, 0.0001d, 1d, 0d, null);

        Assert.True(zero > 0d);
        Assert.Equal(100d * WeaponProfile.MinAttackSeconds, fast, 6);
    }

    [Fact]
    public void TheSameActionIndexRollsTheSameNumber()
    {
        // A retry must not be able to re-roll a bad hit into a good one.
        var first  = new CounterRandom(seed: 4242L, startIndex: 47UL);
        var second = new CounterRandom(seed: 4242L, startIndex: 47UL);

        Assert.Equal(BossEncounter.SwingDamage(100d, 1d, 1d, 0d, first),
                     BossEncounter.SwingDamage(100d, 1d, 1d, 0d, second), 9);
    }

    [Fact]
    public void JitterStaysInsideItsBand()
    {
        var rng = new CounterRandom(7L);

        double baseline = 100d * 1d;

        for (int i = 0; i < 500; i++)
        {
            double swing = BossEncounter.SwingDamage(100d, 1d, 1d, 0d, rng);

            Assert.InRange(swing,
                           baseline * (1d - BossEncounter.SwingVariance),
                           baseline * (1d + BossEncounter.SwingVariance));
        }
    }

    // ══ PHASES ════════════════════════════════════════════════════════════════

    private static BossPhase[] ThreePhases() =>
    [
        new() { name = "one",   fromHealthFraction = 1.00f, abilities = [Cleave()] },
        new() { name = "two",   fromHealthFraction = 0.66f, abilities = [Cleave(), Charge()] },
        new() { name = "three", fromHealthFraction = 0.33f, abilities = [Cleave(), Charge()],
                hasteMultiplier = 1.25f },
    ];

    private static BossAbility Cleave() => new()
    {
        id = "cleave_arc", shape = "cone", telegraphSeconds = 1.2f,
        cooldownSeconds = 7f, damageMultiplier = 1.6f, range = 5f, arcDegrees = 90f,
    };

    private static BossAbility Charge() => new()
    {
        id = "king_charge", shape = "line", telegraphSeconds = 1.5f,
        cooldownSeconds = 11f, damageMultiplier = 2.2f, range = 20f, halfWidth = 1.5f,
    };

    [Theory]
    [InlineData(1.00, 0)]
    [InlineData(0.67, 0)]
    [InlineData(0.66, 1)]
    [InlineData(0.34, 1)]
    [InlineData(0.33, 2)]
    [InlineData(0.00, 2)]
    public void PhaseFollowsHealth(double health, int expected) =>
        Assert.Equal(expected, BossEncounter.PhaseFor(health, ThreePhases()));

    [Fact]
    public void NoPhasesAtAllIsPhaseZeroRatherThanACrash()
    {
        Assert.Equal(0, BossEncounter.PhaseFor(0.5d, null));
        Assert.Equal(0, BossEncounter.PhaseFor(0.5d, []));
    }

    [Fact]
    public void HealthOutsideZeroToOneIsClampedRatherThanTrusted()
    {
        Assert.Equal(0, BossEncounter.PhaseFor(99d,  ThreePhases()));
        Assert.Equal(2, BossEncounter.PhaseFor(-5d,  ThreePhases()));
    }

    // ══ THE TIMELINE ══════════════════════════════════════════════════════════

    [Fact]
    public void TheSameSeedGivesTheSameSchedule()
    {
        var phases = ThreePhases();

        List<BossCast> once  = BossEncounter.Timeline(99L, 1, phases[1], 60d);
        List<BossCast> again = BossEncounter.Timeline(99L, 1, phases[1], 60d);

        Assert.Equal(once.Count, again.Count);

        for (int i = 0; i < once.Count; i++)
        {
            Assert.Equal(once[i].abilityId, again[i].abilityId);
            Assert.Equal(once[i].atSeconds, again[i].atSeconds, 6);
        }
    }

    [Fact]
    public void EachPhaseGetsItsOwnScheduleRatherThanAShiftedCopy()
    {
        var phases = ThreePhases();

        List<BossCast> second = BossEncounter.Timeline(99L, 1, phases[1], 60d);
        List<BossCast> third  = BossEncounter.Timeline(99L, 2, phases[2], 60d);

        // A shared running counter would make phase three phase two, offset -- which a
        // player notices by their third attempt.
        bool identical = second.Count == third.Count;

        if (identical)
        {
            for (int i = 0; i < second.Count && identical; i++)
                identical = Math.Abs(second[i].atSeconds - third[i].atSeconds) < 0.0001f;
        }

        Assert.False(identical);
    }

    [Fact]
    public void CastsArriveInOrderAndInsideTheWindow()
    {
        List<BossCast> casts = BossEncounter.Timeline(5L, 1, ThreePhases()[1], 120d);

        Assert.NotEmpty(casts);

        for (int i = 1; i < casts.Count; i++)
            Assert.True(casts[i].atSeconds >= casts[i - 1].atSeconds);

        foreach (var cast in casts) Assert.InRange(cast.atSeconds, 0f, 120f);
    }

    [Fact]
    public void AnAbilityDoesNotRecastFasterThanItsCooldown()
    {
        // One ability, so every gap in the list is that ability's own cooldown.
        var phase = new BossPhase { fromHealthFraction = 1f, abilities = [Charge()] };

        List<BossCast> casts = BossEncounter.Timeline(11L, 0, phase, 300d);

        Assert.True(casts.Count > 5);

        // 0.9 is the low end of the recast drift.
        double floor = Charge().cooldownSeconds * 0.9d - 0.0001d;

        for (int i = 1; i < casts.Count; i++)
            Assert.True(casts[i].atSeconds - casts[i - 1].atSeconds >= floor);
    }

    [Fact]
    public void HasteMakesAPhaseBusier()
    {
        var slow = new BossPhase { fromHealthFraction = 1f, abilities = [Cleave()], hasteMultiplier = 1f };
        var fast = new BossPhase { fromHealthFraction = 1f, abilities = [Cleave()], hasteMultiplier = 2f };

        Assert.True(BossEncounter.Timeline(3L, 0, fast, 120d).Count >
                    BossEncounter.Timeline(3L, 0, slow, 120d).Count);
    }

    /// <summary>
    /// Content is hand-authored JSON on a live server, and this runs on a request
    /// thread. A zero cooldown must produce a capped timeline, not an out-of-memory.
    /// </summary>
    [Fact]
    public void AZeroCooldownIsBoundedRatherThanInfinite()
    {
        var broken = new BossPhase
        {
            fromHealthFraction = 1f,
            abilities = [new BossAbility { id = "spam", cooldownSeconds = 0f, shape = "circle" }],
        };

        List<BossCast> casts = BossEncounter.Timeline(1L, 0, broken, BossEncounter.MaxEnrageSeconds);

        Assert.NotEmpty(casts);
        Assert.True(casts.Count <= BossEncounter.MaxCastsPerPhase);

        // The floor is what bounds it: a zero becomes one second, not zero seconds.
        for (int i = 1; i < casts.Count; i++)
            Assert.True(casts[i].atSeconds > casts[i - 1].atSeconds);
    }

    [Fact]
    public void APhaseWithNoAbilitiesSchedulesNothing()
    {
        Assert.Empty(BossEncounter.Timeline(1L, 0, new BossPhase(), 60d));
        Assert.Empty(BossEncounter.Timeline(1L, 0, null, 60d));
    }

    [Fact]
    public void EveryCastCarriesWhatItTakesToDrawIt()
    {
        // A client with stale content must still telegraph the right shape. "Your
        // content was out of date" is not a boss mechanic.
        foreach (var cast in BossEncounter.Timeline(2L, 1, ThreePhases()[1], 60d))
        {
            Assert.False(string.IsNullOrEmpty(cast.abilityId));
            Assert.False(string.IsNullOrEmpty(cast.shape));
            Assert.True(cast.telegraphSeconds > 0f);
            Assert.True(cast.pulses >= 1);
        }
    }

    // ══ ENRAGE AND COOLDOWNS ══════════════════════════════════════════════════

    [Fact]
    public void EnrageIsClampedWhateverContentSays()
    {
        Assert.Equal(BossEncounter.MaxEnrageSeconds, BossEncounter.ClampEnrage(0d));
        Assert.Equal(BossEncounter.MaxEnrageSeconds, BossEncounter.ClampEnrage(99_999d));
        Assert.Equal(BossEncounter.MinEnrageSeconds, BossEncounter.ClampEnrage(1d));
        Assert.Equal(300d, BossEncounter.ClampEnrage(300d));
    }

    [Fact]
    public void EnrageFiresAtTheDeadlineNotAfterIt()
    {
        Assert.False(BossEncounter.Enraged(299d, 300d));
        Assert.True(BossEncounter.Enraged(300d, 300d));
        Assert.True(BossEncounter.Enraged(301d, 300d));
    }

    [Fact]
    public void AnAbilityIsReadyOnlyAfterItsCooldown()
    {
        Assert.True(BossEncounter.AbilityReady(lastUsedAtSeconds: -1d, nowSeconds: 0d, cooldownSeconds: 10d));

        Assert.False(BossEncounter.AbilityReady(10d, 15d, 10d));
        Assert.True(BossEncounter.AbilityReady(10d, 20d, 10d));

        // The same tolerance the ceiling uses, so a cooldown that came off during the
        // round trip is not refused on arrival.
        Assert.True(BossEncounter.AbilityReady(10d, 20d - BossEncounter.ToleranceSeconds, 10d));
    }
}
