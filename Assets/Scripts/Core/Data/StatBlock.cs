using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Every number that describes a character, in one composable bundle.
///
/// A character's live stats are the sum of a shared baseline, one contribution per
/// specced class, their gear, their set bonuses and their talents. Summing is the
/// whole design: it is what lets a second class be purely additive, so cross-speccing
/// broadens a character instead of trading one strength for another.
///
/// ══ MULTIPLIERS ARE BONUS FRACTIONS ═══════════════════════════════════════════
///
/// Every field named `...Multiplier` stores a BONUS, not a factor: 0 means no change
/// and 0.25 means +25%. The effective factor is `1 + value`.
///
/// That is not a stylistic choice. If multipliers stored factors, adding two sources
/// of "no change" would give 2.0 and double the stat, and every composition site would
/// need to know whether it was adding or multiplying. Storing bonuses makes `Add` a
/// plain field-by-field sum with no special cases, and it matches what TalentManager
/// already does (`Multiplier => 1f + Bonus(...)`).
/// </summary>
[Serializable]
public class StatBlock
{
    // ── Vitals ────────────────────────────────────────────────────────────────

    public float health;
    public float mana;
    public float stamina;

    /// <summary>Bonus fraction on total health. Talents like Bulwark land here.</summary>
    public float healthMultiplier;

    /// <summary>Points restored per second, out of combat and in.</summary>
    public float healthRegen;
    public float manaRegen;
    public float staminaRegen;

    // ── Offence ───────────────────────────────────────────────────────────────

    public float minHit;
    public float maxHit;
    public float minHitMultiplier;
    public float maxHitMultiplier;

    /// <summary>0-1. A roll under this crits.</summary>
    public float critChance;

    /// <summary>Damage factor on a crit, as a bonus: 0.5 means a crit hits for 1.5x.</summary>
    public float critMultiplier;

    /// <summary>Seconds between swings. LOWER is faster, so this is an interval.</summary>
    public float attackSpeed;

    /// <summary>Bonus fraction that SHORTENS the interval.</summary>
    public float attackSpeedMultiplier;

    // ── Defence ───────────────────────────────────────────────────────────────

    public float armor;
    public float armorMultiplier;

    // ── Yield ─────────────────────────────────────────────────────────────────

    public float dropRate;
    public float dropRateMultiplier;

    // ── The five that are particular to this game ─────────────────────────────

    /// <summary>Builds while one activity runs uninterrupted; resets when you switch.</summary>
    public float momentum;

    /// <summary>Offline accrual rate.</summary>
    public float diligence;

    /// <summary>Chance of a node's special find, and of the rarer loot rolls.</summary>
    public float insight;

    /// <summary>Potency of armour set bonuses.</summary>
    public float resonance;

    /// <summary>Reduces how fast equipment wears out.</summary>
    public float tenacity;

    /// <summary>
    /// How fast the character walks, in world units per second.
    ///
    /// A flat value rather than a bonus fraction, because it is the one stat here the
    /// NavMeshAgent reads directly and an agent speed of 0.15 would be a character who
    /// cannot leave the spawn point. base_stats.json supplies the floor; classes and
    /// gear add to it.
    /// </summary>
    public float moveSpeed;

    // ── Per-skill ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Bonus fraction per skill, same convention as the multipliers above. A List
    /// rather than a Dictionary because JsonUtility silently serializes dictionaries
    /// as empty — the trap that once ate every character's skill progress.
    /// </summary>
    public List<SkillAffinity> skillAffinity = new();

    // ── Composition ───────────────────────────────────────────────────────────

    /// <summary>Adds another block into this one, field by field.</summary>
    public void Add(StatBlock other)
    {
        if (other == null) return;

        health           += other.health;
        healthMultiplier += other.healthMultiplier;
        mana         += other.mana;
        stamina      += other.stamina;
        healthRegen  += other.healthRegen;
        manaRegen    += other.manaRegen;
        staminaRegen += other.staminaRegen;

        minHit                += other.minHit;
        maxHit                += other.maxHit;
        minHitMultiplier      += other.minHitMultiplier;
        maxHitMultiplier      += other.maxHitMultiplier;
        critChance            += other.critChance;
        critMultiplier        += other.critMultiplier;
        attackSpeed           += other.attackSpeed;
        attackSpeedMultiplier += other.attackSpeedMultiplier;

        armor              += other.armor;
        armorMultiplier    += other.armorMultiplier;
        dropRate           += other.dropRate;
        dropRateMultiplier += other.dropRateMultiplier;

        momentum  += other.momentum;
        diligence += other.diligence;
        insight   += other.insight;
        resonance += other.resonance;
        tenacity  += other.tenacity;
        moveSpeed += other.moveSpeed;

        if (other.skillAffinity == null) return;
        foreach (var entry in other.skillAffinity)
            if (entry != null) AddSkillAffinity(entry.skillId, entry.value);
    }

