using System;

namespace IdleExplorers.Rules
{
    /// <summary>What a character is doing. Empty means standing still.</summary>
    public enum ActivityKind
    {
        Idle,
        Gather,
        Craft,
        Combat,
    }

    /// <summary>
    /// The activity row, as the server stores it.
    ///
    /// Rates arrive already resolved -- talents and class affinity have been folded in
    /// by the caller, because working them out needs a talent tree and a stat block
    /// that the rules cannot reach. Keeping the resolution outside is what lets this
    /// run identically on the server and in the client's prediction.
    /// </summary>
    public sealed class ActivityState
    {
        public ActivityKind Kind = ActivityKind.Idle;

        public string SkillId;
        public string TargetItemId;
        public string RecipeId;
        public string MonsterId;

        /// <summary>Already through <see cref="RateMath.AdjustedSeconds"/>.</summary>
        public float SecondsPerAction = 3f;

        /// <summary>The node or station's own multiplier, from content.</summary>
        public float ActiveRateMulti = 1f;

        /// <summary>The node or station's offline multiplier, from content.</summary>
        public float AfkRateMulti = 0.6f;

        public float XpPerAction;

        /// <summary>0-1 chance of the node's special find on any one action.</summary>
        public float SpecialChance;

        /// <summary>
        /// Fraction of an action already completed, carried between settlements.
        /// See <see cref="Settlement.Gather"/> for why this exists.
        /// </summary>
        public double Progress;
    }

    /// <summary>
    /// The elapsed window, split by whether anyone was watching.
    ///
    /// The client heartbeats every twenty seconds while its tab is focused. Seconds
    /// covered by a heartbeat earn the active rate; the rest earn the offline rate.
    /// The server derives both halves from its own clock and the last heartbeat it
    /// received -- the client never states how long it played.
    /// </summary>
    public struct SettlementWindow
    {
        public double SupervisedSeconds;
        public double UnsupervisedSeconds;

        public double TotalSeconds => SupervisedSeconds + UnsupervisedSeconds;
    }

    /// <summary>Everything one settlement produced. The server turns this into rows.</summary>
    public sealed class SettlementResult
    {
        public long Actions;

        /// <summary>
        /// Actions performed while someone was watching.
        ///
        /// Tracked separately because the boss portal counts active kills only, and
        /// that decision has to be made from the same integral that granted the
        /// rewards rather than from a second, guessable code path.
        /// </summary>
        public long SupervisedActions;

        public long ItemsGranted;

        /// <summary>Extra items from the node's special find. Included in ItemsGranted.</summary>
        public long BonusItems;

        public long XpGained;

        /// <summary>Fraction of an action left over, to store back on the activity.</summary>
        public double Progress;

        /// <summary>True when the bag filled before the window ran out.</summary>
        public bool StoppedForRoom;

        /// <summary>Items that could not be stored, so the caller can say so.</summary>
        public long LostToFullInventory;
    }

