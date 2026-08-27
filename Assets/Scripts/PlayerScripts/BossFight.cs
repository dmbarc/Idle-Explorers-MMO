using System;
using System.Collections.Generic;
using IdleExplorers.Backend;
using IdleExplorers.Rules;
using UnityEngine;

/// <summary>
/// The client's side of the boss conversation.
///
/// ══ WHY THIS IS NOT IN BossController ═════════════════════════════════════════
///
/// Because they answer to different authorities, and mixing them is how a
/// presentation change quietly becomes a rules change.
///
/// BossController owns the picture: where the King is standing, which telegraph is
/// on the ground, what the animator is doing, whether the health bar has finished
/// tweening. All of it is client-authoritative and none of it pays anything.
///
/// This owns the fight: what the server says the health is, when the enrage lands,
/// what the swings were worth. None of it is decided here either -- it is asked for.
///
/// Keeping them apart means the whole of BossController can be rewritten for how the
/// fight LOOKS without anybody having to think about whether they just changed how
/// much damage a swing does.
///
/// ══ WHAT IS SENT AND WHAT IS NOT ══════════════════════════════════════════════
///
/// Sent: "I swung, and it was action number 412." That is all.
///
/// Not sent: damage, the boss's health, the player's health, positions, timings,
/// whether anything was dodged. Every one of those would be a claim the server has to
/// trust or ignore, and the server can compute all of them better than it can check
/// them -- from a snapshot frozen at engage and its own clock.
///
/// ══ WHY THE CLIENT PREDICTS AT ALL ════════════════════════════════════════════
///
/// Because a health bar that only moves twice a second is a health bar that feels
/// broken. So damage is predicted locally through the SAME shared rules the server
/// runs, and every report reconciles the prediction to the authoritative number.
/// The two agree except at the edges, and where they disagree the server wins
/// without argument.
/// </summary>
public class BossFight : MonoBehaviour
{
    /// <summary>
    /// Seconds between action reports.
    ///
    /// Twice a second. Often enough that the health bar never drifts far from the
    /// truth, rare enough that a five minute fight is six hundred requests rather
    /// than a request per swing.
    /// </summary>
    public const float ReportEvery = 0.5f;

    /// <summary>
    /// Swings held before a report is forced early.
    ///
    /// The server refuses a larger batch outright rather than truncating it, so this
    /// has to stay under its limit -- and it is the same constant, from the shared
    /// rules, so the two cannot drift apart.
    /// </summary>
    public const int MaxHeld = BossEncounter.MaxActionsPerRequest;

    /// <summary>The fight, as the server described it at engage. Null before then.</summary>
    public EncounterSnapshot Encounter { get; private set; }

    /// <summary>True once the server has confirmed the fight is running.</summary>
    public bool IsLive => Encounter != null && Encounter.Started && !_finished;

    /// <summary>
    /// The health to draw.
    ///
    /// The predicted value between reports, replaced by the server's on every report.
    /// Never the other way round: a prediction that overwrote an authoritative number
    /// would be a client deciding the boss's health.
    /// </summary>
    public double BossHealth { get; private set; }

    public double BossMaxHealth => Encounter?.bossMaxHp ?? 0d;

    public float HealthFraction =>
        BossMaxHealth > 0d ? (float)Math.Max(0d, BossHealth / BossMaxHealth) : 0f;

    /// <summary>Seconds until the King gives up. Counted from the server's own start.</summary>
    public float EnrageRemaining =>
        Encounter == null ? 0f
                          : Mathf.Max(0f, (float)(Encounter.enrageSeconds - Elapsed));

    /// <summary>Seconds since the server started the fight.</summary>
    public double Elapsed => Time.time - _startedAt;

    /// <summary>Which phase the server last confirmed. Drives the pips on the bar.</summary>
    public int PhaseIndex { get; private set; }

    /// <summary>Raised when the server confirms a phase change, never on a prediction.</summary>
    public event Action<int> PhaseChanged;

    /// <summary>Raised once, with the server's verdict.</summary>
    public event Action<EncounterResult> Finished;

    private readonly List<BossActionReport> _held = new();

    private float  _startedAt;
    private float  _nextReportAt;
    private long   _sequence;
    private bool   _finished;
    private bool   _reporting;
    private string _characterId;

