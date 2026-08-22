using System;
using System.Collections.Generic;

// ═══════════════════════════════════════════════════════════════════════════════
//  All plain C# data structs used throughout the game.
//  These are serialized to/from JSON by ContentManager and the save system.
//  No MonoBehaviour or Unity types — pure data.
// ═══════════════════════════════════════════════════════════════════════════════

// ── Remote content data (loaded from Addressables JSON) ──────────────────────

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
    // convenience aliases
    public string DisplayName => name;
}

[Serializable]
public class LootEntry
{
    public string   itemId;
    public long     minQty;             // JSON field: "minQty"
    public long     maxQty;             // JSON field: "maxQty"
    public int      weight;             // relative drop weight
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

    public AccountData()
    {
        characters = new List<CharacterData>();
        ghostsVisible = true;
    }
}

[Serializable]
public class CharacterData
{
    public string   characterId;
    public string   characterName;
    public string   classId;
    public int      level;
    public long     xp;
    public long     lastLogoutUnixTime;
    public bool     isOnline;
    public string   lastMapId;
    public bool     allowGhostDisplay;

    public SpumSaveData             spumConfig;
    public SkillActivityData        currentActivity;

    public Dictionary<string, int>  skillLevels;    // skillId → level (1–999)
    public Dictionary<string, long> skillXP;        // skillId → total XP

    public List<InventoryEntry>     inventory;
    public List<InventoryEntry>     mergeBoard;

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
        skillLevels         = new Dictionary<string, int>();
        skillXP             = new Dictionary<string, long>();
        inventory           = new List<InventoryEntry>();
        mergeBoard          = new List<InventoryEntry>();
        equippedSpiritIds   = new string[0];
        equippedRelicIds    = new string[0];
        unlockedWardrobeIds = new string[0];
        collectedSpiritIds  = new string[0];
        collectedRelicIds   = new string[0];
        talentChoices       = new int[0];
        allowGhostDisplay   = true;
    }
}

[Serializable]
public class InventoryEntry
{
    public string   itemId;
    public long     quantity;
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
