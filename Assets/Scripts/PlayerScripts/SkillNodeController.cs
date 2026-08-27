using UnityEngine;

/// <summary>
/// A gatherable node in the world — a rock, a tree, a fishing spot, a forge.
///
/// Carries only a nodeId. Everything else (which skill, what it yields, the level
/// requirement, the rates) is resolved from zone_data.json at runtime, because
/// SkillNodeEntry has no position data and scenes have no rate data — each half
/// knows what the other cannot.
///
/// The live gathering tick here uses the same rates AFK accrual uses, so playing
/// actively and going AFK differ only by the multiplier.
/// </summary>
public class SkillNodeController : MonoBehaviour
{
    [Tooltip("nodeId from zone_data.json, e.g. 'copper_rock_1'")]
    public string nodeId;

    [Tooltip("How close the player must be to gather.")]
    public float interactionRange = 2.5f;

    [Tooltip("Fallback only. zone_data.json decides the real figure — see NodeSeconds.")]
    public float baseSecondsPerAction = 3f;

    /// <summary>
    /// Seconds per action, from content.
    ///
    /// The Inspector field below it is a fallback for a node the catalogue does not
    /// know about. Content wins because the SERVER reads content and cannot read a
    /// scene — and two different answers to "how long does this take" is two different
    /// answers to "what did four hours of it earn".
    /// </summary>
    private float NodeSeconds =>
        _entry != null && _entry.baseSecondsPerAction > 0.01f
            ? _entry.baseSecondsPerAction
            : baseSecondsPerAction;

    /// <summary>Ceiling on actions completed in a single frame. See TickGather.</summary>
    private const int MaxActionsPerFrame = 20;

    private SkillNodeEntry _entry;
    private float          _actionTimer;
    private bool           _isGathering;

    private WorldStatusBar _progressBar;

    public SkillNodeEntry Entry => _entry;
    public bool IsGathering => _isGathering;

    /// <summary>
    /// Seconds one action takes here, right now — after talents, class affinity and
    /// the node's own rate.
    ///
    /// Public because the progress bar and the HUD both want it, and because the
    /// figure was previously locked inside TickGather where nothing could see how
    /// long anything was going to take. Recomputed rather than cached: a talent point
    /// spent mid-session changes it, and a bar that kept filling at the old rate
    /// would be lying about the very thing the talent was bought for.
    /// </summary>
    public float SecondsPerAction
    {
        get
        {
            if (_entry == null) return Mathf.Max(0.01f, baseSecondsPerAction);

            float perAction = _recipe != null
                ? _recipe.SecondsPerCraft(GameManager.Skills?.GetSkillLevel(_recipe.skillId) ?? 1)
                : NodeSeconds;

            string workedSkill = _recipe != null ? _recipe.skillId : _entry.skillId;
            perAction = ActivityManager.AdjustedSeconds(perAction, crafting: _recipe != null, workedSkill);

            return Mathf.Max(0.01f, perAction / Mathf.Max(0.01f, _entry.activeRateMulti));
        }
    }

    /// <summary>How far through the current action, 0-1. Zero when nobody is working.</summary>
    public float Progress01 =>
        _isGathering ? Mathf.Clamp01(_actionTimer / SecondsPerAction) : 0f;

    /// <summary>
    /// True for an interactive station (bank, campfire, forge) rather than a plain
    /// gathering node. Stations open a panel on arrival instead of producing items.
    /// </summary>
    public bool IsStation => _entry != null && !string.IsNullOrEmpty(_entry.stationType);

    /// <summary>
    /// True when standing here is standing at a container rather than working.
    ///
    /// The distinction exists for the animation: an anvil and a campfire are things a
    /// character swings a hammer at or tends, and the player used to stand frozen at
    /// both because every station was treated as furniture. A bank chest genuinely is.
    /// </summary>
    public bool IsPassiveStation =>
        _entry != null && _entry.stationType == "bank";

    // ── Setup ─────────────────────────────────────────────────────────────────

    void Start()
    {
        ResolveEntry();
    }

    private void ResolveEntry()
    {
        if (_entry != null) return;

        var map = GameManager.Zone?.CurrentMap;
        if (map?.skillNodes == null)
        {
            Debug.LogWarning($"[SkillNode] '{nodeId}': no current map loaded yet.");
            return;
        }

        foreach (var candidate in map.skillNodes)
        {
            if (candidate.nodeId == nodeId) { _entry = candidate; break; }
        }

        if (_entry == null)
            Debug.LogWarning($"[SkillNode] '{nodeId}' not found in map '{map.id}' — check zone_data.json.");
        else
            name = $"Node_{_entry.skillId}_{_entry.targetItemId}";
    }