    public StatBlock Clone()
    {
        var copy = (StatBlock)MemberwiseClone();
        copy.skillAffinity = new List<SkillAffinity>();
        if (skillAffinity != null)
            foreach (var e in skillAffinity)
                if (e != null) copy.skillAffinity.Add(new SkillAffinity { skillId = e.skillId, value = e.value });
        return copy;
    }

    // ── Named access ──────────────────────────────────────────────────────────

    /// <summary>
    /// Adds to a stat by id. The seam gear, talents and set bonuses all come through,
    /// since those name their target as a string in JSON.
    ///
    /// An unknown id is reported rather than dropped: an item promising a bonus that
    /// nothing applies is the exact failure this project keeps rediscovering.
    /// </summary>
    public void Add(string statId, float value)
    {
        if (string.IsNullOrEmpty(statId) || Mathf.Approximately(value, 0f)) return;

        switch (Stats.Canonical(statId))
        {
            case Stats.Health:           health           += value; return;
            case Stats.HealthMultiplier: healthMultiplier += value; return;
            case Stats.Mana:         mana         += value; return;
            case Stats.Stamina:      stamina      += value; return;
            case Stats.HealthRegen:  healthRegen  += value; return;
            case Stats.ManaRegen:    manaRegen    += value; return;
            case Stats.StaminaRegen: staminaRegen += value; return;

            case Stats.MinHit:                minHit                += value; return;
            case Stats.MaxHit:                maxHit                += value; return;
            case Stats.MinHitMultiplier:      minHitMultiplier      += value; return;
            case Stats.MaxHitMultiplier:      maxHitMultiplier      += value; return;
            case Stats.CritChance:            critChance            += value; return;
            case Stats.CritMultiplier:        critMultiplier        += value; return;
            case Stats.AttackSpeed:           attackSpeed           += value; return;
            case Stats.AttackSpeedMultiplier: attackSpeedMultiplier += value; return;

            case Stats.Armor:              armor              += value; return;
            case Stats.ArmorMultiplier:    armorMultiplier    += value; return;
            case Stats.DropRate:           dropRate           += value; return;
            case Stats.DropRateMultiplier: dropRateMultiplier += value; return;

            case Stats.Momentum:  momentum  += value; return;
            case Stats.Diligence: diligence += value; return;
            case Stats.Insight:   insight   += value; return;
            case Stats.Resonance: resonance += value; return;
            case Stats.Tenacity:  tenacity  += value; return;
            case Stats.MoveSpeed: moveSpeed += value; return;
        }

        Debug.LogWarning($"[StatBlock] Nothing applies a stat called '{statId}'. " +
                         "Check the spelling against Stats.All, or the bonus does nothing.");
    }

    /// <summary>Reads a stat by id. Returns 0 for anything unrecognised.</summary>
    public float Get(string statId) => Stats.Canonical(statId) switch
    {
        Stats.Health           => health,
        Stats.HealthMultiplier => healthMultiplier,
        Stats.Mana         => mana,
        Stats.Stamina      => stamina,
        Stats.HealthRegen  => healthRegen,
        Stats.ManaRegen    => manaRegen,
        Stats.StaminaRegen => staminaRegen,

        Stats.MinHit                => minHit,
        Stats.MaxHit                => maxHit,
        Stats.MinHitMultiplier      => minHitMultiplier,
        Stats.MaxHitMultiplier      => maxHitMultiplier,
        Stats.CritChance            => critChance,
        Stats.CritMultiplier        => critMultiplier,
        Stats.AttackSpeed           => attackSpeed,
        Stats.AttackSpeedMultiplier => attackSpeedMultiplier,

        Stats.Armor              => armor,
        Stats.ArmorMultiplier    => armorMultiplier,
        Stats.DropRate           => dropRate,
        Stats.DropRateMultiplier => dropRateMultiplier,

        Stats.Momentum  => momentum,
        Stats.Diligence => diligence,
        Stats.Insight   => insight,
        Stats.Resonance => resonance,
        Stats.Tenacity  => tenacity,
        Stats.MoveSpeed => moveSpeed,

        _ => 0f,
    };

