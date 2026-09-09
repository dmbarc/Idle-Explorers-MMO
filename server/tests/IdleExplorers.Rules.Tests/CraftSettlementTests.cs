using IdleExplorers.Rules;
using Xunit;

namespace IdleExplorers.Rules.Tests
{
    /// <summary>
    /// Crafting settlement.
    ///
    /// The one thing crafting has that gathering does not is an ending: the materials
    /// run out. Most of what is checked here is about that boundary being honest --
    /// the player is paid for exactly the crafts the stock allowed, told when it ran
    /// dry, and never left holding a part-finished craft they can top up hours later.
    /// </summary>
    public class CraftSettlementTests
    {
        /// <summary>The tin furnace, at the rebalanced smelt time.</summary>
        private static CraftRecipe TinBar() => new CraftRecipe
        {
            id           = "smith_tin_bar",
            name         = "Tin Bar",
            skillId      = "smithing",
            stationType  = "furnace",
            inputs       = new[] { new CraftIngredient { itemId = "tin_ore", quantity = 2 } },
            outputItemId = "tin_bar",
            outputQuantity      = 1,
            xpPerCraft          = 12f,
            baseSecondsPerCraft = 1.5f,
        };

        private static ActivityState AtTheFurnace() => new ActivityState
        {
            Kind             = ActivityKind.Craft,
            SkillId          = "smithing",
            RecipeId         = "smith_tin_bar",
            SecondsPerAction = 1.5f,
            ActiveRateMulti  = 1f,
            AfkRateMulti     = 0.5f,
        };

        private static SettlementWindow Offline(double seconds) =>
            new SettlementWindow { UnsupervisedSeconds = seconds };

        private static SettlementWindow Watched(double seconds) =>
            new SettlementWindow { SupervisedSeconds = seconds };

        private const long Plenty = long.MaxValue / 4;

        // ── The rates ─────────────────────────────────────────────────────────

        [Fact]
        public void AnHourAtTheFurnaceSmeltsTheActiveRate()
        {
            var result = Settlement.Craft(AtTheFurnace(), TinBar(), Watched(3600d),
                                          diligence: 0f, outputMultiplier: 1f,
                                          maxCraftsByInputs: Plenty, roomForOutput: Plenty);

            Assert.Equal(2400L, result.Actions);
            Assert.Equal(2400L, result.ItemsGranted);
            Assert.Equal(2400L * 12L, result.XpGained);
            Assert.False(result.RanOutOfInputs);
        }

        [Fact]
        public void AnHourAwayFromTheFurnaceSmeltsTheStationOfflineRate()
        {
            var result = Settlement.Craft(AtTheFurnace(), TinBar(), Offline(3600d),
                                          0f, 1f, Plenty, Plenty);

            Assert.Equal(1200L, result.Actions);
            Assert.Equal(0L, result.SupervisedActions);
        }

        [Fact]
        public void DiligenceRaisesOfflineCraftingToo()
        {
            var result = Settlement.Craft(AtTheFurnace(), TinBar(), Offline(3600d),
                                          diligence: 0.5f, outputMultiplier: 1f,
                                          maxCraftsByInputs: Plenty, roomForOutput: Plenty);

            Assert.Equal(1800L, result.Actions);   // 1200 * 1.5
        }

        /// <summary>
        /// Experience comes from the recipe row, not from ActivityState.XpPerAction --
        /// that field is for gathering nodes, which have no recipe to read. A craft
        /// that quietly paid the node figure would be wrong by whatever the two differ
        /// by, which is exactly the kind of drift nobody notices.
        /// </summary>
        [Fact]
        public void ExperienceComesFromTheRecipeNotTheActivityRow()
        {
            var state = AtTheFurnace();
            state.XpPerAction = 9999f;

            var result = Settlement.Craft(state, TinBar(), Offline(3600d), 0f, 1f, Plenty, Plenty);

            Assert.Equal(1200L * 12L, result.XpGained);
        }

        // ── Running dry ───────────────────────────────────────────────────────

        [Fact]
        public void RunningOutOfInputsTruncatesAndSaysSo()
        {
            var result = Settlement.Craft(AtTheFurnace(), TinBar(), Offline(3600d),
                                          diligence: 0f, outputMultiplier: 1f,
                                          maxCraftsByInputs: 300L, roomForOutput: Plenty);

            Assert.Equal(300L, result.Actions);
            Assert.True(result.RanOutOfInputs);
            Assert.Equal(0.25d, result.ProductiveFraction, 6);   // 300 of 1200 possible
            Assert.Equal(300L * 12L, result.XpGained);
        }

        /// <summary>
        /// The character downed tools when the last ore went in. Keeping a part-built
        /// bar on the activity would let a player restock nine hours later and collect
        /// something they were not present for -- small, but it is progress accruing
        /// while nothing is happening, which is the one thing settlement must not do.
        /// </summary>
        [Fact]
        public void TruncationDiscardsTheHalfFinishedCraft()
        {
            var result = Settlement.Craft(AtTheFurnace(), TinBar(), Offline(1000d),
                                          0f, 1f, maxCraftsByInputs: 5L, roomForOutput: Plenty);

            Assert.Equal(5L, result.Actions);
            Assert.Equal(0d, result.Progress);
        }

        [Fact]
        public void NoInputsAtAllCraftsNothing()
        {
            var result = Settlement.Craft(AtTheFurnace(), TinBar(), Offline(3600d),
                                          0f, 1f, maxCraftsByInputs: 0L, roomForOutput: Plenty);

            Assert.Equal(0L, result.Actions);
            Assert.Equal(0L, result.XpGained);
            Assert.True(result.RanOutOfInputs);
            Assert.Equal(0d, result.ProductiveFraction);
        }

