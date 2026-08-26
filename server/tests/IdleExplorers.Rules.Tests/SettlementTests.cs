using IdleExplorers.Rules;
using Xunit;

namespace IdleExplorers.Rules.Tests
{
    /// <summary>
    /// The function that decides what a player earned.
    ///
    /// Two properties matter more than any individual number here, and both are about
    /// the client being unable to influence the outcome:
    ///
    ///   * Settling often must pay exactly what settling once pays. Otherwise a
    ///     chatty client is punished, a quiet one is rewarded, and the difference is
    ///     something a player can tune.
    ///
    ///   * Rewards must follow wall-clock time and nothing else. No client timestamp,
    ///     no client-reported action count.
    /// </summary>
    public class SettlementTests
    {
        /// <summary>The goblin camp tin rock, as zone_data.json declares it.</summary>
        private static ActivityState TinRock() => new ActivityState
        {
            Kind             = ActivityKind.Gather,
            SkillId          = "mining",
            TargetItemId     = "tin_ore",
            SecondsPerAction = 3f,
            ActiveRateMulti  = 1f,
            AfkRateMulti     = 0.6f,
            XpPerAction      = 14f,
            SpecialChance    = 0f,
        };

        private static SettlementWindow Offline(double seconds) =>
            new SettlementWindow { UnsupervisedSeconds = seconds };

        private static SettlementWindow Watched(double seconds) =>
            new SettlementWindow { SupervisedSeconds = seconds };

        private const long Roomy = long.MaxValue / 4;

        // ── The measured baselines ────────────────────────────────────────────

        [Fact]
        public void ADayOfflineOnTheStarterRockPaysTheBalancedFigure()
        {
            var result = Settlement.Gather(TinRock(), Offline(24 * 3600d),
                                           diligence: 0f, insight: 0f,
                                           roomForItems: Roomy, rng: new CounterRandom(1));

            Assert.Equal(17_280L, result.Actions);
            Assert.Equal(17_280L, result.ItemsGranted);
            Assert.Equal(17_280L * 14L, result.XpGained);
        }

        [Fact]
        public void WatchedTimeEarnsTheActiveRateNotTheOfflineOne()
        {
            var watched = Settlement.Gather(TinRock(), Watched(3600d), 0f, 0f, Roomy,
                                            new CounterRandom(1));
            var offline = Settlement.Gather(TinRock(), Offline(3600d), 0f, 0f, Roomy,
                                            new CounterRandom(1));

            Assert.Equal(1200L, watched.Actions);
            Assert.Equal(720L,  offline.Actions);
        }

        [Fact]
        public void DiligenceRaisesOfflineAccrualAndLeavesActivePlayAlone()
        {
            var offline = Settlement.Gather(TinRock(), Offline(3600d), diligence: 0.5f,
                                            insight: 0f, roomForItems: Roomy,
                                            rng: new CounterRandom(1));
            var watched = Settlement.Gather(TinRock(), Watched(3600d), diligence: 0.5f,
                                            insight: 0f, roomForItems: Roomy,
                                            rng: new CounterRandom(1));

            Assert.Equal(1080L, offline.Actions);   // 720 * 1.5
            Assert.Equal(1200L, watched.Actions);
        }

        // ── Supervision is tracked, because the boss gate depends on it ───────

        [Fact]
        public void OnlyWatchedActionsCountAsSupervised()
        {
            var window = new SettlementWindow
            {
                SupervisedSeconds   = 3600d,
                UnsupervisedSeconds = 3600d,
            };

            var result = Settlement.Gather(TinRock(), window, 0f, 0f, Roomy,
                                           new CounterRandom(1));

            Assert.Equal(1920L, result.Actions);            // 1200 watched + 720 away
            Assert.Equal(1200L, result.SupervisedActions);
        }

        // ── The invariance property ───────────────────────────────────────────

