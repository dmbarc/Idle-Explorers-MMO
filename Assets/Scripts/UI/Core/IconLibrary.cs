using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Maps item and skill ids to sprites drawn from the imported art packs.
///
/// Exists because item_data.json ships no iconAddress values and the art lives
/// outside any Resources folder. Rather than duplicating hundreds of PNGs, an
/// editor pass (Idle Explorers → Rebuild Icon Library) records direct asset
/// references here, and this single asset goes in Resources.
///
/// Anything without an entry falls back to UIFactory.PlaceholderIcon, so a
/// missing mapping is a cosmetic gap, never a blank slot.
/// </summary>
[CreateAssetMenu(fileName = "IconLibrary", menuName = "IdleExplorers/IconLibrary")]
public class IconLibrary : ScriptableObject
{
    [Serializable]
    public class Entry
    {
        public string id;
        public Sprite sprite;
    }

    public List<Entry> itemIcons    = new();
    public List<Entry> skillIcons   = new();
    public List<Entry> abilityIcons = new();

    private Dictionary<string, Sprite> _itemLookup;
    private Dictionary<string, Sprite> _skillLookup;
    private Dictionary<string, Sprite> _abilityLookup;

    public Sprite GetItemIcon(string itemId)
    {
        _itemLookup ??= Build(itemIcons);
        return itemId != null && _itemLookup.TryGetValue(itemId, out var s) ? s : null;
    }

    public Sprite GetSkillIcon(string skillId)
    {
        _skillLookup ??= Build(skillIcons);
        return skillId != null && _skillLookup.TryGetValue(skillId, out var s) ? s : null;
    }

    public Sprite GetAbilityIcon(string abilityId)
    {
        _abilityLookup ??= Build(abilityIcons);
        return abilityId != null && _abilityLookup.TryGetValue(abilityId, out var s) ? s : null;
    }

    private static Dictionary<string, Sprite> Build(List<Entry> entries)
    {
        var map = new Dictionary<string, Sprite>();
        if (entries == null) return map;

        foreach (var e in entries)
            if (e != null && !string.IsNullOrEmpty(e.id) && e.sprite != null)
                map[e.id] = e.sprite;

        return map;
    }

    /// <summary>Drops cached lookups so an editor rebuild takes effect without a restart.</summary>
    public void InvalidateCache()
    {
        _itemLookup    = null;
        _skillLookup   = null;
        _abilityLookup = null;
    }
}
