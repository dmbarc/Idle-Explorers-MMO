using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Tracks what the active character is currently doing (skill node / combat).
/// Saves a SkillActivityData snapshot on logout.
/// On login, calculates and distributes AFK rewards based on elapsed time.
/// </summary>
public class ActivityManager : MonoBehaviour
{
    public SkillActivityData CurrentActivity { get; private set; }

    // ── Set current activity ──────────────────────────────────────────────────

    public void SetActivity(string skillId, string targetId, string targetName, string mapId,
                            float activeRate, float afkRate, float specialChance,
                            string specialLabel, float xpPerHour)
    {
        CurrentActivity = new SkillActivityData
        {
            skillId               = skillId,
            activityTargetId      = targetId,
            activityTargetName    = targetName,
            mapId                 = mapId,
            activeRateMulti       = activeRate,
            afkRateMulti          = afkRate,
            specialChance         = specialChance,
            specialChanceLabel    = specialLabel,
            xpPerHour             = xpPerHour,
            activityStartUnixTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        if (CharacterManager.Current != null)
            CharacterManager.Current.currentActivity = CurrentActivity;

        GameEvents.FireActivityChanged(CurrentActivity);
        GameEvents.FireToast($"Now: {skillId} — {targetName}");
        Debug.Log($"[ActivityManager] Activity set: {skillId} on {targetName}");
    }

    /// <summary>
    /// Forgets the current activity and any pending summary.
    ///
    /// Both are manager-level state shared by every character, so leaving them set
    /// when switching characters showed the previous one's activity and AFK
    /// rewards on the next one.
    /// </summary>
    public void ClearActivity()
    {
        CurrentActivity = null;
        PendingSummary  = null;
        GameEvents.FireActivityChanged(null);
    }

    /// <summary>
    /// Restores a previously saved activity without resetting its start time or
    /// re-announcing it. Used when re-entering the map a character was parked on.
    /// </summary>
    public void ResumeActivity(SkillActivityData saved)
    {
        if (saved == null) return;

        CurrentActivity = saved;
        if (CharacterManager.Current != null)
            CharacterManager.Current.currentActivity = saved;

        GameEvents.FireActivityChanged(saved);
    }

    /// <summary>
    /// Set the default map activity (combat vs. the map's default monster).
    /// Called automatically when entering a map without interacting with a skill node.
    /// </summary>
    public void SetDefaultCombatActivity(string mapId)
    {
        var map = GameManager.Content?.GetMap(mapId);
        if (map == null) return;
        var monster = GameManager.Content?.GetMonster(map.defaultMonsterId);
        if (monster == null) return;

        int combatLevel = CharacterManager.Current?.level ?? 1;
        float afkRate = GameManager.Skills?.GetAFKRateMultiplier("combat", combatLevel) ?? 0.6f;

        SetActivity(
            skillId:       "combat",
            targetId:      map.defaultMonsterId,
            targetName:    monster.DisplayName,
            mapId:         mapId,
            activeRate:    1.0f,
            afkRate:       afkRate,
            specialChance: 0f,
            specialLabel:  "Drop chance",
            xpPerHour:     monster.xpReward * 60f  // rough estimate: 60 kills/hr at low level
        );
    }

    // ── AFK reward calculation (called on login) ──────────────────────────────

    /// <summary>
    /// Maximum offline time credited in one session. Uncapped accrual is the design
    /// goal, but until the numbers are balanced a multi-week gap would hand out
    /// quantities that make the rest of the game meaningless.
    /// </summary>
    public const long MaxAFKSeconds = 24 * 60 * 60;

    /// <summary>
    /// Offline time below this is ignored entirely. Swapping between characters
    /// takes seconds, and without a floor every swap produced a few XP and popped
    /// the "while you were away" screen for an absence that never happened.
    /// </summary>
    public const long MinAFKSeconds = 60;

    /// <summary>The most recently calculated summary, consumed by AFKSummaryScreen.</summary>
    public AFKRewardSummary PendingSummary { get; private set; }

    /// <summary>Clears the pending summary once a screen has displayed it.</summary>
    public void ConsumePendingSummary() => PendingSummary = null;

    /// <summary>
    /// Calculates and grants AFK rewards earned since lastLogoutUnixTime.
    /// Called by CharacterManager when a character is selected after being offline.
    /// Returns the summary (also stored as PendingSummary), or null if nothing accrued.
    /// </summary>
    public AFKRewardSummary ProcessAFKRewards(CharacterData character)
    {
        if (character?.currentActivity == null) return null;
        var activity = character.currentActivity;
        if (activity.activityStartUnixTime <= 0) return null;

        long now         = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long realElapsed = now - character.lastLogoutUnixTime;
        if (realElapsed < MinAFKSeconds) return null;

        // Close the offline window immediately, before any reward is handed out.
        // This used to be left untouched, so the same period was re-granted every
        // time the character was selected — the summary kept reporting an absence
        // that had already been paid out.
        character.lastLogoutUnixTime = now;

        long elapsedSeconds = System.Math.Min(realElapsed, MaxAFKSeconds);
        float hours         = elapsedSeconds / 3600f;

        var summary = new AFKRewardSummary
        {
            elapsedSeconds = elapsedSeconds,
            realElapsed    = realElapsed,
            wasCapped      = realElapsed > MaxAFKSeconds,
            skillId        = activity.skillId,
            activityName   = activity.activityTargetName,
            characterName  = character.characterName,
        };

        Debug.Log($"[ActivityManager] Processing {NumberFormatter.FormatAFKTime(elapsedSeconds)} of AFK for " +
                  $"{character.characterName}: {activity.skillId} ({activity.activityTargetName})");

        // XP reward. Combat XP is granted per-kill inside ProcessCombatAFKRewards
        // instead, so it is not double-counted here.
        if (activity.skillId != "combat")
        {
            long xpGained = (long)(activity.xpPerHour * hours * activity.afkRateMulti);
            if (xpGained > 0)
            {
                GameManager.Skills?.AddSkillXP(activity.skillId, xpGained);
                summary.AddXP(activity.skillId, xpGained);
            }
        }

        // Loot reward (combat or gathering)
        if (activity.skillId == "combat")
            ProcessCombatAFKRewards(activity, hours, character, summary);
        else
            ProcessGatheringAFKRewards(activity, hours, summary);

        PendingSummary = summary;
        GameEvents.OnAFKRewardsCollected?.Invoke(elapsedSeconds);

        // Persist straight away so a crash cannot replay this window.
        GameManager.Save?.Save();

        return summary;
    }

    private void ProcessCombatAFKRewards(SkillActivityData activity, float hours,
                                          CharacterData character, AFKRewardSummary summary)
    {
        var monster = GameManager.Content?.GetMonster(activity.activityTargetId);
        if (monster?.lootTable == null) return;

        // Estimate kills based on character level vs monster level
        float killsPerHour = Mathf.Max(1f, (character.level / (float)monster.level) * 30f);
        long totalKills = (long)(killsPerHour * hours * activity.afkRateMulti);
        if (totalKills <= 0) return;

        foreach (var loot in monster.lootTable)
        {
            long drops = RollBulkDrops(totalKills, loot);
            if (drops > 0)
            {
                GameManager.Inventory?.AddItem(loot.itemId, drops);
                summary?.AddItem(loot.itemId, drops);
            }
        }

        // Combat XP (from kills)
        long combatXP = monster.xpReward * totalKills;
        GameManager.Skills?.AddSkillXP("combat", combatXP);
        GameManager.Character?.AddXP(combatXP / 4); // character level XP = 1/4 of combat XP

        summary?.AddXP("combat", combatXP);
        if (summary != null) summary.kills = totalKills;
    }

    /// <summary>
    /// Total quantity dropped across many kills, computed in constant time.
    /// Rolling each kill individually would loop millions of times after a long
    /// AFK session and hang the game on login, so this uses the expected value
    /// with ±10% jitter — statistically equivalent, instant to evaluate.
    /// </summary>
    private static long RollBulkDrops(long kills, LootEntry loot)
    {
        if (kills <= 0 || loot == null || loot.DropChance <= 0f) return 0;

        double avgQty  = (loot.minQty + loot.maxQty) / 2.0;
        double expected = kills * loot.DropChance * avgQty;
        double jitter   = UnityEngine.Random.Range(0.9f, 1.1f);

        return (long)System.Math.Max(0d, expected * jitter);
    }

    private void ProcessGatheringAFKRewards(SkillActivityData activity, float hours, AFKRewardSummary summary)
    {
        // Resources gathered at AFK rate
        // Base yield: 1 item per action, scaled by level
        int skillLevel = GameManager.Skills?.GetSkillLevel(activity.skillId) ?? 1;
        float actionsPerHour = skillLevel * 20f; // scales with skill level
        long totalActions = (long)(actionsPerHour * hours * activity.afkRateMulti);

        if (totalActions > 0 && !string.IsNullOrEmpty(activity.activityTargetId))
        {
            long qty = totalActions;
            // Special chance rolls (e.g. bird's nest). Expected value rather than a
            // per-action loop — see RollBulkDrops for why.
            double jitter   = UnityEngine.Random.Range(0.9f, 1.1f);
            long   bonusQty = (long)System.Math.Max(0d, totalActions * activity.specialChance * jitter);

            GameManager.Inventory?.AddItem(activity.activityTargetId, qty + bonusQty);
            summary?.AddItem(activity.activityTargetId, qty + bonusQty);
        }
    }
}

/// <summary>
/// What a character earned while logged out. Built by ActivityManager and rendered
/// by AFKSummaryScreen — this is the screen that makes the game's central promise
/// legible, so the numbers are kept intact rather than folded into toasts.
/// </summary>
public class AFKRewardSummary
{
    public long   elapsedSeconds;      // credited time (may be capped)
    public long   realElapsed;         // actual wall-clock time offline
    public bool   wasCapped;
    public string skillId;
    public string activityName;
    public string characterName;
    public long   kills;

    public readonly List<InventoryEntry> itemsGained = new();
    public readonly List<InventoryEntry> xpGained    = new();   // itemId field holds skillId

    public bool HasAnything => itemsGained.Count > 0 || xpGained.Count > 0;

    public void AddItem(string itemId, long qty) => Accumulate(itemsGained, itemId, qty);
    public void AddXP(string skillId, long xp)   => Accumulate(xpGained,    skillId, xp);

    private static void Accumulate(List<InventoryEntry> list, string id, long amount)
    {
        if (string.IsNullOrEmpty(id) || amount <= 0) return;

        foreach (var entry in list)
        {
            if (entry.itemId == id) { entry.quantity += amount; return; }
        }
        list.Add(new InventoryEntry { itemId = id, quantity = amount });
    }
}