        /// <summary>
        /// An hour settled once and an hour settled 3,600 times must pay identically.
        /// This is the property that stops the client influencing rewards by choosing
        /// how often to talk to the server.
        /// </summary>
        [Fact]
        public void SettlingEverySecondPaysTheSameAsSettlingOnce()
        {
            var once = Settlement.Gather(TinRock(), Offline(3600d), 0f, 0f, Roomy,
                                         new CounterRandom(1));

            var state = TinRock();
            long piecemeal = 0L;

            for (int second = 0; second < 3600; second++)
            {
                var step = Settlement.Gather(state, Offline(1d), 0f, 0f, Roomy,
                                             new CounterRandom(1));
                piecemeal      += step.Actions;
                state.Progress  = step.Progress;
            }

            Assert.Equal(720L, once.Actions);
            Assert.Equal(once.Actions, piecemeal);
        }

        /// <summary>
        /// The reason progress is carried as a fraction of an action rather than as
        /// leftover seconds. A window that straddles the moment the tab closed would
        /// otherwise bank seconds at the active rate and spend them at the offline
        /// one -- a small, permanent, invisible overpayment.
        /// </summary>
        [Fact]
        public void ProgressSurvivesAChangeOfRateWithoutBeingRevalued()
        {
            var split = new SettlementWindow
            {
                SupervisedSeconds   = 10d,   // 3.33 actions at the active rate
                UnsupervisedSeconds = 10d,   // 2.00 actions at the offline rate
            };

            var result = Settlement.Gather(TinRock(), split, 0f, 0f, Roomy,
                                           new CounterRandom(1));

            Assert.Equal(5L, result.Actions);
            Assert.Equal(3L, result.SupervisedActions);
            Assert.InRange(result.Progress, 0.32d, 0.34d);
        }

        [Fact]
        public void LeftoverProgressIsNeverLostAndNeverExceedsOneAction()
        {
            var state = TinRock();

            for (int i = 0; i < 50; i++)
            {
                var step = Settlement.Gather(state, Offline(1.7d), 0f, 0f, Roomy,
                                             new CounterRandom(1));
                Assert.InRange(step.Progress, 0d, 1d);
                state.Progress = step.Progress;
            }
        }

        // ── Inventory pressure ────────────────────────────────────────────────

        [Fact]
        public void AFullBagCapsWhatIsGrantedAndSaysSo()
        {
            var result = Settlement.Gather(TinRock(), Offline(24 * 3600d), 0f, 0f,
                                           roomForItems: 500L, rng: new CounterRandom(1));

            Assert.Equal(500L, result.ItemsGranted);
            Assert.True(result.StoppedForRoom);
            Assert.Equal(17_280L - 500L, result.LostToFullInventory);
        }

        /// <summary>
        /// The character swung the pickaxe. Losing the experience as well as the ore
        /// would punish them twice for one storage mistake.
        /// </summary>
        [Fact]
        public void ExperienceIsStillPaidWhenTheBagIsFull()
        {
            var result = Settlement.Gather(TinRock(), Offline(3600d), 0f, 0f,
                                           roomForItems: 0L, rng: new CounterRandom(1));

            Assert.Equal(0L, result.ItemsGranted);
            Assert.Equal(720L * 14L, result.XpGained);
        }

        // ── Special finds ─────────────────────────────────────────────────────

        [Fact]
        public void AShortWindowRollsTheSpecialFindPerAction()
        {
            var state = TinRock();
            state.SpecialChance = 0.5f;

            var result = Settlement.Gather(state, Offline(300d), 0f, 0f, Roomy,
                                           new CounterRandom(4242));

            Assert.Equal(60L, result.Actions);
            Assert.InRange(result.BonusItems, 15L, 45L);            // 50% of 60, loosely
            Assert.Equal(result.Actions + result.BonusItems, result.ItemsGranted);
        }

