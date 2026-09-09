using System;

namespace IdleExplorers.Rules
{
    /// <summary>
    /// What the thing in your hands does.
    ///
    /// ══ WHY THIS EXISTS AT ALL ════════════════════════════════════════════════════
    ///
    /// There was no weapon system. `attackDistance` was a hard-coded 2f on
    /// PlayerController, no item declared a range, damage came entirely from the stat
    /// block, and `magic_staff` and `iron_sword` had no equipSlot -- so the two things
    /// in the game most obviously shaped like weapons could not be equipped.
    ///
    /// ══ WHY IT IS A RESOLVER RATHER THAN FIELDS ON THE ITEM ═══════════════════════
    ///
    /// Every question here has a fallback, and the fallbacks are where the bugs live:
    /// a bow with no authored range should not have reach zero, a sword with no
    /// authored speed should swing at the character's speed rather than instantly, and
    /// an empty hand should still be able to punch. Answering each of those in the
    /// attack loop means answering them again in the server's validation, differently.
    ///
    /// So the questions are asked here, once, and both hosts ask them the same way.
    ///
    /// ══ EVERY CEILING IS DELIBERATE ═══════════════════════════════════════════════
    ///
    /// Range and attack speed both divide into how much damage a character does per
    /// second, and that figure decides how fast they farm and whether they beat the
    /// boss enrage timer. A weapon authored with a 0.001-second swing is not a fast
    /// weapon, it is an infinite one.
    /// </summary>
    public static class WeaponProfile
    {
        /// <summary>Reach with nothing equipped. The old hard-coded attackDistance.</summary>
        public const float UnarmedRange = 2f;

        /// <summary>Nothing reaches further than this, whatever the item claims.</summary>
        public const float MaxRange = 30f;

        /// <summary>
        /// Fastest any weapon may swing.
        ///
        /// A tenth of a second is already five times faster than the quickest thing
        /// the game is balanced around. Below it, the number stops describing an
        /// animation anyone can see and starts being a damage multiplier.
        /// </summary>
        public const float MinAttackSeconds = 0.1f;

        /// <summary>Slowest, so a typo cannot leave a character unable to attack.</summary>
        public const float MaxAttackSeconds = 10f;

        /// <summary>Most projectiles one shot may fan out. The Trisong Bow is three.</summary>
        public const int MaxProjectiles = 12;

        /// <summary>
        /// How close the character must get before swinging.
        ///
        /// A melee weapon with no authored range gets the unarmed reach rather than
        /// zero, because zero means "occupy the same point as the monster", which the
        /// navmesh will never satisfy and the attack loop would wait for forever.
        /// </summary>
        public static float AttackRange(ItemData mainHand)
        {
            if (mainHand == null || !mainHand.IsWeapon) return UnarmedRange;

            float authored = mainHand.attackRange;
            if (authored <= 0f) return UnarmedRange;

            return RulesMath.Clamp(authored, UnarmedRange, MaxRange);
        }

        /// <summary>
        /// Seconds between attacks, from the weapon or the character.
        /// </summary>
        /// <param name="mainHand">The equipped weapon, or null for bare hands.</param>
        /// <param name="characterAttackSeconds">
        /// The character's own swing interval, already resolved from stats. Used when
        /// the weapon does not declare one -- an unauthored speed should inherit,
        /// not zero out.
        /// </param>
        public static float AttackSeconds(ItemData mainHand, float characterAttackSeconds)
        {
            float fallback = Clamped(characterAttackSeconds > 0f ? characterAttackSeconds : 1f);

            if (mainHand == null || !mainHand.IsWeapon) return fallback;
            if (mainHand.attackSpeedSeconds <= 0f)      return fallback;

            return Clamped(mainHand.attackSpeedSeconds);
        }

        /// <summary>
        /// Damage the weapon itself contributes, on top of the character's stats.
        ///
        /// Zero for an unauthored weapon, and that is correct: an item that declares
        /// no damage adds none, and the character still hits for their stat block.
        /// A weapon is a modifier here, never the whole source -- so an unequipped
        /// character is weak rather than harmless.
        /// </summary>
        public static DamageProfile WeaponDamage(ItemData mainHand)
        {
            if (mainHand == null || !mainHand.IsWeapon) return new DamageProfile();

            float min = Math.Max(0f, mainHand.damageMin);
            float max = Math.Max(min, mainHand.damageMax);

            return new DamageProfile { Min = min, Max = max };
        }

        /// <summary>Projectiles a single shot launches. Always at least one for a bow.</summary>
        public static int ProjectilesPerShot(ItemData mainHand)
        {
            if (mainHand == null || !mainHand.IsRanged) return 0;

            int authored = mainHand.projectilesPerShot;
            if (authored <= 0) return 1;

            return (int)RulesMath.Clamp(authored, 1L, MaxProjectiles);
        }

        /// <summary>
        /// Whether equipping this main hand forces the off hand off.
        ///
        /// Asked here rather than read off the item so the answer survives a null and
        /// so a two-handed thing that is not a weapon -- a bundle of logs, a banner --
        /// behaves the same way.
        /// </summary>
        public static bool NeedsBothHands(ItemData mainHand) => mainHand is { twoHanded: true };

        /// <summary>
        /// Whether an off-hand item may be worn beside this main hand.
        ///
        /// The one rule the equipment code has to enforce in two directions: putting
        /// on a two-hander takes the off hand off, and putting something in the off
        /// hand while holding a two-hander must be refused. Both call this.
        /// </summary>
        public static bool CanHoldOffHand(ItemData mainHand) => !NeedsBothHands(mainHand);

        private static float Clamped(float seconds) =>
            RulesMath.Clamp(seconds, MinAttackSeconds, MaxAttackSeconds);
    }
}
