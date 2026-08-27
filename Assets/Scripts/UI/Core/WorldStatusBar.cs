using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// A small bar floating above something in the world.
///
/// ══ WHY IT IS BUILT IN CODE AND NOT WIRED ON A PREFAB ═════════════════════════
///
/// The old path was three serialised fields on MonsterController — healthUI,
/// healthNumber and healthSlider — pointing at objects inside the SPUM donor rig.
/// MonsterPrefabSetup deliberately NULLS all three when it builds a monster, because
/// the references belong to the donor and following them dresses the wrong prefab.
/// Which is exactly why the goblin and the bramblekin, the only two monsters in the
/// two playable maps, have no health bar at all: the wiring is removed on purpose
/// and nothing puts it back.
///
/// So it attaches itself at runtime and owns everything it needs. Nothing to wire,
/// nothing to null, and a new monster type gets a bar by existing.
///
/// ══ THE CHIP BAR ══════════════════════════════════════════════════════════════
///
/// A second bar drains behind the first, a beat later. It is the cheapest way to
/// make a hit READ as a hit: the eye catches the pale sliver shrinking even when the
/// red bar moved too little to notice, and a burst of small hits looks like a burst
/// rather than a bar quietly getting shorter.
/// </summary>
public class WorldStatusBar : MonoBehaviour
{
    /// <summary>Pixels per world unit for the internal canvas. Bigger means crisper.</summary>
    private const float PixelsPerUnit = 100f;

    private const float BarWidth  = 90f;
    private const float BarHeight = 11f;

    /// <summary>Seconds before the chip bar starts catching up.</summary>
    private const float ChipDelay = 0.25f;

    /// <summary>Fraction per second the chip bar drains at.</summary>
    private const float ChipSpeed = 0.9f;

    /// <summary>How long it stays up after the last change, when auto-hiding.</summary>
    public float HideAfterSeconds = 3f;

    /// <summary>
    /// When true, the bar hides at full and after a quiet spell. Health bars want
    /// this; a skilling node's progress bar does not, because it is showing an
    /// activity rather than reporting damage.
    /// </summary>
    public bool AutoHide = true;

    /// <summary>Keeps the bar up regardless — set while the thing is targeted.</summary>
    public bool Pinned { get; set; }

    private Canvas        _canvas;
    private Image         _fill;
    private Image         _chip;
    private TMP_Text      _label;
    private float         _value = 1f;
    private float         _chipValue = 1f;
    private float         _lastChangeAt = float.NegativeInfinity;

    /// <summary>
    /// Puts a bar above <paramref name="host"/>, or returns the one already there.
    /// </summary>
    /// <param name="host">The thing being described.</param>
    /// <param name="heightAbove">World units above the host's origin.</param>
    /// <param name="fill">Bar colour. Health is theme hpFill, a node is xpFill.</param>
    public static WorldStatusBar Attach(GameObject host, float heightAbove, Color fill)
    {
        if (host == null) return null;

        var existing = host.GetComponentInChildren<WorldStatusBar>(includeInactive: true);
        if (existing != null) return existing;

        var go = new GameObject("StatusBar");
        go.transform.SetParent(host.transform, false);
        go.transform.localPosition = new Vector3(0f, heightAbove, 0f);

        var bar = go.AddComponent<WorldStatusBar>();
        bar.Build(fill);

        return bar;
    }