    // ── Starting ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Asks the server to start the fight.
    ///
    /// Returns null when it refused -- a sealed portal, a fight already running, the
    /// feature switched off, or no server at all. The caller shows the reason rather
    /// than dropping the player into an arena with an inert King.
    /// </summary>
    public async Awaitable<EncounterSnapshot> EngageAsync(string monsterId)
    {
        _characterId = CharacterManager.Current?.characterId;

        if (string.IsNullOrEmpty(_characterId)) return null;

        try
        {
            EncounterSnapshot fight =
                await GameBackend.Current.EngageBossAsync(_characterId, monsterId);

            if (fight == null || !fight.Started) return null;

            Encounter  = fight;
            BossHealth = fight.bossMaxHp;

            // The server's own elapsed, folded in, so a reconnect mid-fight lines the
            // timeline up with a King that has been swinging for ninety seconds.
            _startedAt    = Time.time - (float)fight.elapsedSeconds;
            _nextReportAt = Time.time + ReportEvery;

            _sequence  = 0L;
            _finished  = false;
            PhaseIndex = 0;

            return fight;
        }
        catch (BackendException e)
        {
            GameEvents.FireToast(e.Title, ChatTone.Bad);
            return null;
        }
    }

    // ── Swinging ──────────────────────────────────────────────────────────────

    /// <summary>
    /// "The player just attacked."
    ///
    /// Called by the attack loop, which already knows when a swing lands. Predicts the
    /// damage locally so the bar moves now, and holds the action for the next report.
    /// </summary>
    /// <param name="abilityId">Empty for an ordinary swing.</param>
    public void Swung(string abilityId = "")
    {
        if (!IsLive) return;

        _held.Add(new BossActionReport { sequence = ++_sequence, abilityId = abilityId ?? "" });

        // Predicted through the SAME function the server runs, at the SAME index, so
        // the two agree rather than merely resembling each other. Without a shared
        // implementation this is where a client and a server start disagreeing about
        // a health bar and nobody can say which is wrong.
        var rng = new CounterRandom(0L, (ulong)_sequence);

        double swing = BossEncounter.SwingDamage(
            Encounter.frozenDps, Encounter.frozenAttackSeconds,
            AbilityMultiplier(abilityId), Encounter.armor, rng);

        BossHealth = Math.Max(0d, BossHealth - swing);

        if (_held.Count >= MaxHeld) Report();
    }

    private void Update()
    {
        if (!IsLive) return;

        if (Time.time >= _nextReportAt)
        {
            _nextReportAt = Time.time + ReportEvery;

            // Reported even with nothing held, because the answer carries the enrage
            // clock and the phase -- a player who stops attacking must still find out
            // that the King gave up.
            Report();
        }
    }

    /// <summary>
    /// Sends what is held and reconciles to the answer.
    ///
    /// Fire and forget with an in-flight guard: overlapping reports would deliver
    /// sequences out of order, and the server rejects anything that does not increase.
    /// </summary>
    private async void Report()
    {
        if (_reporting || !IsLive) return;

        _reporting = true;

        var sending = _held.ToArray();
        _held.Clear();

        try
        {
            EncounterTick tick =
                await GameBackend.Current.ReportBossActionsAsync(_characterId, sending);

            if (tick == null) return;

            Reconcile(tick);
        }
        catch (BackendException e)
        {
            // Swallowed. A dropped report costs the actions it carried, which the
            // ceiling would mostly have refused anyway -- and an exception out of an
            // async void takes the frame with it.
            Debug.LogWarning($"[Boss] Report of {sending.Length} action(s) failed: {e.Message}");
        }
        finally
        {
            _reporting = false;
        }
    }

    /// <summary>
    /// The server's numbers replace the predicted ones. Always, in that direction.
    /// </summary>
    private void Reconcile(EncounterTick tick)
    {
        BossHealth = tick.bossHp;

        if (tick.phase != PhaseIndex)
        {
            PhaseIndex = tick.phase;
            PhaseChanged?.Invoke(PhaseIndex);
        }

        // Its clock, not ours. A tab that was suspended for thirty seconds has a
        // Time.time that says nothing happened, and the enrage timer must not care.
        _startedAt = Time.time - (float)tick.elapsedSeconds;

        if (tick.clamped)
        {
            // Not shown to the player -- being told "the server did not believe you"
            // is alarming and, at the start of a fight, wrong. Logged, because a
            // client seeing this repeatedly has a real bug.
            Debug.Log("[Boss] Damage was clamped to what the clock allows.");
        }

        if (tick.dead || tick.enraged) Finish();
    }

