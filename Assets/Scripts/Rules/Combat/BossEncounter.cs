using System;
using System.Collections.Generic;

namespace IdleExplorers.Rules
{
    /// <summary>
    /// The one fight the server validates per action, and the reason it can.
    ///
    /// ══ THE INVARIANT THIS WHOLE FILE EXISTS TO STATE ═════════════════════════════
    ///
    ///     cumulative damage to the boss  ≤  frozen DPS × elapsed wall-clock
    ///
    /// That is the entire anti-cheat, and it holds no matter what the client sends. It
    /// does not depend on a client timestamp, a reported interval, a sequence number or
    /// an honest batch size. A client that posts ten thousand actions in one millisecond
    /// is credited with exactly what its character could have done in a millisecond,
    /// which is nothing.
    ///
    /// Everything else here -- cooldowns, sequences, ability multipliers -- shapes how
    /// the damage is spent inside that ceiling. None of it is load-bearing for safety,
    /// which is deliberate: a defence made of five checks fails when one is forgotten,
    /// and a defence made of one arithmetic bound does not.
    ///
    /// ══ WHY THE FAIL CONDITION IS THE TIMER AND NOT DEATH ═════════════════════════
    ///
    /// The server cannot verify that you dodged a cone. It does not know where you were
    /// standing, and trusting a claimed position would be theatre -- the client would
    /// simply claim to have dodged. So dodging matters for how the fight FEELS, and the
    /// win condition is a DPS check against a server clock.
    ///
    /// You cannot fake DPS: it comes from the frozen snapshot. You cannot fake the
    /// clock: it is the database's. So the fight is honest even though half of what
    /// happens on screen is unverifiable.
    ///
    /// ══ WHY THE SNAPSHOT IS FROZEN AT ENGAGE ══════════════════════════════════════
    ///
    /// Because otherwise the fight is a gear-swapping puzzle: equip the highest-DPS set
    /// for the damage check, swap to armour for the telegraphs. Freezing means what you
    /// walked in wearing is what you fight with, and it means the damage ceiling above
    /// is a single number for the whole encounter rather than something that has to be
    /// re-integrated every time a ring changes.
    /// </summary>
    public static class BossEncounter
    {
        /// <summary>
        /// Slack on the damage ceiling, in seconds of DPS.
        ///
        /// ══ WHY THERE IS ANY ══════════════════════════════════════════════════════
        ///
        /// A player who swings the instant the fight starts has done one swing's damage
        /// at elapsed ≈ 0, and a ceiling of exactly `dps × elapsed` would refuse it. The
        /// tolerance is the room for that first swing plus the round trip that reported
        /// it.
        ///
        /// It is deliberately small, and deliberately a CONSTANT rather than a fraction
        /// of the fight: as a fraction it would grow with the encounter, and a five
        /// minute enrage would end with a cheater owed thirty seconds of free damage.
        /// Two seconds is worth about one percent of a Goblin King kill.
        /// </summary>
        public const double ToleranceSeconds = 2d;

        /// <summary>
        /// Most actions one request may carry.
        ///
        /// The client posts at most twice a second, so this is a couple of seconds of
        /// the fastest plausible attack speed with room to spare. It is not a damage
        /// bound -- the ceiling above is -- it is a bound on how much work one request
        /// can ask the server to do.
        /// </summary>
        public const int MaxActionsPerRequest = 32;

        /// <summary>Shortest and longest an encounter may run, whatever content says.</summary>
        public const double MinEnrageSeconds = 30d;
        public const double MaxEnrageSeconds = 900d;

        /// <summary>
        /// How much of a swing's damage is random, either way.
        ///
        /// Enough that two identical characters do not post identical logs, small
        /// enough that it never decides a clear. The roll is drawn at the action's own
        /// index, so any number in an audit log can be re-derived from the seed months
        /// later.
        /// </summary>
        public const double SwingVariance = 0.15d;

        /// <summary>
        /// Armour value at which incoming damage is halved.
        ///
        /// A soft curve rather than flat subtraction: subtraction makes a high-armour
        /// boss immune to a low-damage character rather than slow, and "slow" is a
        /// fight while "immune" is a wall with no feedback.
        /// </summary>
        public const double ArmourHalvingPoint = 100d;

        // ── The ceiling ───────────────────────────────────────────────────────

        /// <summary>
        /// The most damage this character can possibly have done by now.
        ///
        /// The whole defence, in one expression. Called on every action request and
        /// compared against the cumulative total, never against the batch -- a batch
        /// bound would let a client wait ten minutes and then send a legitimate-looking
        /// burst every second forever.
        /// </summary>
        public static double DamageCeiling(double frozenDps, double elapsedSeconds) =>
            Math.Max(0d, frozenDps) * (Math.Max(0d, elapsedSeconds) + ToleranceSeconds);

