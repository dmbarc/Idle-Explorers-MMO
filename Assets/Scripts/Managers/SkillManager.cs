using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Manages all 13 skill levels and XP for the active character.
/// XP table: skill levels 1–999 with major milestone unlocks.
/// </summary>
public class SkillManager : MonoBehaviour
{
    // Milestone levels that trigger unlock events
    public static readonly int[] Milestones = { 20, 40, 60, 80, 99, 150, 200, 300, 500, 750, 999 };

    /// <summary>Grant XP to a skill for the active character.</summary>
    public void AddSkillXP(string skillId, long amount)
    {
        var ch = CharacterManager.Current;
        if (ch == null) return;

        // Talent XP bonus is applied here, at the single choke point every source of
        // skill XP passes through — live gathering, crafting, combat and offline
        // accrual alike. Applying it at the call sites would mean finding all of them
        // again every time a new one appears.
        float xpMultiplier = TalentManager.Multiplier(TalentManager.SkillXpPercent);
        if (xpMultiplier > 1f) amount = (long)(amount * xpMultiplier);

        var progress = ch.GetOrCreateSkill(skillId);
        progress.xp += amount;
        GameEvents.OnSkillXPGained?.Invoke(skillId, amount);

        // Character XP is a quarter of ALL skill XP, from every skill.
        //
        // It used to come from combat kills alone, granted at the two places kills are
        // awarded. A character who mined, fished and cooked exclusively therefore
        // stayed at level 1 for as long as they played — which, now that talent points
        // come from character level, would have meant an entire playstyle never
        // earning a single talent point. One rule, applied wherever skill XP lands.
        GameManager.Character?.AddXP(amount / 4);

        int newLevel = XPToSkillLevel(progress.xp);
        if (newLevel > progress.level)
        {
            int old = progress.level;
            progress.level = newLevel;
            GameEvents.OnSkillLevelUp?.Invoke(skillId, newLevel);
            GameEvents.FireToast($"⬆ {SkillDisplayName(skillId)}: {old} → {newLevel}");
            CheckMilestones(skillId, old, newLevel);
        }
    }

    public int GetSkillLevel(string skillId)
    {
        var progress = CharacterManager.Current?.GetSkill(skillId);
        return progress?.level ?? 1;
    }

    public long GetSkillXP(string skillId)
    {
        var progress = CharacterManager.Current?.GetSkill(skillId);
        return progress?.xp ?? 0;
    }

    public long XPToNextSkillLevel(string skillId)
    {
        int current = GetSkillLevel(skillId);
        if (current >= 999) return 0;
        return SkillLevelToXP(current + 1) - GetSkillXP(skillId);
    }

    // ── XP Table (skill-specific, 1–999) ─────────────────────────────────────
    // Same formula as CharacterManager but for skill levels:
    // XP for level L ≈ L^2 * 100 (roughly OSRS-like but for 999 cap)
    public static int XPToSkillLevel(long totalXP)
    {
        return Mathf.Clamp(1 + Mathf.FloorToInt(Mathf.Sqrt(totalXP / 100f)), 1, 999);
    }

    public static long SkillLevelToXP(int level)
    {
        level = Mathf.Clamp(level, 1, 999);
        return (long)(level - 1) * (level - 1) * 100;
    }

    // ── Milestones ────────────────────────────────────────────────────────────
    private void CheckMilestones(string skillId, int oldLevel, int newLevel)
    {
        foreach (int m in Milestones)
            if (oldLevel < m && newLevel >= m)
                GameEvents.OnMilestoneUnlocked?.Invoke(skillId, m);
    }

    // ── AFK rate calculation ──────────────────────────────────────────────────
    /// <summary>
    /// Returns the AFK rate multiplier for a skill at the given level.
    /// Starts low, increases slowly with level. Never exceeds 0.9x active rate.
    /// </summary>
    public float GetAFKRateMultiplier(string skillId, int level)
    {
        var skillData = GameManager.Content?.GetSkill(skillId);
        float baseRate = skillData?.afkRateDefault ?? 0.6f;
        // Rate improves slightly with level: max 0.9x at level 999
        float bonus = (level / 999f) * 0.3f;
        return Mathf.Clamp(baseRate + bonus, 0.1f, 0.9f);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    private string SkillDisplayName(string skillId)
    {
        var data = GameManager.Content?.GetSkill(skillId);
        return data?.DisplayName ?? skillId;
    }
}