        [Fact]
        public void SupervisedCraftsCannotExceedWhatTheStockAllowed()
        {
            var window = new SettlementWindow
            {
                SupervisedSeconds   = 3600d,   // 2400 possible
                UnsupervisedSeconds = 3600d,   // 1200 more
            };

            var result = Settlement.Craft(AtTheFurnace(), TinBar(), window,
                                          0f, 1f, maxCraftsByInputs: 100L, roomForOutput: Plenty);

            Assert.Equal(100L, result.Actions);
            Assert.Equal(100L, result.SupervisedActions);
        }

        // ── Output multipliers ────────────────────────────────────────────────

        [Fact]
        public void AnOutputProcRaisesWhatIsMadeAndNotWhatItCost()
        {
            var result = Settlement.Craft(AtTheFurnace(), TinBar(), Offline(3600d),
                                          diligence: 0f, outputMultiplier: 1.5f,
                                          maxCraftsByInputs: Plenty, roomForOutput: Plenty);

            Assert.Equal(1200L, result.Actions);        // the cost is unchanged
            Assert.Equal(1800L, result.ItemsGranted);   // the output is not
            Assert.Equal(1200L * 12L, result.XpGained);
        }

        [Fact]
        public void ARecipeMakingSeveralAtOnceMultipliesTheOutput()
        {
            var recipe = TinBar();
            recipe.outputQuantity = 8;

            var result = Settlement.Craft(AtTheFurnace(), recipe, Offline(3600d),
                                          0f, 1f, Plenty, Plenty);

            Assert.Equal(1200L, result.Actions);
            Assert.Equal(9600L, result.ItemsGranted);
        }

        /// <summary>
        /// The multiplier arrives from a resolver reading hand-authored JSON. Below one
        /// it would silently destroy output; far above it, it is a printing press.
        /// </summary>
        [Fact]
        public void TheOutputMultiplierIsClampedAtBothEnds()
        {
            var starved = Settlement.Craft(AtTheFurnace(), TinBar(), Offline(3600d),
                                           0f, outputMultiplier: 0.1f,
                                           maxCraftsByInputs: Plenty, roomForOutput: Plenty);

            var absurd = Settlement.Craft(AtTheFurnace(), TinBar(), Offline(3600d),
                                          0f, outputMultiplier: 1000f,
                                          maxCraftsByInputs: Plenty, roomForOutput: Plenty);

            Assert.Equal(1200L, starved.ItemsGranted);        // never below one each
            Assert.Equal(1200L * 10L, absurd.ItemsGranted);   // and never above ten
        }

        // ── Inventory pressure ────────────────────────────────────────────────

        [Fact]
        public void AFullBagCapsTheOutputAndStillPaysTheExperience()
        {
            var result = Settlement.Craft(AtTheFurnace(), TinBar(), Offline(3600d),
                                          0f, 1f, Plenty, roomForOutput: 100L);

            Assert.Equal(100L, result.ItemsGranted);
            Assert.Equal(1100L, result.LostToFullInventory);
            Assert.True(result.StoppedForRoom);
            Assert.Equal(1200L * 12L, result.XpGained);
        }

        // ── The invariance property ───────────────────────────────────────────

        [Fact]
        public void SettlingEverySecondCraftsTheSameAsSettlingOnce()
        {
            var once = Settlement.Craft(AtTheFurnace(), TinBar(), Offline(3600d),
                                        0f, 1f, Plenty, Plenty);

            var state = AtTheFurnace();
            long piecemeal = 0L;

            for (int second = 0; second < 3600; second++)
            {
                var step = Settlement.Craft(state, TinBar(), Offline(1d), 0f, 1f, Plenty, Plenty);
                piecemeal      += step.Actions;
                state.Progress  = step.Progress;
            }

            Assert.Equal(1200L, once.Actions);
            Assert.Equal(once.Actions, piecemeal);
        }

        // ── Degenerate input ──────────────────────────────────────────────────

        [Fact]
        public void AnEmptyWindowCraftsNothing()
        {
            var result = Settlement.Craft(AtTheFurnace(), TinBar(), Offline(0d),
                                          0f, 1f, Plenty, Plenty);

            Assert.Equal(0L, result.Actions);
            Assert.False(result.RanOutOfInputs);
            Assert.Equal(1d, result.ProductiveFraction);
        }

        [Fact]
        public void AMissingRecipeIsNotAnException()
        {
            var result = Settlement.Craft(AtTheFurnace(), null, Offline(3600d),
                                          0f, 1f, Plenty, Plenty);

            Assert.Equal(0L, result.Actions);
        }

        [Fact]
        public void AGatheringActivityDoesNotSettleAsACraft()
        {
            var state = AtTheFurnace();
            state.Kind = ActivityKind.Gather;

            var result = Settlement.Craft(state, TinBar(), Offline(3600d), 0f, 1f, Plenty, Plenty);

            Assert.Equal(0L, result.Actions);
        }

        [Fact]
        public void AnAbsurdCraftTimeSaturatesRatherThanGoingNegative()
        {
            var state = AtTheFurnace();
            state.SecondsPerAction = 0.000001f;
            state.ActiveRateMulti  = 1_000_000f;

            var result = Settlement.Craft(state, TinBar(), Offline(24 * 3600d),
                                          0f, 1f, Plenty, Plenty);

            Assert.True(result.Actions > 0L, "craft count went non-positive");
            Assert.True(result.ItemsGranted >= 0L, "granted a negative quantity");
            Assert.True(result.XpGained >= 0L, "granted negative experience");
        }
    }
}
