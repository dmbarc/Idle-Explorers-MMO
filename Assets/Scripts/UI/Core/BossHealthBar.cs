using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The boss bar across the top of the screen.
///
/// ══ WHY IT IS NOT THE FLOATING BAR, LARGER ════════════════════════════════════
///
/// The world-space bar above a monster answers "how is this one doing" while you are
/// looking at it. During a boss fight the player is looking at the ground, at a
/// telegraph, at where they are running to — anywhere but the boss. A bar that lives
/// in a fixed place is a bar they can read without deciding to.
///
/// It also carries two things the floating one has no concept of: the phase pips,
/// so a player can see a threshold coming rather than being surprised by it, and the
/// enrage clock, which is the actual fail condition and therefore the actual tension.
///
/// ══ IT LISTENS RATHER THAN POLLS ══════════════════════════════════════════════
///
/// Driven entirely by GameEvents. Nothing holds a reference to the boss, so a boss
/// that despawns, a scene that unloads, or a fight that never starts all leave this
/// hidden and harmless rather than dereferencing something gone.
/// </summary>
public class BossHealthBar : MonoBehaviour
{
    private const float BarWidth  = 720f;
    private const float BarHeight = 26f;

    /// <summary>Seconds the chip bar takes to catch up. Slower than a monster's.</summary>
    private const float ChipSpeed = 0.25f;

    /// <summary>Below this many seconds the clock turns urgent.</summary>
    private const float UrgentSeconds = 30f;

    private GameObject _root;
    private Image      _fill;
    private Image      _chip;
    private TMP_Text   _name;
    private TMP_Text   _phase;
    private TMP_Text   _clock;

    private readonly List<Image> _pips = new();

    private float _value = 1f;
    private float _chipValue = 1f;
    private float _enrageEndsAt = -1f;

    private void OnEnable()
    {
        GameEvents.OnBossEngaged       += OnEngaged;
        GameEvents.OnBossHealthChanged += OnHealth;
        GameEvents.OnBossPhaseChanged  += OnPhase;
        GameEvents.OnBossDefeated      += OnEnded;
        GameEvents.OnBossEnraged       += OnEnrageEnded;
    }

    private void OnDisable()
    {
        GameEvents.OnBossEngaged       -= OnEngaged;
        GameEvents.OnBossHealthChanged -= OnHealth;
        GameEvents.OnBossPhaseChanged  -= OnPhase;
        GameEvents.OnBossDefeated      -= OnEnded;
        GameEvents.OnBossEnraged       -= OnEnrageEnded;
    }

    // ── Reacting ──────────────────────────────────────────────────────────────

    private void OnEngaged(string bossName, float maxHealth, float enrageSeconds)
    {
        Build();

        _name.text  = bossName;
        _phase.text = "";
        _value      = 1f;
        _chipValue  = 1f;

        // ══ THE CLOCK IS THE FIGHT'S, NOT THE CATALOGUE'S ═══════════════════
        //
        // This used to read enrageSeconds out of the monster file and start a fresh
        // countdown. That is right for whoever opened the door and wrong for everybody
        // who followed them in: a group fight has ONE clock, and the third person
        // through it has however much of it is left, not all of it.
        _enrageEndsAt = enrageSeconds > 0f ? Time.time + enrageSeconds : -1f;

        var monster = GameManager.Content?.GetMonster(GameManager.Zone?.CurrentMap?.defaultMonsterId);

        BuildPips(monster);

        _root.SetActive(true);
    }

    private void OnHealth(float fraction)
    {
        _value = Mathf.Clamp01(fraction);

        if (_fill != null) _fill.fillAmount = _value;
    }

    private void OnPhase(string phaseName, float fraction)
    {
        if (_phase != null) _phase.text = phaseName;
    }

    private void OnEnded(string monsterId) => Hide();

    private void OnEnrageEnded() => Hide();

    private void Hide()
    {
        _enrageEndsAt = -1f;
        if (_root != null) _root.SetActive(false);
    }

    private void Update()
    {
        if (_root == null || !_root.activeSelf) return;

        if (_chip != null)
        {
            _chipValue = Mathf.Max(_value, _chipValue - ChipSpeed * Time.deltaTime);
            _chip.fillAmount = _chipValue;
        }

        if (_clock == null) return;

        if (_enrageEndsAt < 0f) { _clock.text = ""; return; }

        float remaining = Mathf.Max(0f, _enrageEndsAt - Time.time);

        _clock.text  = $"{Mathf.FloorToInt(remaining / 60f)}:{Mathf.FloorToInt(remaining % 60f):00}";

        // Red under the last thirty seconds. The clock is the fail condition, so it
        // is the thing that should be shouting rather than the health bar.
        _clock.color = remaining <= UrgentSeconds
            ? UIManager.Theme.chatBad
            : UIManager.Theme.textSecondary;
    }

