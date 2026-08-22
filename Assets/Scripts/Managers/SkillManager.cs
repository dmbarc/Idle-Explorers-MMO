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

        if (!ch.skillXP.ContainsKey(skillId))    ch.skillXP[skillId]    = 0;
        if (!ch.skillLevels.ContainsKey(skillId)) ch.skillLevels[skillId] = 1;

        ch.skillXP[skillId] += amount;
        GameEvents.OnSkillXPGained?.Invoke(skillId, amount);

        int newLevel = XPToSkillLevel(ch.skillXP[skillId]);
        if (newLevel > ch.skillLevels[skillId])
        {
            int old = ch.skillLevels[skillId];
            ch.skillLevels[skillId] = newLevel;
            GameEvents.OnSkillLevelUp?.Invoke(skillId, newLevel);
            GameEvents.FireToast($"⬆ {SkillDisplayName(skillId)}: {old} → {newLevel}");
            CheckMilestones(skillId, old, newLevel);
        }
    }

    public int GetSkillLevel(string skillId)
    {
        var ch = CharacterManager.Current;
        if (ch == null) return 1;
        return ch.skillLevels.TryGetValue(skillId, out int lvl) ? lvl : 1;
    }

    public long GetSkillXP(string skillId)
    {
        var ch = CharacterManager.Current;
        if (ch == null) return 0;
        return ch.skillXP.TryGetValue(skillId, out long xp) ? xp : 0;
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
        return data?.displayName ?? skillId;
    }
}
