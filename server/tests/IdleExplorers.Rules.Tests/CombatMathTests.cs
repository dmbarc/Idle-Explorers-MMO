using IdleExplorers.Rules;
using Xunit;

namespace IdleExplorers.Rules.Tests
{
    /// <summary>
    /// The kill-rate model that replaces <c>characterLevel / monsterLevel * 30</c>.
    ///
    /// The old formula ignored health, damage, weapons and gear, so a naked level 20
    /// and a fully-equipped one farmed at identical speed. What is checked here is
    /// mostly that the replacement cannot be pushed to infinity, because its output
    /// divides into a time window and an unbounded rate is an item printer.
    /// </summary>
    public class CombatMathTests
    {
        private const double GoblinHp = 40d;

        [Fact]
        public void TimeToKillIsHealthOverDamage()
        {
            Assert.Equal(5d, CombatMath.TimeToKill(GoblinHp, dps: 8d), 6);
            Assert.Equal(4d, CombatMath.TimeToKill(GoblinHp, dps: 10d), 6);
        }

        [Fact]
        public void AKillCostsTheFightPlusTheWalkToIt()
        {
            Assert.Equal(7.5f, CombatMath.SecondsPerKill(GoblinHp, 8d, 2.5f), 4);
        }

        [Fact]
        public void StrongerCharactersKillFaster()
        {
            float weak   = CombatMath.KillsPerHour(GoblinHp, dps: 4d,  travelAndRespawnSeconds: 2.5f);
            float strong = CombatMath.KillsPerHour(GoblinHp, dps: 40d, travelAndRespawnSeconds: 2.5f);

            Assert.True(strong > weak * 2f, $"strong {strong}/h vs weak {weak}/h");
        }

        // ── The clamps, each of which is a division waiting to happen ─────────

        [Fact]
        public void ACharacterWhoDealsNoDamageDoesNotDivideByZero()
        {
            float perKill = CombatMath.SecondsPerKill(GoblinHp, dps: 0d, travelAndRespawnSeconds: 2.5f);

            Assert.True(perKill > 0f);
            Assert.False(float.IsInfinity(perKill), "zero damage produced an infinite fight");
            Assert.Equal(GoblinHp / CombatMath.MinDps + 2.5d, perKill, 2);
        }

        [Fact]
        public void ANegativeDpsIsTreatedAsTheWeakestPossible()
        {
            Assert.Equal(CombatMath.SecondsPerKill(GoblinHp, 0d, 2.5f),
                         CombatMath.SecondsPerKill(GoblinHp, -500d, 2.5f), 4);
        }

        /// <summary>
        /// A monster authored with no health is a typo, and an infinite KILL RATE --
        /// which mints loot rather than merely being wrong.
        /// </summary>
        [Fact]
        public void AMonsterWithNoHealthDoesNotMint()
        {
            float perHour = CombatMath.KillsPerHour(monsterMaxHp: 0d, dps: 1000d,
                                                    travelAndRespawnSeconds: 2.5f);

            // The literal 1,440 rather than MaxKillsPerHour: a bound expressed in
            // terms of the constant being tested moves whenever the bug does, and
            // stops being a test.
            Assert.InRange(perHour, 1_400f, 1_450f);

            Assert.Equal(CombatMath.KillsPerHour(1d, 1000d, 2.5f), perHour, 3);
        }

        [Fact]
        public void TheTravelFloorIsWhatCapsAnOverlevelledCharacter()
        {
            float perHour = CombatMath.KillsPerHour(GoblinHp, dps: 1e9d,
                                                    travelAndRespawnSeconds: 0f);

            Assert.Equal(CombatMath.MaxKillsPerHour, perHour, 1);
            Assert.Equal(2400f, CombatMath.MaxKillsPerHour, 1);
        }

        [Fact]
        public void ADenserZoneStillPaysTheFloor()
        {
            Assert.Equal(CombatMath.SecondsPerKill(GoblinHp, 8d, 0f),
                         CombatMath.SecondsPerKill(GoblinHp, 8d, CombatMath.MinTravelAndRespawnSeconds), 4);
        }

        [Fact]
        public void ASparserZoneSlowsFarmingDown()
        {
            float dense  = CombatMath.KillsPerHour(GoblinHp, 8d, 2.5f);
            float sparse = CombatMath.KillsPerHour(GoblinHp, 8d, 20f);

            Assert.True(sparse < dense, $"sparse {sparse}/h was not slower than dense {dense}/h");
        }
    }
}
