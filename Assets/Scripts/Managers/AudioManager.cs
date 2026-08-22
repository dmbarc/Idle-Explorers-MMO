using System.Collections;
using UnityEngine;

/// <summary>
/// Manages background music and SFX.
/// Crossfades between tracks on state changes.
/// Phase 1: uses AudioClips assigned in Inspector (placeholder tracks).
/// </summary>
public class AudioManager : MonoBehaviour
{
    [Header("Music")]
    public AudioClip menuMusic;
    public AudioClip gameMusic;

    [Header("SFX — wire up Brackeys clips")]
    public AudioClip sfxButtonClick;
    public AudioClip sfxButtonHover;
    public AudioClip sfxHit;
    public AudioClip sfxDeath;
    public AudioClip sfxLevelUp;
    public AudioClip sfxItemPickup;

    [Range(0f, 1f)] public float musicVolume = 0.5f;
    [Range(0f, 1f)] public float sfxVolume   = 1.0f;

    private AudioSource _musicSource;
    private AudioSource _sfxSource;

    void Awake()
    {
        _musicSource            = gameObject.AddComponent<AudioSource>();
        _musicSource.loop       = true;
        _musicSource.volume     = musicVolume;

        _sfxSource              = gameObject.AddComponent<AudioSource>();
        _sfxSource.loop         = false;
        _sfxSource.volume       = sfxVolume;
    }

    public void OnStateChanged(GameManager.GameState state)
    {
        switch (state)
        {
            case GameManager.GameState.Login:
            case GameManager.GameState.CharacterSelect:
            case GameManager.GameState.CharacterCreate:
                CrossfadeTo(menuMusic);
                break;
            case GameManager.GameState.InGame:
                CrossfadeTo(gameMusic);
                break;
        }
    }

    public void PlaySFX(AudioClip clip)
    {
        if (clip == null) return;
        _sfxSource.volume = sfxVolume;
        _sfxSource.PlayOneShot(clip);
    }

    public void PlayClick()   => PlaySFX(sfxButtonClick);
    public void PlayHover()   => PlaySFX(sfxButtonHover);
    public void PlayHit()     => PlaySFX(sfxHit);
    public void PlayDeath()   => PlaySFX(sfxDeath);
    public void PlayLevelUp() => PlaySFX(sfxLevelUp);
    public void PlayPickup()  => PlaySFX(sfxItemPickup);

    public void SetMusicVolume(float v) { musicVolume = v; _musicSource.volume = v; }
    public void SetSFXVolume(float v)   { sfxVolume   = v; _sfxSource.volume   = v; }

    private void CrossfadeTo(AudioClip clip)
    {
        if (clip == null || _musicSource.clip == clip) return;
        StartCoroutine(CrossfadeRoutine(clip));
    }

    private IEnumerator CrossfadeRoutine(AudioClip clip)
    {
        float duration = 1.5f;
        float startVol = _musicSource.volume;

        // Fade out
        for (float t = 0; t < duration; t += Time.deltaTime)
        {
            _musicSource.volume = Mathf.Lerp(startVol, 0f, t / duration);
            yield return null;
        }

        _musicSource.clip = clip;
        _musicSource.Play();

        // Fade in
        for (float t = 0; t < duration; t += Time.deltaTime)
        {
            _musicSource.volume = Mathf.Lerp(0f, musicVolume, t / duration);
            yield return null;
        }
        _musicSource.volume = musicVolume;
    }
}