    /// <summary>
    /// The one function that decides what a player earned.
    ///
    /// ══ WHY THERE IS ONLY ONE ═════════════════════════════════════════════════════
    ///
    /// This game is time-integrated, not frame-integrated: what you get for four hours
    /// of mining is a function of four hours and a rate, and the animation of a
    /// pickaxe swinging is decoration on top of it. So there is no "live" reward path
    /// and no separate "offline" one -- both were the same integral, computed twice,
    /// and for a long time they disagreed by a factor of sixty.
    ///
    /// Settlement runs on every state read, on heartbeat, on login and on logout. The
    /// client animates locally and predicts; this decides.
    ///
    /// ══ WHY PROGRESS IS CARRIED ═══════════════════════════════════════════════════
    ///
    /// Settlement runs whenever the client asks, which might be twice a second or once
    /// a day. If each call floored the elapsed time and discarded the remainder, a
    /// chatty client would earn strictly less than a quiet one, and a client settling
    /// every second would earn nothing at all forever -- no single second completes a
    /// three-second action.
    ///
    /// Carrying a FRACTION OF AN ACTION rather than leftover seconds is what makes
    /// that safe across a change of rate. A window can straddle the moment the player
    /// closed their tab; leftover seconds banked at the active rate would silently be
    /// re-valued at the offline rate. A fraction is rate-agnostic.
    /// </summary>
    public static class Settlement
    {
        /// <summary>
        /// Integrates a gathering window.
        /// </summary>
        /// <param name="state">The activity. Its Progress is read, not written.</param>
        /// <param name="window">Elapsed time, split by supervision.</param>
        /// <param name="diligence">Stat that raises the offline rate.</param>
        /// <param name="insight">Stat that raises the special-find chance.</param>
        /// <param name="roomForItems">
        /// How many more of the target item will fit. Gathering stops when the bag is
        /// full, matching what a watching player sees happen.
        /// </param>
        /// <param name="rng">Server-seeded, so the special finds are reproducible.</param>
        public static SettlementResult Gather(ActivityState state,
                                              SettlementWindow window,
                                              float diligence,
                                              float insight,
                                              long roomForItems,
                                              IRandomSource rng)
        {
            var result = new SettlementResult();
            if (state == null || state.Kind != ActivityKind.Gather) return result;

            result.Progress = Math.Max(0d, state.Progress);

            long supervised = AdvanceProgress(result, window.SupervisedSeconds,
                                              state.SecondsPerAction, state.ActiveRateMulti);

            float offlineRate = state.ActiveRateMulti
                              * RateMath.EffectiveAfkRate(state.AfkRateMulti, diligence);

            long unsupervised = AdvanceProgress(result, window.UnsupervisedSeconds,
                                                state.SecondsPerAction, offlineRate);

            result.Actions           = supervised + unsupervised;
            result.SupervisedActions = supervised;

            if (result.Actions <= 0L) return result;

            // XP is paid for every action, whether or not the item fitted. The
            // character did the work; a full bag is a storage problem, and losing the
            // experience too would punish the player twice for one mistake.
            result.XpGained = MultiplyClamped(result.Actions, (long)state.XpPerAction);

            long wanted = result.Actions;

            // ══ THE SPECIAL FIND ══════════════════════════════════════════════════
            //
            // Rolled per action for short windows and by expectation for long ones.
            // A day offline is seventeen thousand actions, and rolling each one would
            // turn a settlement into a loop the server runs for every player on every
            // read. The crossover keeps live play feeling granular -- a nest either
            // dropped or it did not -- while a night away pays its statistical due.
            if (state.SpecialChance > 0f)
            {
                double chance = Math.Min(1d, state.SpecialChance * (1d + Math.Max(0f, insight)));
                result.BonusItems = RollBonus(result.Actions, chance, rng);
                wanted = AddClamped(wanted, result.BonusItems);
            }

            long room = Math.Max(0L, roomForItems);

            if (wanted > room)
            {
                result.ItemsGranted       = room;
                result.LostToFullInventory = wanted - room;
                result.StoppedForRoom      = true;
            }
            else
            {
                result.ItemsGranted = wanted;
            }

            return result;
        }

        /// <summary>
        /// Adds one segment's worth of progress and takes out the whole actions.
        /// Writes the leftover fraction back onto the result.
        /// </summary>
        private static long AdvanceProgress(SettlementResult result,
                                            double elapsedSeconds,
                                            float secondsPerAction,
                                            float rateMultiplier)
        {
            if (elapsedSeconds <= 0d) return 0L;

            double each = RateMath.SecondsEach(secondsPerAction, rateMultiplier);
            if (each <= 0d) return 0L;

            double progress  = result.Progress + elapsedSeconds / each;
            double completed = Math.Floor(progress);

            // A corrupt interval over a long window can exceed what a long can hold.
            // Cast blindly that becomes a negative quantity in someone's inventory,
            // which is an item duplication bug wearing a rounding error's clothes.
            if (completed >= long.MaxValue)
            {
                result.Progress = 0d;
                return long.MaxValue;
            }

            result.Progress = progress - completed;
            return (long)completed;
        }

        /// <summary>
        /// Above this many actions, the special find pays its expected value instead
        /// of being rolled one action at a time.
        /// </summary>
        private const long IndividualRollLimit = 500L;

        private static long RollBonus(long actions, double chance, IRandomSource rng)
        {
            if (chance <= 0d) return 0L;

            if (actions <= IndividualRollLimit && rng != null)
            {
                long hits = 0L;
                for (long i = 0; i < actions; i++)
                    if (rng.Next01() <= chance) hits++;
                return hits;
            }

            double expected = actions * chance;

            // A little jitter so a long absence does not return a suspiciously round
            // number, but bounded, because this is the server's own arithmetic and it
            // has to stay defensible when a player adds it up.
            double jitter = rng != null ? 0.9d + rng.Next01() * 0.2d : 1d;

            double bonus = Math.Floor(expected * jitter);
            return bonus >= long.MaxValue ? long.MaxValue : (long)Math.Max(0d, bonus);
        }

        private static long AddClamped(long a, long b) =>
            a > long.MaxValue - b ? long.MaxValue : a + b;

        private static long MultiplyClamped(long count, long each)
        {
            if (count <= 0L || each <= 0L) return 0L;
            return count > long.MaxValue / each ? long.MaxValue : count * each;
        }
    }
}