    // ── Interaction ───────────────────────────────────────────────────────────

    /// <summary>
    /// Returns true if the player may gather here. Fires an explanatory toast when
    /// they may not, so a silent no-op never leaves the player guessing.
    /// </summary>
    public bool CanGather()
    {
        ResolveEntry();
        if (_entry == null) return false;

        int level = GameManager.Skills?.GetSkillLevel(_entry.skillId) ?? 1;
        if (level < _entry.reqSkillLevel)
        {
            string skillName = GameManager.Content?.GetSkill(_entry.skillId)?.DisplayName ?? _entry.skillId;
            GameEvents.FireToast($"Requires {skillName} level {_entry.reqSkillLevel} (you are {level}).", ChatTone.Bad);
            return false;
        }
        return true;
    }

    /// <summary>Begins gathering and registers this as the character's current activity.</summary>
    public void BeginGathering()
    {
        if (_isGathering || !CanGather()) return;

        // A station is an arrival, not an activity. _isGathering is still set so the
        // player controller does not reopen the panel on the very next frame; walking
        // away calls StopGathering and re-arms it.
        if (IsStation)
        {
            _isGathering = true;
            OpenStation();
            return;
        }

        _isGathering = true;
        _actionTimer = 0f;

        var item     = GameManager.Content?.GetItem(_entry.targetItemId);
        string label = item?.DisplayName ?? _entry.targetItemId;

        // Registering the activity is what makes this node survive logout: the
        // snapshot is what AFK accrual reads on next login.
        GameManager.Activity?.SetActivity(
            skillId:          _entry.skillId,
            targetId:         _entry.targetItemId,
            targetName:       label,
            mapId:            GameManager.Zone?.CurrentMapId,
            activeRate:       _entry.activeRateMulti,
            afkRate:          _entry.afkRateMulti,
            specialChance:    _entry.specialChance,
            specialLabel:     _entry.specialLabel,
            xpPerHour:        XpPerHour(),
            secondsPerAction: NodeSeconds);

        GameEvents.OnSkillNodeInteracted?.Invoke(nodeId);
    }

    // ── Crafting ──────────────────────────────────────────────────────────────

    private CraftRecipe _recipe;

    /// <summary>The recipe selected at this station, if any.</summary>
    public CraftRecipe Recipe => _recipe;

    /// <summary>True while a station is actively working a recipe.</summary>
    public bool IsCrafting => _recipe != null && _isGathering;

    /// <summary>
    /// Starts producing a recipe here and registers it as the current activity, so it
    /// keeps running while logged out.
    /// </summary>
    public void BeginCrafting(CraftRecipe recipe)
    {
        if (recipe == null || _entry == null) return;

        int level = GameManager.Skills?.GetSkillLevel(recipe.skillId) ?? 1;
        if (level < recipe.reqSkillLevel)
        {
            string skillName = GameManager.Content?.GetSkill(recipe.skillId)?.DisplayName ?? recipe.skillId;
            GameEvents.FireToast($"Requires {skillName} level {recipe.reqSkillLevel} (you are {level}).", ChatTone.Bad);
            return;
        }

        _recipe      = recipe;
        _isGathering = true;
        _actionTimer = 0f;

        float secondsPerCraft = recipe.SecondsPerCraft(level);
        float craftsPerHour   = ActivityManager.ActionsPerHour(secondsPerCraft, _entry.activeRateMulti);

        GameManager.Activity?.SetActivity(
            skillId:          recipe.skillId,
            targetId:         recipe.outputItemId,
            targetName:       recipe.DisplayName,
            mapId:            GameManager.Zone?.CurrentMapId,
            activeRate:       _entry.activeRateMulti,
            afkRate:          _entry.afkRateMulti,
            specialChance:    0f,
            specialLabel:     "",
            xpPerHour:        craftsPerHour * recipe.xpPerCraft,
            recipeId:         recipe.id,
            secondsPerAction: secondsPerCraft);

        // Persist immediately: a crash between choosing a recipe and the first craft
        // would otherwise resume the previous activity on next login.
        GameManager.Save?.Save();

        GameEvents.OnSkillNodeInteracted?.Invoke(nodeId);
    }

