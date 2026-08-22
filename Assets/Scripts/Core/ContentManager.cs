using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// Loads all JSON content files from StreamingAssets and provides typed lookups.
/// Phase 1: StreamingAssets (no extra packages needed).
/// Phase 2+: swap LoadJsonRoutine to use Addressables or a remote CDN URL when
///            the com.unity.addressables package is installed.
///
/// Files expected in Assets/StreamingAssets/:
///   item_data.json, monster_data.json, zone_data.json, skill_data.json,
///   class_data.json, merge_recipes.json, slot_unlock.json
/// </summary>
public class ContentManager : MonoBehaviour
{
    // ── Typed catalogues ─────────────────────────────────────────────────────
    public Dictionary<string, ItemData>    Items       { get; } = new Dictionary<string, ItemData>();
    public Dictionary<string, MonsterData> Monsters    { get; } = new Dictionary<string, MonsterData>();
    public Dictionary<string, ZoneData>    Zones       { get; } = new Dictionary<string, ZoneData>();
    public Dictionary<string, MapData>     Maps        { get; } = new Dictionary<string, MapData>();
    public Dictionary<string, SkillData>   Skills      { get; } = new Dictionary<string, SkillData>();
    public Dictionary<string, ClassData>   Classes     { get; } = new Dictionary<string, ClassData>();
    public List<MergeRecipe>               MergeRecipes { get; } = new List<MergeRecipe>();
    public List<SlotUnlockRequirement>     SlotUnlocks  { get; } = new List<SlotUnlockRequirement>();

    public bool IsLoaded { get; private set; }

    private int    _pendingLoads;
    private Action _onComplete;

    // ── Wrapper types (JsonUtility cannot parse root arrays) ─────────────────
    [Serializable] private class ItemList    { public ItemData[]             items;    }
    [Serializable] private class MonsterList { public MonsterData[]          monsters; }
    [Serializable] private class ZoneList    { public ZoneData[]             zones;    }
    [Serializable] private class SkillList   { public SkillData[]            skills;   }
    [Serializable] private class ClassList   { public ClassData[]            classes;  }
    [Serializable] private class MergeList   { public MergeRecipe[]          recipes;  }
    [Serializable] private class SlotList    { public SlotUnlockRequirement[] slots;   }

    // ── Public API ────────────────────────────────────────────────────────────
    public void LoadAll(Action onComplete)
    {
        _onComplete   = onComplete;
        _pendingLoads = 7;

        LoadJson<ItemList>   ("item_data",     "items",    j => { foreach (var x in j.items)    Items[x.id]    = x; });
        LoadJson<MonsterList>("monster_data",  "monsters", j => { foreach (var x in j.monsters) Monsters[x.id] = x; });
        // zone_data embeds maps — extract both in one pass
        LoadJson<ZoneList>   ("zone_data",     "zones",    j =>
        {
            foreach (var zone in j.zones)
            {
                Zones[zone.id] = zone;
                if (zone.maps != null)
                    foreach (var map in zone.maps)
                        Maps[map.id] = map;
            }
        });
        LoadJson<SkillList>  ("skill_data",    "skills",   j => { foreach (var x in j.skills)  Skills[x.id]  = x; });
        LoadJson<ClassList>  ("class_data",    "classes",  j => { foreach (var x in j.classes) Classes[x.id] = x; });
        LoadJson<MergeList>  ("merge_recipes", "recipes",  j => MergeRecipes.AddRange(j.recipes));
        LoadJson<SlotList>   ("slot_unlock",   "slots",    j => SlotUnlocks.AddRange(j.slots));
    }

    // ── Sprite loading (stub — wire to Resources or Addressables in Phase 2) ─
    public void LoadSprite(string address, Action<Sprite> callback)
    {
        // Phase 1: load from Resources folder by address string
        if (string.IsNullOrEmpty(address)) { callback?.Invoke(null); return; }
        var sprite = Resources.Load<Sprite>(address);
        callback?.Invoke(sprite); // null is acceptable — icon will be blank
    }

    // ── Lookup helpers ────────────────────────────────────────────────────────
    public ItemData    GetItem(string id)    => Items.TryGetValue(id,    out var v) ? v : null;
    public MonsterData GetMonster(string id) => Monsters.TryGetValue(id, out var v) ? v : null;
    public SkillData   GetSkill(string id)   => Skills.TryGetValue(id,   out var v) ? v : null;
    public ClassData   GetClass(string id)   => Classes.TryGetValue(id,  out var v) ? v : null;
    public MapData     GetMap(string id)     => Maps.TryGetValue(id,     out var v) ? v : null;
    public ZoneData    GetZone(string id)    => Zones.TryGetValue(id,    out var v) ? v : null;

    public MergeRecipe GetMergeRecipe(string inputItemId)
    {
        foreach (var r in MergeRecipes)
            if (r.inputItemId == inputItemId) return r;
        return null;
    }

    public bool IsSlotUnlocked(int slotIndex, int accountLevel, int highestCharLevel)
    {
        foreach (var req in SlotUnlocks)
            if (req.slot == slotIndex)
                return accountLevel >= req.reqAccountLevel &&
                       highestCharLevel >= req.reqAnyCharLevel;
        return false;
    }

    // ── Private: load from StreamingAssets ────────────────────────────────────
    private void LoadJson<T>(string fileName, string wrapField, Action<T> onParsed)
    {
        StartCoroutine(LoadJsonRoutine<T>(fileName + ".json", wrapField, onParsed));
    }

    private IEnumerator LoadJsonRoutine<T>(string fileName, string wrapField, Action<T> onParsed)
    {
        // StreamingAssets requires UnityWebRequest on all platforms (including Android)
        string uri = Path.Combine(Application.streamingAssetsPath, fileName);
#if UNITY_ANDROID && !UNITY_EDITOR
        // Android StreamingAssets are inside the APK — must use jar:// URI as-is
#else
        uri = "file:///" + uri.Replace("\\", "/");
#endif

        using var req = UnityWebRequest.Get(uri);
        yield return req.SendWebRequest();

        if (req.result == UnityWebRequest.Result.Success)
        {
            try
            {
                string raw     = req.downloadHandler.text.Trim();
                // Wrap root array so JsonUtility can parse: [...] → {"field":[...]}
                string wrapped = raw.StartsWith("[")
                    ? $"{{\"{wrapField}\":{raw}}}"
                    : raw;
                var parsed = JsonUtility.FromJson<T>(wrapped);
                if (parsed != null)
                    onParsed?.Invoke(parsed);
                else
                    Debug.LogWarning($"[ContentManager] '{fileName}' parsed to null.");
            }
            catch (Exception e)
            {
                Debug.LogError($"[ContentManager] Failed to parse '{fileName}': {e.Message}");
            }
        }
        else
        {
            Debug.LogWarning($"[ContentManager] Could not load '{fileName}': {req.error} — catalogue will be empty for this type.");
        }

        _pendingLoads--;
        if (_pendingLoads <= 0)
        {
            IsLoaded = true;
            Debug.Log($"[ContentManager] Loaded: {Items.Count} items, {Monsters.Count} monsters, " +
                      $"{Zones.Count} zones, {Maps.Count} maps, {Skills.Count} skills, {Classes.Count} classes.");
            _onComplete?.Invoke();
        }
    }
}
