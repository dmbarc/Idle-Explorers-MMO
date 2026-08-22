using System;
using System.Collections.Generic;

// ═══════════════════════════════════════════════════════════════════════════════
//  All plain C# data structs used throughout the game.
//  These are serialized to/from JSON by ContentManager and the save system.
//  No MonoBehaviour or Unity types — pure data.
// ═══════════════════════════════════════════════════════════════════════════════

// ── Remote content data (loaded from Addressables JSON) ──────────────────────

/// <summary>
/// Something an item does. Kept as strings rather than enums so new effects ship in
/// item_data.json without a client rebuild, matching how AbilityData.effect works.
///
/// Triggers:
///   onConsume       — the player used it from the item menu
///   onEquipPassive  — always active while equipped
///   onCraft         — a craft completed at a station
///   onGather        — a gathering action completed
///   onKill          — a monster died
///   onHit           — a basic attack landed
///   onAbilityUse    — an action bar ability fired
///
/// Actions:
///   heal | damageSelf | grantAfkTime | statBonus |
///   doubleOutput | bonusXp | extraLoot | castEffect
/// </summary>
[Serializable]
public class ItemEffect
{
    public string trigger;
    public string action;
    public float  chance = 1f;      // 0-1; 1 = always
    public float  magnitude = 1f;
    public string param;            // action-specific: skillId, stat id, vfx id

    /// <summary>Shown verbatim in the tooltip, e.g. "Equip: has a chance to ...".</summary>
    public string equipText;

    public bool AlwaysFires => chance >= 1f;
}

[Serializable]
public class ItemData
{
    public string   id;
    public string   name;               // JSON field: "name"
    public string   description;
    public string   iconAddress;
    public bool     stackable;
    public int      levelReq;           // JSON field: "levelReq"
    public string   sourceSkill;        // JSON field: "sourceSkill"

    /// <summary>Empty when the item cannot be worn; otherwise a slot id.</summary>
    public string   equipSlot;

    public ItemEffect[] effects;

    // convenience aliases
    public string DisplayName => name;

    public bool IsEquippable => !string.IsNullOrEmpty(equipSlot);

    /// <summary>True when the item menu should offer Consume.</summary>
    public bool IsConsumable => HasTrigger("onConsume");

    public bool HasTrigger(string trigger)
    {
        if (effects == null) return false;
        foreach (var e in effects)
            if (e != null && e.trigger == trigger) return true;
        return false;
    }
}

[Serializable]
public class LootEntry
{
    public string   itemId;
    public long     minQty;             // JSON field: "minQty"
    public long     maxQty;             // JSON field: "maxQty"
    public int      weight;             // JSON field: "weight" — drop chance out of 100

    /// <summary>
    /// Drop chance as a 0–1 probability. The JSON stores it out of 100 (bones: 100
    /// always drops, skull: 20 drops one kill in five), so each entry is rolled
    /// independently rather than being selected from a weighted table.
    /// </summary>
    public float DropChance => UnityEngine.Mathf.Clamp01(weight / 100f);
}

[Serializable]
public class MonsterData
{
    public string       id;
    public string       name;           // JSON field: "name"
    public string       description;
    public int          level;
    public double       maxHp;
    public int          attackDamageMin;
    public int          attackDamageMax;
    public float        attackSpeedSeconds;
    public long         xpReward;
    public string       spriteAddress;
    public LootEntry[]  lootTable;
    public string DisplayName => name;
}

[Serializable]
public class SkillNodeEntry
{
    public string   nodeId;
    public string   skillId;
    public string   targetItemId;       // itemId produced by this node

    /// <summary>
    /// Empty for a gathering node, which yields targetItemId from nothing.
    /// Non-empty marks an interactive station that opens a panel instead:
    /// "bank" opens the vault, "campfire"/"forge" open the recipe list.
    ///
    /// Stations must NOT set targetItemId — that field is what made the campfire
    /// and both forges conjure cooked food and metal bars out of thin air.
    /// </summary>
    public string   stationType;
    public int      reqSkillLevel;
    public float    activeRateMulti;    // active play rate multiplier
    public float    afkRateMulti;       // AFK rate multiplier
    public float    xpPerAction;
    public float    specialChance;      // 0–1
    public string   specialLabel;       // name of the special drop
}

[Serializable]
public class MapData
{
    public string           id;
    public string           name;           // JSON field: "name"
    public string           description;
    public string           zoneId;
    public string           defaultMonsterId;
    public int              reqCharLevel;
    public int              reqAccountLevel;
    public int              reqAnyCharLevel;
    public string           sceneAddress;
    public SkillNodeEntry[] skillNodes;
    public string DisplayName => name;
}

[Serializable]
public class ZoneData
{
    public string     id;
    public string     name;             // JSON field: "name"
    public string     description;
    public string     backgroundAddress;
    public int        reqAccountLevel;
    public MapData[]  maps;             // embedded in zone_data.json
    public string DisplayName => name;
}

[Serializable]
public class SkillData
{
    public string   id;
    public string   name;               // JSON field: "name"
    public string   description;
    public string   iconAddress;
    public bool     hasAfkMode;         // JSON field: "hasAfkMode"
    public float    afkRateDefault;
    public string DisplayName => name;
}

