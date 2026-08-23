using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Assembles a character's live stats from every source, in one place.
///
/// Sources, summed in this order:
///
///   1. The shared baseline from base_stats.json — what every character has before
///      being anything in particular.
///   2. One contribution per specced class. Additive, which is the whole point of
///      cross-speccing: a second class broadens you rather than trading away the first.
///   3. Worn equipment, via its onEquipPassive statBonus effects.
///   4. Armour set bonuses that are currently active.
///   5. Talents.
///
/// Cached, and recomputed only when something that feeds it changes. The alternative —
/// recomputing on read — would walk every equipped item and every talent node several
/// times per frame, since PlayerController asks for damage on every swing.
/// </summary>
public class StatsManager : MonoBehaviour
{
    private StatBlock _current;
    private bool      _dirty = true;

    /// <summary>
    /// The active character's stats. Never null — a character with no class and no
    /// gear still has the baseline, and returning null here would mean a null check
    /// at every combat site.
    /// </summary>
    public StatBlock Current
    {
        get
        {
            if (_dirty || _current == null) Recompute();
            return _current;
        }
    }

    void OnEnable()
    {
        GameEvents.OnEquipmentChanged  += MarkDirty;
        GameEvents.OnDurabilityChanged += MarkDirty;
        GameEvents.OnTalentsChanged    += MarkDirty;
        GameEvents.OnClassChanged      += OnClassChanged;
        GameEvents.OnCharacterSelected += OnCharacterSelected;
    }

    void OnDisable()
    {
        GameEvents.OnEquipmentChanged  -= MarkDirty;
        GameEvents.OnDurabilityChanged -= MarkDirty;
        GameEvents.OnTalentsChanged    -= MarkDirty;
        GameEvents.OnClassChanged      -= OnClassChanged;
        GameEvents.OnCharacterSelected -= OnCharacterSelected;
    }

    private void OnClassChanged(string _)              => MarkDirty();
    private void OnCharacterSelected(CharacterData _)  => MarkDirty();

    /// <summary>
    /// Invalidates the cache. Deliberately does NOT recompute immediately — several of
    /// these events fire together (equipping one item raises both OnEquipmentChanged
    /// and OnDurabilityChanged), and recomputing per event would do the work three
    /// times before anyone read the result.
    /// </summary>
    public void MarkDirty()
    {
        _dirty = true;
        GameEvents.OnStatsChanged?.Invoke();
    }

    // ── Assembly ──────────────────────────────────────────────────────────────

    private void Recompute()
    {
        var block = GameManager.Content?.BaseStats?.Clone() ?? new StatBlock();

        AddClasses(block);
        GameManager.Equipment?.ContributeTo(block);
        SetBonusResolver.ContributeTo(block);
        AddTalents(block);

        _current = block;
        _dirty   = false;
    }

    private static void AddClasses(StatBlock block)
    {
        var character = CharacterManager.Current;
        if (character == null) return;

        foreach (var classId in character.ClassIds())
        {
            var cls = GameManager.Content?.GetClass(classId);
            if (cls?.stats == null) continue;

            block.Add(cls.stats);
        }
    }

    /// <summary>
    /// Talent effect types that are character STATS, and which stat each becomes.
    ///
    /// The rest of TalentManager's effect types are not stats and stay where they are:
    /// cooldown and ability power belong to abilities, craft and gather speed belong to
    /// the activity loop, skill XP belongs to SkillManager. Forcing those into a stat
    /// block would mean inventing stats nothing displays in order to move a number from
    /// one system to another.
    /// </summary>
    private static readonly (string Effect, string Stat)[] TalentStatMap =
    {
        (TalentManager.MaxHpPercent,       Stats.HealthMultiplier),
        (TalentManager.AttackSpeedPercent, Stats.AttackSpeedMultiplier),
        (TalentManager.HealthRegenFlat,    Stats.HealthRegen),
        (TalentManager.DropQuantityPercent, Stats.DropRateMultiplier),
        (TalentManager.AfkRatePercent,     Stats.Diligence),
    };

    private static void AddTalents(StatBlock block)
    {
        foreach (var (effect, stat) in TalentStatMap)
        {
            float bonus = TalentManager.Bonus(effect);
            if (!Mathf.Approximately(bonus, 0f)) block.Add(stat, bonus);
        }

        // "Attack damage" means the whole band, not just its ceiling — a talent that
        // reads "+3% attack damage" should raise what you hit for at both ends, or
        // stacking it would widen the spread rather than improve the average.
        float damage = TalentManager.Bonus(TalentManager.AttackDamagePercent);
        if (!Mathf.Approximately(damage, 0f))
        {
            block.Add(Stats.MinHitMultiplier, damage);
            block.Add(Stats.MaxHitMultiplier, damage);
        }
    }

    // ── Convenience ───────────────────────────────────────────────────────────

    /// <summary>
    /// A skill's rate multiplier from class affinities. 1.0 when no class favours it.
    ///
    /// Floored well above zero: a negative affinity from some future class must never
    /// be able to stop a skill progressing entirely.
    /// </summary>
    public float SkillMultiplier(string skillId) =>
        Mathf.Max(0.25f, 1f + Current.GetSkillAffinity(skillId));

    /// <summary>Every skill this character is better than average at, best first.</summary>
    public List<(string SkillId, float Bonus)> Affinities()
    {
        var results = new List<(string, float)>();

        var affinities = Current.skillAffinity;
        if (affinities == null) return results;

        foreach (var entry in affinities)
            if (entry != null && entry.value > 0.001f) results.Add((entry.skillId, entry.value));

        results.Sort((a, b) => b.Item2.CompareTo(a.Item2));
        return results;
    }
}
