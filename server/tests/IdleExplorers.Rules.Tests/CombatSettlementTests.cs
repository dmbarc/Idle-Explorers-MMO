using System.Collections.Generic;
using IdleExplorers.Rules;
using Xunit;

namespace IdleExplorers.Rules.Tests
{
    /// <summary>
    /// Combat settlement.
    ///
    /// Kills are actions, integrated out of wall-clock time exactly the way ore is.
    /// Nothing here asks the client what it killed, and nothing here simulates a
    /// fight -- the only inputs are a frozen damage figure, the monster's own health,
    /// and how long the window was.
    /// </summary>
    public class CombatSettlementTests
    {
        /// <summary>The goblin, as monster_data.json declares it.</summary>
        private static MonsterData Goblin() => new MonsterData
        {
            id        = "goblin",
            name      = "Goblin",
            level     = 2,
            maxHp     = 40,
            xpReward  = 25,
            lootTable = new[]
            {
                new LootEntry { itemId = "coins",      minQty = 5, maxQty = 30, weight = 80  },
                new LootEntry { itemId = "bones",      minQty = 1, maxQty = 1,  weight = 100 },
                new LootEntry { itemId = "copper_ore", minQty = 1, maxQty = 5,  weight = 50  },
            },
        };

        private static ActivityState FightingGoblins() => new ActivityState
        {
            Kind            = ActivityKind.Combat,
            SkillId         = "combat",
            MonsterId       = "goblin",
            ActiveRateMulti = 1f,
            AfkRateMulti    = 0.6f,
        };

        /// <summary>8 damage a second against 40 health, plus a 2.5s walk: 7.5s a kill.</summary>
        private const double Dps    = 8d;
        private const float  Travel = 2.5f;

        private static SettlementWindow Offline(double seconds) =>
            new SettlementWindow { UnsupervisedSeconds = seconds };

        private static SettlementWindow Watched(double seconds) =>
            new SettlementWindow { SupervisedSeconds = seconds };

        /// <summary>Room for everything unless told otherwise.</summary>
        private sealed class Bag : IItemRoom
        {
            private readonly Dictionary<string, long> _room = new Dictionary<string, long>();

            public Bag Holding(string itemId, long room)
            {
                _room[itemId] = room;
                return this;
            }

            public long RoomFor(string itemId) =>
                _room.TryGetValue(itemId, out long room) ? room : long.MaxValue / 4;
        }

        private static SettlementResult Settle(ActivityState state, SettlementWindow window,
                                               double dps = Dps, float diligence = 0f,
                                               float dropMulti = 1f, IItemRoom room = null,
                                               long seed = 1L) =>
            Settlement.Combat(state, Goblin(), window, dps, Travel, diligence, dropMulti,
                              room ?? new Bag(), new CounterRandom(seed));

        // ── The rates ─────────────────────────────────────────────────────────

        [Fact]
        public void AnHourOfWatchedFarmingKillsAtTheModelledRate()
        {
            var result = Settle(FightingGoblins(), Watched(3600d));

            Assert.Equal(480L, result.Kills);            // 3600 / 7.5
            Assert.Equal(480L, result.Actions);
            Assert.Equal(480L * 25L, result.XpGained);
        }

        [Fact]
        public void AnHourAwayKillsAtTheOfflineRate()
        {
            var result = Settle(FightingGoblins(), Offline(3600d));

            Assert.Equal(288L, result.Kills);            // 480 * 0.6
        }

        /// <summary>
        /// The property the old level-ratio formula did not have: what you are wearing
        /// changes how fast you farm.
        /// </summary>
        [Fact]
        public void BetterGearMeansMoreKills()
        {
            var weak   = Settle(FightingGoblins(), Watched(3600d), dps: 4d);
            var strong = Settle(FightingGoblins(), Watched(3600d), dps: 40d);

            Assert.True(strong.Kills > weak.Kills * 2L,
                        $"strong killed {strong.Kills}, weak killed {weak.Kills}");
        }