[Serializable]
public class MilestoneUnlock
{
    public int      level;
    public string   title;
    public string   description;
    public string   unlockId;           // what is unlocked
}

[Serializable]
public class TalentNode
{
    public string   id;
    public string   name;
    public string   description;
    public string   effectType;
    public float    effectValue;
    public int      talentPointCost;
    public string[] requiresNodeIds;
}

[Serializable]
public class AbilityData
{
    public string   id;
    public string   name;
    public string   description;
    public bool     isPassive;

    /// <summary>
    /// What pressing this does. Kept as a string rather than an enum so new effects
    /// can ship in class_data.json without a client rebuild:
    ///   "damage"     — single target, scaled by power
    ///   "aoe"        — damages every monster within aoeRadius
    ///   "heal"       — restores power × maxHP as a fraction
    ///   "haste"      — multiplies attack speed for durationSeconds
    ///   "passive"    — never activates; shown greyed on the bar
    /// </summary>
    public string   effect;
    public float    power = 1f;            // damage multiplier, or heal fraction
    public float    cooldownSeconds = 6f;
    public float    aoeRadius = 5f;
    public float    durationSeconds = 5f;
    public string   iconAddress;

    public bool IsActivatable => !isPassive && effect != "passive";
}

[Serializable]
public class ClassData
{
    public string        id;
    public string        name;              // JSON field: "name"
    public string        flavorText;
    public int           baseHp;
    public int           baseMp;
    public int           baseAttackMin;
    public int           baseAttackMax;
    public float         attackSpeedSeconds;
    public AbilityData[] abilities;
    public TalentNode[]  talentTree;
    public string DisplayName => name;
}

[Serializable]
public class CraftIngredient
{
    public string itemId;
    public long   quantity = 1;
}

/// <summary>
/// A recipe at a crafting station. Unlike a gathering node, this CONSUMES its
/// inputs — which is the whole difference between cooking and conjuring.
/// </summary>
[Serializable]
public class CraftRecipe
{
    public string id;
    public string name;
    public string skillId;
    public string stationType;          // which station offers it: "campfire", "forge"
    public int    reqSkillLevel = 1;

    public CraftIngredient[] inputs;
    public string outputItemId;
    public long   outputQuantity = 1;

    public float  xpPerCraft;
    public float  baseSecondsPerCraft = 3f;

    public string DisplayName => string.IsNullOrEmpty(name) ? id : name;

    /// <summary>
    /// Seconds per craft at a given skill level. Higher level, faster work:
    /// level 1 is the base rate, level 99 roughly doubles it.
    /// </summary>
    public float SecondsPerCraft(int skillLevel)
    {
        float scaled = baseSecondsPerCraft / (1f + UnityEngine.Mathf.Max(0, skillLevel - 1) * 0.01f);
        return UnityEngine.Mathf.Max(0.1f, scaled);
    }
}

[Serializable]
public class MergeRecipe
{
    public string   id;
    public string   inputItemId;
    public int      inputQuantity;
    public string   secondaryItemId;
    public int      secondaryQuantity;
    public string   outputItemId;
    public int      outputQuantity;
    public int      reqConvergenceLevel;
    public float    xpGained;
}

[Serializable]
public class SlotUnlockRequirement
{
    public int slot;
    public int reqAccountLevel;
    public int reqAnyCharLevel;
}

// ── Save data (per player, server + local cache) ──────────────────────────────

[Serializable]
public class AccountData
{
    public string               accountId;
    public string               accountName;
    public int                  accountLevel;
    public long                 accountXP;
    public string               guildId;            // null if not in a guild
    public bool                 ghostsVisible;      // global ghost privacy toggle
    public List<CharacterData>  characters;

    // ── Account-wide bank ─────────────────────────────────────────────────────
    // Shared across every character on the account: what one character banks,
    // another can withdraw, and crafting stations can consume directly.
    public List<InventoryEntry> bank;
    public long                 bankCoins;

    public AccountData()
    {
        characters = new List<CharacterData>();
        bank = new List<InventoryEntry>();
        ghostsVisible = true;
    }
}

[Serializable]
public class CharacterData
{
    public string   characterId;
    public string   characterName;
    /// <summary>How many times this character has been renamed. Nothing gates on it
    /// yet; it exists so a future paid-rename has somewhere to hook.</summary>
    public int      renameCount;
    public string   classId;
    public int      level;
    public long     xp;
    public long     lastLogoutUnixTime;
    public bool     isOnline;
    public string   lastMapId;
    public bool     allowGhostDisplay;

    public SpumSaveData             spumConfig;
    public SkillActivityData        currentActivity;

    // Skill progress. Deliberately a List, not a Dictionary: JsonUtility cannot
    // serialize dictionaries — it writes them as empty without warning, so every
    // save would silently wipe all skill levels. Use GetSkill/GetOrCreateSkill.
    public List<SkillProgress>      skills;

    public List<InventoryEntry>     inventory;
    public List<InventoryEntry>     mergeBoard;
    public List<EquipmentEntry>     equipment;

