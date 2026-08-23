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

    /// <summary>
    /// Armour set this piece belongs to, matching an id in set_data.json. Empty for
    /// everything that is not part of a set.
    /// </summary>
    public string   setId;

    /// <summary>
    /// Points of wear this piece can take before it stops working. ZERO MEANS
    /// INDESTRUCTIBLE, which is the right default: rings, trinkets and companions
    /// have no business degrading, and neither does anything authored before
    /// durability existed.
    ///
    /// A broken piece is never destroyed — it stays worn and stops contributing its
    /// stats until repaired. Deleting gear a player earned because they forgot to
    /// check a bar is a punishment, not a mechanic.
    /// </summary>
    public int      maxDurability;

    /// <summary>
    /// Resources path to the sprite drawn ON THE CHARACTER when this is worn —
    /// deliberately separate from iconAddress, which is the inventory icon. An
    /// inventory icon pasted onto a SPUM layer would be the wrong art at the wrong
    /// scale. Empty means the item equips without changing how the character looks.
    /// </summary>
    public string   equipSpriteAddress;

    public ItemEffect[] effects;

    // convenience aliases
    public string DisplayName => name;

    public bool IsEquippable => !string.IsNullOrEmpty(equipSlot);

    /// <summary>True when this piece wears out and can be repaired.</summary>
    public bool HasDurability => maxDurability > 0;

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

/// <summary>
/// One node in a class's talent tree.
///
/// effectType is a string rather than an enum for the same reason AbilityData.effect
/// is: a new talent should be a content change, not a client rebuild. Every value
/// TalentBonuses understands is listed there, and one it does not understand is
/// logged rather than silently ignored — a talent that costs a point and does
/// nothing is worse than one that does not exist.
///
/// effectValue is PER RANK and expressed as a fraction (0.04 = 4%), except for the
/// flat types where it is an absolute amount.
/// </summary>
[Serializable]
public class TalentNode
{
    public string   id;
    public string   name;
    public string   description;
    public string   effectType;
    public float    effectValue;

    /// <summary>How many points can be sunk into this node. 1 for a single toggle.</summary>
    public int      maxRank = 1;

    /// <summary>Row in the tree. Tier N needs TierPointRequirement(N) points spent below it.</summary>
    public int      tier;

    /// <summary>Position within its row, left to right.</summary>
    public int      column;

    public int      talentPointCost = 1;

    /// <summary>Optional hard prerequisites, on top of the tier's point requirement.</summary>
    public string[] requiresNodeIds;

    public int PointCost => UnityEngine.Mathf.Max(1, talentPointCost);
    public int RankCap   => UnityEngine.Mathf.Max(1, maxRank);
}

/// <summary>
/// How many points a character has put into one talent node.
///
/// A List of these rather than the int[] of node indices this replaces: indices
/// break the moment a talent is inserted into the middle of a tree, silently moving
/// every character's choices onto different talents. Ids survive content edits, and
/// an id that no longer exists is simply skipped.
/// </summary>
[Serializable]
public class TalentRank
{
    public string nodeId;
    public int    rank;
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

    /// <summary>
    /// Resources/VFX prefab played on cast. Empty falls back to a per-effect-type
    /// default, so a new ability has a visual before it has bespoke art.
    /// </summary>
    public string   vfxAddress;

    /// <summary>How many times a "damage" ability strikes. Rapid Shot fires three.</summary>
    public int      hits = 1;

    /// <summary>Fraction of damage dealt returned as healing. Soul Drain uses this.</summary>
    public float    lifestealFraction;

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

