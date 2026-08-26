using System;

// ═══════════════════════════════════════════════════════════════════════════════
//  CONTENT — the authored game, as the twelve StreamingAssets JSON files declare it.
//
//  ══ WHY THIS LIVES IN THE SHARED RULES TREE ═══════════════════════════════════
//
//  The server seeds its content tables from these same files and deserialises them
//  into these same classes. Not a copy, not a hand-written mapping — one definition,
//  compiled twice.
//
//  The alternative was a set of server-side "spec" structs projected from these, and
//  that reintroduces exactly the drift the shared tree exists to remove: the mapping
//  is code, code rots, and a recipe that costs 2,000 bars on one side and 2 on the
//  other is an economy bug nobody can see in a diff.
//
//  Content is also the reason the client can still be quick. A tooltip saying
//  "requires Mining 15" and a cooldown sweep need these numbers locally. The
//  distinction that matters is not what the client KNOWS — it is what the client is
//  BELIEVED about, and that is nothing.
//
//  Nothing here may reference UnityEngine. Save data lives in Core/Data/GameData.cs,
//  which is Unity-side and free to.
// ═══════════════════════════════════════════════════════════════════════════════


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

    // ── Weapons ───────────────────────────────────────────────────────────────
    //
    // Additive, so no save migration: an item authored before these existed reads
    // back as an empty weaponType, which is exactly what "not a weapon" means.
    //
    // Damage lives on the item rather than being derived from a tier, because the
    // boss drops are meant to feel individually authored — a spear that reaches
    // further, a bow that fires three arrows — and a formula would flatten them.

    /// <summary>Empty, "melee" or "ranged". Empty means this is not a weapon.</summary>
    public string weaponType;

    /// <summary>
    /// How close the character has to get. Zero falls back to the unarmed reach,
    /// which is what an item with no authored range should behave as.
    /// </summary>
    public float  attackRange;

    /// <summary>Seconds between swings. Zero means "use the character's own speed".</summary>
    public float  attackSpeedSeconds;

    public float  damageMin;
    public float  damageMax;

    /// <summary>Arrows per shot. Zero and one both mean one; the Trisong Bow is three.</summary>
    public int    projectilesPerShot;

    /// <summary>Occupies both hands: equipping it takes the off hand off.</summary>
    public bool   twoHanded;

    public ItemEffect[] effects;

    // convenience aliases
    public string DisplayName => name;

    public bool IsEquippable => !string.IsNullOrEmpty(equipSlot);

    /// <summary>True when this item can be swung or fired.</summary>
    public bool IsWeapon => !string.IsNullOrEmpty(weaponType);

    /// <summary>True when attacking with it launches a projectile.</summary>
    public bool IsRanged => weaponType == "ranged";

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
    public float DropChance => IdleExplorers.Rules.RulesMath.Clamp01(weight / 100f);
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

    /// <summary>
    /// The ability this node concerns, if any.
    ///
    /// With effectType "grantAbility" the first rank unlocks it and further ranks make
    /// it stronger. With any other effectType, the node's bonus is SCOPED to just this
    /// ability rather than the whole character — so "Fireball costs 20% less mana" is
    /// expressible without a new effect type, and TalentManager.Bonus knows to leave
    /// ability-scoped nodes out of character-wide totals.
    /// </summary>
    public string   abilityId;

    /// <summary>True when taking this node puts a new ability in the player's hands.</summary>
    public bool GrantsAbility =>
        effectType == "grantAbility" && !string.IsNullOrEmpty(abilityId);

    public int PointCost => Math.Max(1, talentPointCost);
    public int RankCap   => Math.Max(1, maxRank);
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

    /// <summary>
    /// Which pool this ability spends: "mana", "stamina", or empty for free.
    ///
    /// This is what makes mana and stamina two different resources rather than two
    /// identical bars — a Sorcerer is limited by mana and a Warrior by stamina, so the
    /// same number on a piece of gear means different things to each.
    /// </summary>
    public string   costType;

    public float    cost;

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
    /// <summary>
    /// What this class ADDS to the shared baseline in base_stats.json.
    ///
    /// A contribution rather than a complete stat line, because a character can carry
    /// up to three classes and they stack. The legacy baseHp/baseMp/baseAttack fields
    /// above are kept only so the character-select card can show a familiar summary;
    /// nothing in combat reads them any more.
    /// </summary>
    public StatBlock     stats;

    public AbilityData[] abilities;
    public TalentNode[]  talentTree;

    /// <summary>
    /// How this class is dressed on the character-select preview — a signature weapon
    /// and colouring, so the five cards read as five different people rather than the
    /// same figure five times. Purely cosmetic; it is not the character's starting gear.
    /// </summary>
    public SpumSaveData previewLook;

    /// <summary>
    /// Armour worn on the preview, on top of previewLook.
    ///
    /// Separate from previewLook because SpumSaveData describes a BODY — race, hair,
    /// eyes, weapon — and these are equipment slots, addressed exactly the way worn
    /// items are. Every class used to show the same plate and helmet, which was not a
    /// choice anyone had made: it was the SPUM prefab's own clothing, which nothing
    /// ever took off.
    /// </summary>
    public PreviewEquipment[] previewEquipment;

    public string DisplayName => name;
}

/// <summary>One piece of armour on a class card, by equipment slot and sprite sheet.</summary>
[Serializable]
public class PreviewEquipment
{
    /// <summary>An EquipmentSlots id: "chest", "helmet", "legs", "boots"…</summary>
    public string slot;

    /// <summary>A Resources address, the same form items use for equipSpriteAddress.</summary>
    public string sprite;
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
        float scaled = baseSecondsPerCraft / (1f + Math.Max(0, skillLevel - 1) * 0.01f);
        return Math.Max(0.1f, scaled);
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

/// <summary>
/// A named combination of classes — Warrior + Sorcerer is a Paladin.
///
/// Matched order-insensitively by ClassManager, so the file lists each combination
/// once rather than once per ordering.
/// </summary>
[Serializable]
public class SpecCombo
{
    public string[] classIds;
    public string   name;
    public string   description;

    public string DisplayName => string.IsNullOrEmpty(name) ? "" : name;
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

    /// <summary>
    /// Points of durability the bonus costs the wearer when it fires.
    ///
    /// Separate from magnitude because the tin set's shard burst needs both: how hard
    /// the blast hits, and how much armour it costs to throw. It used to cost the
    /// whole piece, which took that piece out of the set and turned the 2-piece bonus
    /// into a way of switching the 6-piece bonus off.
    /// </summary>
    public int    durabilityCost;
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
