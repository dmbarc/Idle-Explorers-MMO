using IdleExplorers.Rules;
using Xunit;

namespace IdleExplorers.Rules.Tests
{
    /// <summary>
    /// What the thing in your hands does.
    ///
    /// Most of these are about the FALLBACKS, because that is where a weapon system
    /// goes wrong: an unauthored range that reads as zero means the attack loop waits
    /// for the character to stand inside the monster, and an unauthored swing speed
    /// that reads as zero means infinite damage.
    /// </summary>
    public class WeaponProfileTests
    {
        private static ItemData Sword() => new ItemData
        {
            id = "iron_sword", name = "Iron Sword", equipSlot = "mainhand",
            weaponType = "melee", attackRange = 2.2f, attackSpeedSeconds = 1.6f,
            damageMin = 4f, damageMax = 7f,
        };

        private static ItemData Bow() => new ItemData
        {
            id = "trisong_bow", name = "Trisong Bow", equipSlot = "mainhand",
            weaponType = "ranged", attackRange = 12f, attackSpeedSeconds = 2.4f,
            damageMin = 6f, damageMax = 11f, projectilesPerShot = 3,
        };

        private static ItemData Buckler() => new ItemData
        {
            id = "tin_buckler", equipSlot = "offhand",
        };

        // ── Reach ─────────────────────────────────────────────────────────────

        [Fact]
        public void BareHandsReachAsFarAsTheyAlwaysDid()
        {
            Assert.Equal(WeaponProfile.UnarmedRange, WeaponProfile.AttackRange(null));
            Assert.Equal(2f, WeaponProfile.UnarmedRange);
        }

        [Fact]
        public void AWeaponReachesAsFarAsItSays()
        {
            Assert.Equal(2.2f, WeaponProfile.AttackRange(Sword()), 3);
            Assert.Equal(12f,  WeaponProfile.AttackRange(Bow()), 3);
        }

        /// <summary>
        /// Zero reach means "stand inside the monster", which the navmesh will never
        /// satisfy — the character walks up and then waits there forever.
        /// </summary>
        [Fact]
        public void AnUnauthoredRangeFallsBackRatherThanReachingNothing()
        {
            var sword = Sword();
            sword.attackRange = 0f;

            Assert.Equal(WeaponProfile.UnarmedRange, WeaponProfile.AttackRange(sword));
        }

        [Fact]
        public void RangeIsClampedAtBothEnds()
        {
            var reachy = Bow();
            reachy.attackRange = 5000f;
            Assert.Equal(WeaponProfile.MaxRange, WeaponProfile.AttackRange(reachy));

            var stubby = Sword();
            stubby.attackRange = 0.01f;
            Assert.Equal(WeaponProfile.UnarmedRange, WeaponProfile.AttackRange(stubby));
        }

        [Fact]
        public void ANonWeaponHeldInTheHandDoesNotChangeReach()
        {
            Assert.Equal(WeaponProfile.UnarmedRange, WeaponProfile.AttackRange(Buckler()));
        }

        // ── Speed ─────────────────────────────────────────────────────────────

        [Fact]
        public void AWeaponSwingsAtItsOwnSpeed()
        {
            Assert.Equal(1.6f, WeaponProfile.AttackSeconds(Sword(), 1.0f), 3);
        }

        [Fact]
        public void AWeaponWithNoSpeedInheritsTheCharacterFigure()
        {
            var sword = Sword();
            sword.attackSpeedSeconds = 0f;

            Assert.Equal(1.3f, WeaponProfile.AttackSeconds(sword, 1.3f), 3);
        }

        [Fact]
        public void BareHandsUseTheCharacterFigure()
        {
            Assert.Equal(1.3f, WeaponProfile.AttackSeconds(null, 1.3f), 3);
        }

