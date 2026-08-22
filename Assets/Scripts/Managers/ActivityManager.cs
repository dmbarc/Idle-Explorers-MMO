using System;
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
            targetName:    monster.displayName,
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
    /// Calculates and grants AFK rewards earned since lastLogoutUnixTime.
    /// Called by CharacterManager when a character is selected after being offline.
    /// </summary>
    public void ProcessAFKRewards(CharacterData character)
    {
        if (character?.currentActivity == null) return;
        var activity = character.currentActivity;
        if (activity.activityStartUnixTime <= 0) return;

        long elapsedSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - character.lastLogoutUnixTime;
        if (elapsedSeconds <= 0) return;

        float hours = elapsedSeconds / 3600f;
        Debug.Log($"[ActivityManager] Processing {NumberFormatter.FormatAFKTime(elapsedSeconds)} of AFK for {character.characterName}: {activity.skillId} ({activity.activityTargetName})");

        // XP reward
        long xpGained = (long)(activity.xpPerHour * hours * activity.afkRateMulti);
        if (xpGained > 0 && activity.skillId != "combat")
            GameManager.Skills?.AddSkillXP(activity.skillId, xpGained);

        // Loot reward (combat or gathering)
        if (activity.skillId == "combat")
            ProcessCombatAFKRewards(activity, hours, character);
        else
            ProcessGatheringAFKRewards(activity, hours);

        GameEvents.OnAFKRewardsCollected?.Invoke(elapsedSeconds);
    }

    private void ProcessCombatAFKRewards(SkillActivityData activity, float hours, CharacterData character)
    {
        var monster = GameManager.Content?.GetMonster(activity.activityTargetId);
        if (monster?.lootTable == null) return;

        // Estimate kills based on character level vs monster level
        float killsPerHour = Mathf.Max(1f, (character.level / (float)monster.level) * 30f);
        long totalKills = (long)(killsPerHour * hours * activity.afkRateMulti);
        if (totalKills <= 0) return;

        foreach (var loot in monster.lootTable)
        {
            long drops = 0;
            for (long k = 0; k < totalKills; k++)
                if (UnityEngine.Random.value <= loot.dropChance)
                    drops += UnityEngine.Random.Range((int)loot.minQuantity, (int)loot.maxQuantity + 1);

            if (drops > 0)
            {
                GameManager.Inventory?.AddItem(loot.itemId, drops);
                GameEvents.FireItemPickedUp(loot.itemId, drops);
            }
        }

        // Combat XP (from kills)
        long combatXP = monster.xpReward * totalKills;
        GameManager.Skills?.AddSkillXP("combat", combatXP);
        CharacterManager.Instance?.AddXP(combatXP / 4); // character level XP = 1/4 of combat XP
    }

    private void ProcessGatheringAFKRewards(SkillActivityData activity, float hours)
    {
        // Resources gathered at AFK rate
        // Base yield: 1 item per action, scaled by level
        int skillLevel = GameManager.Skills?.GetSkillLevel(activity.skillId) ?? 1;
        float actionsPerHour = skillLevel * 20f; // scales with skill level
        long totalActions = (long)(actionsPerHour * hours * activity.afkRateMulti);

        if (totalActions > 0 && !string.IsNullOrEmpty(activity.activityTargetId))
        {
            long qty = totalActions;
            // Special chance rolls (e.g. double ore chance)
            long bonusQty = 0;
            for (long a = 0; a < totalActions; a++)
                if (UnityEngine.Random.value <= activity.specialChance)
                    bonusQty++;

            GameManager.Inventory?.AddItem(activity.activityTargetId, qty + bonusQty);
            GameEvents.FireItemPickedUp(activity.activityTargetId, qty + bonusQty);
        }
    }
}
