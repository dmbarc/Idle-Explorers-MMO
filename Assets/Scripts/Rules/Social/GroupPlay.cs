using System;
using System.Collections.Generic;

namespace IdleExplorers.Rules
{
    /// <summary>
    /// Pulling a whole group through one door.
    ///
    /// ══ WHY A CALL AND NOT A TELEPORT ═════════════════════════════════════════════
    ///
    /// One person walking into the throne used to take one person into the throne, and
    /// the other three stood in a field wondering where their tank went. The obvious
    /// fix -- move everybody the instant somebody steps through -- is worse than it
    /// sounds: it hands any member of your group the power to yank you out of whatever
    /// you were doing, mid-craft, mid-sentence, with no warning.
    ///
    /// So a CALL. Somebody raises it, everybody sees the same countdown, and anybody
    /// can step out of it. At the end of the count whoever has not declined travels.
    /// It is the same shape as a dungeon queue pop, for the same reason: being moved
    /// is fine, being moved by surprise is not.
    ///
    /// ══ WHY THE COUNT IS ON THE SERVER AND THE TRAVEL IS ON THE CLIENT ════════════
    ///
    /// The call has one instant of truth -- when it was raised -- and it lives in one
    /// row, so every client counts down to the same moment however far apart their
    /// clocks are. A countdown each client started for itself would drift by however
    /// long the poll took to reach it, and four people would arrive over four seconds.
    ///
    /// The travel itself is presentation: which map you are looking at earns nothing,
    /// and the boss gate is checked again, server-side, at engage. So the client moves
    /// itself, and a client that refuses to has simply declined.
    /// </summary>
    public static class ThroneCall
    {
        /// <summary>
        /// How long the group has to get ready, or to back out.
        ///
        /// Ten seconds. Long enough to swallow a two-second presence poll twice over
        /// and still leave a human time to read the modal and press a button; short
        /// enough that the person who raised it is not standing in a portal wondering
        /// whether it worked.
        /// </summary>
        public const double CountdownSeconds = 10d;

        /// <summary>
        /// How long a raised call stays raised.
        ///
        /// Past this it is not "about to happen", it is a stale row -- somebody raised
        /// it, everybody declined, and nobody cleared it. Well past the countdown so a
        /// client that polls late still finds the call and travels rather than being
        /// left behind by a fraction of a second.
        /// </summary>
        public const double ExpiresAfterSeconds = 40d;

        /// <summary>Seconds left before the group is pulled through. Never negative.</summary>
        public static double Remaining(double secondsSinceRaised) =>
            Math.Max(0d, CountdownSeconds - secondsSinceRaised);

        /// <summary>
        /// Whether a call raised this long ago should still move anybody.
        ///
        /// True only inside the window between the countdown finishing and the call
        /// going stale. Before it, the group is still deciding; after it, the moment
        /// has passed and moving somebody would be the surprise teleport this whole
        /// mechanism exists to avoid.
        /// </summary>
        public static bool ShouldTravel(double secondsSinceRaised) =>
            secondsSinceRaised >= CountdownSeconds &&
            secondsSinceRaised <  ExpiresAfterSeconds;

        /// <summary>Whether the row is worth showing at all.</summary>
        public static bool IsLive(double secondsSinceRaised) =>
            secondsSinceRaised >= 0d && secondsSinceRaised < ExpiresAfterSeconds;
    }

    /// <summary>
    /// Who gets the crown when four people killed the thing wearing it.
    ///
    /// ══ WHY ROLLING AND NOT A COPY EACH ═══════════════════════════════════════════
    ///
    /// A copy each is what the fight paid before this, and it is the quiet way to
    /// ruin a boss: four people kill the King, four Goblin Spears exist, and by the
    /// second clear nobody wants one. Group content that multiplies its own rewards by
    /// the size of the group has no reason to be group content.
    ///
    /// One drop, rolled for, means the King's table stays worth something and a clear
    /// stays worth turning up to.
    ///
    /// ══ NEED BEATS GREED, AND THAT IS THE WHOLE RULE ══════════════════════════════
    ///
    /// Deliberately the oldest and dullest version of this. Need beats every greed
    /// however high it rolled; inside a band the highest roll wins; a pass is not a
    /// roll. Everybody already knows these rules from somewhere else, and a loot
    /// system that has to be explained is a loot system that gets argued about.
    ///
    /// Ties break on the roll, then on the order the participants joined the fight --
    /// which is arbitrary but stable, and stable matters more than fair here because
    /// the alternative is a tie that never resolves.
    /// </summary>
    public static class LootRoll
    {
        /// <summary>How long the group has to answer before the roll settles itself.</summary>
        public const double DecideSeconds = 60d;

