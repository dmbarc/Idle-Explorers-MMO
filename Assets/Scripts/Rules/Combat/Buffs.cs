using System;
using System.Collections.Generic;

namespace IdleExplorers.Rules
{
    /// <summary>
    /// Something a potion did to you, and for how long.
    ///
    /// ══ WHY A BUFF IS SERVER STATE ════════════════════════════════════════════════
    ///
    /// Because it multiplies damage, and damage decides the farm rate and whether the
    /// enrage timer is beaten. A buff the client asserted would be a client asserting
    /// its own DPS, which is the whole thing this architecture removes.
    ///
    /// So drinking is an endpoint, the effect is a row, and the row is read by the one
    /// function that assembles a character's stats. The client renders a timer.
    ///
    /// ══ WHY A POTION IS A PRE-FIGHT DECISION ══════════════════════════════════════
    ///
    /// The boss FREEZES a stat snapshot at engage, so what you drank before the fight
    /// is in the numbers and what you drink during it is not. That is deliberate on
    /// both counts: the freeze is what stops gear-swapping mid-fight, and it cannot
    /// tell a potion from a sword.
    ///
    /// It also makes the potions interesting rather than a tax -- you decide before
    /// the door, not on a cooldown during. The item text says so.
    ///
    /// ══ WHY ONE PER STAT, REFRESHED RATHER THAN STACKED ═══════════════════════════
    ///
    /// A stacking buff is a currency: fifty potions is fifty times the damage, and the
    /// only limit is how much gold somebody has. Refreshing means a potion is worth
    /// exactly what it says on it, however many are in the bag.
    /// </summary>
    public static class Buffs
    {
        /// <summary>
        /// The stats a potion may touch, and the ONLY ones.
        ///
        /// These three names are the talent vocabulary, deliberately: talents already
        /// apply through them and StatBlock already carries the multiplier fields with
        /// their own floors. A fourth name here would be a stat with no code behind
        /// it -- a potion that says +30% and does nothing.
        /// </summary>
        public const string AttackDamagePercent = "attackDamagePercent";
        public const string AttackSpeedPercent  = "attackSpeedPercent";
        public const string MaxHpPercent        = "maxHpPercent";

        /// <summary>Whether a stat id is one a buff is allowed to move.</summary>
        public static bool IsBuffable(string statId) =>
            statId == AttackDamagePercent ||
            statId == AttackSpeedPercent  ||
            statId == MaxHpPercent;

        /// <summary>
        /// The most any single buff may add, as a fraction.
        ///
        /// A ceiling rather than a trust: content is data, and a potion authored at
        /// 4.0 by a slipped decimal point would be a five-times damage multiplier for
        /// the price of a potion.
        /// </summary>
        public const float MaxMagnitude = 1.0f;

        /// <summary>The longest a buff may last. Half an hour is already generous.</summary>
        public const double MaxDurationSeconds = 1800d;

        /// <summary>Clamps an authored magnitude into what the game will honour.</summary>
        public static float Clamp(float magnitude) =>
            magnitude <= 0f ? 0f : (magnitude > MaxMagnitude ? MaxMagnitude : magnitude);

        /// <summary>Clamps an authored duration the same way.</summary>
        public static double ClampSeconds(double seconds) =>
            seconds <= 0d ? 0d : (seconds > MaxDurationSeconds ? MaxDurationSeconds : seconds);

        /// <summary>One buff in force.</summary>
        [Serializable]
        public class Active
        {
            /// <summary>One of the three names above.</summary>
            public string statId;

            /// <summary>Fraction added, already clamped.</summary>
            public float  magnitude;

            /// <summary>Seconds left when this was read. Counted down for display only.</summary>
            public double secondsRemaining;

            /// <summary>What to call it on screen.</summary>
            public string label;
        }

        /// <summary>
        /// What a potion does, read off its onConsume effect.
        ///
        /// Returns null for anything that is not a buff potion, which is almost every
        /// consumable -- food heals, gems grant time, and neither goes near this.
        /// </summary>
        public static Active Read(ItemData item)
        {
            if (item?.effects == null) return null;

            foreach (ItemEffect effect in item.effects)
            {
                if (effect == null) continue;
                if (effect.trigger != "onConsume" || effect.action != "buff") continue;
                if (!IsBuffable(effect.param)) continue;

                return new Active
                {
                    statId           = effect.param,
                    magnitude        = Clamp(effect.magnitude),
                    secondsRemaining = ClampSeconds(effect.durationSeconds),
                    label            = item.DisplayName,
                };
            }

            return null;
        }

        /// <summary>
        /// Folds a set of live buffs into an assembled stat block.
        ///
        /// ══ WHY IT MUTATES MULTIPLIERS RATHER THAN VALUES ═════════════════════
        ///
        /// The same reason talents do, and the comment there is the one that matters:
        /// StatBlock.Resolve and EffectiveAttackSpeed read the multiplier fields and
        /// apply their own floors, so scaling the raw numbers would bypass those and
        /// double-count against gear that also multiplies.
        ///
        /// attackSpeedPercent is a REDUCTION in the interval between swings, so a
        /// POSITIVE addition here is a FASTER swing. That reads backwards at a glance,
        /// which is why the sign is decided in one place for talents and here.
        /// </summary>
        public static void Apply(StatBlock block, IEnumerable<Active> active)
        {
            if (block == null || active == null) return;

            foreach (Active buff in active)
            {
                if (buff == null || buff.magnitude <= 0f) continue;

                float amount = Clamp(buff.magnitude);

                switch (buff.statId)
                {
                    case AttackDamagePercent:
                        block.minHitMultiplier += amount;
                        block.maxHitMultiplier += amount;
                        break;

                    case AttackSpeedPercent:
                        block.attackSpeedMultiplier += amount;
                        break;

                    case MaxHpPercent:
                        block.healthMultiplier += amount;
                        break;
                }
            }
        }
    }
}