        [Fact]
        public void ALongWindowPaysTheSpecialFindByExpectation()
        {
            var state = TinRock();
            state.SpecialChance = 0.02f;                            // the bird's nest

            var result = Settlement.Gather(state, Offline(24 * 3600d), 0f, 0f, Roomy,
                                           new CounterRandom(7));

            double expected = 17_280d * 0.02d;                      // ~345
            Assert.InRange(result.BonusItems, (long)(expected * 0.85d), (long)(expected * 1.15d));
        }

        [Fact]
        public void InsightRaisesTheSpecialFindRate()
        {
            var state = TinRock();
            state.SpecialChance = 0.02f;

            var plain    = Settlement.Gather(state, Offline(24 * 3600d), 0f, 0f, Roomy,
                                             new CounterRandom(7));
            var insighted = Settlement.Gather(state, Offline(24 * 3600d), 0f, insight: 1f,
                                              roomForItems: Roomy, rng: new CounterRandom(7));

            Assert.True(insighted.BonusItems > plain.BonusItems,
                        $"insight paid {insighted.BonusItems}, plain paid {plain.BonusItems}");
        }

        [Fact]
        public void TheSameSeedSettlesToTheSameResult()
        {
            var state = TinRock();
            state.SpecialChance = 0.1f;

            var first  = Settlement.Gather(state, Offline(600d), 0f, 0f, Roomy, new CounterRandom(99));
            var second = Settlement.Gather(state, Offline(600d), 0f, 0f, Roomy, new CounterRandom(99));

            Assert.Equal(first.BonusItems, second.BonusItems);
            Assert.Equal(first.ItemsGranted, second.ItemsGranted);
        }

        // ── Degenerate input ──────────────────────────────────────────────────

        [Fact]
        public void AnEmptyWindowChangesNothing()
        {
            var result = Settlement.Gather(TinRock(), Offline(0d), 0f, 0f, Roomy,
                                           new CounterRandom(1));

            Assert.Equal(0L, result.Actions);
            Assert.Equal(0L, result.ItemsGranted);
            Assert.Equal(0L, result.XpGained);
            Assert.False(result.StoppedForRoom);
        }

        [Fact]
        public void ANegativeWindowCannotRunTheClockBackwards()
        {
            var result = Settlement.Gather(TinRock(), Offline(-9999d), 0f, 0f, Roomy,
                                           new CounterRandom(1));

            Assert.Equal(0L, result.Actions);
            Assert.Equal(0L, result.XpGained);
        }

        [Fact]
        public void AnIdleCharacterEarnsNothing()
        {
            var state = TinRock();
            state.Kind = ActivityKind.Idle;

            var result = Settlement.Gather(state, Offline(24 * 3600d), 0f, 0f, Roomy,
                                           new CounterRandom(1));

            Assert.Equal(0L, result.Actions);
        }

        [Fact]
        public void ANullActivityIsNotAnException()
        {
            var result = Settlement.Gather(null, Offline(3600d), 0f, 0f, Roomy,
                                           new CounterRandom(1));

            Assert.Equal(0L, result.Actions);
        }

        /// <summary>
        /// A corrupt interval over a long window overflows the action count. Cast
        /// blindly that lands in the inventory as a negative quantity, which is an
        /// item duplication bug wearing a rounding error's clothes.
        /// </summary>
        [Fact]
        public void AnAbsurdRateSaturatesRatherThanGoingNegative()
        {
            var state = TinRock();
            state.SecondsPerAction = 0.000001f;
            state.ActiveRateMulti  = 1_000_000f;

            var result = Settlement.Gather(state, Offline(24 * 3600d), 0f, 0f, Roomy,
                                           new CounterRandom(1));

            Assert.True(result.Actions > 0L, "action count went non-positive");
            Assert.True(result.ItemsGranted >= 0L, "granted a negative quantity");
            Assert.True(result.XpGained >= 0L, "granted negative experience");
        }
    }
}
