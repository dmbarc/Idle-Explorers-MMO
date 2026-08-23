using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Music and sound effects.
///
/// Clips are resolved by id through AudioLibrary rather than assigned in the
/// Inspector. The Inspector fields this replaces were all null and had been since the
/// project started, so every PlaySFX call was a no-op — the game had a complete sound
/// system that made no sound. An asset built by an editor pass cannot fall out of a
/// scene the way six unassigned Inspector slots did.
///
/// SFX go through a small pool of AudioSources rather than one. PlayOneShot on a
/// single source works, but the source's volume applies to everything already in
/// flight — so turning the slider down mid-fight would duck sounds that had already
/// started. A pool also means a burst of hits does not steal each other's channel.
/// </summary>
public class AudioManager : MonoBehaviour
{
    [Range(0f, 1f)] public float musicVolume = 0.4f;
    [Range(0f, 1f)] public float sfxVolume   = 0.8f;

    /// <summary>How many SFX can overlap. Past this the oldest is reused.</summary>
    private const int SfxChannels = 8;

    private AudioSource       _musicSource;
    private List<AudioSource> _sfxSources = new();
    private int               _nextChannel;

    private AudioLibrary _library;
    private bool         _libraryLoaded;

    /// <summary>
    /// Ids that fire many times a second. A second request inside this window is
    /// dropped, so a wall of drops or a fast attack chain does not stack forty copies
    /// of the same sample into a wall of noise.
    /// </summary>
    private const float RepeatGuardSeconds = 0.06f;

    private readonly Dictionary<string, float> _lastPlayedAt = new();

    void Awake()
    {
        _musicSource        = gameObject.AddComponent<AudioSource>();
        _musicSource.loop   = true;
        _musicSource.volume = musicVolume;
        _musicSource.playOnAwake = false;

        for (int i = 0; i < SfxChannels; i++)
        {
            var source = gameObject.AddComponent<AudioSource>();
            source.loop        = false;
            source.playOnAwake = false;
            _sfxSources.Add(source);
        }
    }

    private AudioLibrary Library
    {
        get
        {
            if (!_libraryLoaded)
            {
                _library       = Resources.Load<AudioLibrary>("AudioLibrary");
                _libraryLoaded = true;

                if (_library == null)
                    Debug.LogWarning("[Audio] No AudioLibrary asset — the game will be silent. " +
                                     "Run: Idle Explorers → Rebuild Audio Library");
            }
            return _library;
        }
    }

    // ── SFX ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Plays a sound by id. Unknown ids are silently ignored at runtime — the editor
    /// pass is where a missing mapping gets reported, and a warning per frame during a
    /// fight would be worse than the silence it is complaining about.
    /// </summary>
    public void Play(string soundId, float volumeScale = 1f)
    {
        if (string.IsNullOrEmpty(soundId) || sfxVolume <= 0f) return;

        if (_lastPlayedAt.TryGetValue(soundId, out float last) &&
            Time.unscaledTime - last < RepeatGuardSeconds)
            return;

        var entry = Library?.GetSound(soundId);
        var clip  = Library?.PickSound(soundId);
        if (clip == null) return;

        _lastPlayedAt[soundId] = Time.unscaledTime;

        var source = NextChannel();
        source.volume = Mathf.Clamp01(sfxVolume * volumeScale * (entry?.volume ?? 1f));
        source.PlayOneShot(clip, source.volume);
    }

    /// <summary>Round-robin so a burst of sounds does not cut itself off.</summary>
    private AudioSource NextChannel()
    {
        // Prefer an idle channel; fall back to the oldest in the rotation.
        for (int i = 0; i < _sfxSources.Count; i++)
        {
            var candidate = _sfxSources[(_nextChannel + i) % _sfxSources.Count];
            if (candidate.isPlaying) continue;

            _nextChannel = (_nextChannel + i + 1) % _sfxSources.Count;
            return candidate;
        }

        var source = _sfxSources[_nextChannel];
        _nextChannel = (_nextChannel + 1) % _sfxSources.Count;
        return source;
    }

    // Named helpers, so call sites read as intent rather than as string lookups.
    public void PlayClick()    => Play(Sfx.Click);
    public void PlayHover()    => Play(Sfx.Hover, 0.5f);
    public void PlayHit()      => Play(Sfx.Hit);
    public void PlayDeath()    => Play(Sfx.MonsterDeath);
    public void PlayLevelUp()  => Play(Sfx.LevelUp);
    public void PlayPickup()   => Play(Sfx.Pickup);
    public void PlayError()    => Play(Sfx.Error);
    public void PlayEquip()    => Play(Sfx.Equip);
    public void PlayBreak()    => Play(Sfx.Break);
    public void PlayPurchase() => Play(Sfx.Purchase);

    // ── Music ─────────────────────────────────────────────────────────────────

    public void OnStateChanged(GameManager.GameState state)
    {
        switch (state)
        {
            case GameManager.GameState.Login:
            case GameManager.GameState.CharacterSelect:
            case GameManager.GameState.CharacterCreate:
                PlayMusic(Bgm.Menu);
                break;

            case GameManager.GameState.InGame:
                PlayMusic(Bgm.Game);
                break;
        }
    }

    public void PlayMusic(string trackId)
    {
        var clip = Library?.PickMusic(trackId);
        CrossfadeTo(clip);
    }

    public void SetMusicVolume(float v)
    {
        musicVolume = Mathf.Clamp01(v);
        if (_musicSource != null && !_fading) _musicSource.volume = musicVolume;
    }

    public void SetSFXVolume(float v) => sfxVolume = Mathf.Clamp01(v);

    private bool _fading;

    private void CrossfadeTo(AudioClip clip)
    {
        if (clip == null || _musicSource == null) return;
        if (_musicSource.clip == clip && _musicSource.isPlaying) return;

        StopAllCoroutines();
        StartCoroutine(CrossfadeRoutine(clip));
    }

    private IEnumerator CrossfadeRoutine(AudioClip clip)
    {
        const float duration = 1.2f;

        _fading = true;

        float startVol = _musicSource.volume;
        for (float t = 0; t < duration; t += Time.unscaledDeltaTime)
        {
            _musicSource.volume = Mathf.Lerp(startVol, 0f, t / duration);
            yield return null;
        }

        _musicSource.clip = clip;
        _musicSource.Play();

        for (float t = 0; t < duration; t += Time.unscaledDeltaTime)
        {
            _musicSource.volume = Mathf.Lerp(0f, musicVolume, t / duration);
            yield return null;
        }

        _musicSource.volume = musicVolume;
        _fading = false;
    }
}
