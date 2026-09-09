using System;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// Screen 17: audio and display settings, nested under the ≡ menu.
/// Values persist through PlayerPrefs so they survive a restart independently of
/// the character save.
/// </summary>
public class SettingsPanel : UIScreen
{
    private const string PrefMusic      = "settings.musicVolume";
    private const string PrefSfx        = "settings.sfxVolume";
    private const string PrefQuality    = "settings.qualityLevel";
    private const string PrefFullscreen = "settings.fullscreen";

    private TMP_Text _musicValue;
    private TMP_Text _sfxValue;
    private TMP_Text _qualityValue;
    private TMP_Text _fullscreenValue;

    /// <summary>Nested inside the menu, which is itself an overlay.</summary>
    public override bool IsOverlay => true;

    public override void Build()
    {
        var theme = UIManager.Theme;

        UIFactory.Panel(transform, "Backdrop", theme.overlayBg, true);

        var card = UIFactory.Panel(transform, "SettingsCard", theme.panelBg, false);
        UIFactory.At(card.transform, 0.30f, 0.18f, 0.70f, 0.84f);

        var title = UIFactory.Label(card.transform, "SETTINGS", theme.fontSizeBody,
                                     theme.accentGold, TextAlignmentOptions.Center);
        UIFactory.At(title, 0.05f, 0.90f, 0.95f, 0.97f);

        UIFactory.At(UIFactory.HorizontalDivider(card.transform).transform, 0.05f, 0.87f, 0.95f, 0.89f);

        SectionLabel(card.transform, "AUDIO", 0.78f, 0.85f);
        _musicValue = BuildStepper(card.transform, "Music", 0.68f, 0.77f,
                                    () => AdjustMusic(-0.1f), () => AdjustMusic(0.1f));
        _sfxValue   = BuildStepper(card.transform, "Sound Effects", 0.57f, 0.66f,
                                    () => AdjustSfx(-0.1f), () => AdjustSfx(0.1f));

        SectionLabel(card.transform, "DISPLAY", 0.45f, 0.52f);
        _qualityValue    = BuildStepper(card.transform, "Quality", 0.35f, 0.44f,
                                         () => CycleQuality(-1), () => CycleQuality(1));
        _fullscreenValue = BuildStepper(card.transform, "Fullscreen", 0.24f, 0.33f,
                                         ToggleFullscreen, ToggleFullscreen);

        var backBtn = UIFactory.Button(card.transform, "← BACK", () => GameManager.UI?.Pop(), width: 0f);
        UIFactory.At(backBtn, 0.30f, 0.05f, 0.70f, 0.14f);

        RefreshAll();
    }

    public override void OnShow() => RefreshAll();

    // ── Widgets ───────────────────────────────────────────────────────────────

    private void SectionLabel(Transform parent, string text, float yMin, float yMax)
    {
        var label = UIFactory.Label(parent, text, UIManager.Theme.fontSizeLabel,
                                     UIManager.Theme.accentGold, TextAlignmentOptions.MidlineLeft);
        UIFactory.At(label, 0.06f, yMin, 0.60f, yMax);
    }

    /// <summary>A labelled row with -/+ buttons and a value readout between them.</summary>
    private TMP_Text BuildStepper(Transform parent, string label, float yMin, float yMax,
                                   Action onDecrease, Action onIncrease)
    {
        var theme = UIManager.Theme;

        var row = UIFactory.Panel(parent, $"Row_{label}", theme.cardBg, false);
        UIFactory.At(row.transform, 0.06f, yMin, 0.94f, yMax);

        var nameLabel = UIFactory.Label(row.transform, label, theme.fontSizeSmall,
                                         theme.textPrimary, TextAlignmentOptions.MidlineLeft);
        UIFactory.At(nameLabel, 0.04f, 0f, 0.50f, 1f);

        var minus = UIFactory.Button(row.transform, "−", onDecrease, width: 44f);
        UIFactory.At(minus, 0.54f, 0.12f, 0.64f, 0.88f);

        var value = UIFactory.Label(row.transform, "", theme.fontSizeSmall,
                                     theme.accentGreen, TextAlignmentOptions.Center);
        UIFactory.At(value, 0.65f, 0f, 0.83f, 1f);

        var plus = UIFactory.Button(row.transform, "+", onIncrease, width: 44f);
        UIFactory.At(plus, 0.84f, 0.12f, 0.94f, 0.88f);

        return value;
    }

