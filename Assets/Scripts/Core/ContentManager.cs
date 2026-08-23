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
    public List<CraftRecipe>               CraftRecipes { get; } = new List<CraftRecipe>();
    public List<RelicCoinPack>             CoinPacks    { get; } = new List<RelicCoinPack>();
    public List<ShopProduct>               ShopProducts { get; } = new List<ShopProduct>();
    public Dictionary<string, ItemSetData> ItemSets     { get; } = new Dictionary<string, ItemSetData>();
    public List<SpecCombo>                 SpecCombos   { get; } = new List<SpecCombo>();

    /// <summary>
    /// The stat baseline every character starts from, before any class.
    ///
    /// Never null: a missing or malformed base_stats.json leaves an empty block rather
    /// than a null reference, so combat still runs — badly, and loudly, but it runs.
    /// </summary>
    public StatBlock BaseStats { get; private set; } = new StatBlock();

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
    [Serializable] private class CraftList   { public CraftRecipe[]          recipes;  }
    [Serializable] private class SetList     { public ItemSetData[]          sets;     }
    [Serializable] private class SpecList    { public SpecCombo[]            specs;    }

    // ── Public API ────────────────────────────────────────────────────────────
    public void LoadAll(Action onComplete)
    {
        _onComplete   = onComplete;
        _pendingLoads = 12;

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
        LoadJson<CraftList>  ("recipe_data",   "recipes",  j => CraftRecipes.AddRange(j.recipes));
        LoadJson<SetList>    ("set_data",      "sets",     j => { foreach (var x in j.sets) ItemSets[x.id] = x; });

        // A root OBJECT, like shop_data.json — it is one stat block, not a list, so the
        // array wrapper below is bypassed and wrapField goes unused.
        LoadJson<StatBlock>  ("base_stats",    "",         j => { if (j != null) BaseStats = j; });
        LoadJson<SpecList>   ("spec_data",     "specs",    j => { if (j.specs != null) SpecCombos.AddRange(j.specs); });

        // shop_data.json is a root OBJECT, not an array — it carries two lists, and
        // the array wrapper below only handles one. The wrapField is unused for it.
        LoadJson<ShopCatalog>("shop_data",     "",         j =>
        {
            if (j.coinPacks != null) CoinPacks.AddRange(j.coinPacks);
            if (j.products  != null) ShopProducts.AddRange(j.products);
        });
    }

    // ── Shop ──────────────────────────────────────────────────────────────────

    public RelicCoinPack GetCoinPack(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        foreach (var pack in CoinPacks)
            if (pack != null && pack.id == id) return pack;
        return null;
    }

    public ShopProduct GetShopProduct(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        foreach (var product in ShopProducts)
            if (product != null && product.id == id) return product;
        return null;
    }

    // ── Crafting recipes ──────────────────────────────────────────────────────

    public CraftRecipe GetRecipe(string recipeId)
    {
        if (string.IsNullOrEmpty(recipeId)) return null;

        foreach (var r in CraftRecipes)
            if (r.id == recipeId) return r;
        return null;
    }

    /// <summary>Every recipe a given station offers, in file order.</summary>
    public List<CraftRecipe> GetRecipesForStation(string stationType)
    {
        var results = new List<CraftRecipe>();
        if (string.IsNullOrEmpty(stationType)) return results;

        foreach (var r in CraftRecipes)
            if (r.stationType == stationType) results.Add(r);
        return results;
    }

    // ── Sprite loading ────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves a sprite by address, falling back to a generated placeholder so a
    /// slot is never blank. Phase 1 loads from Resources; swap for Addressables
    /// once the package is installed.
    /// </summary>
    public void LoadSprite(string address, Action<Sprite> callback, string fallbackId = null)
    {
        callback?.Invoke(GetSprite(address, fallbackId));
    }

    /// <summary>Synchronous sprite lookup with placeholder fallback.</summary>
    public Sprite GetSprite(string address, string fallbackId = null)
    {
        // Through SpriteLoader, not Resources.Load — half the art in the SPUM packs is
        // in Multiple-mode sheets, for which Resources.Load returns null with no error.
        var sprite = SpriteLoader.Load(address);
        if (sprite != null) return sprite;

        return UIFactory.PlaceholderIcon(fallbackId ?? address);
    }

    // ── Icon library (real art from the imported packs) ────────────────────────

    private IconLibrary _icons;
    private bool        _iconsLoaded;

    private IconLibrary Icons
    {
        get
        {
            if (!_iconsLoaded)
            {
                _icons = Resources.Load<IconLibrary>("IconLibrary");
                _iconsLoaded = true;
                if (_icons == null)
                    Debug.Log("[ContentManager] No IconLibrary asset — using placeholder icons. " +
                              "Run: Idle Explorers → Rebuild Icon Library");
            }
            return _icons;
        }
    }

    /// <summary>
    /// Icon for an item. Resolution order: explicit iconAddress → the icon library
    /// built from the art packs → a generated placeholder. Never returns null.
    /// </summary>
    public Sprite GetItemIcon(string itemId)
    {
        var item = GetItem(itemId);

        if (!string.IsNullOrEmpty(item?.iconAddress))
        {
            var direct = SpriteLoader.Load(item.iconAddress);
            if (direct != null) return direct;
        }

        var mapped = Icons?.GetItemIcon(itemId);
        if (mapped != null) return mapped;

        return UIFactory.PlaceholderIcon(itemId);
    }

    /// <summary>Icon for a skill, with the same resolution order as items.</summary>
    public Sprite GetSkillIcon(string skillId)
    {
        var skill = GetSkill(skillId);

        if (!string.IsNullOrEmpty(skill?.iconAddress))
        {
            var direct = SpriteLoader.Load(skill.iconAddress);
            if (direct != null) return direct;
        }

        var mapped = Icons?.GetSkillIcon(skillId);
        if (mapped != null) return mapped;

        return UIFactory.PlaceholderIcon(skillId);
    }

    /// <summary>
    /// Icon for an ability. Unlike items and skills this returns null when unmapped
    /// rather than a placeholder — the action bar already shows the ability's name,
    /// and a coloured square behind the text would only make it harder to read.
    /// </summary>
    public Sprite GetAbilityIcon(AbilityData ability)
    {
        if (ability == null) return null;

        if (!string.IsNullOrEmpty(ability.iconAddress))
        {
            var direct = SpriteLoader.Load(ability.iconAddress);
            if (direct != null) return direct;
        }

        return Icons?.GetAbilityIcon(ability.id);
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
                      $"{Zones.Count} zones, {Maps.Count} maps, {Skills.Count} skills, " +
                      $"{Classes.Count} classes, {ItemSets.Count} armour sets.");

            // Checked once, here, rather than discovered in a playtest: a set bonus
            // that names an action nothing implements costs the player six armour
            // slots and gives no sign that it is doing nothing at all.
            ItemSetManager.ValidateContent();

            _onComplete?.Invoke();
        }
    }
}
