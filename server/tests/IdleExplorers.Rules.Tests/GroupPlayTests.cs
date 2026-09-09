using System;
using System.Collections.Generic;
using IdleExplorers.Rules;
using Xunit;

namespace IdleExplorers.Rules.Tests;

/// <summary>
/// The two rules a group boss added: being called through a door, and arguing about
/// what fell out of it.
///
/// Both are here rather than in the endpoint because both are decisions, and a
/// decision the server makes inside a transaction is a decision nobody can test in
/// under a second. The endpoints above them are wiring.
/// </summary>
public class ThroneCallTests
{
    [Fact]
    public void TheCountRunsDownAndStops()
    {
        Assert.Equal(ThroneCall.CountdownSeconds, ThroneCall.Remaining(0d));
        Assert.Equal(0d, ThroneCall.Remaining(ThroneCall.CountdownSeconds));

        // Never negative. A client renders this straight into a label.
        Assert.Equal(0d, ThroneCall.Remaining(ThroneCall.CountdownSeconds + 500d));
    }

    /// <summary>
    /// Nobody moves before the count finishes.
    ///
    /// The whole reason a call is an offer rather than a teleport: any member of the
    /// group could otherwise pull the other three out of whatever they were doing,
    /// with no warning and no undo.
    /// </summary>
    [Fact]
    public void NobodyTravelsDuringTheCount()
    {
        Assert.False(ThroneCall.ShouldTravel(0d));
        Assert.False(ThroneCall.ShouldTravel(ThroneCall.CountdownSeconds - 0.1d));

        Assert.True(ThroneCall.ShouldTravel(ThroneCall.CountdownSeconds));
    }

    /// <summary>
    /// AND A STALE CALL MOVES NOBODY EITHER.
    ///
    /// The failure this prevents is the nastier of the two. The row outlives the
    /// countdown -- it has to, or a client whose poll landed a second late would be
    /// left behind -- so without an upper bound, somebody who went in, killed the
    /// King and walked back out to the camp would be dragged straight back through
    /// the door by a call that technically still said go.
    /// </summary>
    [Fact]
    public void AStaleCallMovesNobody()
    {
        Assert.True(ThroneCall.ShouldTravel(ThroneCall.ExpiresAfterSeconds - 1d));

        Assert.False(ThroneCall.ShouldTravel(ThroneCall.ExpiresAfterSeconds));
        Assert.False(ThroneCall.ShouldTravel(ThroneCall.ExpiresAfterSeconds + 60d));

        Assert.False(ThroneCall.IsLive(ThroneCall.ExpiresAfterSeconds));
    }

    /// <summary>
    /// The window has to outlast several polls.
    ///
    /// Stated as a test because it is a relationship between two constants in
    /// different files, and the day somebody shortens the countdown to five seconds
    /// this is what says the group will not all make it.
    /// </summary>
    [Fact]
    public void ThereIsTimeForSeveralPollsToArrive()
    {
        Assert.True(ThroneCall.CountdownSeconds >= Presence.ReportSeconds * 4d,
                    "the countdown is shorter than four presence polls — somebody will miss it");

        Assert.True(ThroneCall.ExpiresAfterSeconds > ThroneCall.CountdownSeconds * 2d,
                    "a call goes stale too soon after it fires");
    }
}

public class LootRollTests
{
    /// <summary>NEED BEATS GREED, however good the greed's dice were.</summary>
    [Fact]
    public void NeedBeatsGreedWhateverTheDiceSaid()
    {
        string winner = LootRoll.Decide(new List<LootRoll.Entry>
        {
            new("greedy", LootRoll.Greed, 100, 0),
            new("needy",  LootRoll.Need,    1, 1),
        });

        Assert.Equal("needy", winner);
    }

    [Fact]
    public void InsideABandTheHighestRollWins()
    {
        string winner = LootRoll.Decide(new List<LootRoll.Entry>
        {
            new("low",  LootRoll.Need, 12, 0),
            new("high", LootRoll.Need, 88, 1),
            new("mid",  LootRoll.Need, 51, 2),
        });

        Assert.Equal("high", winner);
    }

    /// <summary>
    /// A PASS IS NOT A ROLL.
    ///
    /// Not "a roll of zero", which would still be an entry that could win a table of
    /// passes. Nobody wanting it is a real outcome and the item is destroyed, which is
    /// the honest reading of everybody pressing pass.
    /// </summary>
    [Fact]
    public void EverybodyPassingMeansNobodyWins()
    {
        string winner = LootRoll.Decide(new List<LootRoll.Entry>
        {
            new("a", LootRoll.Pass, 99, 0),
            new("b", LootRoll.Pass, 98, 1),
        });

        Assert.Equal("", winner);
    }

