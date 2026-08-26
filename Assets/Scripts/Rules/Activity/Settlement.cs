using System;
using System.Collections.Generic;

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

        /// <summary>
        /// Every stack combat rolled, uncapped.
        ///
        /// Gathering and crafting each produce ONE item, so their capacity check is a
        /// single number the caller can work out up front. A loot table cannot be
        /// reduced to one number, so combat asks for capacity through IItemRoom
        /// instead and fills this list with what actually fitted.
        /// </summary>
        public readonly List<LootStack> Loot = new List<LootStack>();

        /// <summary>Crafting only: an input ran out before the window did.</summary>
        public bool RanOutOfInputs;

        /// <summary>
        /// Crafting only: how much of the window was actually productive, 0-1.
        ///
        /// A player who banked twenty bars and left for nine hours was busy for four
        /// minutes of it. Reporting the full nine would be a lie the summary screen
        /// then tells them.
        /// </summary>
        public double ProductiveFraction = 1d;

        /// <summary>Combat only, and the same number as Actions. Named for what it is.</summary>
        public long Kills => Actions;

        internal void AddLoot(string itemId, long quantity)
        {
            if (string.IsNullOrEmpty(itemId) || quantity <= 0L) return;

            for (int i = 0; i < Loot.Count; i++)
            {
                if (Loot[i].ItemId != itemId) continue;
                Loot[i] = new LootStack { ItemId = itemId, Quantity = AddClampedTo(Loot[i].Quantity, quantity) };
                return;
            }

            Loot.Add(new LootStack { ItemId = itemId, Quantity = quantity });
        }

        private static long AddClampedTo(long a, long b) => a > long.MaxValue - b ? long.MaxValue : a + b;
    }

    /// <summary>One line of a loot table, resolved.</summary>
    public struct LootStack
    {
        public string ItemId;
        public long   Quantity;
    }

    /// <summary>
    /// How much of an item will still fit.
    ///
    /// An interface rather than a dictionary so the server can answer from inventory
    /// rows and the client from its local bag, without either handing the rules a
    /// snapshot of the whole inventory. It stays pure: no clock, no engine, no
    /// randomness -- just a question the caller answers.
    /// </summary>
    public interface IItemRoom
    {
        long RoomFor(string itemId);
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
        /// Integrates a crafting window.
        ///
        /// Crafting differs from gathering in one way that changes everything: it
        /// CONSUMES inputs, so the session can end before the window does. When it
        /// does, the caller has to be able to say so -- a player who banked twenty
        /// bars and left for nine hours was busy for four minutes of it, and telling
        /// them otherwise turns the summary screen into a lie.
        ///
        /// The rules decide HOW MANY crafts happened. They do not consume anything:
        /// the caller passes in how many the stock allows and spends the materials in
        /// the same transaction that writes the output, so a crash between the two
        /// cannot charge for goods never delivered.
        /// </summary>
        /// <param name="state">Activity row. SecondsPerAction is already resolved for level and talents.</param>
        /// <param name="recipe">Authoritative for output, cost and experience.</param>
        /// <param name="window">Elapsed time, split by supervision.</param>
        /// <param name="diligence">Stat that raises the offline rate.</param>
        /// <param name="outputMultiplier">Proc and talent output bonus. 1 means none.</param>
        /// <param name="maxCraftsByInputs">What the materials on hand allow.</param>
        /// <param name="roomForOutput">How many more of the output item will fit.</param>
        public static SettlementResult Craft(ActivityState state,
                                             CraftRecipe recipe,
                                             SettlementWindow window,
                                             float diligence,
                                             float outputMultiplier,
                                             long maxCraftsByInputs,
                                             long roomForOutput)
        {
            var result = new SettlementResult();
            if (state == null || state.Kind != ActivityKind.Craft || recipe == null) return result;

            result.Progress = Math.Max(0d, state.Progress);

            long supervised = AdvanceProgress(result, window.SupervisedSeconds,
                                              state.SecondsPerAction, state.ActiveRateMulti);

            float offlineRate = state.ActiveRateMulti
                              * RateMath.EffectiveAfkRate(state.AfkRateMulti, diligence);

            long unsupervised = AdvanceProgress(result, window.UnsupervisedSeconds,
                                                state.SecondsPerAction, offlineRate);

            long possible = AddClamped(supervised, unsupervised);
            long allowed  = Math.Max(0L, maxCraftsByInputs);
            long actual   = Math.Min(possible, allowed);

            if (actual < possible)
            {
                result.RanOutOfInputs     = true;
                result.ProductiveFraction = possible > 0L ? actual / (double)possible : 0d;

                // The character downed tools the moment the last bar went in. Carrying
                // a part-finished craft past that point would let a player top the bin
                // up hours later and collect an item they had not been present for.
                result.Progress = 0d;
            }

            result.Actions           = actual;
            result.SupervisedActions = Math.Min(supervised, actual);

            if (actual <= 0L) return result;

            // Experience comes from the recipe, not from state.XpPerAction. That field
            // exists for gathering nodes, which have no recipe row to read.
            result.XpGained = MultiplyClamped(actual, (long)recipe.xpPerCraft);

            // The multiplier lands on output only -- a proc that doubles what you make
            // must not also double what it cost you to make it.
            double each     = Math.Max(1L, recipe.outputQuantity) * ClampOutputMultiplier(outputMultiplier);
            double produced = actual * each;

            long wanted = produced >= long.MaxValue ? long.MaxValue : (long)produced;
            long room   = Math.Max(0L, roomForOutput);

            if (wanted > room)
            {
                result.ItemsGranted        = room;
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
        /// Integrates a combat window.
        ///
        /// == WHY THIS IS NOT A SIMULATION =========================================
        ///
        /// Farm combat resolves statistically. There is no per-monster fight on the
        /// server and no "I killed a goblin" call from the client -- that call would
        /// make the client the author of kills, which is the entire thing being
        /// removed. Damage per second comes from a stat snapshot frozen at the start
        /// of the window, time-to-kill follows from the monster's health, and kills
        /// fall out of the same integral gathering uses. A kill is an action.
        ///
        /// The client's spawner and monster controllers keep running as THEATRE,
        /// seeded from the same value, so what is on screen and what is in the
        /// database agree without either one being in charge of the other.
        ///
        /// The boss is the one place this is not enough, and it is handled separately:
        /// per-action validation is worth paying for once, against one monster.
        /// </summary>
        /// <param name="state">Activity row. SecondsPerAction is IGNORED -- combat derives its own.</param>
        /// <param name="monster">Health, experience and loot table, from content.</param>
        /// <param name="window">Elapsed time, split by supervision.</param>
        /// <param name="dps">From the frozen stat snapshot, never from the client.</param>
        /// <param name="travelAndRespawnSeconds">Zone density. See CombatMath.</param>
        /// <param name="diligence">Stat that raises the offline rate.</param>
        /// <param name="dropQuantityMultiplier">Talent and proc bonus to quantity. 1 means none.</param>
        /// <param name="room">How much of each item will still fit.</param>
        /// <param name="rng">Server-seeded, so a disputed drop is re-derivable.</param>
        public static SettlementResult Combat(ActivityState state,
                                              MonsterData monster,
                                              SettlementWindow window,
                                              double dps,
                                              float travelAndRespawnSeconds,
                                              float diligence,
                                              float dropQuantityMultiplier,
                                              IItemRoom room,
                                              IRandomSource rng)
        {
            var result = new SettlementResult();
            if (state == null || state.Kind != ActivityKind.Combat || monster == null) return result;

            float secondsPerKill = CombatMath.SecondsPerKill(monster.maxHp, dps,
                                                             travelAndRespawnSeconds);

            result.Progress = Math.Max(0d, state.Progress);

            long supervised = AdvanceProgress(result, window.SupervisedSeconds,
                                              secondsPerKill, state.ActiveRateMulti);

            float offlineRate = state.ActiveRateMulti
                              * RateMath.EffectiveAfkRate(state.AfkRateMulti, diligence);

            long unsupervised = AdvanceProgress(result, window.UnsupervisedSeconds,
                                                secondsPerKill, offlineRate);

            result.Actions           = AddClamped(supervised, unsupervised);
            result.SupervisedActions = supervised;

            if (result.Actions <= 0L) return result;

            result.XpGained = MultiplyClamped(result.Actions, monster.xpReward);

            if (monster.lootTable == null) return result;

            double quantityMulti = ClampDropMultiplier(dropQuantityMultiplier);

            foreach (var entry in monster.lootTable)
            {
                if (entry == null || string.IsNullOrEmpty(entry.itemId)) continue;

                long rolled = RollLoot(result.Actions, entry, rng);
                if (rolled <= 0L) continue;

                double scaled = rolled * quantityMulti;
                long   wanted = scaled >= long.MaxValue ? long.MaxValue : (long)scaled;
                if (wanted <= 0L) continue;

                long space   = room != null ? Math.Max(0L, room.RoomFor(entry.itemId)) : wanted;
                long granted = Math.Min(wanted, space);

                result.AddLoot(entry.itemId, granted);
                result.ItemsGranted = AddClamped(result.ItemsGranted, granted);

                if (granted >= wanted) continue;

                result.LostToFullInventory = AddClamped(result.LostToFullInventory, wanted - granted);
                result.StoppedForRoom      = true;
            }

            return result;
        }

        /// <summary>
        /// One loot line across many kills.
        ///
        /// Rolled per kill while the count is small, and by expected value once it is
        /// not -- the same crossover the special find uses, and for the same reason: a
        /// night away is tens of thousands of kills, and looping them is a settlement
        /// the server runs for every player on every read.
        /// </summary>
        private static long RollLoot(long kills, LootEntry entry, IRandomSource rng)
        {
            double chance = entry.DropChance;
            if (kills <= 0L || chance <= 0d) return 0L;

            long min = Math.Max(0L, entry.minQty);
            long max = Math.Max(min, entry.maxQty);

            if (kills <= IndividualRollLimit && rng != null)
            {
                long total = 0L;
                for (long i = 0; i < kills; i++)
                {
                    if (rng.Next01() > chance) continue;
                    total = AddClamped(total, min == max ? min : rng.RangeInclusive(min, max));
                }
                return total;
            }

            double expected = kills * chance * ((min + max) / 2.0);
            double jitter   = rng != null ? 0.9d + rng.Next01() * 0.2d : 1d;
            double bulk     = Math.Floor(expected * jitter);

            return bulk >= long.MaxValue ? long.MaxValue : (long)Math.Max(0d, bulk);
        }

        /// <summary>
        /// Ceilings on the caller-supplied bonus multipliers.
        ///
        /// A proc cannot make you produce LESS than you crafted, and no combination of
        /// talents is meant to multiply output tenfold. Both bounds exist because an
        /// unclamped multiplier in a server-authoritative economy is a printing press,
        /// and the value arrives from a resolver reading hand-authored JSON.
        /// </summary>
        private const double MaxOutputMultiplier = 10d;

        private static double ClampOutputMultiplier(float multiplier) =>
            RulesMath.Clamp((double)multiplier, 1d, MaxOutputMultiplier);

        private static double ClampDropMultiplier(float multiplier) =>
            RulesMath.Clamp((double)multiplier, 0d, MaxOutputMultiplier);

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