        [Fact]
        public void DiligenceRaisesOfflineKillsAndLeavesWatchedPlayAlone()
        {
            var away    = Settle(FightingGoblins(), Offline(3600d), diligence: 0.5f);
            var watched = Settle(FightingGoblins(), Watched(3600d), diligence: 0.5f);

            Assert.Equal(432L, away.Kills);              // 288 * 1.5
            Assert.Equal(480L, watched.Kills);
        }

        // ── The boss gate ─────────────────────────────────────────────────────

        /// <summary>
        /// The portal opens on a thousand ACTIVE kills. That decision has to come from
        /// the same integral that paid the loot -- a second code path counting kills
        /// separately is a second thing to get wrong, and the one a cheater would aim
        /// at.
        /// </summary>
        [Fact]
        public void OnlyWatchedKillsCountTowardsTheBossGate()
        {
            var window = new SettlementWindow
            {
                SupervisedSeconds   = 3600d,
                UnsupervisedSeconds = 3600d,
            };

            var result = Settle(FightingGoblins(), window);

            Assert.Equal(768L, result.Kills);              // 480 watched + 288 away
            Assert.Equal(480L, result.SupervisedActions);
        }

        // ── Loot ──────────────────────────────────────────────────────────────

        [Fact]
        public void AGuaranteedDropLandsOnEveryKill()
        {
            var result = Settle(FightingGoblins(), Watched(3600d));

            Assert.Equal(480L, Quantity(result, "bones"));
        }

        [Fact]
        public void ChancedDropsLandNearTheirExpectation()
        {
            var result = Settle(FightingGoblins(), Watched(3600d));

            // coins: 480 kills * 80% * an average of 17.5 = 6,720
            Assert.InRange(Quantity(result, "coins"), 5_000L, 8_500L);

            // copper ore: 480 * 50% * an average of 3 = 720
            Assert.InRange(Quantity(result, "copper_ore"), 550L, 900L);
        }

        /// <summary>
        /// Past five hundred kills the table is paid by expectation rather than rolled
        /// one kill at a time. A night away is tens of thousands of kills, and looping
        /// them is a settlement the server runs for every player on every read.
        /// </summary>
        [Fact]
        public void ALongAbsencePaysTheTableByExpectation()
        {
            var result = Settle(FightingGoblins(), Offline(24 * 3600d));

            Assert.Equal(6912L, result.Kills);             // 288 an hour, for a day

            // bones drop every time, so expectation is the kill count give or take
            // the bounded jitter.
            Assert.InRange(Quantity(result, "bones"), 6_220L, 7_604L);
        }

        [Fact]
        public void DropQuantityTalentsRaiseWhatLands()
        {
            var plain   = Settle(FightingGoblins(), Watched(3600d));
            var boosted = Settle(FightingGoblins(), Watched(3600d), dropMulti: 2f);

            Assert.Equal(Quantity(plain, "bones") * 2L, Quantity(boosted, "bones"));
            Assert.Equal(plain.Kills, boosted.Kills);      // it multiplies loot, not kills
        }

        [Fact]
        public void TheDropMultiplierIsClamped()
        {
            var absurd = Settle(FightingGoblins(), Watched(3600d), dropMulti: 1e6f);

            Assert.Equal(480L * 10L, Quantity(absurd, "bones"));
        }

        [Fact]
        public void TheSameSeedRollsTheSameLoot()
        {
            var first  = Settle(FightingGoblins(), Watched(3600d), seed: 99L);
            var second = Settle(FightingGoblins(), Watched(3600d), seed: 99L);

            Assert.Equal(Quantity(first, "coins"), Quantity(second, "coins"));
            Assert.Equal(first.ItemsGranted, second.ItemsGranted);
        }

        [Fact]
        public void DifferentSeedsRollDifferentLoot()
        {
            var first  = Settle(FightingGoblins(), Watched(3600d), seed: 1L);
            var second = Settle(FightingGoblins(), Watched(3600d), seed: 2L);

            Assert.NotEqual(Quantity(first, "coins"), Quantity(second, "coins"));
        }

