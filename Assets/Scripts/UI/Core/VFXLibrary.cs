using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Maps effect ids to particle prefabs drawn from the imported VFX packs.
///
/// The same shape as IconLibrary, and for the same reason: the art lives outside
/// any Resources folder, and duplicating sixty-five prefabs to get them loadable
/// would leave two copies to drift apart. An editor pass
/// (Idle Explorers → Rebuild VFX Library) records direct references here, and this
/// one asset goes in Resources.
///
/// Anything without an entry simply plays nothing, so an ability is never blocked
/// on art being present.
/// </summary>
[CreateAssetMenu(fileName = "VFXLibrary", menuName = "IdleExplorers/VFXLibrary")]
public class VFXLibrary : ScriptableObject
{
    [Serializable]
    public class Entry
    {
        public string     id;
        public GameObject prefab;
    }

    public List<Entry> effects = new();

    private Dictionary<string, GameObject> _lookup;

    public GameObject Get(string effectId)
    {
        if (string.IsNullOrEmpty(effectId)) return null;

        _lookup ??= Build(effects);
        return _lookup.TryGetValue(effectId, out var prefab) ? prefab : null;
    }

    private static Dictionary<string, GameObject> Build(List<Entry> entries)
    {
        var map = new Dictionary<string, GameObject>();
        if (entries == null) return map;

        foreach (var e in entries)
            if (e != null && !string.IsNullOrEmpty(e.id) && e.prefab != null)
                map[e.id] = e.prefab;

        return map;
    }

    /// <summary>Drops the cached lookup so an editor rebuild takes effect without a restart.</summary>
    public void InvalidateCache() => _lookup = null;
}