        /// <summary>Highest number the dice can show. One to a hundred, as everywhere else.</summary>
        public const int Sides = 100;

        public const string Need = "need";
        public const string Greed = "greed";
        public const string Pass = "pass";

        /// <summary>Whether a client's answer is one of the three that exist.</summary>
        public static bool IsChoice(string choice) =>
            choice == Need || choice == Greed || choice == Pass;

        /// <summary>
        /// Which band a choice sits in. Higher wins outright, whatever the dice said.
        ///
        /// A pass scores below every roll rather than being filtered out separately,
        /// so "everybody passed" needs no special case: the best entry simply has a
        /// band of zero, and <see cref="Decide"/> reads that as nobody wanting it.
        /// </summary>
        public static int Band(string choice) =>
            choice == Need ? 2 : choice == Greed ? 1 : 0;

        /// <summary>One participant's answer, as the decision sees it.</summary>
        public readonly struct Entry
        {
            public Entry(string characterId, string choice, int roll, int joinOrder)
            {
                CharacterId = characterId ?? "";
                Choice      = choice ?? Pass;
                Roll        = roll;
                JoinOrder   = joinOrder;
            }

            public string CharacterId { get; }
            public string Choice { get; }
            public int Roll { get; }

            /// <summary>Where this fighter sits in the order they joined. The tie-break of last resort.</summary>
            public int JoinOrder { get; }
        }

        /// <summary>
        /// Who wins, or empty when nobody wanted it.
        ///
        /// Pure, total, and takes no clock: whether the roll is READY to be decided is
        /// a separate question with a separate answer, because a decision that also
        /// decided its own timing could not be tested without waiting for it.
        /// </summary>
        public static string Decide(IReadOnlyList<Entry> entries)
        {
            if (entries == null || entries.Count == 0) return "";

            Entry best = default;
            bool  have = false;

            foreach (Entry entry in entries)
            {
                if (Band(entry.Choice) <= 0) continue;

                if (!have || Beats(entry, best)) { best = entry; have = true; }
            }

            return have ? best.CharacterId : "";
        }

        private static bool Beats(Entry challenger, Entry holder)
        {
            int a = Band(challenger.Choice);
            int b = Band(holder.Choice);

            if (a != b) return a > b;
            if (challenger.Roll != holder.Roll) return challenger.Roll > holder.Roll;

            return challenger.JoinOrder < holder.JoinOrder;
        }

        /// <summary>
        /// Whether the roll can be settled now.
        ///
        /// Either everybody has answered, or the clock ran out. The clock is what stops
        /// one person who closed their browser holding a crown hostage for ever, and
        /// "everybody answered" is what stops a group that agreed in four seconds from
        /// standing around for fifty-six more.
        /// </summary>
        public static bool CanSettle(int answered, int participants, double secondsSinceOffered) =>
            participants > 0 &&
            (answered >= participants || secondsSinceOffered >= DecideSeconds);

        /// <summary>Seconds left to answer. Never negative.</summary>
        public static double Remaining(double secondsSinceOffered) =>
            Math.Max(0d, DecideSeconds - secondsSinceOffered);

        /// <summary>
        /// The dice, from the fight's own seed.
        ///
        /// Derived rather than random so a contested drop can be re-derived from two
        /// columns months later -- which is the difference between "the server says you
        /// rolled 4" and being able to show that you did.
        /// </summary>
        public static int RollFor(long seed, int rollIndex, int participantIndex)
        {
            var rng = new CounterRandom(seed, DiceOffset + (ulong)(rollIndex * 64 + participantIndex));

            return (int)rng.RangeInclusive(1L, Sides);
        }

        /// <summary>
        /// Where the dice start in the fight's random stream.
        ///
        /// Far past both the damage rolls and the drop rolls, so a die can never share
        /// an index with either. Sharing would make somebody's roll a function of how
        /// many times they swung, which is not a die -- it is a strategy.
        /// </summary>
        private const ulong DiceOffset = 2_000_000UL;
    }
}