    // ── Skill affinity ────────────────────────────────────────────────────────

    public float GetSkillAffinity(string skillId)
    {
        if (skillAffinity == null || string.IsNullOrEmpty(skillId)) return 0f;

        foreach (var entry in skillAffinity)
            if (entry != null && entry.skillId == skillId) return entry.value;
        return 0f;
    }

    public void AddSkillAffinity(string skillId, float value)
    {
        if (string.IsNullOrEmpty(skillId) || Mathf.Approximately(value, 0f)) return;

        skillAffinity ??= new List<SkillAffinity>();

        foreach (var entry in skillAffinity)
        {
            if (entry == null || entry.skillId != skillId) continue;
            entry.value += value;
            return;
        }
        skillAffinity.Add(new SkillAffinity { skillId = skillId, value = value });
    }

    // ── Derived combat values ─────────────────────────────────────────────────

    public float EffectiveMaxHit => Mathf.Max(1f, maxHit * (1f + maxHitMultiplier));

    /// <summary>
    /// The damage band, after the min-hit cascade.
    ///
    /// Raising minimum damage past your maximum is not wasted — the excess converts,
    /// first into crit chance and then, once crits are certain, into crit damage. That
    /// makes stacking minimum hit a real build rather than a stat with a ceiling.
    ///
    /// The conversion is scale-free: it measures overflow as a FRACTION of max hit, so
    /// doubling your minimum past your maximum is +100% crit chance whether the numbers
    /// are single digits or millions. An absolute rate would stop working the moment
    /// the game's numbers grew.
    /// </summary>
    public DamageProfile Resolve()
    {
        float max = EffectiveMaxHit;
        float min = Mathf.Max(0f, minHit * (1f + minHitMultiplier));

        float chance     = critChance;
        float critFactor = 1f + Mathf.Max(0f, critMultiplier);

        if (min > max)
        {
            float overflow = min - max;
            min = max;

            chance += overflow / max;

            if (chance > 1f)
            {
                // Crits are already certain, so further overflow makes them harder.
                critFactor += chance - 1f;
                chance      = 1f;
            }
        }

        return new DamageProfile
        {
            Min            = min,
            Max            = max,
            CritChance     = Mathf.Clamp01(chance),
            CritMultiplier = critFactor,
        };
    }

    /// <summary>
    /// Seconds between swings.
    ///
    /// The multiplier is allowed to go NEGATIVE — a Sorcerer swings slower than the
    /// baseline, and that is how classes express it — so the divisor is floored rather
    /// than the bonus. Without the floor a bonus of -1 divides by zero and a swing
    /// takes forever; without the outer floor a large positive one reaches an infinite
    /// attack rate.
    /// </summary>
    public float EffectiveAttackSpeed =>
        Mathf.Max(0.2f, attackSpeed / Mathf.Max(0.1f, 1f + attackSpeedMultiplier));

    public float EffectiveArmor  => Mathf.Max(0f, armor * (1f + armorMultiplier));
    public float EffectiveHealth => Mathf.Max(1f, health * (1f + healthMultiplier));

    /// <summary>
    /// Fraction of incoming damage that gets through this much armour.
    ///
    /// A diminishing curve rather than flat subtraction: subtraction lets armour reach
    /// total immunity and makes every weak hit land for exactly zero, which is both
    /// unbalanceable and dull. This asymptotes towards zero and never reaches it.
    /// </summary>
    public static float DamageThrough(float armor)
    {
        const float Softness = 120f;   // armour needed to halve incoming damage
        return Softness / (Softness + Mathf.Max(0f, armor));
    }
}

/// <summary>One skill's bonus fraction. See StatBlock.skillAffinity for why it is a List.</summary>
[Serializable]
public class SkillAffinity
{
    public string skillId;
    public float  value;
}

/// <summary>A resolved damage band, ready to roll.</summary>
public struct DamageProfile
{
    public float Min;
    public float Max;
    public float CritChance;
    public float CritMultiplier;

    /// <summary>Rolls one hit. <paramref name="wasCrit"/> drives the damage number's colour.</summary>
    public double Roll(out bool wasCrit)
    {
        double damage = UnityEngine.Random.Range(Min, Mathf.Max(Min, Max));
        wasCrit = UnityEngine.Random.value < CritChance;
        if (wasCrit) damage *= CritMultiplier;
        return damage;
    }
}
