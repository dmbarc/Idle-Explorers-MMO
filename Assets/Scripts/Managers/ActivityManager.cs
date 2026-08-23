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
                            string specialLabel, float xpPerHour,
                            string recipeId = null, float secondsPerAction = 0f)
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
            recipeId              = recipeId ?? "",
            secondsPerAction      = secondsPerAction,
            activityStartUnixTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        if (CharacterManager.Current != null)
            CharacterManager.Current.currentActivity = CurrentActivity;

        GameEvents.FireActivityChanged(CurrentActivity);

        // The display name, not the raw id — every other message in the game reads
        // "Cooking", not "cooking".
        string skillName = GameManager.Content?.GetSkill(skillId)?.DisplayName ?? skillId;
        GameEvents.FireToast($"Now: {skillName} — {targetName}");
        Debug.Log($"[ActivityManager] Activity set: {skillId} on {targetName}");
    }

    /// <summary>
    /// Actions completed per hour. The single definition used by live ticking and by
    /// offline accrual, so the two can no longer drift apart.
    /// </summary>
    public static float ActionsPerHour(float secondsPerAction, float rateMulti)
    {
        float effective = Mathf.Max(0.01f, secondsPerAction) / Mathf.Max(0.01f, rateMulti);
        return 3600f / Mathf.Max(0.01f, effective);
    }

    // ── Talent adjustments ────────────────────────────────────────────────────
    //
    // These exist so a talent applies identically whether the player is watching or
    // offline. Adjusting only the live tick is the exact shape of the bug that made
    // AFK gathering 60x worse than its own multiplier claimed: two code paths, two
    // formulas, no way to notice they disagreed.

    /// <summary>
    /// Seconds per action after speed talents. Called by the live node tick AND by
    /// offline accrual — the stored snapshot keeps the raw figure, and both sides
    /// adjust it the same way at the moment of use.
    /// </summary>
    public static float TalentAdjustedSeconds(float secondsPerAction, bool crafting)
    {
        string effect = crafting ? TalentManager.CraftSpeedPercent : TalentManager.GatherRatePercent;
        return Mathf.Max(0.05f, secondsPerAction * TalentManager.ReductionMultiplier(effect));
    }

    /// <summary>The activity's AFK multiplier after talents that improve offline rate.</summary>
    public static float EffectiveAfkRate(SkillActivityData activity)
    {
        if (activity == null) return 0f;
        return activity.afkRateMulti * TalentManager.Multiplier(TalentManager.AfkRatePercent);
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

    // ── Validation ────────────────────────────────────────────────────────────

    /// <summary>
    /// Whether a saved activity still describes something the game can actually do.
    ///
    /// Checked against live content rather than a save-version stamp, so this keeps
    /// working for any future data change that orphans an activity — a node that
    /// becomes a station, a recipe that is renamed, a monster that is removed.
    /// </summary>
    public static bool IsActivityValid(SkillActivityData activity, out string reason)
    {
        if (activity == null || string.IsNullOrEmpty(activity.skillId))
        {
            reason = "no skill recorded";
            return false;
        }

        var content = GameManager.Content;
        if (content == null)
        {
            // Content not loaded yet — assume valid rather than destroying a good save.
            reason = null;
            return true;
        }

        if (activity.skillId == "combat")
        {
            if (content.GetMonster(activity.activityTargetId) == null)
            {
                reason = $"no monster '{activity.activityTargetId}' exists";
                return false;
            }
            reason = null;
            return true;
        }

        if (!string.IsNullOrEmpty(activity.recipeId))
        {
            if (content.GetRecipe(activity.recipeId) == null)
            {
                reason = $"no recipe '{activity.recipeId}' exists";
                return false;
            }
            reason = null;
            return true;
        }

        // Gathering: some node on that map must actually yield this item. A station
        // does not count — stations consume inputs and have no targetItemId.
        var map = content.GetMap(activity.mapId);
        if (map?.skillNodes == null)
        {
            reason = $"map '{activity.mapId}' has no skill nodes";
            return false;
        }

        foreach (var node in map.skillNodes)
        {
            if (node == null) continue;
            if (!string.IsNullOrEmpty(node.stationType)) continue;
            if (node.skillId != activity.skillId) continue;
            if (node.targetItemId != activity.activityTargetId) continue;

            reason = null;
            return true;
        }

        reason = $"nothing on '{activity.mapId}' gathers '{activity.activityTargetId}' " +
                 $"with {activity.skillId} any more";
        return false;
    }

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

        // A snapshot can outlive the content that produced it. The campfire used to be
        // a gathering node yielding cooked shrimp from nothing; it is a station now, so
        // a save written before that change still describes an activity the game no
        // longer has any way to perform — and the gathering path would happily conjure
        // its target item forever. Discard rather than pay out.
        if (!IsActivityValid(activity, out string reason))
        {
            Debug.LogWarning($"[ActivityManager] Discarding {character.characterName}'s saved activity " +
                             $"('{activity.skillId}' → '{activity.activityTargetName}'): {reason}");

            character.currentActivity = null;
            if (ReferenceEquals(CurrentActivity, activity)) ClearActivity();

            // Close the window anyway, or the same stale span is re-examined on every
            // selection and the warning repeats forever.
            character.lastLogoutUnixTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            GameManager.Save?.Save();
            return null;
        }

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

        // Each branch grants its own XP, derived from the same action count that
        // produces its items. XP used to be computed here from xpPerHour while items
        // came from an unrelated formula below, so a single session paid out two
        // numbers that could not both be true.
        if (activity.skillId == "combat")
            ProcessCombatAFKRewards(activity, hours, character, summary);
        else if (!string.IsNullOrEmpty(activity.recipeId))
            ProcessCraftingAFKRewards(activity, hours, summary);
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
        long totalKills = (long)(killsPerHour * hours * EffectiveAfkRate(activity));
        if (totalKills <= 0) return;

        // Drop-quantity talents apply offline too. Expected value rather than a roll,
        // for the same reason RollBulkDrops uses one.
        float quantityMultiplier = TalentManager.Multiplier(TalentManager.DropQuantityPercent);

        foreach (var loot in monster.lootTable)
        {
            long drops = (long)(RollBulkDrops(totalKills, loot) * quantityMultiplier);
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
        // Same rate the live tick uses, scaled by the AFK multiplier — which is what
        // "AFK earns 60% of active" was always supposed to mean. The old formula
        // (skillLevel * 20 per hour) produced ~20/hr at level 1 against the live
        // rate of 1200/hr, so going AFK was ~60x worse than the multiplier claimed.
        float secondsPerAction = TalentAdjustedSeconds(ActionSeconds(activity), crafting: false);
        long  totalActions     = (long)(ActionsPerHour(secondsPerAction, activity.activeRateMulti)
                                        * hours * EffectiveAfkRate(activity));
        if (totalActions <= 0) return;

        if (!string.IsNullOrEmpty(activity.activityTargetId))
        {
            // Special chance rolls (e.g. bird's nest). Expected value rather than a
            // per-action loop — see RollBulkDrops for why.
            double jitter   = UnityEngine.Random.Range(0.9f, 1.1f);
            long   bonusQty = (long)System.Math.Max(0d, totalActions * activity.specialChance * jitter);

            GameManager.Inventory?.AddItem(activity.activityTargetId, totalActions + bonusQty);
            summary?.AddItem(activity.activityTargetId, totalActions + bonusQty);
        }

        GrantSkillXP(activity, totalActions, summary);
    }

    /// <summary>
    /// Crafting differs from gathering in one way that changes everything: it
    /// CONSUMES inputs, so the session can end early. When it does, the summary has
    /// to say so — otherwise a player who banked 20 shrimp and left for nine hours
    /// is quietly told they were productive the whole time.
    /// </summary>
    private void ProcessCraftingAFKRewards(SkillActivityData activity, float hours, AFKRewardSummary summary)
    {
        var recipe = GameManager.Content?.GetRecipe(activity.recipeId);
        if (recipe == null) return;

        float secondsPerAction = TalentAdjustedSeconds(ActionSeconds(activity), crafting: true);
        long  possibleCrafts   = (long)(ActionsPerHour(secondsPerAction, activity.activeRateMulti)
                                        * hours * EffectiveAfkRate(activity));
        if (possibleCrafts <= 0) return;

        // Worn procs have to apply offline too, or an item that doubles campfire
        // output is worthless in an idle game. Expected value rather than per-craft
        // rolls, for the same reason the consumption below is bulk. Talent
        // double-output stacks additively on top, by the same expected-value rule.
        float outputMultiplier = ItemEffectResolver.AggregateMultiplier("onCraft", "doubleOutput", recipe.skillId)
                                 + TalentManager.Bonus(TalentManager.CraftDoubleChance);

        long maxByInputs  = CraftingSupply.MaxCrafts(recipe, out string limitingItemId);
        long actualCrafts = System.Math.Min(possibleCrafts, maxByInputs);

        if (actualCrafts < possibleCrafts && summary != null)
        {
            summary.ranOutOfItemId = limitingItemId;
            summary.effectiveSeconds = (long)(summary.elapsedSeconds *
                                              (actualCrafts / (double)possibleCrafts));
        }

        if (actualCrafts <= 0)
        {
            // Nothing to work with at all. Stop claiming to be busy.
            ClearActivity();
            return;
        }

        // Consume in bulk rather than per craft: after a 24h gap this can be tens of
        // thousands of iterations, and the login would visibly hang. All-or-nothing,
        // so a disagreement between the stock check and the spend cannot charge the
        // player for crafts they do not receive.
        if (!CraftingSupply.ConsumeFor(recipe, actualCrafts))
        {
            Debug.LogWarning($"[ActivityManager] Could not afford {actualCrafts}x '{recipe.id}' " +
                             "despite the stock check — granting nothing.");
            return;
        }

        // The multiplier lands on output only — a proc that doubles what you make
        // must not also double what it cost you to make it.
        long produced = (long)(recipe.outputQuantity * actualCrafts * outputMultiplier);
        GameManager.Inventory?.AddItem(recipe.outputItemId, produced);
        summary?.AddItem(recipe.outputItemId, produced);

        long xp = (long)(recipe.xpPerCraft * actualCrafts);
        if (xp > 0)
        {
            GameManager.Skills?.AddSkillXP(recipe.skillId, xp);
            summary?.AddXP(recipe.skillId, xp);
        }

        // Ran dry: the character is no longer doing anything, and their card should
        // say Idle rather than claiming to still be at the campfire.
        if (actualCrafts >= maxByInputs) ClearActivity();
    }

    /// <summary>
    /// Seconds per action for an activity, falling back for snapshots written before
    /// the field existed. Without the fallback, every save from an earlier build
    /// would divide by zero and grant an absurd number of actions.
    /// </summary>
    private static float ActionSeconds(SkillActivityData activity)
    {
        const float LegacyDefault = 3f;   // SkillNodeController.baseSecondsPerAction
        return activity.secondsPerAction > 0.01f ? activity.secondsPerAction : LegacyDefault;
    }

    /// <summary>Grants XP proportional to the actions actually completed.</summary>
    private static void GrantSkillXP(SkillActivityData activity, long actions, AFKRewardSummary summary)
    {
        if (actions <= 0 || string.IsNullOrEmpty(activity.skillId)) return;

        // xpPerHour was derived from the live action rate when the activity started,
        // so dividing it back out recovers xp-per-action without needing the node.
        float actionsPerHourLive = ActionsPerHour(ActionSeconds(activity), activity.activeRateMulti);
        if (actionsPerHourLive <= 0f) return;

        long xp = (long)(activity.xpPerHour / actionsPerHourLive * actions);
        if (xp <= 0) return;

        GameManager.Skills?.AddSkillXP(activity.skillId, xp);
        summary?.AddXP(activity.skillId, xp);
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

    /// <summary>
    /// Set when a crafting session ran out of an input before the offline window
    /// closed. effectiveSeconds is how much of that window was actually productive.
    /// </summary>
    public string ranOutOfItemId;
    public long   effectiveSeconds;

    public bool WasTruncated => !string.IsNullOrEmpty(ranOutOfItemId);

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