        /// <summary>
        /// What one action is worth before the ceiling is applied.
        ///
        /// A swing is DPS times the swing interval, so a character's boss damage per
        /// second equals their farming damage per second. That equality is worth
        /// protecting: the moment the boss uses a different damage model, gear that is
        /// good for farming stops being good for the boss for reasons no player can
        /// see, and the two have to be balanced separately forever.
        /// </summary>
        public static double SwingDamage(double frozenDps, double attackSeconds,
                                         double abilityMultiplier, double armour,
                                         IRandomSource rng)
        {
            double baseline = Math.Max(0d, frozenDps) * ClampAttackSeconds(attackSeconds);
            double scaled   = baseline * ClampMultiplier(abilityMultiplier);

            double jitter = rng == null
                ? 1d
                : 1d - SwingVariance + rng.Next01() * (2d * SwingVariance);

            return Mitigate(scaled * jitter, armour);
        }

        /// <summary>
        /// Armour, as a fraction of damage that gets through.
        ///
        /// Asymptotic to zero and never reaching it, so no armour value in content can
        /// make a boss unkillable -- which a subtraction model does the moment somebody
        /// authors armour above a character's damage.
        /// </summary>
        public static double Mitigate(double damage, double armour)
        {
            if (armour <= 0d) return Math.Max(0d, damage);

            double through = ArmourHalvingPoint / (ArmourHalvingPoint + armour);

            return Math.Max(0d, damage) * through;
        }

        // ── Phases ────────────────────────────────────────────────────────────

        /// <summary>
        /// Which phase a boss is in at this health.
        ///
        /// Phases are authored as "from this health fraction downwards", first entry
        /// highest. Read by scanning rather than by index arithmetic, because content
        /// is hand-authored JSON and an unsorted or gappy list should degrade to the
        /// nearest sensible phase rather than to an exception on a live server.
        /// </summary>
        public static int PhaseFor(double healthFraction, BossPhase[] phases)
        {
            if (phases == null || phases.Length == 0) return 0;

            double health = RulesMath.Clamp(healthFraction, 0d, 1d);
            int    chosen = 0;

            for (int i = 0; i < phases.Length; i++)
            {
                if (phases[i] == null) continue;

                if (health <= phases[i].fromHealthFraction) chosen = i;
            }

            return chosen;
        }

        /// <summary>Enrage is the fail condition, so it is asked as its own question.</summary>
        public static bool Enraged(double elapsedSeconds, double enrageSeconds) =>
            elapsedSeconds >= ClampEnrage(enrageSeconds);

        public static double ClampEnrage(double enrageSeconds) =>
            RulesMath.Clamp(enrageSeconds <= 0d ? MaxEnrageSeconds : enrageSeconds,
                            MinEnrageSeconds, MaxEnrageSeconds);

        // ── The timeline ──────────────────────────────────────────────────────

        /// <summary>
        /// Every attack a phase will make, from the moment it begins.
        ///
        /// ══ WHY THE WHOLE THING UP FRONT ══════════════════════════════════════════
        ///
        /// This is what makes a telegraphed boss fight playable over the internet. The
        /// client receives the schedule at engage and draws every wind-up at exactly
        /// the right moment with ZERO network involvement -- so a 200 ms connection
        /// does not turn a 1.2 second telegraph into a 1.0 second one, and a dropped
        /// packet does not delete a mechanic.
        ///
        /// Asking the server "what is the boss doing now?" would put the network
        /// latency inside the reaction window, which is the difference between a fight
        /// and a slideshow.
        ///
        /// ══ WHY IT IS PER PHASE RATHER THAN FOR THE WHOLE FIGHT ═══════════════════
        ///
        /// Phase changes are driven by the boss's health, and its health depends on how
        /// hard the player hits -- which is not known at engage. So each phase carries
        /// its own schedule measured from the moment that phase begins, and the client
        /// starts a phase's schedule when the server confirms the phase.
        ///
        /// The alternative, a single timeline for the whole encounter, would have to
        /// assume a clear time in advance and would be wrong for every player who is
        /// not exactly average.
        ///
        /// ══ WHY IT IS DETERMINISTIC ═══════════════════════════════════════════════
        ///
        /// Generated from (seed, phase) through CounterRandom, so the server can
        /// re-derive the identical schedule from two numbers when somebody asks what
        /// happened. It also means the client cannot be told a softer timeline than the
        /// server believes in, because neither of them is choosing it.
        /// </summary>
        public static List<BossCast> Timeline(long seed, int phaseIndex, BossPhase phase,
                                              double forSeconds)
        {
            var casts = new List<BossCast>();

            if (phase?.abilities == null || phase.abilities.Length == 0) return casts;

            double window = RulesMath.Clamp(forSeconds, 0d, MaxEnrageSeconds);
            if (window <= 0d) return casts;

            double haste = phase.hasteMultiplier > 0f ? phase.hasteMultiplier : 1f;

            // One generator per phase, indexed from the phase number, so phase two's
            // schedule is not phase one's schedule shifted -- which is what a shared
            // running counter would produce and what a player would notice by the
            // third attempt.
            var rng = new CounterRandom(seed + phaseIndex * 7919L);

            // Each ability is scheduled on its own cooldown clock. Independent clocks
            // rather than one global rotation because a rotation is memorisable in
            // three attempts, and because it makes an ability's cooldown mean what it
            // says rather than "its share of a shared timer".
            var readyAt = new double[phase.abilities.Length];

            for (int i = 0; i < phase.abilities.Length; i++)
            {
                var ability = phase.abilities[i];
                if (ability == null) { readyAt[i] = double.MaxValue; continue; }

                // Staggered opening, so a phase does not begin with every ability at
                // once -- which is unsurvivable rather than difficult.
                readyAt[i] = i * OpeningStaggerSeconds + rng.Next01() * OpeningStaggerSeconds;
            }

            // A bounded loop, not `while (true)`. This runs on a request thread with a
            // hand-authored cooldown that could be zero, and a boss with a zero cooldown
            // should produce a capped timeline rather than an out-of-memory.
            for (int step = 0; step < MaxCastsPerPhase; step++)
            {
                int    next    = -1;
                double soonest = double.MaxValue;

                for (int i = 0; i < readyAt.Length; i++)
                {
                    if (readyAt[i] < soonest) { soonest = readyAt[i]; next = i; }
                }

                if (next < 0 || soonest > window) break;

                var ability = phase.abilities[next];

                casts.Add(new BossCast
                {
                    atSeconds        = (float)soonest,
                    abilityId        = ability.id ?? "",
                    telegraphSeconds = Math.Max(0f, ability.telegraphSeconds),
                    shape            = ability.shape ?? "single",
                    range            = ability.range,
                    halfWidth        = ability.halfWidth,
                    arcDegrees       = ability.arcDegrees,
                    innerRadius      = ability.innerRadius,
                    pulses           = Math.Max(1, ability.pulses),
                    secondsBetweenPulses = Math.Max(0f, ability.secondsBetweenPulses),
                });

                double cooldown = Math.Max(MinCooldownSeconds, ability.cooldownSeconds) / haste;

                // A little drift on each recast, so the rhythm cannot be played with
                // the eyes shut after the second attempt.
                readyAt[next] = soonest + cooldown * (0.9d + rng.Next01() * 0.2d);
            }

            casts.Sort((a, b) => a.atSeconds.CompareTo(b.atSeconds));

            return casts;
        }