    // Collection
    public string   equippedWardrobeId;
    public string[] equippedSpiritIds;
    public string[] equippedRelicIds;
    public string[] unlockedWardrobeIds;
    public string[] collectedSpiritIds;
    public string[] collectedRelicIds;

    // Class / combat
    public int[]    talentChoices;      // indices of chosen talent nodes in class's talent tree
    public long     coins;

    public CharacterData()
    {
        skills              = new List<SkillProgress>();
        inventory           = new List<InventoryEntry>();
        mergeBoard          = new List<InventoryEntry>();
        equipment           = new List<EquipmentEntry>();
        equippedSpiritIds   = new string[0];
        equippedRelicIds    = new string[0];
        unlockedWardrobeIds = new string[0];
        collectedSpiritIds  = new string[0];
        collectedRelicIds   = new string[0];
        talentChoices       = new int[0];
        allowGhostDisplay   = true;
    }

    /// <summary>Returns the skill's progress, or null if this character has never trained it.</summary>
    public SkillProgress GetSkill(string skillId)
    {
        if (skills == null) return null;
        foreach (var s in skills)
            if (s.skillId == skillId) return s;
        return null;
    }

    /// <summary>Returns the skill's progress, creating it at level 1 / 0 xp on first use.</summary>
    public SkillProgress GetOrCreateSkill(string skillId)
    {
        skills ??= new List<SkillProgress>();
        var existing = GetSkill(skillId);
        if (existing != null) return existing;

        var created = new SkillProgress { skillId = skillId, level = 1, xp = 0 };
        skills.Add(created);
        return created;
    }
}

[Serializable]
public class SkillProgress
{
    public string   skillId;
    public int      level = 1;
    public long     xp;
}

[Serializable]
public class InventoryEntry
{
    public string   itemId;
    public long     quantity;
}

/// <summary>
/// One worn item. A List of these rather than a Dictionary keyed by slot, because
/// JsonUtility silently serializes dictionaries as empty — the same trap that ate
/// every character's skill progress before skills moved to a List.
/// </summary>
[Serializable]
public class EquipmentEntry
{
    public string slotId;
    public string itemId;
}

[Serializable]
public class SkillActivityData
{
    public string   skillId;
    public string   activityTargetId;   // itemId, monsterId, or nodeId
    public string   activityTargetName; // display name cached
    public string   mapId;
    public float    activeRateMulti;
    public float    afkRateMulti;
    public float    specialChance;
    public string   specialChanceLabel;
    public float    xpPerHour;
    public long     activityStartUnixTime;

    /// <summary>
    /// Set when the activity is crafting at a station. Empty for gathering and combat.
    /// AFK accrual branches on this to consume inputs rather than conjure output.
    /// </summary>
    public string   recipeId;

    /// <summary>
    /// Seconds per action at 1.0x rate, captured when the activity started.
    ///
    /// AFK accrual used to invent its own action rate (skillLevel * 20 per hour)
    /// which disagreed with the live rate by ~60x at level 1, while AFK *XP* was
    /// derived from a third figure — so one offline session paid out XP and items
    /// that were mutually inconsistent. Carrying the real rate here is what lets
    /// both sides compute from the same number.
    /// </summary>
    public float    secondsPerAction;
}

// ── Ghost system ───────────────────────────────────────────────────────────────

[Serializable]
public class GhostFrame
{
    public float    timestamp;
    public float    posX, posY, posZ;
    public string   animationState;     // e.g. "1_Move", "2_Attack"
}

[Serializable]
public class GhostSnapshot
{
    public string       characterId;
    public string       characterName;
    public string       classId;
    public int          characterLevel;
    public string       monsterId;      // which monster they were fighting
    public SpumSaveData spumConfig;
    public string[]     spiritIds;      // spirits visible on ghost
    public string       relicId;        // relic glyph to show
    public GhostFrame[] frames;
    public long         logoutUnixTime;
}

// ── Guild system (stub) ────────────────────────────────────────────────────────

[Serializable]
public class GuildData
{
    public string   guildId;
    public string   guildName;
    public string   tag;
    public string[] memberAccountIds;
    public string   leaderId;
    public int      guildLevel;
}

// ── SPUM appearance serialization ─────────────────────────────────────────────

[Serializable]
public class SpumSaveData
{
    // Indices into SPUM's sprite lists for each equipment slot.
    // These are passed to SPUM's PlayerObj at runtime to reconstruct appearance.
    public int hairIndex;
    public int faceIndex;
    public int bodyIndex;
    public int armorIndex;
    public int pantsIndex;
    public int shoesIndex;
    public int weaponIndex;
    public int backWeaponIndex;
}

// ── Serialization helper for Vector3 (avoids Unity dependency in JSON) ─────────

[Serializable]
public class Vector3Data
{
    public float x, y, z;
    public UnityEngine.Vector3 ToVector3() => new UnityEngine.Vector3(x, y, z);
    public static Vector3Data From(UnityEngine.Vector3 v) => new Vector3Data { x = v.x, y = v.y, z = v.z };
}
