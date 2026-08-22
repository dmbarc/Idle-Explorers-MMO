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
        // Load the local save first — a fresh stub every launch would mean no time
        // ever elapses between sessions, which makes AFK rewards impossible.
        // Runs in Awake via GetComponent rather than GameManager.Save, because
        // Awake order between sibling components on Managers is undefined.
        var save = GetComponent<SaveManager>();
        Current = save != null ? save.Load() : null;

        if (Current != null)
        {
            Debug.Log($"[AccountManager] Restored account '{Current.accountName}' (level {Current.accountLevel}).");
            GameEvents.OnAccountLoaded?.Invoke(Current);
            return;
        }

        // No save yet: create a local account until server auth exists (Phase 8)
        Current = new AccountData
        {
            accountId      = "local_player",
            accountName    = "Adventurer",
            accountLevel   = 1,
            accountXP      = 0,
            ghostsVisible  = true
        };
        Debug.Log("[AccountManager] No save found — created a new local account.");
    }

    public void LoadAccount(AccountData data)
    {
        Current = data;
        GameEvents.OnAccountLoaded?.Invoke(data);
    }

    // ── Account progression ───────────────────────────────────────────────────
    //
    // Account XP is pooled from every character's milestones, which is what makes
    // it the "breadth" stat: playing a second character advances the account, and
    // the account is what gates additional character slots.
    //
    // Before this, AddXP had no callers at all. accountXP could never leave 0, so
    // Account Lv. was permanently 1 — which silently locked character slots 2 and
    // 3 (reqAccountLevel 5 / 10) and every map above reqAccountLevel 1.

    void OnEnable()
    {
        GameEvents.OnCharacterLevelUp  += OnCharacterLevelUp;
        GameEvents.OnSkillLevelUp      += OnSkillLevelUp;
        GameEvents.OnMilestoneUnlocked += OnMilestoneUnlocked;
    }

    void OnDisable()
    {
        GameEvents.OnCharacterLevelUp  -= OnCharacterLevelUp;
        GameEvents.OnSkillLevelUp      -= OnSkillLevelUp;
        GameEvents.OnMilestoneUnlocked -= OnMilestoneUnlocked;
    }

    private void OnCharacterLevelUp(int newLevel)          => AddXP(newLevel * 10L);
    private void OnSkillLevelUp(string _, int newLevel)    => AddXP(newLevel * 5L);
    private void OnMilestoneUnlocked(string _, int level)  => AddXP(level * 25L);

    public void AddXP(long amount)
    {
        if (Current == null || amount <= 0) return;

        Current.accountXP += amount;

        int newLevel = XPToLevel(Current.accountXP);
        if (newLevel > Current.accountLevel)
        {
            Current.accountLevel = newLevel;
            GameEvents.OnAccountLevelUp?.Invoke(newLevel);

            // Toasted here rather than from a UI screen because account levels can
            // rise during AFK accrual on character select, where no HUD is loaded.
            GameEvents.FireToast($"★ Account Level {newLevel}!");
        }
    }

    /// <summary>
    /// Total account XP to account level. Same shape as the character and skill
    /// curves in CharacterManager / SkillManager so all three read alike.
    /// </summary>
    public static int XPToLevel(long totalXP)
    {
        if (totalXP <= 0) return 1;
        return Mathf.Clamp(1 + Mathf.FloorToInt(Mathf.Sqrt(totalXP / 100f)), 1, 999);
    }

    /// <summary>Account XP required to reach a given level.</summary>
    public static long LevelToXP(int level)
    {
        level = Mathf.Clamp(level, 1, 999);
        return (long)(level - 1) * (level - 1) * 100;
    }

    /// <summary>Progress through the current level, 0–1. Drives the XP bar.</summary>
    public static float LevelProgress(AccountData account)
    {
        if (account == null) return 0f;

        long floor = LevelToXP(account.accountLevel);
        long roof  = LevelToXP(account.accountLevel + 1);
        if (roof <= floor) return 1f;

        return Mathf.Clamp01((account.accountXP - floor) / (float)(roof - floor));
    }

    public bool IsSlotUnlocked(int slotIndex)
    {
        if (Current == null || GameManager.Content == null) return slotIndex < 2;
        int highestCharLevel = 0;
        foreach (var c in Current.characters)
            if (c.level > highestCharLevel) highestCharLevel = c.level;
        return GameManager.Content.IsSlotUnlocked(slotIndex, Current.accountLevel, highestCharLevel);
    }

}