    /// <summary>
    /// One craft: consume every input, produce the output, grant XP.
    /// Returns false when an input ran out, which stops the loop.
    /// </summary>
    private bool PerformCraft()
    {
        if (_recipe?.inputs == null) return false;

        // Report which input ran out before attempting anything, so the toast names
        // the actual blocker rather than just failing.
        foreach (var input in _recipe.inputs)
        {
            if (input == null) continue;
            if (CraftingSupply.Available(input.itemId) >= input.quantity) continue;

            var item = GameManager.Content?.GetItem(input.itemId);
            GameEvents.FireToast($"Out of {item?.DisplayName ?? input.itemId}.", ChatTone.Bad);
            return false;
        }

        // Procs multiply the output, never the inputs — doubling a craft must not
        // also double what it cost. Talent double-output is a genuine chance here
        // rather than expected value: a live craft produces a whole item or it does
        // not, and the offline path uses the average of exactly this.
        float multiplier = ItemEffectResolver.AggregateMultiplier("onCraft", "doubleOutput", _recipe.skillId);
        if (Random.value < TalentManager.Bonus(TalentManager.CraftDoubleChance)) multiplier += 1f;

        long produced = (long)Mathf.Max(1f, _recipe.outputQuantity * multiplier);

        // Room is checked for the WHOLE output, before anything is spent. A recipe
        // that doubles can produce more than one slot's remaining space, and finding
        // that out after consuming the inputs would charge for a craft that vanished.
        if (GameManager.Inventory?.CanAddItem(_recipe.outputItemId, produced) != true)
        {
            GameEvents.FireToast("Inventory full.", ChatTone.Warning);
            return false;
        }

        // All-or-nothing: verifies again and only then spends, so nothing is consumed
        // unless every input is affordable.
        if (!CraftingSupply.ConsumeFor(_recipe, 1)) return false;

        GameManager.Inventory.AddItem(_recipe.outputItemId, produced);
        GameManager.Skills?.AddSkillXP(_recipe.skillId, (long)_recipe.xpPerCraft);

        GameManager.Audio?.Play(GatherSound(_recipe.skillId));
        ItemEffectResolver.Fire("onCraft", _recipe.skillId);
        return true;
    }

    public void StopGathering()
    {
        _isGathering = false;
        _actionTimer = 0f;
        _recipe      = null;

        // The bar describes an activity, so it goes when the activity does. Hidden
        // rather than destroyed: the player will very likely be back in a second.
        _progressBar?.SetVisible(false);
    }

    /// <summary>Opens whatever UI this station fronts.</summary>
    private void OpenStation()
    {
        switch (_entry.stationType)
        {
            case "bank":
                GameManager.UI?.Push<BankPanel>();
                break;

            default:
                // Crafting stations (campfire, forge) resolve their recipe list from
                // stationType, so one panel serves all of them.
                CraftingPanel.Station = this;
                GameManager.UI?.Push<CraftingPanel>();
                break;
        }
    }

    /// <summary>
    /// Draws how far through the current action this node is.
    ///
    /// An idle game spends most of its time waiting for a timer nobody can see. A
    /// three-second rock with no bar is indistinguishable from a broken one for the
    /// first three seconds, every time, and the player learns to distrust it.
    ///
    /// AutoHide is off: this describes an activity rather than reporting damage, so
    /// it should stay up for as long as the activity does. StopGathering hides it.
    /// </summary>
    private void ShowProgress()
    {
        if (_progressBar == null)
        {
            _progressBar = WorldStatusBar.Attach(gameObject, NodeBarHeight, UIManager.Theme.xpFill);
            if (_progressBar == null) return;

            _progressBar.AutoHide = false;

            // The bar IS the minigame's timeline, so the game attaches where the bar
            // does. Only for gathering nodes and stations -- a bank chest has no
            // action to time against.
            if (!IsPassiveStation) MinigameBar.Attach(this, _progressBar);
        }

        _progressBar.SetVisible(true);
        _progressBar.SetFraction(Progress01, ProgressLabel());
    }

