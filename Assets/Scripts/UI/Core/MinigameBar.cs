using System;
using System.Collections.Generic;
using IdleExplorers.Backend;
using IdleExplorers.Rules;
using UnityEngine;

/// <summary>
/// The two minigames, both built on the progress bar that was already there.
///
/// ══ WHY THEY SHARE ONE COMPONENT ══════════════════════════════════════════════
///
/// Fault Line and Strike the Beat are the same interaction with different framing:
/// a marker crosses the action's own progress bar, there is a window somewhere on
/// it, and pressing inside the window is worth something. Mining calls the window a
/// seam; smithing calls it a beat and asks for three in a row.
///
/// Building them as two systems would mean two timing implementations, two grading
/// paths and two chances to disagree with the server about what "perfect" means.
///
/// ══ WHY IT IS THE SKILLING BAR AND NOT A NEW PANEL ════════════════════════════
///
/// The progress bar above the node already shows how far through an action the
/// character is, and that IS the minigame's timeline. A separate panel would ask
/// the player to watch two representations of the same clock, and to look away from
/// the world to play a game about the world.
///
/// ══ WHAT IT SENDS ═════════════════════════════════════════════════════════════
///
/// Grades. Never a reward, never a count of ore. It collects them locally and hands
/// them to the backend in batches, and the server decides what they were worth
/// against actions its own clock produced -- see Minigame in the shared rules for
/// the whole argument, including what a scripted client gets.
/// </summary>
public class MinigameBar : MonoBehaviour
{
    /// <summary>
    /// How many graded actions are held before reporting.
    ///
    /// Batched because a request per swing would spend more time on the network than
    /// on the game, and bounded by what the server accepts. Twenty at three seconds
    /// an action is a minute of play, which is short enough that a player who stops
    /// mid-run loses very little.
    /// </summary>
    private const int BatchSize = 20;

    /// <summary>Seconds of inactivity after which a part-full batch is sent anyway.</summary>
    private const float FlushAfterIdle = 8f;

    /// <summary>
    /// Where the window sits within the action, as a fraction.
    ///
    /// Re-rolled every action, so the rhythm cannot be learned as a metronome and
    /// played with the eyes shut. Kept away from the very start and end because a
    /// window at zero is unhittable by anyone with a reaction time.
    /// </summary>
    private const float EarliestTarget = 0.25f;
    private const float LatestTarget   = 0.85f;

    /// <summary>Perfect strikes needed in a row for smithing's combo.</summary>
    public const int ComboLength = 3;

    private SkillNodeController _node;
    private WorldStatusBar      _bar;

    private readonly List<MinigameGrade> _pending = new();

    private float _target;
    private bool  _struckThisAction;
    private int   _actionsSeen;
    private float _lastReportAt;
    private int   _combo;

    /// <summary>What the player is currently on course for. Purely for the HUD.</summary>
    public int Combo => _combo;

    /// <summary>Where the window is, 0-1 along the current action.</summary>
    public float Target => _target;

    /// <summary>Half-width of the hit window, 0-1. Drawn by the bar.</summary>
    public float Window => Minigame.GoodWindow * 0.5f;

    /// <summary>
    /// True while a minigame is available: the character is working a node that has
    /// one, and the game is not showing a modal over the top of it.
    /// </summary>
    public bool IsPlayable =>
        _node != null && _node.IsGathering && !UIManager.WorldInputBlocked;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    /// <summary>Attaches to a node's own progress bar. Idempotent.</summary>
    public static MinigameBar Attach(SkillNodeController node, WorldStatusBar bar)
    {
        if (node == null) return null;

        var existing = node.GetComponent<MinigameBar>();
        if (existing != null) return existing;

        var game = node.gameObject.AddComponent<MinigameBar>();
        game._node = node;
        game._bar  = bar;

        game.NextAction();

        return game;
    }

    private void Update()
    {
        if (!IsPlayable)
        {
            // Stopped working, so whatever is held goes now rather than waiting for
            // a batch that will never fill.
            Flush();

            if (_bar != null) _bar.ShowWindow(0f, 0f);
            return;
        }

        DetectNewAction();
        ReadInput();

        // Redrawn every frame rather than on change, because the target moves every
        // action and a stale marker is worse than none -- the player would time
        // against a window that is no longer there.
        if (_bar != null) _bar.ShowWindow(_target, Window);

        if (_pending.Count >= BatchSize) Flush();
        else if (_pending.Count > 0 && Time.time - _lastReportAt > FlushAfterIdle) Flush();
    }