    /// <summary>
    /// How this class is dressed on the character-select preview — a signature weapon
    /// and colouring, so the five cards read as five different people rather than the
    /// same figure five times. Purely cosmetic; it is not the character's starting gear.
    /// </summary>
    public SpumSaveData previewLook;

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

// ── Armour sets ───────────────────────────────────────────────────────────────

/// <summary>
/// One threshold in a set: what you get for wearing N pieces at once.
///
/// `action` is a string for the same reason ItemEffect.action and AbilityData.effect
/// are — a new set bonus should be a content change, not a client rebuild. Every
/// value SetBonusResolver understands is listed there, and one it does not
/// understand is reported by ItemSetManager.ValidateContent rather than silently
/// costing the player four armour slots for nothing.
/// </summary>
[Serializable]
public class ItemSetBonus
{
    /// <summary>How many pieces of the set must be worn for this to be active.</summary>
    public int    piecesRequired;

    /// <summary>Shown verbatim in the tooltip, after "N Set Bonus - ".</summary>
    public string description;

    public string action;

    /// <summary>0-1 chance for proc bonuses. Ignored by always-on ones.</summary>
    public float  chance;

    /// <summary>Action-specific size: a damage multiplier, a stat amount, a fraction.</summary>
    public float  magnitude = 1f;

    /// <summary>Action-specific target: a stat id for statBonus, unused elsewhere.</summary>
    public string param;

    /// <summary>Metres, for the bonuses that hit everything nearby.</summary>
    public float  radius = 6f;
}

/// <summary>
/// An armour set. The piece list is authoritative and ORDERED — the tooltip prints
/// it in this order, so it reads head to toe rather than in whatever sequence the
/// items happen to appear in item_data.json.
/// </summary>
[Serializable]
public class ItemSetData
{
    public string         id;
    public string         name;
    public string[]       itemIds;
    public ItemSetBonus[] bonuses;

    public string DisplayName => string.IsNullOrEmpty(name) ? id : name;

    public int PieceCount => itemIds?.Length ?? 0;
}

// ── Shop ──────────────────────────────────────────────────────────────────────

/// <summary>
/// A bundle of relic coins bought with real money.
///
/// priceUsd is display only. The authoritative price comes from the store at
/// runtime once real IAP is wired — a hardcoded price shown next to a store's own
/// localised, tax-adjusted figure is how a shop ends up lying about what it costs.
/// </summary>
[Serializable]
public class RelicCoinPack
{
    public string id;
    public string name;

    /// <summary>Store product id (Google Play / App Store). Reserved for Phase 8.</summary>
    public string productId;

    public long   coins;
    public float  priceUsd;

    /// <summary>Optional flash such as "BEST VALUE". Empty for most rows.</summary>
    public string badge;

    public string DisplayName  => string.IsNullOrEmpty(name) ? id : name;
    public string DisplayPrice => $"${priceUsd:0.00}";

    /// <summary>Coins per dollar — the number that tells you whether a ladder is honest.</summary>
    public float CoinsPerDollar => priceUsd > 0f ? coins / priceUsd : 0f;
}

/// <summary>Something bought with relic coins rather than money.</summary>
[Serializable]
public class ShopProduct
{
    public string id;
    public string name;
    public string description;

    /// <summary>Item granted on purchase.</summary>
    public string itemId;
    public long   quantity = 1;
    public long   relicCoinCost;

    public string DisplayName => string.IsNullOrEmpty(name) ? id : name;
}

/// <summary>
/// Root of shop_data.json. A root object rather than an array, because this file
/// carries two lists — ContentManager's array wrapper only handles one.
/// </summary>
[Serializable]
public class ShopCatalog
{
    public RelicCoinPack[] coinPacks;
    public ShopProduct[]   products;
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

    /// <summary>
    /// Premium currency, bought with money and — later — earned very rarely in game.
    ///
    /// On the ACCOUNT, not the character: paying for something and then finding it
    /// stranded on a character you have stopped playing is indefensible. Same reason
    /// the bank is account-wide.
    /// </summary>
    public long                 relicCoins;

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

    /// <summary>
    /// Durability remembered for gear that is NOT currently worn.
    ///
    /// Without this, taking a helmet off and putting it back on is a free repair —
    /// the inventory has no per-item condition to carry, so the piece would come back
    /// pristine and durability would be theatre. Keyed by itemId, which is exact for
    /// anything unique and a reasonable average for the rare case of owning two of the
    /// same piece.
    /// </summary>
    public List<ItemDurability>     storedDurability;