    /// <summary>
    /// What the bar says above itself: the thing being produced, and how often.
    ///
    /// The rate is the half that matters. A player choosing between two rocks is
    /// choosing between two rates, and until now the only way to compare them was to
    /// stand at each one and count.
    /// </summary>
    private string ProgressLabel()
    {
        string produced = _recipe != null
            ? GameManager.Content?.GetItem(_recipe.outputItemId)?.DisplayName ?? _recipe.DisplayName
            : GameManager.Content?.GetItem(_entry.targetItemId)?.DisplayName ?? _entry.targetItemId;

        float perHour = 3600f / Mathf.Max(0.01f, SecondsPerAction);

        return $"{produced}  ·  {perHour:0}/h";
    }

    /// <summary>
    /// Above a node rather than above a character, so it is a fixed height: rocks,
    /// trees and anvils are all authored around the same size and none of them has a
    /// SPUM rig to measure.
    /// </summary>
    private const float NodeBarHeight = 2.1f;

    /// <summary>Called each frame by PlayerController while it is parked at this node.</summary>
    public void TickGather(float deltaTime)
    {
        if (!_isGathering || _entry == null) return;

        ShowProgress();

        // One definition, shared with the progress bar. Speed talents are applied
        // through the same helper offline accrual uses, so a talent cannot make
        // active play faster than the AFK figure it advertises.
        float secondsPerAction = SecondsPerAction;
        _actionTimer += deltaTime;

        // Bounded, because the loop count is (deltaTime / secondsPerAction) and both
        // sides of that come from JSON. A node authored with a large activeRateMulti,
        // or a single long frame after a stall, would otherwise run thousands of
        // crafts inside one frame and make the hitch worse.
        int guard = 0;
        while (_actionTimer >= secondsPerAction && guard++ < MaxActionsPerFrame)
        {
            _actionTimer -= secondsPerAction;

            if (_recipe != null)
            {
                // Running dry stops the station and clears the activity, so the
                // character card reads Idle rather than claiming to still be cooking.
                if (!PerformCraft())
                {
                    StopGathering();
                    GameManager.Activity?.ClearActivity();
                    return;
                }
            }
            else
            {
                PerformAction();
            }
        }
    }

    private void PerformAction()
    {
        // Some nodes (convergence, spectral work) grant XP but no item yet
        if (!string.IsNullOrEmpty(_entry.targetItemId))
        {
            long qty = 1;

            // Special roll — the bird's nest / treasure casket moment. Insight is what
            // makes it happen more often; drop quantity would only make it bigger.
            float insight = GameManager.Stats?.Current.insight ?? 0f;
            float chance  = _entry.specialChance * (1f + insight);

            if (_entry.specialChance > 0f && Random.value <= chance)
            {
                qty += 1;
                if (!string.IsNullOrEmpty(_entry.specialLabel))
                    GameEvents.FireToast($"✦ {_entry.specialLabel}!", ChatTone.Good);
            }

            if (GameManager.Inventory?.CanAddItem(_entry.targetItemId, qty) == true)
            {
                GameManager.Inventory.AddItem(_entry.targetItemId, qty);
                GameEvents.FireItemPickedUp(_entry.targetItemId, qty);
            }
            else
            {
                GameEvents.FireToast("Inventory full.", ChatTone.Warning);
                StopGathering();
                return;
            }
        }

        GameManager.Skills?.AddSkillXP(_entry.skillId, (long)_entry.xpPerAction);
        GameManager.Audio?.Play(GatherSound(_entry.skillId));
        ItemEffectResolver.Fire("onGather", _entry.skillId);
    }

    /// <summary>
    /// The noise a gathering action makes. Falls back to the generic craft sound
    /// rather than silence, so a skill added later is audible before it is bespoke.
    /// </summary>
    private static string GatherSound(string skillId) => skillId switch
    {
        "mining"      => Sfx.Mine,
        "woodcutting" => Sfx.Chop,
        "smithing"    => Sfx.Smith,
        _             => Sfx.Craft,
    };

    /// <summary>XP/hour at the active rate — shown in the HUD and used for AFK accrual.</summary>
    private float XpPerHour()
    {
        if (_entry == null) return 0f;
        float secondsPerAction = NodeSeconds / Mathf.Max(0.01f, _entry.activeRateMulti);
        float actionsPerHour   = 3600f / Mathf.Max(0.01f, secondsPerAction);
        return actionsPerHour * _entry.xpPerAction;
    }

    // ── Gizmo so the interaction radius is visible while placing nodes ────────

    void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(0.2f, 0.85f, 0.4f, 0.35f);
        Gizmos.DrawWireSphere(transform.position, interactionRange);
    }
}