    // ── Audio ─────────────────────────────────────────────────────────────────

    private void AdjustMusic(float delta)
    {
        var audio = GameManager.Audio;
        if (audio == null) return;

        float v = Mathf.Clamp01(audio.musicVolume + delta);
        audio.SetMusicVolume(v);
        PlayerPrefs.SetFloat(PrefMusic, v);
        PlayerPrefs.Save();
        RefreshAll();
    }

    private void AdjustSfx(float delta)
    {
        var audio = GameManager.Audio;
        if (audio == null) return;

        float v = Mathf.Clamp01(audio.sfxVolume + delta);
        audio.SetSFXVolume(v);
        PlayerPrefs.SetFloat(PrefSfx, v);
        PlayerPrefs.Save();
        RefreshAll();
    }

    // ── Display ───────────────────────────────────────────────────────────────

    private void CycleQuality(int direction)
    {
        int count = QualitySettings.names.Length;
        if (count == 0) return;

        int next = (QualitySettings.GetQualityLevel() + direction + count) % count;
        QualitySettings.SetQualityLevel(next, applyExpensiveChanges: true);
        PlayerPrefs.SetInt(PrefQuality, next);
        PlayerPrefs.Save();
        RefreshAll();
    }

    private void ToggleFullscreen()
    {
        bool next = !Screen.fullScreen;
        Screen.fullScreen = next;
        PlayerPrefs.SetInt(PrefFullscreen, next ? 1 : 0);
        PlayerPrefs.Save();
        RefreshAll();
    }

    // ── Readouts ──────────────────────────────────────────────────────────────

    private void RefreshAll()
    {
        var audio = GameManager.Audio;

        if (_musicValue != null)
            _musicValue.text = $"{Mathf.RoundToInt((audio?.musicVolume ?? 0f) * 100f)}%";
        if (_sfxValue != null)
            _sfxValue.text = $"{Mathf.RoundToInt((audio?.sfxVolume ?? 0f) * 100f)}%";

        if (_qualityValue != null)
        {
            var names = QualitySettings.names;
            int level = QualitySettings.GetQualityLevel();
            _qualityValue.text = (level >= 0 && level < names.Length) ? names[level] : "—";
        }

        if (_fullscreenValue != null)
            _fullscreenValue.text = Screen.fullScreen ? "On" : "Off";
    }

    /// <summary>
    /// Applies saved preferences at startup. Called by GameManager rather than by
    /// this screen, which may never be opened in a given session.
    /// </summary>
    public static void ApplySavedSettings()
    {
        var audio = GameManager.Audio;
        if (audio != null)
        {
            if (PlayerPrefs.HasKey(PrefMusic)) audio.SetMusicVolume(PlayerPrefs.GetFloat(PrefMusic));
            if (PlayerPrefs.HasKey(PrefSfx))   audio.SetSFXVolume(PlayerPrefs.GetFloat(PrefSfx));
        }

        if (PlayerPrefs.HasKey(PrefQuality))
        {
            int level = Mathf.Clamp(PlayerPrefs.GetInt(PrefQuality), 0, Mathf.Max(0, QualitySettings.names.Length - 1));
            QualitySettings.SetQualityLevel(level, applyExpensiveChanges: false);
        }

        if (PlayerPrefs.HasKey(PrefFullscreen))
            Screen.fullScreen = PlayerPrefs.GetInt(PrefFullscreen) == 1;
    }
}