        // ── Inventory pressure ────────────────────────────────────────────────

        /// <summary>
        /// Capacity is per item, which is why combat asks through IItemRoom rather
        /// than taking one number: a bag full of coins must not stop the bones.
        /// </summary>
        [Fact]
        public void OneFullStackDoesNotBlockTheRest()
        {
            var bag    = new Bag().Holding("coins", 10L);
            var result = Settle(FightingGoblins(), Watched(3600d), room: bag);

            Assert.Equal(10L,  Quantity(result, "coins"));
            Assert.Equal(480L, Quantity(result, "bones"));
            Assert.True(result.StoppedForRoom);
            Assert.True(result.LostToFullInventory > 0L);
        }

        [Fact]
        public void ExperienceIsStillPaidWhenNothingFits()
        {
            var bag = new Bag().Holding("coins", 0L)
                               .Holding("bones", 0L)
                               .Holding("copper_ore", 0L);

            var result = Settle(FightingGoblins(), Watched(3600d), room: bag);

            Assert.Equal(0L, result.ItemsGranted);
            Assert.Equal(480L * 25L, result.XpGained);
        }

        // ── The invariance property ───────────────────────────────────────────

        [Fact]
        public void SettlingEverySecondKillsTheSameAsSettlingOnce()
        {
            var once = Settle(FightingGoblins(), Offline(3600d));

            var state = FightingGoblins();
            long piecemeal = 0L;

            for (int second = 0; second < 3600; second++)
            {
                var step = Settle(state, Offline(1d));
                piecemeal      += step.Kills;
                state.Progress  = step.Progress;
            }

            Assert.Equal(288L, once.Kills);
            Assert.Equal(once.Kills, piecemeal);
        }

        // ── Degenerate input ──────────────────────────────────────────────────

        [Fact]
        public void AnEmptyWindowKillsNothing()
        {
            var result = Settle(FightingGoblins(), Offline(0d));

            Assert.Equal(0L, result.Kills);
            Assert.Empty(result.Loot);
        }

        [Fact]
        public void AMissingMonsterIsNotAnException()
        {
            var result = Settlement.Combat(FightingGoblins(), null, Watched(3600d),
                                           Dps, Travel, 0f, 1f, new Bag(), new CounterRandom(1));

            Assert.Equal(0L, result.Kills);
        }

        [Fact]
        public void AGatheringActivityDoesNotSettleAsCombat()
        {
            var state = FightingGoblins();
            state.Kind = ActivityKind.Gather;

            Assert.Equal(0L, Settle(state, Watched(3600d)).Kills);
        }

        [Fact]
        public void AMonsterWithNoLootTableStillPaysExperience()
        {
            var monster = Goblin();
            monster.lootTable = null;

            var result = Settlement.Combat(FightingGoblins(), monster, Watched(3600d),
                                           Dps, Travel, 0f, 1f, new Bag(), new CounterRandom(1));

            Assert.Equal(480L, result.Kills);
            Assert.Equal(480L * 25L, result.XpGained);
            Assert.Empty(result.Loot);
        }

        [Fact]
        public void ADayAgainstAZeroHealthMonsterDoesNotMint()
        {
            var monster = Goblin();
            monster.maxHp = 0;

            var result = Settlement.Combat(FightingGoblins(), monster, Watched(24 * 3600d),
                                           dps: 1e9d, travelAndRespawnSeconds: 0f,
                                           diligence: 0f, dropQuantityMultiplier: 1f,
                                           room: new Bag(), rng: new CounterRandom(1));

            Assert.Equal(57_600L, result.Kills);        // 2,400 an hour, the travel floor
            Assert.True(result.ItemsGranted >= 0L);
        }

        private static long Quantity(SettlementResult result, string itemId)
        {
            foreach (var stack in result.Loot)
                if (stack.ItemId == itemId) return stack.Quantity;

            return 0L;
        }
    }
}
