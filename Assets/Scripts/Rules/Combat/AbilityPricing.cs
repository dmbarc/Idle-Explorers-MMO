using System;

namespace IdleExplorers.Rules
{
    /// <summary>
    /// What pressing an ability is worth, in swings.
    ///
    /// ══ WHY THIS IS SHARED AND NOT A SWITCH IN THE ENDPOINT ═══════════════════════
    ///
    /// Because the client predicts the damage and the server decides it, and the first
    /// version of both had its own answer. Worse, both had the WRONG answer in the same
    /// interesting way -- each looked a player's ability up in the boss's attack list,
    /// found nothing, and quietly did something reasonable.
    ///
    /// One function, compiled into both, is the only arrangement where "the health bar
    /// jumped when the server replied" cannot happen for this reason.
    ///
    /// ══ WHY AN UNKNOWN EFFECT IS ZERO AND ALSO A CONTENT ERROR ════════════════════
    ///
    /// Zero, so a new effect type cannot become a damage ability by being unrecognised
    /// -- that is the direction that mints damage, and it would do it silently.
    ///
    /// But zero alone is the wrong failure: it makes an authoring mistake look like a
    /// balance problem, and nobody investigates a weak ability for six weeks. So
    /// GameContent.Validate refuses to load an ability whose effect is not listed here,
    /// which turns "this ability does nothing" into "the server would not start".
    ///
    /// Adding an effect means adding it in both places, deliberately.
    /// </summary>
    public static class AbilityPricing
    {
        /// <summary>
        /// Every effect the game authors, and whether it deals direct damage.
        ///
        /// `summon` is deliberately NOT damage. The ally it spawns hits things, but the
        /// summon itself does not -- pricing the press as damage would pay for the ally
        /// twice, once on cast and again on every swing it makes.
        ///
        /// `slow` and `stealth` likewise change a fight without being one.
        /// </summary>
        public static bool IsKnownEffect(string effect) => effect switch
        {
            "damage" or "aoe" or "bladestorm" => true,
            "heal" or "haste" or "blink" or "slow" or "stealth" or "summon" or "passive" => true,
            "" => true,   // an ability with no effect is inert, and that is authorable
            _  => false,
        };

        /// <summary>
        /// The multiplier on one swing's damage. Zero for anything that is not damage.
        /// </summary>
        public static double DamageMultiplier(AbilityData ability)
        {
            if (ability == null || ability.isPassive) return 0d;

            double power = Math.Max(0d, ability.power);

            return ability.effect switch
            {
                // hits is how many times it strikes -- Rapid Shot fires three, and each
                // is worth the ability's power.
                "damage"     => power * Math.Max(1, ability.hits),

                "aoe"        => power,

                // The sword pulses while it is away. Priced as the whole flight rather
                // than per pulse, because the action is the throw: a client reporting
                // one press must not be credited eight times for it.
                "bladestorm" => power * BladestormPulses,

                _            => 0d,
            };
        }

        /// <summary>
        /// Pulses a bladestorm lands, for pricing.
        ///
        /// Eight: four seconds at one every half second, which is what the client
        /// animates. Stated here rather than read from the ability so the price cannot
        /// drift from the animation by an author editing one and not the other.
        /// </summary>
        public const int BladestormPulses = 8;

        /// <summary>
        /// Whether an ability is something a player can press at all.
        ///
        /// A passive reported as an action is either a client bug or somebody looking
        /// for a free swing on a zero cooldown.
        /// </summary>
        public static bool IsPressable(AbilityData ability) =>
            ability != null && !ability.isPassive && ability.effect != "passive";
    }
}