        /// <summary>Seconds between the opening casts of a phase.</summary>
        public const double OpeningStaggerSeconds = 2.5d;

        /// <summary>Floor on an authored cooldown, so a zero cannot mean "continuously".</summary>
        public const float MinCooldownSeconds = 1f;

        /// <summary>
        /// Ceiling on how many casts one phase schedules.
        ///
        /// Fifteen minutes of the fastest legal cooldown, with room. It exists so a
        /// content typo produces a long timeline rather than an unbounded one.
        /// </summary>
        public const int MaxCastsPerPhase = 2048;

        // ── Validating what the client says it did ────────────────────────────

        /// <summary>
        /// Whether an ability was off cooldown, given when it was last used.
        ///
        /// Not a safety check -- the damage ceiling is -- but a correctness one: a
        /// client that can fire its long cooldown continuously will spend the whole
        /// ceiling on one animation, and the fight it is playing is not the fight
        /// anyone designed.
        /// </summary>
        public static bool AbilityReady(double lastUsedAtSeconds, double nowSeconds,
                                        double cooldownSeconds)
        {
            if (lastUsedAtSeconds < 0d) return true;   // never used

            return nowSeconds + ToleranceSeconds >= lastUsedAtSeconds + Math.Max(0d, cooldownSeconds);
        }

        private static double ClampAttackSeconds(double seconds) =>
            RulesMath.Clamp(seconds <= 0d ? 1d : seconds,
                            WeaponProfile.MinAttackSeconds, WeaponProfile.MaxAttackSeconds);

        /// <summary>
        /// Bounds on an ability's damage multiplier.
        ///
        /// The ceiling makes this cosmetic for safety, but an unbounded multiplier read
        /// from hand-authored JSON would still let one typo end the fight in a swing,
        /// and "the ceiling caught it" is not a reason to let content lie.
        /// </summary>
        public const double MaxAbilityMultiplier = 10d;

        private static double ClampMultiplier(double multiplier) =>
            RulesMath.Clamp(multiplier <= 0d ? 1d : multiplier, 0d, MaxAbilityMultiplier);
    }

    /// <summary>
    /// One scheduled boss attack, with everything the client needs to draw it.
    ///
    /// Carries the SHAPE rather than an ability id alone, so a client that has not
    /// downloaded fresh content still telegraphs the right thing. A boss attack the
    /// player cannot see coming is unavoidable damage, and "your content was stale" is
    /// not a mechanic.
    /// </summary>
    [Serializable]
    public class BossCast
    {
        /// <summary>Seconds from the start of the phase. The telegraph begins earlier.</summary>
        public float  atSeconds;

        public string abilityId;
        public string shape;

        public float  telegraphSeconds;
        public float  range;
        public float  halfWidth;
        public float  arcDegrees;
        public float  innerRadius;

        public int    pulses;
        public float  secondsBetweenPulses;
    }
}