    // ── Construction ──────────────────────────────────────────────────────────

    /// <summary>
    /// Built on first engage rather than at startup.
    ///
    /// Most sessions never fight a boss, and a hidden canvas that exists anyway is a
    /// hidden canvas somebody eventually finds enabled in a screenshot.
    /// </summary>
    private void Build()
    {
        if (_root != null) return;

        var theme = UIManager.Theme;
        Canvas canvas = UIManager.MainCanvas;

        _root = new GameObject("BossBar", typeof(RectTransform));
        _root.transform.SetParent(canvas != null ? canvas.transform : transform, false);

        var rect = _root.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0.5f, 1f);
        rect.anchorMax = new Vector2(0.5f, 1f);
        rect.pivot     = new Vector2(0.5f, 1f);
        rect.anchoredPosition = new Vector2(0f, -24f);
        rect.sizeDelta = new Vector2(BarWidth, BarHeight + 44f);

        _name = UIFactory.Label(_root.transform, "", theme.fontSizeTitle,
                                theme.textPrimary, TextAlignmentOptions.Center);
        Place(_name, new Vector2(BarWidth, 24f), new Vector2(0f, 0f));

        var (barRoot, fill) = UIFactory.ProgressBar(_root.transform, "Health",
                                                    theme.hpFill, BarWidth, BarHeight);

        var (chipRoot, chip) = UIFactory.ProgressBar(_root.transform, "Chip",
                                                     new Color(1f, 1f, 1f, 0.45f),
                                                     BarWidth, BarHeight);
        chipRoot.GetComponent<Image>().enabled = false;

        Place(barRoot.GetComponent<RectTransform>(),  new Vector2(BarWidth, BarHeight), new Vector2(0f, -26f));
        Place(chipRoot.GetComponent<RectTransform>(), new Vector2(BarWidth, BarHeight), new Vector2(0f, -26f));

        chipRoot.transform.SetSiblingIndex(1);
        barRoot.transform.SetSiblingIndex(2);

        _fill = fill;
        _chip = chip;

        _phase = UIFactory.Label(_root.transform, "", theme.fontSizeSmall,
                                 theme.textSecondary, TextAlignmentOptions.Left);
        Place(_phase, new Vector2(BarWidth * 0.5f, 20f), new Vector2(-BarWidth * 0.25f, -54f));

        _clock = UIFactory.Label(_root.transform, "", theme.fontSizeSmall,
                                 theme.textSecondary, TextAlignmentOptions.Right);
        Place(_clock, new Vector2(BarWidth * 0.5f, 20f), new Vector2(BarWidth * 0.25f, -54f));

        _root.SetActive(false);
    }

    /// <summary>
    /// A tick on the bar for each phase threshold.
    ///
    /// So a player can SEE the next phase coming. A boss that changes behaviour at
    /// two thirds with no warning teaches the player that bosses change behaviour
    /// randomly, which is the opposite of what the phases are for.
    /// </summary>
    private void BuildPips(MonsterData monster)
    {
        foreach (var pip in _pips) if (pip != null) Destroy(pip.gameObject);
        _pips.Clear();

        if (monster?.phases == null) return;

        foreach (var phase in monster.phases)
        {
            // The first phase begins at full health, and a pip at the far edge of the
            // bar reads as a rendering fault rather than a threshold.
            if (phase == null || phase.fromHealthFraction >= 1f) continue;

            var pip = new GameObject("Pip", typeof(RectTransform), typeof(Image));
            pip.transform.SetParent(_root.transform, false);

            var image = pip.GetComponent<Image>();
            image.color = new Color(0f, 0f, 0f, 0.55f);
            image.raycastTarget = false;

            Place(pip.GetComponent<RectTransform>(), new Vector2(2f, BarHeight),
                  new Vector2(-BarWidth * 0.5f + BarWidth * phase.fromHealthFraction, -26f));

            pip.transform.SetAsLastSibling();
            _pips.Add(image);
        }
    }

    private static void Place(Component target, Vector2 size, Vector2 position)
    {
        var rect = target.GetComponent<RectTransform>();

        rect.anchorMin = new Vector2(0.5f, 1f);
        rect.anchorMax = new Vector2(0.5f, 1f);
        rect.pivot     = new Vector2(0.5f, 1f);
        rect.sizeDelta = size;
        rect.anchoredPosition = position;
    }
}