    [Fact]
    public void APassLosesToASingleGreed()
    {
        string winner = LootRoll.Decide(new List<LootRoll.Entry>
        {
            new("a", LootRoll.Pass,  99, 0),
            new("b", LootRoll.Greed,  3, 1),
        });

        Assert.Equal("b", winner);
    }

    /// <summary>
    /// A TIE RESOLVES, AND RESOLVES THE SAME WAY EVERY TIME.
    ///
    /// Two people needing the same thing and rolling the same number is rare and
    /// entirely possible over a hundred sides. Stability matters more than fairness
    /// here: an unresolved tie is an item nobody ever gets, and "who joined the fight
    /// first" is at least a fact rather than a coin.
    /// </summary>
    [Fact]
    public void ATieGoesToWhoeverJoinedFirst()
    {
        var entries = new List<LootRoll.Entry>
        {
            new("late",  LootRoll.Need, 50, 3),
            new("early", LootRoll.Need, 50, 0),
        };

        Assert.Equal("early", LootRoll.Decide(entries));

        // And the other way round in the list, because a decision that depends on
        // enumeration order is not a decision.
        entries.Reverse();
        Assert.Equal("early", LootRoll.Decide(entries));
    }

    [Fact]
    public void NoAnswersAtAllMeansNobodyWins() =>
        Assert.Equal("", LootRoll.Decide(new List<LootRoll.Entry>()));

    /// <summary>
    /// It settles when everybody has spoken, or when the clock runs out.
    ///
    /// The clock is what stops one person who closed their browser holding a crown
    /// hostage; "everybody answered" is what stops a group who agreed in four seconds
    /// standing around for the other fifty-six.
    /// </summary>
    [Fact]
    public void ItSettlesOnTheLastAnswerOrTheClock()
    {
        Assert.False(LootRoll.CanSettle(answered: 2, participants: 4, secondsSinceOffered: 5d));
        Assert.True(LootRoll.CanSettle(answered: 4, participants: 4, secondsSinceOffered: 5d));

        Assert.True(LootRoll.CanSettle(answered: 1, participants: 4,
                                       secondsSinceOffered: LootRoll.DecideSeconds));

        // An encounter with nobody in it cannot settle, rather than settling instantly
        // for nobody -- that would be a divide-by-zero shaped bug in a different form.
        Assert.False(LootRoll.CanSettle(answered: 0, participants: 0, secondsSinceOffered: 999d));
    }

    /// <summary>
    /// THE DICE ARE DERIVED, NOT RANDOM.
    ///
    /// Which is what lets a contested drop be re-derived from two columns months
    /// later, rather than taken on the server's word.
    /// </summary>
    [Fact]
    public void TheSameSeedAlwaysRollsTheSameNumber()
    {
        Assert.Equal(LootRoll.RollFor(1234L, 0, 0), LootRoll.RollFor(1234L, 0, 0));

        // And different players on the same drop do not all roll the same, which would
        // make every contested item a permanent tie.
        Assert.NotEqual(LootRoll.RollFor(1234L, 0, 0), LootRoll.RollFor(1234L, 0, 1));

        // Nor does the same player roll the same on every drop of one kill.
        Assert.NotEqual(LootRoll.RollFor(1234L, 0, 0), LootRoll.RollFor(1234L, 1, 0));
    }

    [Fact]
    public void EveryRollIsOnTheDice()
    {
        for (int index = 0; index < 200; index++)
        {
            int roll = LootRoll.RollFor(98765L, index / 4, index % 4);

            Assert.InRange(roll, 1, LootRoll.Sides);
        }
    }

    [Fact]
    public void OnlyThreeAnswersExist()
    {
        Assert.True(LootRoll.IsChoice(LootRoll.Need));
        Assert.True(LootRoll.IsChoice(LootRoll.Greed));
        Assert.True(LootRoll.IsChoice(LootRoll.Pass));

        Assert.False(LootRoll.IsChoice("NEED"));
        Assert.False(LootRoll.IsChoice("disenchant"));
        Assert.False(LootRoll.IsChoice(""));
        Assert.False(LootRoll.IsChoice(null));
    }
}
