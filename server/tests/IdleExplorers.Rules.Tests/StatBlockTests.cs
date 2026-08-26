using IdleExplorers.Rules;
using Xunit;

namespace IdleExplorers.Rules.Tests
{
    /// <summary>
    /// The damage and defence model, which the server now owns and the client only
    /// predicts. Every number a player argues about comes out of these functions.
    /// </summary>
    public class StatBlockTests
    {
        private static StatBlock Fighter(float min, float max) => new StatBlock
        {
            minHit = min, maxHit = max, attackSpeed = 3f, health = 100f,
        };

        // ── Composition ───────────────────────────────────────────────────────

        [Fact]
        public void AddingBlocksSumsFieldByField()
        {
            var a = new StatBlock { health = 100f, maxHit = 5f, critChance = 0.05f };
            var b = new StatBlock { health = 25f,  maxHit = 3f, critChance = 0.10f };

            a.Add(b);

            Assert.Equal(125f, a.health);
            Assert.Equal(8f,   a.maxHit);
            Assert.Equal(0.15f, a.critChance, 5);
        }

        /// <summary>
        /// Multipliers store a BONUS, not a factor. Two sources of "no change" must
        /// sum to no change; if they stored factors, 1 + 1 would double the stat and
        /// every composition site would need to know which kind it was adding.
        /// </summary>
        [Fact]
        public void MultipliersAreBonusesSoNoChangePlusNoChangeIsNoChange()
        {
            var a = new StatBlock { health = 100f, healthMultiplier = 0f };
            a.Add(new StatBlock { healthMultiplier = 0f });

            Assert.Equal(100f, a.EffectiveHealth);

            a.Add(new StatBlock { healthMultiplier = 0.25f });
            a.Add(new StatBlock { healthMultiplier = 0.25f });

            Assert.Equal(150f, a.EffectiveHealth);
        }

        [Fact]
        public void CloneDoesNotShareSkillAffinity()
        {
            var original = new StatBlock();
            original.AddSkillAffinity("mining", 0.2f);

            var copy = original.Clone();
            copy.AddSkillAffinity("mining", 0.3f);

            Assert.Equal(0.2f, original.GetSkillAffinity("mining"), 5);
            Assert.Equal(0.5f, copy.GetSkillAffinity("mining"), 5);
        }

        [Fact]
        public void AnUnknownStatIdIsReportedRatherThanSwallowed()
        {
            string warned = null;
            var previous = RulesLog.OnWarn;
            RulesLog.OnWarn = m => warned = m;

            try
            {
                new StatBlock().Add("thisStatDoesNotExist", 5f);
            }
            finally
            {
                RulesLog.OnWarn = previous;
            }

            Assert.NotNull(warned);
            Assert.Contains("thisStatDoesNotExist", warned);
        }

        [Fact]
        public void LegacyStatNamesStillLand()
        {
            var block = new StatBlock();
            block.Add("maxHp", 40f);          // shipped in item_data.json before Stats existed
            block.Add("attackDamage", 7f);

            Assert.Equal(40f, block.health);
            Assert.Equal(7f,  block.maxHit);
        }

        // ── The min-hit cascade ───────────────────────────────────────────────

        [Fact]
        public void AnOrdinaryBandResolvesUnchanged()
        {
            var profile = Fighter(2f, 10f).Resolve();

            Assert.Equal(2f,  profile.Min);
            Assert.Equal(10f, profile.Max);
            Assert.Equal(0f,  profile.CritChance);
            Assert.Equal(1f,  profile.CritMultiplier);
        }

        /// <summary>
        /// Raising minimum damage past maximum is not wasted: the excess becomes crit
        /// chance, measured as a fraction of max hit so the conversion keeps working
        /// when the numbers grow.
        /// </summary>
        [Fact]
        public void MinimumHitOverflowBecomesCritChance()
        {
            var profile = Fighter(15f, 10f).Resolve();

            Assert.Equal(10f, profile.Min);
            Assert.Equal(10f, profile.Max);
            Assert.Equal(0.5f, profile.CritChance, 5);   // 5 overflow over 10 max
        }

        [Fact]
        public void OverflowPastCertainCritsBecomesCritDamage()
        {
            var profile = Fighter(25f, 10f).Resolve();   // 15 overflow over 10 max

            Assert.Equal(1f, profile.CritChance);
            Assert.Equal(1.5f, profile.CritMultiplier, 5);
        }