    private void Build(Color fillColour)
    {
        var theme = UIManager.Theme;

        _canvas = gameObject.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.WorldSpace;

        // Sized in pixels and scaled down, rather than sized in world units: the
        // whole UI toolkit here is authored in pixels, so anything built with
        // UIFactory expects that coordinate space.
        var rect = _canvas.GetComponent<RectTransform>();
        rect.sizeDelta  = new Vector2(BarWidth, BarHeight + 14f);
        rect.localScale = Vector3.one / PixelsPerUnit;

        // Flat artwork in a 3D world, same as everything else here.
        gameObject.AddComponent<Billboard>();

        var (root, fill) = UIFactory.ProgressBar(transform, "Bar", fillColour, BarWidth, BarHeight);

        // The chip sits BETWEEN the background and the fill, so the pale sliver
        // shows only where the fill has already retreated from.
        var (chipRoot, chip) = UIFactory.ProgressBar(transform, "Chip",
                                                     new Color(1f, 1f, 1f, 0.55f),
                                                     BarWidth, BarHeight);
        chipRoot.GetComponent<Image>().enabled = false;   // no second background
        chipRoot.transform.SetSiblingIndex(0);
        root.transform.SetSiblingIndex(1);

        _fill = fill;
        _chip = chip;

        _label = UIFactory.Label(transform, "", theme.fontSizeSmall, theme.textPrimary,
                                 TextAlignmentOptions.Center);

        var labelRect = _label.GetComponent<RectTransform>();
        labelRect.sizeDelta        = new Vector2(BarWidth, 14f);
        labelRect.anchoredPosition = new Vector2(0f, BarHeight);

        SetVisible(false);
    }

    /// <summary>
    /// Updates the bar.
    /// </summary>
    /// <param name="current">Where it is now.</param>
    /// <param name="max">What full looks like.</param>
    /// <param name="label">Text above the bar, or null for none.</param>
    public void Set(double current, double max, string label = null)
    {
        float wanted = max > 0d ? Mathf.Clamp01((float)(current / max)) : 0f;

        if (!Mathf.Approximately(wanted, _value))
        {
            // Rising is a heal or a restart, and neither wants a chip trailing it.
            if (wanted > _value) _chipValue = wanted;

            _value        = wanted;
            _lastChangeAt = Time.time;

            SetVisible(true);
        }

        if (_fill != null) _fill.fillAmount = _value;

        if (_label == null) return;

        _label.text = label ?? "";
        _label.gameObject.SetActive(!string.IsNullOrEmpty(label));
    }

    /// <summary>Sets the bar from an already-computed 0-1 fraction.</summary>
    public void SetFraction(float fraction, string label = null) => Set(fraction, 1d, label);

    private void LateUpdate()
    {
        if (_chip != null)
        {
            if (_chipValue > _value && Time.time - _lastChangeAt >= ChipDelay)
                _chipValue = Mathf.Max(_value, _chipValue - ChipSpeed * Time.deltaTime);

            _chip.fillAmount = _chipValue;
        }

        if (!AutoHide || Pinned) return;

        // Full and quiet for a while: nothing to say.
        bool quiet = Time.time - _lastChangeAt > HideAfterSeconds;
        if (quiet && _value >= 1f) SetVisible(false);
    }

    /// <summary>Shows or hides the bar without destroying it.</summary>
    public void SetVisible(bool visible)
    {
        if (_canvas != null) _canvas.enabled = visible;
    }

    // ── The minigame's target window ──────────────────────────────────────────

    private Image _window;

    /// <summary>
    /// Marks the stretch of the bar worth striking in.
    ///
    /// Drawn UNDER the fill, so the fill sweeping across it is what tells the player
    /// when to press. A marker on top would hide the very thing it is timing against.
    ///
    /// Passing a width of zero hides it, which is what a node with no minigame wants.
    /// </summary>
    public void ShowWindow(float centre, float halfWidth)
    {
        if (halfWidth <= 0f)
        {
            if (_window != null) _window.enabled = false;
            return;
        }

        if (_window == null)
        {
            var go = new GameObject("Window", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(transform, false);
            go.transform.SetSiblingIndex(1);

            _window = go.GetComponent<Image>();
            _window.color = new Color(1f, 0.85f, 0.30f, 0.5f);
            _window.raycastTarget = false;
        }

        var rect = _window.GetComponent<RectTransform>();

        rect.anchorMin = new Vector2(0f, 0.5f);
        rect.anchorMax = new Vector2(0f, 0.5f);
        rect.pivot     = new Vector2(0.5f, 0.5f);
        rect.sizeDelta = new Vector2(BarWidth * halfWidth * 2f, BarHeight);
        rect.anchoredPosition = new Vector2(BarWidth * Mathf.Clamp01(centre), 0f);

        _window.enabled = true;
    }
}
