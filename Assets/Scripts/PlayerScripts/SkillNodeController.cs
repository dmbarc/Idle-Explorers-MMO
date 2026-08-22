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

    [Tooltip("Seconds per gather action at 1.0x rate.")]
    public float baseSecondsPerAction = 3f;

    private SkillNodeEntry _entry;
    private float          _actionTimer;
    private bool           _isGathering;

    public SkillNodeEntry Entry => _entry;
    public bool IsGathering => _isGathering;

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
            GameEvents.FireToast($"Requires {skillName} level {_entry.reqSkillLevel} (you are {level}).");
            return false;
        }
        return true;
    }

    /// <summary>Begins gathering and registers this as the character's current activity.</summary>
    public void BeginGathering()
    {
        if (_isGathering || !CanGather()) return;

        _isGathering = true;
        _actionTimer = 0f;

        var item     = GameManager.Content?.GetItem(_entry.targetItemId);
        string label = item?.DisplayName ?? _entry.targetItemId;

        // Registering the activity is what makes this node survive logout: the
        // snapshot is what AFK accrual reads on next login.
        GameManager.Activity?.SetActivity(
            skillId:       _entry.skillId,
            targetId:      _entry.targetItemId,
            targetName:    label,
            mapId:         GameManager.Zone?.CurrentMapId,
            activeRate:    _entry.activeRateMulti,
            afkRate:       _entry.afkRateMulti,
            specialChance: _entry.specialChance,
            specialLabel:  _entry.specialLabel,
            xpPerHour:     XpPerHour());

        GameEvents.OnSkillNodeInteracted?.Invoke(nodeId);
    }

    public void StopGathering()
    {
        _isGathering = false;
        _actionTimer = 0f;
    }

    /// <summary>Called each frame by PlayerController while it is parked at this node.</summary>
    public void TickGather(float deltaTime)
    {
        if (!_isGathering || _entry == null) return;

        float secondsPerAction = baseSecondsPerAction / Mathf.Max(0.01f, _entry.activeRateMulti);
        _actionTimer += deltaTime;

        while (_actionTimer >= secondsPerAction)
        {
            _actionTimer -= secondsPerAction;
            PerformAction();
        }
    }

    private void PerformAction()
    {
        // Some nodes (convergence, spectral work) grant XP but no item yet
        if (!string.IsNullOrEmpty(_entry.targetItemId))
        {
            long qty = 1;

            // Special roll — the bird's nest / treasure casket moment
            if (_entry.specialChance > 0f && Random.value <= _entry.specialChance)
            {
                qty += 1;
                if (!string.IsNullOrEmpty(_entry.specialLabel))
                    GameEvents.FireToast($"✦ {_entry.specialLabel}!");
            }

            if (GameManager.Inventory?.CanAddItem(_entry.targetItemId) == true)
            {
                GameManager.Inventory.AddItem(_entry.targetItemId, qty);
                GameEvents.FireItemPickedUp(_entry.targetItemId, qty);
            }
            else
            {
                GameEvents.FireToast("Inventory full.");
                StopGathering();
                return;
            }
        }

        GameManager.Skills?.AddSkillXP(_entry.skillId, (long)_entry.xpPerAction);
    }

    /// <summary>XP/hour at the active rate — shown in the HUD and used for AFK accrual.</summary>
    private float XpPerHour()
    {
        if (_entry == null) return 0f;
        float secondsPerAction = baseSecondsPerAction / Mathf.Max(0.01f, _entry.activeRateMulti);
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
