using System.Collections.Generic;

namespace IdleExplorers.Rules
{
    /// <summary>
    /// Summing a character's stats from every source.
    ///
    /// ══ WHY THE SUM IS SHARED AND THE GATHERING IS NOT ════════════════════════════
    ///
    /// The client assembles this from its managers; the server assembles it from rows.
    /// Those two cannot share code, and should not — one has an equipment manager and
    /// the other has a table.
    ///
    /// But the ORDER and the ARITHMETIC are rules, and they decide how hard a
    /// character hits. If the server sums class contributions before equipment and the
    /// client sums them after, nothing visible breaks until some stat is multiplicative
    /// — and then the two disagree about damage forever, quietly, and the client's
    /// damage numbers stop matching the monster's health bar.
    ///
    /// So both hosts gather their own inputs and hand them here.
    ///
    /// ══ WHAT A BROKEN PIECE CONTRIBUTES ═══════════════════════════════════════════
    ///
    /// Nothing, and the caller enforces that by not passing it. Durability is the
    /// payoff for maintaining gear, and armour that keeps working at zero makes the
    /// whole system decorative for exactly the players it is aimed at.
    /// </summary>
    public static class StatAssembly
    {
        /// <summary>
        /// Builds a stat block.
        /// </summary>
        /// <param name="baseStats">
        /// From base_stats.json: what every character has before being anything in
        /// particular. Copied, never mutated -- it is the shared catalogue's own
        /// object, and one caller adding to it in place would raise the floor for
        /// every character on the server.
        /// </param>
        /// <param name="classes">One contribution per specced class, added.</param>
        /// <param name="equipped">
        /// Worn, unbroken items. Their onEquipPassive statBonus effects are summed.
        /// </param>
        /// <param name="setBonuses">
        /// Flat contributions from active armour sets, already resolved by the caller
        /// -- deciding which sets are active needs the in-flight ledger and a clock.
        /// </param>
        public static StatBlock Build(StatBlock baseStats,
                                      IEnumerable<ClassData> classes = null,
                                      IEnumerable<ItemData> equipped = null,
                                      IEnumerable<KeyValuePair<string, float>> setBonuses = null)
        {
            StatBlock block = baseStats?.Clone() ?? new StatBlock();

            // Additive across classes, which is the whole point of cross-speccing: a
            // second class broadens a character rather than trading away the first.
            if (classes != null)
            {
                foreach (var classData in classes)
                    if (classData?.stats != null) block.Add(classData.stats);
            }

            if (equipped != null)
            {
                foreach (var item in equipped)
                    ContributeItem(block, item);
            }

            if (setBonuses != null)
            {
                foreach (var bonus in setBonuses)
                    block.Add(bonus.Key, bonus.Value);
            }

            return block;
        }

        /// <summary>
        /// One item's always-on stat bonuses.
        ///
        /// Only onEquipPassive/statBonus. Every other effect is a trigger that fires
        /// during play -- a proc, a heal, a summon -- and folding those into a stat
        /// block would pay them once per read forever.
        /// </summary>
        private static void ContributeItem(StatBlock block, ItemData item)
        {
            if (item?.effects == null) return;

            foreach (var effect in item.effects)
            {
                if (effect == null) continue;
                if (effect.trigger != "onEquipPassive" || effect.action != "statBonus") continue;

                block.Add(effect.param, effect.magnitude);
            }
        }

        /// <summary>
        /// Damage per second, for the statistical combat model.
        ///
        /// The midpoint of the band over the swing interval. Not a simulation and not
        /// meant to be: farm combat resolves as an integral, and the honest input to
        /// that integral is an average rate rather than a sampled roll.
        ///
        /// Crits are folded in as expected value, because over the thousands of swings
        /// a settlement window covers they ARE their expectation, and leaving them out
        /// would under-pay every character who invested in them.
        /// </summary>
        public static double DamagePerSecond(StatBlock stats, ItemData mainHand)
        {
            if (stats == null) return 0d;

            DamageProfile profile = stats.Resolve();
            DamageProfile weapon  = WeaponProfile.WeaponDamage(mainHand);

            double average = (profile.Min + weapon.Min + profile.Max + weapon.Max) * 0.5d;

            // A crit chance above one is a content error, not a guarantee of two crits.
            double critChance = RulesMath.Clamp(profile.CritChance, 0d, 1d);
            double critBonus  = System.Math.Max(0d, profile.CritMultiplier - 1d);

            average *= 1d + critChance * critBonus;

            float seconds = WeaponProfile.AttackSeconds(mainHand, stats.EffectiveAttackSpeed);

            return seconds <= 0f ? 0d : average / seconds;
        }
    }
}