        /// <summary>
        /// Attack speed divides into damage per second, which decides farm rate and
        /// whether the boss enrage timer is beaten. A 0.001-second swing is not a fast
        /// weapon, it is an infinite one.
        /// </summary>
        [Fact]
        public void SpeedIsClampedAtBothEnds()
        {
            var quick = Sword();
            quick.attackSpeedSeconds = 0.0001f;
            Assert.Equal(WeaponProfile.MinAttackSeconds, WeaponProfile.AttackSeconds(quick, 1f));

            var glacial = Sword();
            glacial.attackSpeedSeconds = 900f;
            Assert.Equal(WeaponProfile.MaxAttackSeconds, WeaponProfile.AttackSeconds(glacial, 1f));
        }

        [Fact]
        public void ANonsenseCharacterSpeedStillProducesAUsableInterval()
        {
            float seconds = WeaponProfile.AttackSeconds(null, characterAttackSeconds: 0f);

            Assert.InRange(seconds, WeaponProfile.MinAttackSeconds, WeaponProfile.MaxAttackSeconds);
        }

        // ── Damage ────────────────────────────────────────────────────────────

        [Fact]
        public void AWeaponContributesItsOwnDamageBand()
        {
            var damage = WeaponProfile.WeaponDamage(Sword());

            Assert.Equal(4f, damage.Min, 3);
            Assert.Equal(7f, damage.Max, 3);
        }

        /// <summary>
        /// A weapon is a modifier, never the whole source — so an unequipped character
        /// is weak rather than harmless.
        /// </summary>
        [Fact]
        public void BareHandsContributeNothingRatherThanBreaking()
        {
            var damage = WeaponProfile.WeaponDamage(null);

            Assert.Equal(0f, damage.Min);
            Assert.Equal(0f, damage.Max);
        }

        [Fact]
        public void AnInvertedDamageBandIsStraightened()
        {
            var wrong = Sword();
            wrong.damageMin = 20f;
            wrong.damageMax = 3f;

            var damage = WeaponProfile.WeaponDamage(wrong);

            Assert.True(damage.Max >= damage.Min, $"max {damage.Max} below min {damage.Min}");
        }

        [Fact]
        public void NegativeDamageIsFloored()
        {
            var wrong = Sword();
            wrong.damageMin = -50f;
            wrong.damageMax = -10f;

            Assert.Equal(0f, WeaponProfile.WeaponDamage(wrong).Min);
        }

        // ── Projectiles ───────────────────────────────────────────────────────

        [Fact]
        public void TheTrisongBowFiresThree()
        {
            Assert.Equal(3, WeaponProfile.ProjectilesPerShot(Bow()));
        }

        [Fact]
        public void ABowWithNoCountStillFiresOne()
        {
            var bow = Bow();
            bow.projectilesPerShot = 0;

            Assert.Equal(1, WeaponProfile.ProjectilesPerShot(bow));
        }

        [Fact]
        public void AMeleeWeaponFiresNothing()
        {
            Assert.Equal(0, WeaponProfile.ProjectilesPerShot(Sword()));
            Assert.Equal(0, WeaponProfile.ProjectilesPerShot(null));
        }

        [Fact]
        public void TheProjectileCountIsCapped()
        {
            var bow = Bow();
            bow.projectilesPerShot = 10_000;

            Assert.Equal(WeaponProfile.MaxProjectiles, WeaponProfile.ProjectilesPerShot(bow));
        }

        // ── Hands ─────────────────────────────────────────────────────────────

        [Fact]
        public void AOneHandedWeaponLeavesTheOffHandFree()
        {
            Assert.False(WeaponProfile.NeedsBothHands(Sword()));
            Assert.True(WeaponProfile.CanHoldOffHand(Sword()));
        }

        [Fact]
        public void ATwoHanderClaimsBoth()
        {
            var destroyer = Sword();
            destroyer.twoHanded = true;

            Assert.True(WeaponProfile.NeedsBothHands(destroyer));
            Assert.False(WeaponProfile.CanHoldOffHand(destroyer));
        }

        [Fact]
        public void EmptyHandsCanHoldAnOffHand()
        {
            Assert.False(WeaponProfile.NeedsBothHands(null));
            Assert.True(WeaponProfile.CanHoldOffHand(null));
        }
    }
}