    /// <summary>
    /// Notices when the node has rolled over into a new action.
    ///
    /// Watched rather than driven, because the ACTION is the node's business and the
    /// minigame is a passenger on it. A minigame that advanced its own clock would
    /// drift from the thing it is grading within a minute.
    /// </summary>
    private void DetectNewAction()
    {
        float progress = _node.Progress01;

        // Progress running backwards means the node completed an action and started
        // the next -- there is no other way for it to decrease.
        if (progress >= _lastProgress)
        {
            _lastProgress = progress;
            return;
        }

        // An action the player let pass without striking is a miss, recorded so the
        // server sees an honest run rather than only the good parts.
        if (!_struckThisAction) _pending.Add(MinigameGrade.Miss);

        _lastProgress = progress;
        NextAction();
    }

    private float _lastProgress;

    private void NextAction()
    {
        _struckThisAction = false;
        _actionsSeen++;

        _target = UnityEngine.Random.Range(EarliestTarget, LatestTarget);
    }

    private void ReadInput()
    {
        if (_struckThisAction) return;
        if (!StrikePressed()) return;

        _struckThisAction = true;

        MinigameGrade grade = Minigame.GradeFor(_node.Progress01 - _target);
        _pending.Add(grade);

        if (grade == MinigameGrade.Perfect)
        {
            _combo++;

            // Smithing's combo. Announced rather than silent, because a chain the
            // player cannot see is a chain they cannot try to keep.
            if (_combo % ComboLength == 0)
                GameEvents.FireToast($"✦ {_combo} perfect in a row.", ChatTone.Good);
        }
        else
        {
            _combo = 0;
        }

        Feedback(grade);
    }

    /// <summary>
    /// One key, and it is the one already bound to interacting.
    ///
    /// A minigame with its own key is a minigame players discover in the options
    /// screen. Space is what a player already presses at a node.
    /// </summary>
    private static bool StrikePressed()
    {
        var keyboard = UnityEngine.InputSystem.Keyboard.current;

        return keyboard != null && keyboard.spaceKey.wasPressedThisFrame;
    }

    private void Feedback(MinigameGrade grade)
    {
        Vector3 at = transform.position + Vector3.up * 2.2f;

        switch (grade)
        {
            case MinigameGrade.Perfect:
                AbilityVFX.Play("impact", at);
                GameManager.Audio?.Play(Sfx.SetProc);
                break;

            case MinigameGrade.Good:
                GameManager.Audio?.PlayHit();
                break;

            default:
                // Nothing for a miss. A sound that punishes a mistake in an idle game
                // teaches the player to stop playing the optional part.
                break;
        }
    }

    // ── Reporting ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Hands the collected grades to the backend.
    ///
    /// Fire and forget, deliberately. The bonus lands in the next state read and
    /// nothing on screen waits for it -- a minigame that stalls the game while it
    /// reports would be worse than no minigame.
    /// </summary>
    private async void Flush()
    {
        if (_pending.Count == 0) return;

        var grades = _pending.ToArray();
        _pending.Clear();
        _lastReportAt = Time.time;

        var backend = GameBackend.Current;
        string characterId = CharacterManager.Current?.characterId;

        if (backend == null || string.IsNullOrEmpty(characterId)) return;

        try
        {
            await backend.ReportMinigameAsync(characterId, Wire(grades));
        }
        catch (Exception e)
        {
            // Swallowed. A dropped report costs the player a small bonus; an
            // exception out of an async void would take the frame with it.
            Debug.LogWarning($"[Minigame] Could not report {grades.Length} grade(s): {e.Message}");
        }
    }

    private static string[] Wire(MinigameGrade[] grades)
    {
        var wire = new string[grades.Length];

        for (int i = 0; i < grades.Length; i++)
        {
            wire[i] = grades[i] switch
            {
                MinigameGrade.Perfect => "perfect",
                MinigameGrade.Good    => "good",
                _                     => "miss",
            };
        }

        return wire;
    }

    private void OnDisable() => Flush();
}