    // Collection
    public string   equippedWardrobeId;
    public string[] equippedSpiritIds;
    public string[] equippedRelicIds;
    public string[] unlockedWardrobeIds;
    public string[] collectedSpiritIds;
    public string[] collectedRelicIds;

    // Class / combat
    //
    // Replaces an int[] of indices into the class's talent tree, which nothing ever
    // read or wrote — and which would have silently reassigned every character's
    // talents the first time a node was inserted into the middle of a tree.
    public List<TalentRank> talents;
    public long     coins;

    /// <summary>
    /// How many times this character has changed class. Nothing gates on it; it exists
    /// so a future limit or price has somewhere to hook, the same way renameCount does.
    /// </summary>
    public int      classChangeCount;

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
        talents             = new List<TalentRank>();
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

/// <summary>Condition remembered for a piece of gear while it is off the character.</summary>
[Serializable]
public class ItemDurability
{
    public string itemId;
    public int    durability;
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

    /// <summary>Wear left on this piece. Meaningless when the item has no maxDurability.</summary>
    public int    durability;

    /// <summary>
    /// Whether `durability` has ever been written.
    ///
    /// Load-bearing, because JsonUtility fills a missing int with 0 and 0 durability
    /// means BROKEN. Without this flag every piece of gear in every save written
    /// before durability existed would come back shattered. A bool defaults to false,
    /// which reads as "never initialised" — the only default that is safe here.
    /// </summary>
    public bool   durabilitySet;
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

/// <summary>
/// How a character looks, underneath whatever they are wearing.
///
/// This used to be eight ints indexed into "SPUM's sprite lists", with a comment
/// saying they were passed to SPUM's PlayerObj at runtime. Neither half was true:
/// SPUM ships no runtime appearance API at all — no script in the package assigns a
/// sprite — and nothing in this project ever read the fields. They were written
/// all-zero at character creation and that was the end of it.
///
/// So it is redefined as Resources addresses, which is what the rig actually needs.
/// The format mirrors the manifest SPUM already bakes into each prefab
/// (SPUM_Prefabs.ImageElement), where an ItemPath plus a Structure name resolves
/// directly through SpriteLoader. Indices would have broken the moment a sprite pack
/// was reordered; addresses survive it.
///
/// Colours are hex strings rather than UnityEngine.Color, because JsonUtility writes
/// a Color as four floats and this has to stay legible in account.json.
/// </summary>
[Serializable]
public class SpumSaveData
{
    /// <summary>Body sheet — supplies head, torso, both arms and both feet.</summary>
    public string bodyAddress;

    public string hairAddress;
    public string faceHairAddress;
    public string eyeAddress;

    /// <summary>Held weapon. Cosmetic only — it is not the equipment system.</summary>
    public string weaponAddress;

    /// <summary>Cape or quiver behind the character, under any equipped cape.</summary>
    public string backAddress;

    // "#RRGGBB", or empty for the sprite's own colours.
    public string hairColor;
    public string eyeColor;
    public string skinTint;

    /// <summary>True when this has never been filled in — an old save, or a default.</summary>
    public bool IsEmpty => string.IsNullOrEmpty(bodyAddress) &&
                           string.IsNullOrEmpty(hairAddress) &&
                           string.IsNullOrEmpty(eyeAddress);

    public SpumSaveData Clone() => (SpumSaveData)MemberwiseClone();
}

// ── Serialization helper for Vector3 (avoids Unity dependency in JSON) ─────────

[Serializable]
public class Vector3Data
{
    public float x, y, z;
    public UnityEngine.Vector3 ToVector3() => new UnityEngine.Vector3(x, y, z);
    public static Vector3Data From(UnityEngine.Vector3 v) => new Vector3Data { x = v.x, y = v.y, z = v.z };
}
