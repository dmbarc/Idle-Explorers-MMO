using System;
using UnityEngine;

/// <summary>
/// Owns the currently active character's runtime state.
/// Saves SkillActivityData on disconnect for the AFK system.
/// Phase 1: local stub. Phase 8: server sync.
/// </summary>
public class CharacterManager : MonoBehaviour
{
    public static CharacterData Current { get; private set; }

    public void SelectCharacter(CharacterData character)
    {
        Current = character;
        Current.isOnline = true;
        GameEvents.OnCharacterSelected?.Invoke(character);
        Debug.Log($"[CharacterManager] Selected: {character.characterName} ({character.classId})");

        // Grant everything earned while this character was logged out. Must run
        // after Current is set — the reward path writes into the active character.
        if (character.lastLogoutUnixTime > 0)
            GameManager.Activity?.ProcessAFKRewards(character);
    }

    public void CreateCharacter(CharacterData character)
    {
        if (AccountManager.Current == null) return;
        character.characterId = Guid.NewGuid().ToString();
        character.level = 1;
        character.xp = 0;
        AccountManager.Current.characters.Add(character);
        GameEvents.OnCharacterCreated?.Invoke(character);
        Debug.Log($"[CharacterManager] Created: {character.characterName}");
    }

    /// <summary>
    /// Saves current activity snapshot and marks the character offline.
    /// Called before returning to the main menu or app backgrounding.
    /// </summary>
    public void SaveAndDisconnect()
    {
        if (Current == null) return;
        Current.isOnline = false;
        Current.lastLogoutUnixTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Snapshot whatever the character was doing so AFK accrual has something
        // to work from on next login.
        if (GameManager.Activity?.CurrentActivity != null)
            Current.currentActivity = GameManager.Activity.CurrentActivity;

        Debug.Log($"[CharacterManager] Saved and disconnected: {Current.characterName}");

        // Write to disk — the logout timestamp is worthless if it dies with the process.
        GameManager.Save?.Save();

        // TODO Phase 8: push to server
        Current = null;
    }

    public void AddXP(long amount)
    {
        if (Current == null) return;
        Current.xp += amount;
        // Level formula: XP required for level L = L^2 * 83 (OSRS-like but scaled to 1-999)
        int newLevel = XPToLevel(Current.xp);
        if (newLevel > Current.level)
        {
            Current.level = newLevel;
            GameEvents.OnCharacterLevelUp?.Invoke(newLevel);
        }
    }

    /// <summary>Convert total XP to character level (1–999).</summary>
    public static int XPToLevel(long totalXP)
    {
        // Using OSRS formula extended to 999: sum of floor((L + 300 * 2^(L/7)) / 4) for L=1 to target-1
        // For performance, use a precomputed approximation:
        // Level ≈ floor(1 + (totalXP / 83)^0.5), clamped to 1-999
        int level = Mathf.Clamp(1 + Mathf.FloorToInt(Mathf.Sqrt(totalXP / 83f)), 1, 999);
        return level;
    }

    /// <summary>XP required to reach a given level.</summary>
    public static long LevelToXP(int level)
    {
        level = Mathf.Clamp(level, 1, 999);
        long xp = (long)((level - 1) * (level - 1)) * 83;
        return xp;
    }

    /// <summary>XP needed to reach the next level from current XP.</summary>
    public static long XPToNextLevel(long currentXP)
    {
        int currentLevel = XPToLevel(currentXP);
        if (currentLevel >= 999) return 0;
        return LevelToXP(currentLevel + 1) - currentXP;
    }
}
