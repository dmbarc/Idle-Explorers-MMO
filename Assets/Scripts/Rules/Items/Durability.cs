namespace IdleExplorers.Rules
{
    /// <summary>
    /// How fast worn gear wears out.
    ///
    /// ══ WHY WEAR IS SETTLED RATHER THAN PER-HIT ═══════════════════════════════════
    ///
    /// The obvious design is "every hit you take scuffs your armour", and the client
    /// could do that in one line. It would also be a lie: durability lives in the
    /// equipment table, the server owns it, and the next pull would put every point
    /// straight back. The client would show armour degrading and repairing itself on a
    /// two-second cycle.
    ///
    /// So wear happens where every other consequence of fighting happens -- inside
    /// settlement, integrated over the window, from the server's own count of what the
    /// character did. Which has a second advantage worth having on purpose: AFK combat
    /// wears gear at the same rate as active combat. If it did not, leaving the game
    /// running would be strictly better than playing it, which is the wrong incentive
    /// for an idle game to create about its own idling.
    ///
    /// ══ WHY KILLS AND NOT DAMAGE TAKEN ════════════════════════════════════════════
    ///
    /// The server does not model incoming hits. Farm combat is resolved as an integral
    /// -- damage per second against monster health over elapsed time -- so there is no
    /// list of blows to count. Kills are the server's honest proxy for "how much
    /// fighting happened", and they are the number every other reward is already paid
    /// from.
    ///
    /// ══ THE NUMBERS, AND WHERE THEY COME FROM ═════════════════════════════════════
    ///
    /// A goblin takes roughly six seconds to kill, so a solid hour of combat is about
    /// six hundred kills. At the rate below that is a little over two hundred points of
    /// wear an hour, spread across the eight or so pieces a dressed character has on.
    ///
    /// Against the gear that exists -- starter sets at 40, tin around 100 to 150,
    /// bronze at 200 to 400 -- a single piece survives several hours of fighting, and a
    /// full set accumulates enough wear to be worth a trip to repair within one or two.
    /// Which is what "needs repair once in a while" has to mean to be a mechanic rather
    /// than a chore.
    ///
    /// At two coins a point that is around four hundred coins an hour, against a goblin
    /// camp that pays considerably more -- so it is a running cost rather than a wall.
    /// </summary>
    public static class Durability
    {
        /// <summary>Points of wear per monster killed, across the whole outfit.</summary>
        public const double PointsPerKill = 0.35d;

        /// <summary>
        /// Most wear one settlement may apply, however long the window was.
        ///
        /// A player who leaves for two days comes back to gear that needs repairing,
        /// not to a full set broken twice over. The cap is deliberately generous
        /// enough that an ordinary session never reaches it and a very long absence
        /// stings without being punitive -- an idle game must not punish idling.
        /// </summary>
        public const int MaxPerSettlement = 120;

        /// <summary>
        /// How many points of wear a window's fighting produced.
        ///
        /// The fractional part is resolved by a roll rather than dropped, so a hundred
        /// windows of three kills each wear the same as one window of three hundred.
        /// Truncating instead would make frequent short settles free, which is an
        /// exploit shaped exactly like standing up and sitting down repeatedly.
        /// </summary>
        public static int WearFor(long kills, IRandomSource rng)
        {
            if (kills <= 0L) return 0;

            double exact = kills * PointsPerKill;

            int whole = (int)exact;
            double remainder = exact - whole;

            if (rng != null && remainder > 0d && rng.Next01() < remainder) whole++;

            return whole > MaxPerSettlement ? MaxPerSettlement : whole;
        }

        /// <summary>
        /// True when a piece at this condition should stop contributing its stats.
        ///
        /// Zero, and nothing else. A piece is never destroyed and never unequipped --
        /// deleting gear somebody earned because they did not watch a bar is a
        /// punishment rather than a mechanic, and the whole repair loop depends on the
        /// broken thing still being there to repair.
        /// </summary>
        public static bool IsBroken(int durability) => durability <= 0;
    }
}
