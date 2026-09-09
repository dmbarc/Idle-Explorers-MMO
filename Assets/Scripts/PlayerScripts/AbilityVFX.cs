using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Spawns a named visual effect in the world and cleans it up afterwards.
///
/// Effects are addressed by id and loaded from Resources/VFX, so which prefab an
/// ability or an item proc uses is a data decision (AbilityData.vfxAddress,
/// ItemEffect.param) rather than a code one. Swapping the whole set for a different
/// art pack means replacing prefabs, not editing scripts.
///
/// A missing id is not an error — it logs once and does nothing, so gameplay never
/// depends on art being present.
/// </summary>
public static class AbilityVFX
{
    private const string ResourceRoot = "VFX/";

    private static readonly Dictionary<string, GameObject> _cache = new();
    private static readonly HashSet<string>                _missingLogged = new();

    private static VFXLibrary _library;
    private static bool       _libraryLoaded;

    private static VFXLibrary Library
    {
        get
        {
            if (!_libraryLoaded)
            {
                _library       = Resources.Load<VFXLibrary>("VFXLibrary");
                _libraryLoaded = true;

                if (_library == null)
                    Debug.Log("[AbilityVFX] No VFXLibrary asset — effects will not render. " +
                              "Run: Idle Explorers → Rebuild VFX Library");
            }
            return _library;
        }
    }

    /// <summary>Plays an effect at a world position. Safe to call with a null or unknown id.</summary>
    public static GameObject Play(string vfxId, Vector3 position, float lifetime = 3f)
    {
        var prefab = Resolve(vfxId);
        if (prefab == null) return null;

        var instance = Object.Instantiate(prefab, position, Quaternion.identity);
        if (lifetime > 0f) Object.Destroy(instance, lifetime);
        return instance;
    }

    /// <summary>
    /// Plays an effect parented to a transform, so it follows the thing it belongs to.
    /// Used for auras and haste buffs, which have to track the character.
    /// </summary>
    public static GameObject PlayAttached(string vfxId, Transform parent, float lifetime = 0f)
    {
        var prefab = Resolve(vfxId);
        if (prefab == null || parent == null) return null;

        var instance = Object.Instantiate(prefab, parent.position, Quaternion.identity, parent);
        instance.transform.localPosition = Vector3.zero;

        // lifetime 0 means "until something destroys it" — auras persist while equipped.
        if (lifetime > 0f) Object.Destroy(instance, lifetime);
        return instance;
    }

    private static GameObject Resolve(string vfxId)
    {
        if (string.IsNullOrEmpty(vfxId)) return null;

        if (_cache.TryGetValue(vfxId, out var cached)) return cached;

        // The library first, since that is where the imported packs are referenced;
        // Resources/VFX remains as an escape hatch for hand-made prefabs.
        var prefab = Library?.Get(vfxId) ?? Resources.Load<GameObject>(ResourceRoot + vfxId);

        _cache[vfxId] = prefab;   // cache the miss too, so lookups are not repeated per frame

        if (prefab == null && _missingLogged.Add(vfxId))
            Debug.Log($"[AbilityVFX] No effect registered for '{vfxId}' — skipped.");

        return prefab;
    }

    /// <summary>Drops cached lookups. Called on domain reload so stale prefab refs do not persist.</summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        _cache.Clear();
        _missingLogged.Clear();
        _library       = null;
        _libraryLoaded = false;
    }
}
