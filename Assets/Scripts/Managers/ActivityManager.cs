using System;
using System.Collections.Generic;
using UnityEngine;
using IdleExplorers.Rules;

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
                            string recipeId = null, float secondsPerAction = 0f,
                            bool announce = true)
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

        // Switching what you are doing costs you every second of momentum you built.
        _momentumSeconds = 0f;

        GameEvents.FireActivityChanged(CurrentActivity);

        // The display name, not the raw id — every other message in the game reads
        // "Cooking", not "cooking".
        string skillName = GameManager.Content?.GetSkill(skillId)?.DisplayName ?? skillId;
        if (announce) GameEvents.FireToast($"Now: {skillName} — {targetName}");
        Debug.Log($"[ActivityManager] Activity set: {skillId} on {targetName}");
    }

    /// <summary>
    /// Actions completed per hour. The single definition used by live ticking and by
    /// offline accrual, so the two can no longer drift apart.
    /// </summary>
    public static float ActionsPerHour(float secondsPerAction, float rateMulti) =>
        RateMath.ActionsPerHour(secondsPerAction, rateMulti);

    // ── Talent adjustments ────────────────────────────────────────────────────
    //
    // These exist so a talent applies identically whether the player is watching or
    // offline. Adjusting only the live tick is the exact shape of the bug that made
    // AFK gathering 60x worse than its own multiplier claimed: two code paths, two
    // formulas, no way to notice they disagreed.
    //
    // The arithmetic itself now lives in IdleExplorers.Rules.RateMath, which the game
    // server compiles too. These methods survive as the seam that resolves the talent
    // tree and the stat block -- neither of which the rules can reach -- and then hand
    // the resolved numbers over. So the client and the server agree by construction
    // rather than by two people remembering to change two files.

    /// <summary>
    /// Seconds per action after speed talents. Called by the live node tick AND by
    /// offline accrual — the stored snapshot keeps the raw figure, and both sides
    /// adjust it the same way at the moment of use.
    /// </summary>
    public static float TalentAdjustedSeconds(float secondsPerAction, bool crafting) =>
        RateMath.AdjustedSeconds(secondsPerAction, SpeedTalentMultiplier(crafting), 1f);

    /// <summary>
    /// Seconds per action after talents AND the class affinity for that skill.
    ///
    /// Separate from TalentAdjustedSeconds because affinity needs to know WHICH skill
    /// is being worked, and the older signature only knew whether it was crafting.
    /// Both live and offline paths call this, so a Ranger chops faster in both.
    /// </summary>
    public static float AdjustedSeconds(float secondsPerAction, bool crafting, string skillId) =>
        RateMath.AdjustedSeconds(secondsPerAction,
                                 SpeedTalentMultiplier(crafting),
                                 GameManager.Stats?.SkillMultiplier(skillId) ?? 1f);

    private static float SpeedTalentMultiplier(bool crafting)
    {
        string effect = crafting ? TalentManager.CraftSpeedPercent : TalentManager.GatherRatePercent;
        return TalentManager.ReductionMultiplier(effect);
    }

    /// <summary>
    /// The activity's AFK multiplier after everything that improves offline rate.
    ///
    /// Diligence rather than the talent bonus directly: StatsManager already folds
    /// afkRatePercent into diligence, so reading both here would count it twice.
    /// </summary>
    public static float EffectiveAfkRate(SkillActivityData activity)
    {
        if (activity == null) return 0f;

        float diligence = GameManager.Stats?.Current.diligence ?? 0f;
        return RateMath.EffectiveAfkRate(activity.afkRateMulti, diligence);
    }

    // ── Momentum ──────────────────────────────────────────────────────────────
    //
    // The stat is a ceiling; this is how much of it you have earned. It climbs while
    // you stay on one thing and drops to nothing the moment you switch, which is the
    // whole point: an idle game asks you to commit to an activity, and this is the
    // only stat that pays you for actually doing so.

    /// <summary>How long uninterrupted work takes to reach full momentum.</summary>
    private const float MomentumRampSeconds = 600f;   // ten minutes

    private float _momentumSeconds;

    /// <summary>0-1, how much of the momentum stat is currently earned.</summary>
    public float MomentumFraction => Mathf.Clamp01(_momentumSeconds / MomentumRampSeconds);

    /// <summary>
    /// The live yield bonus from momentum.
    ///
    /// Offline accrual deliberately uses the FULL stat instead of this: a character
    /// who was logged out did exactly one thing for the entire window, which is the
    /// definition of uninterrupted. Charging them a ramp they could not have watched
    /// would punish the playstyle the game is built around.
    /// </summary>
    public float MomentumBonus
    {
        get
        {
            float stat = GameManager.Stats?.Current.momentum ?? 0f;
            return _creditingOfflineTime ? stat : stat * MomentumFraction;
        }
    }

    /// <summary>
    /// True only while offline rewards are being paid out. Set around the whole
    /// payout rather than passed down, because the XP it affects is granted several
    /// call layers below through SkillManager, which has no idea where it came from.
    /// </summary>
    private bool _creditingOfflineTime;

    void Update()
    {
        if (CurrentActivity != null) _momentumSeconds += Time.deltaTime;
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
    ///
    /// Called when entering a map, and again whenever the player walks away from a
    /// skill node — combat is what the character falls back to, so it is what the
    /// readout should say once they stop mining.
    ///
    /// IDEMPOTENT, and that matters more than it looks. This is now called on every
    /// target switch, and SetActivity stamps a fresh activityStartUnixTime; without
    /// the early return, wandering between goblins would restart the AFK clock several
    /// times a minute and toast "Now: Combat" each time.
    /// </summary>
    /// <param name="startFighting">
    /// Whether to actually start swinging, as opposed to merely recording that combat
    /// is what this character is doing.
    ///
    /// ══ WHY THE DISTINCTION EXISTS ══════════════════════════════════════
    ///
    /// Combat is the fallback activity, so it is set in two very different situations:
    /// arriving on a map with nothing else to do, and STOPPING something else. The
    /// second one is a player walking away from the anvil, and turning auto-attack on
    /// for them sent their character sprinting at the nearest goblin.
    ///
    /// Arriving means fight. Stopping means stop.
    /// </param>
    public void SetDefaultCombatActivity(string mapId, bool startFighting = false)
    {
        var map = GameManager.Content?.GetMap(mapId);
        if (map == null) return;
        var monster = GameManager.Content?.GetMonster(map.defaultMonsterId);
        if (monster == null) return;

        // A BOSS IS NOT AN ACTIVITY. The throne's defaultMonsterId is goblin_king,
        // because that is what a player fights there -- and settlement resolves combat
        // statistically, so a standing activity pointed at him would pay for hundreds
        // of Kings overnight without anybody entering the arena.
        //
        // The server refuses it too, which is the enforcement. This stops the client
        // asking on every entry and collecting a 409 for its trouble.
        if (monster.isBoss) return;

        var current = CurrentActivity;
        if (current != null &&
            current.skillId          == "combat" &&
            current.activityTargetId == map.defaultMonsterId &&
            current.mapId            == mapId)
            return;

        int combatLevel = CharacterManager.Current?.level ?? 1;
        float afkRate = GameManager.Skills?.GetAFKRateMultiplier("combat", combatLevel) ?? 0.6f;

        // Combat, by monster id. Here rather than inside SetActivity for the same
        // reason as the gathering hook: this is where the server-side id is known.
        if (IdleExplorers.Backend.ServerState.IsAuthoritative)
            _ = IdleExplorers.Backend.ServerState.SetFightingAsync(map.defaultMonsterId);

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

        // ══ AND THE CHARACTER ACTUALLY SWINGS ═══════════════════════════════════
        //
        // Setting the activity told the SERVER to fight and told the HUD to say
        // "Now: combat — Goblin". It never told the character. autoAttack defaults to
        // false and only the HUD button had ever set it, so the server farmed goblins
        // and paid the experience while the player stood next to one doing nothing.
        //
        // It looked like several different bugs at once: no damage numbers, no swing
        // animation, and two sprites standing side by side -- because none of the
        // combat code was running at all. The experience bar still climbed, which is
        // what made it look like combat was working.
        //
        // The local fight is theatre for what the server is settling, so the theatre
        // starts when the settlement does -- but only when combat was CHOSEN. See the
        // note on startFighting.
        if (startFighting) FindPlayer()?.SetAutoAttack(true);
    }

    /// <summary>
    /// The player, if one is in the scene.
    ///
    /// Looked up rather than cached: this manager outlives every map, and a reference
    /// taken on the goblin camp is a destroyed object by the time somebody is fishing
    /// somewhere else.
    /// </summary>
    private static PlayerController FindPlayer() =>
        UnityEngine.Object.FindAnyObjectByType<PlayerController>();

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
            var recipe = content.GetRecipe(activity.recipeId);
            if (recipe == null)
            {
                reason = $"no recipe '{activity.recipeId}' exists";
                return false;
            }

            // Same reasoning as the gathering gate below: a recipe you can no longer
            // make must not keep producing while you are away.
            int craftLevel = GameManager.Skills?.GetSkillLevel(recipe.skillId) ?? 1;
            if (craftLevel < recipe.reqSkillLevel)
            {
                reason = $"{recipe.skillId} level {craftLevel} is below the " +
                         $"{recipe.reqSkillLevel} '{recipe.id}' requires";
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

            // The level gate has to hold offline too. A character who can no longer
            // mine copper standing at the rock must not keep producing it while
            // logged out — that is the same "conjuring items you cannot make" the
            // stale-campfire fix was about, arriving by a different route now that
            // levels can go down (Draught of Unlearning) as well as content change.
            int level = GameManager.Skills?.GetSkillLevel(node.skillId) ?? 1;
            if (level < node.reqSkillLevel)
            {
                reason = $"{activity.skillId} level {level} is below the {node.reqSkillLevel} " +
                         $"that '{node.nodeId}' now requires";
                return false;
            }

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
    /// Turns a server settlement into the summary the AFK screen already knows how to draw.
    ///
    /// ══ WHY THIS EXISTS ═════════════════════════════════════════════════════════
    ///
    /// The server settles the time away and grants everything correctly -- and the
    /// player saw none of it. ProcessAFKRewards returns null under an authoritative
    /// server, by design, so PendingSummary stayed empty and CharacterSelectScreen
    /// went straight into the game.
    ///
    /// The rewards were in the inventory. Nothing said where they came from, which
    /// makes the game silently break its central promise: you go away, you come back,
    /// and something happened while you were gone.
    ///
    /// ══ WHY IT CONVERTS RATHER THAN RECALCULATES ════════════════════════════════
    ///
    /// The numbers are the SERVER'S. This reads them across and formats them; it does
    /// not decide anything. A screen that recomputed its own totals would be a second
    /// implementation of the payout, which is exactly what the shared rules tree
    /// exists to prevent.
    /// </summary>
    public AFKRewardSummary AdoptServerSettlement(
        IdleExplorers.Backend.SettlementSnapshot settled, CharacterData character)
    {
        if (settled == null || settled.IsEmpty) return null;

        var summary = new AFKRewardSummary
        {
            elapsedSeconds = (long)settled.elapsedSeconds,
            realElapsed    = (long)settled.elapsedSeconds,
            characterName  = character?.characterName ?? "",
            skillId        = character?.currentActivity?.skillId ?? "",
            activityName   = DescribeActivity(character),
        };

        if (settled.items != null)
            foreach (var stack in settled.items)
                if (stack != null) summary.AddItem(stack.itemId, stack.quantity);

        // The settlement reports one XP total against the activity's own skill --
        // the server does not split a fishing session across several skills, because
        // one activity trains one thing.
        if (settled.xpGained > 0 && !string.IsNullOrEmpty(summary.skillId))
            summary.AddXP(summary.skillId, settled.xpGained);

        PendingSummary = summary.HasAnything ? summary : null;

        return PendingSummary;
    }

    private static string DescribeActivity(CharacterData character)
    {
        var activity = character?.currentActivity;

        if (activity == null || string.IsNullOrEmpty(activity.skillId)) return "Idling";

        var skill = GameManager.Content?.GetSkill(activity.skillId);

        return skill != null ? skill.DisplayName : activity.skillId;
    }

    /// <summary>
    /// Calculates and grants AFK rewards earned since lastLogoutUnixTime.
    /// Called by CharacterManager when a character is selected after being offline.
    /// Returns the summary (also stored as PendingSummary), or null if nothing accrued.
    /// </summary>
    /// <param name="capSeconds">
    /// Most offline time credited in this pass. Defaults to MaxAFKSeconds, which
    /// exists to stop a multi-week absence handing out quantities that trivialise the
    /// game. PURCHASED time must raise it: a 72-hour gem routed through the default
    /// cap would silently pay out 24 hours and the summary would helpfully explain
    /// that it had been capped — to someone who had just paid a thousand relic coins
    /// for the other 48.
    /// </param>
    public AFKRewardSummary ProcessAFKRewards(CharacterData character, long capSeconds = MaxAFKSeconds)
    {
        // ══ THE SERVER OWNS THIS WHEN THERE IS ONE ════════════════════════════
        //
        // This method is the 665-line offline payout the whole re-architecture set out
        // to replace: it reads a timestamp the player's own machine wrote, integrates
        // it against rates the player's own machine holds, and grants the result. That
        // is the exploit, not a step towards fixing it.
        //
        // Under an authoritative server the same integral runs in SettlementService,
        // from the DATABASE clock, and arrives through ServerState. Running both would
        // pay twice -- and the local half would be the half a cheat could edit.
        //
        // Left intact rather than deleted because offline play is still a supported
        // mode and this is the whole of it. It goes when LocalBackend does, at cutover.
        if (IdleExplorers.Backend.ServerState.IsAuthoritative) return null;

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

        long cap            = System.Math.Max(1L, capSeconds);
        long elapsedSeconds = System.Math.Min(realElapsed, cap);
        float hours         = elapsedSeconds / 3600f;

        var summary = new AFKRewardSummary
        {
            elapsedSeconds = elapsedSeconds,
            realElapsed    = realElapsed,
            wasCapped      = realElapsed > cap,
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
        // Offline time counts as full momentum — see MomentumBonus. Wrapped in a
        // try/finally so an exception mid-payout cannot leave every subsequent live
        // action permanently earning the offline rate.
        _creditingOfflineTime = true;
        try
        {
            if (activity.skillId == "combat")
                ProcessCombatAFKRewards(activity, hours, character, summary);
            else if (!string.IsNullOrEmpty(activity.recipeId))
                ProcessCraftingAFKRewards(activity, hours, summary);
            else
                ProcessGatheringAFKRewards(activity, hours, summary);
        }
        finally
        {
            _creditingOfflineTime = false;
        }

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
            if (drops <= 0) continue;

            // Record what actually landed, not what was rolled. Offline rewards used
            // to be added with no capacity check at all, so a full inventory dropped
            // them on the floor while the summary went on claiming the player had
            // received them.
            long granted = GameManager.Inventory?.AddUpTo(loot.itemId, drops) ?? 0;
            summary?.AddItem(loot.itemId, granted);
        }

        // Combat XP (from kills). Character XP follows from AddSkillXP, which derives
        // it for every skill — adding it here too would pay combat twice.
        long combatXP = monster.xpReward * totalKills;
        GameManager.Skills?.AddSkillXP("combat", combatXP);

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
        float secondsPerAction = AdjustedSeconds(ActionSeconds(activity), crafting: false, activity.skillId);
        long  totalActions     = (long)(ActionsPerHour(secondsPerAction, activity.activeRateMulti)
                                        * hours * EffectiveAfkRate(activity));
        if (totalActions <= 0) return;

        if (!string.IsNullOrEmpty(activity.activityTargetId))
        {
            // Special chance rolls (e.g. bird's nest). Expected value rather than a
            // per-action loop — see RollBulkDrops for why. Insight raises the rate the
            // same way it does live, so a stat that pays while watching also pays
            // while away — which for this game is the more important half.
            float  insight  = GameManager.Stats?.Current.insight ?? 0f;
            double jitter   = UnityEngine.Random.Range(0.9f, 1.1f);
            long   bonusQty = (long)System.Math.Max(0d,
                totalActions * activity.specialChance * (1f + insight) * jitter);

            long gathered = GameManager.Inventory?.AddUpTo(activity.activityTargetId,
                                                            totalActions + bonusQty) ?? 0;
            summary?.AddItem(activity.activityTargetId, gathered);
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

        float secondsPerAction = AdjustedSeconds(ActionSeconds(activity), crafting: true, activity.skillId);
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
        long delivered = GameManager.Inventory?.AddUpTo(recipe.outputItemId, produced) ?? 0;
        summary?.AddItem(recipe.outputItemId, delivered);

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