    // ── Ending ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Asks the server how it went.
    ///
    /// The client never decides. It has a predicted health that says the King is
    /// down, and that prediction is not evidence -- the verdict comes from the
    /// server's own value, computed from damage it credited itself.
    /// </summary>
    public async void Finish()
    {
        if (_finished || Encounter == null) return;

        _finished = true;

        try
        {
            EncounterResult result = await GameBackend.Current.ResolveBossAsync(_characterId);

            if (result == null) return;

            Finished?.Invoke(result);

            if (!result.won)
            {
                GameEvents.FireToast("The King tires of you.", ChatTone.Bad);
                return;
            }

            GameEvents.FireToast($"✦ The King falls. {result.xpGained:N0} xp.", ChatTone.Good);

            await ClaimAsync();
        }
        catch (BackendException e)
        {
            // The fight IS resolved server-side whether or not this call got through,
            // so nothing is lost -- the loot is sitting in pending_loot and the next
            // claim collects it. Worth saying so, because a player who saw the King
            // fall and got nothing will otherwise assume the worst.
            GameEvents.FireToast("Could not collect just now — your loot is safe.", ChatTone.Warning);

            Debug.LogWarning($"[Boss] Resolve failed: {e.Message}");
        }
    }

    /// <summary>
    /// Collects what the fight earned.
    ///
    /// Separate from resolving, because earning and collecting are separate: the first
    /// is decided once and cannot be undone, the second is retryable forever. A full
    /// bag delays the loot rather than destroying it, and says so.
    /// </summary>
    public async Awaitable ClaimAsync()
    {
        if (string.IsNullOrEmpty(_characterId)) return;

        try
        {
            LootClaim claim = await GameBackend.Current.ClaimLootAsync(_characterId);

            if (claim?.claimed != null)
            {
                foreach (var stack in claim.claimed)
                    if (stack != null) GameEvents.FireItemPickedUp(stack.itemId, stack.quantity);
            }

            if (claim != null && claim.bagWasFull)
            {
                GameEvents.FireToast("Your bag is full — the rest is waiting for you.",
                                     ChatTone.Warning);
            }
        }
        catch (BackendException e)
        {
            Debug.LogWarning($"[Boss] Claim failed: {e.Message}");
        }
    }

    // ── The schedule ──────────────────────────────────────────────────────────

    /// <summary>
    /// What the King does during a phase, and when.
    ///
    /// Read straight from the engage response, which is why the telegraphs never wait
    /// on a packet. Empty when there is no fight or the phase is out of range, so a
    /// caller loops over nothing rather than checking.
    /// </summary>
    public BossCast[] CastsFor(int phaseIndex)
    {
        if (Encounter?.phases == null) return Array.Empty<BossCast>();

        foreach (var phase in Encounter.phases)
            if (phase != null && phase.index == phaseIndex)
                return phase.casts ?? Array.Empty<BossCast>();

        return Array.Empty<BossCast>();
    }

    /// <summary>The phase the server described, for the name and the adds.</summary>
    public EncounterPhase PhaseAt(int phaseIndex)
    {
        if (Encounter?.phases == null) return null;

        foreach (var phase in Encounter.phases)
            if (phase != null && phase.index == phaseIndex) return phase;

        return null;
    }

    /// <summary>
    /// The damage multiplier for an ability, from the client's own content copy.
    ///
    /// Only ever used to PREDICT. If the client's content is stale its prediction is
    /// wrong and the next report corrects it -- which is exactly the failure this
    /// architecture is meant to have, rather than a wrong number being paid out.
    /// </summary>
    private float AbilityMultiplier(string abilityId)
    {
        if (string.IsNullOrEmpty(abilityId)) return 1f;

        MonsterData boss = GameManager.Content?.GetMonster(Encounter?.monsterId);

        if (boss?.phases == null) return 1f;

        foreach (var phase in boss.phases)
        {
            if (phase?.abilities == null) continue;

            foreach (var ability in phase.abilities)
                if (ability != null && ability.id == abilityId) return ability.damageMultiplier;
        }

        return 1f;
    }
}
