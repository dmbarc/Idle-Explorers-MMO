using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;
using IdleExplorers.Rules;

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

    /// <summary>
    /// The authored game, indexed -- and indexed by code the SERVER also runs.
    ///
    /// This used to be thirteen dictionaries and lists owned here, with the lookups
    /// written beside them. They moved into the shared rules tree, because the server
    /// has to answer the same questions from the same twelve files and a second index
    /// over the same data is a second thing to get wrong.
    ///
    /// What is left on this side is the half the two hosts genuinely cannot share:
    /// fetching the files (UnityWebRequest here, the filesystem there), parsing them
    /// (JsonUtility here, System.Text.Json there), and sprites, which mean nothing to
    /// a server. The properties below forward so that every existing call site --
    /// GameManager.Content.Items[id] and the rest -- keeps working unchanged.
    /// </summary>
    public GameContent Catalogue { get; } = new GameContent();

    public Dictionary<string, ItemData>    Items        => Catalogue.Items;
    public Dictionary<string, MonsterData> Monsters     => Catalogue.Monsters;
    public Dictionary<string, ZoneData>    Zones        => Catalogue.Zones;
    public Dictionary<string, MapData>     Maps         => Catalogue.Maps;
    public Dictionary<string, SkillData>   Skills       => Catalogue.Skills;
    public Dictionary<string, ClassData>   Classes      => Catalogue.Classes;
    public Dictionary<string, ItemSetData> ItemSets     => Catalogue.ItemSets;
    public List<MergeRecipe>               MergeRecipes => Catalogue.MergeRecipes;
    public List<SlotUnlockRequirement>     SlotUnlocks  => Catalogue.SlotUnlocks;
    public List<CraftRecipe>               CraftRecipes => Catalogue.CraftRecipes;
    public List<RelicCoinPack>             CoinPacks    => Catalogue.CoinPacks;
    public List<ShopProduct>               ShopProducts => Catalogue.ShopProducts;
    public List<SpecCombo>                 SpecCombos   => Catalogue.SpecCombos;

    /// <summary>
    /// The stat baseline every character starts from, before any class.
    ///
    /// Never null: a missing or malformed base_stats.json leaves an empty block rather
    /// than a null reference, so combat still runs — badly, and loudly, but it runs.
    /// </summary>
    public StatBlock BaseStats => Catalogue.BaseStats;

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

        // Parsing is ours; indexing belongs to the shared catalogue. The server calls
        // the same Ingest methods with the same arrays, having read the same files a
        // different way — see IdleExplorers.Content.ContentFiles. The SET of files
        // here and there has to stay in step, which is why both sides list them.
        LoadJson<ItemList>   ("item_data",     "items",    j => Catalogue.IngestItems(j.items));
        LoadJson<MonsterList>("monster_data",  "monsters", j => Catalogue.IngestMonsters(j.monsters));
        LoadJson<ZoneList>   ("zone_data",     "zones",    j => Catalogue.IngestZones(j.zones));
        LoadJson<SkillList>  ("skill_data",    "skills",   j => Catalogue.IngestSkills(j.skills));
        LoadJson<ClassList>  ("class_data",    "classes",  j => Catalogue.IngestClasses(j.classes));
        LoadJson<MergeList>  ("merge_recipes", "recipes",  j => Catalogue.IngestMergeRecipes(j.recipes));
        LoadJson<SlotList>   ("slot_unlock",   "slots",    j => Catalogue.IngestSlotUnlocks(j.slots));
        LoadJson<CraftList>  ("recipe_data",   "recipes",  j => Catalogue.IngestCraftRecipes(j.recipes));
        LoadJson<SetList>    ("set_data",      "sets",     j => Catalogue.IngestItemSets(j.sets));
        LoadJson<SpecList>   ("spec_data",     "specs",    j => Catalogue.IngestSpecCombos(j.specs));

        // The two root OBJECTS. They are not lists, so the array wrapper in
        // LoadJsonRoutine is bypassed and their wrapField goes unused.
        LoadJson<StatBlock>  ("base_stats",    "",         j => Catalogue.IngestBaseStats(j));
        LoadJson<ShopCatalog>("shop_data",     "",         j => Catalogue.IngestShop(j));
    }

    // ── Shop ──────────────────────────────────────────────────────────────────

    public RelicCoinPack GetCoinPack(string id)    => Catalogue.GetCoinPack(id);
    public ShopProduct   GetShopProduct(string id) => Catalogue.GetShopProduct(id);

    // ── Crafting recipes ──────────────────────────────────────────────────────

    public CraftRecipe GetRecipe(string recipeId) => Catalogue.GetRecipe(recipeId);

    /// <summary>Every recipe a given station offers, in file order.</summary>
    public List<CraftRecipe> GetRecipesForStation(string stationType) =>
        Catalogue.GetRecipesForStation(stationType);

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

    /// <summary>
    /// Emblem for a class. Never null — the HUD shows it in place of the class name,
    /// so a missing mapping has to be a shape rather than a gap.
    /// </summary>
    public Sprite GetClassIcon(string classId)
    {
        var mapped = Icons?.GetClassIcon(classId);
        if (mapped != null) return mapped;

        return UIFactory.PlaceholderIcon(classId);
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
    public ItemData    GetItem(string id)    => Catalogue.GetItem(id);
    public MonsterData GetMonster(string id) => Catalogue.GetMonster(id);
    public SkillData   GetSkill(string id)   => Catalogue.GetSkill(id);
    public ClassData   GetClass(string id)   => Catalogue.GetClass(id);
    public MapData     GetMap(string id)     => Catalogue.GetMap(id);
    public ZoneData    GetZone(string id)    => Catalogue.GetZone(id);

    public MergeRecipe GetMergeRecipe(string inputItemId) => Catalogue.GetMergeRecipe(inputItemId);

    public bool IsSlotUnlocked(int slotIndex, int accountLevel, int highestCharLevel) =>
        Catalogue.IsSlotUnlocked(slotIndex, accountLevel, highestCharLevel);

    // ── Private: load from StreamingAssets ────────────────────────────────────
    private void LoadJson<T>(string fileName, string wrapField, Action<T> onParsed)
    {
        StartCoroutine(LoadJsonRoutine<T>(fileName + ".json", wrapField, onParsed));
    }

    private IEnumerator LoadJsonRoutine<T>(string fileName, string wrapField, Action<T> onParsed)
    {
        // StreamingAssets requires UnityWebRequest on every platform, but what
        // streamingAssetsPath MEANS differs by three, and getting it wrong is total:
        // the catalogue comes back empty and the game boots into a world with no
        // items, no monsters and no recipes.
        string uri = Path.Combine(Application.streamingAssetsPath, fileName);

#if UNITY_WEBGL && !UNITY_EDITOR
        // Already an absolute http(s) URL on the web -- the build is being served,
        // not read. Prefixing file:/// produces "file:///https://..." and every one
        // of the twelve loads fails.
        uri = uri.Replace("\\", "/");
#elif UNITY_ANDROID && !UNITY_EDITOR
        // Android StreamingAssets live inside the APK -- the jar:// URI is used as-is.
#else
        // A real path on disk, which UnityWebRequest needs as a file URL.
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
                      $"{Classes.Count} classes, {ItemSets.Count} armor sets.");

            // Checked once, here, rather than discovered in a playtest: a set bonus
            // that names an action nothing implements costs the player six armour
            // slots and gives no sign that it is doing nothing at all.
            ItemSetManager.ValidateContent();

            _onComplete?.Invoke();
        }
    }
}
