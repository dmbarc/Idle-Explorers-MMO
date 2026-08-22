using UnityEngine;

/// <summary>
/// Manages AccountData for the logged-in player.
/// Handles slot unlock checks, account level, and character list.
/// Phase 1: local stub. Phase 8: server sync via Mirror.
/// </summary>
public class AccountManager : MonoBehaviour
{
    public static AccountData Current { get; private set; }

    void Awake()
    {
        // Stub: create a local account for testing until server auth is implemented (Phase 8)
        Current = new AccountData
        {
            accountId      = "local_player",
            accountName    = "Adventurer",
            accountLevel   = 1,
            accountXP      = 0,
            ghostsVisible  = true
        };
    }

    public void LoadAccount(AccountData data)
    {
        Current = data;
        GameEvents.OnAccountLoaded?.Invoke(data);
    }

    public bool IsSlotUnlocked(int slotIndex)
    {
        if (Current == null || GameManager.Content == null) return slotIndex < 2;
        int highestCharLevel = 0;
        foreach (var c in Current.characters)
            if (c.level > highestCharLevel) highestCharLevel = c.level;
        return GameManager.Content.IsSlotUnlocked(slotIndex, Current.accountLevel, highestCharLevel);
    }

    public void AddXP(long amount)
    {
        if (Current == null) return;
        Current.accountXP += amount;
        // Simple level-up formula: level = floor(sqrt(accountXP / 100)) + 1, capped at 999
        int newLevel = Mathf.Clamp(Mathf.FloorToInt(Mathf.Sqrt((float)(Current.accountXP / 100f))) + 1, 1, 999);
        if (newLevel > Current.accountLevel)
        {
            Current.accountLevel = newLevel;
            GameEvents.OnAccountLevelUp?.Invoke(newLevel);
        }
    }
}