        [Fact]
        public void TheConversionIsScaleFree()
        {
            var small = Fighter(15f, 10f).Resolve();
            var large = Fighter(15_000_000f, 10_000_000f).Resolve();

            Assert.Equal(small.CritChance, large.CritChance, 4);
        }

        [Fact]
        public void MaxHitNeverFallsBelowOne()
        {
            Assert.Equal(1f, new StatBlock { maxHit = 0f }.EffectiveMaxHit);
            Assert.Equal(1f, new StatBlock { maxHit = -50f }.EffectiveMaxHit);
        }

        // ── Attack speed ──────────────────────────────────────────────────────

        [Fact]
        public void AttackSpeedBonusShortensTheInterval()
        {
            var block = new StatBlock { attackSpeed = 3f, attackSpeedMultiplier = 0.5f };
            Assert.Equal(2f, block.EffectiveAttackSpeed, 5);
        }

        /// <summary>
        /// A negative multiplier is legitimate: a Sorcerer swings slower than baseline
        /// and that is how the class expresses it. The divisor is floored so -1 cannot
        /// divide by zero, and the result is floored so a huge positive bonus cannot
        /// reach an infinite attack rate.
        /// </summary>
        [Fact]
        public void AttackSpeedSurvivesBothExtremes()
        {
            Assert.Equal(30f, new StatBlock { attackSpeed = 3f, attackSpeedMultiplier = -1f }
                                  .EffectiveAttackSpeed, 5);

            Assert.Equal(0.2f, new StatBlock { attackSpeed = 3f, attackSpeedMultiplier = 1000f }
                                   .EffectiveAttackSpeed, 5);
        }

        // ── Armour ────────────────────────────────────────────────────────────

        [Fact]
        public void ArmourHalvesDamageAtItsSoftnessConstant()
        {
            Assert.Equal(1f,   StatBlock.DamageThrough(0f), 5);
            Assert.Equal(0.5f, StatBlock.DamageThrough(120f), 5);
        }

        /// <summary>
        /// The curve must approach zero without reaching it. Flat subtraction would
        /// let armour become total immunity and make every weak hit land for exactly
        /// nothing, which is both unbalanceable and dull.
        /// </summary>
        [Fact]
        public void ArmourNeverReachesImmunity()
        {
            foreach (float armour in new[] { 1_000f, 100_000f, 10_000_000f })
                Assert.True(StatBlock.DamageThrough(armour) > 0f,
                            armour + " armour blocked everything");
        }

        [Fact]
        public void NegativeArmourDoesNotAmplifyDamage()
        {
            Assert.Equal(1f, StatBlock.DamageThrough(-500f), 5);
        }

        // ── Rolling ───────────────────────────────────────────────────────────

        [Fact]
        public void RollsStayInsideTheBandWhenNothingCrits()
        {
            var profile = Fighter(4f, 9f).Resolve();
            var rng     = new CounterRandom(31337);

            for (int i = 0; i < 5000; i++)
            {
                double hit = profile.Roll(rng, out bool crit);
                Assert.False(crit);
                Assert.InRange(hit, 4d, 9d);
            }
        }

        [Fact]
        public void ACertainCritAlwaysAppliesItsMultiplier()
        {
            var block   = new StatBlock { minHit = 5f, maxHit = 5f, critChance = 1f, critMultiplier = 1f };
            var profile = block.Resolve();
            var rng     = new CounterRandom(8);

            for (int i = 0; i < 100; i++)
            {
                double hit = profile.Roll(rng, out bool crit);
                Assert.True(crit);
                Assert.Equal(10d, hit, 5);
            }
        }

        /// <summary>
        /// The whole point of taking the source as a parameter: the same seed and
        /// index must reproduce the same hit, months later, from a log row.
        /// </summary>
        [Fact]
        public void TheSameSeedAndIndexReproduceTheSameHit()
        {
            var profile = Fighter(3f, 17f).Resolve();

            double first  = profile.Roll(new CounterRandom(555, 42), out bool critA);
            double second = profile.Roll(new CounterRandom(555, 42), out bool critB);

            Assert.Equal(first, second);
            Assert.Equal(critA, critB);
        }

        [Fact]
        public void AMissingSourceFailsClosedRatherThanThrowing()
        {
            var profile = Fighter(3f, 17f).Resolve();

            double hit = profile.Roll(null, out bool crit);

            Assert.Equal(3d, hit);
            Assert.False(crit);
        }
    }
}
