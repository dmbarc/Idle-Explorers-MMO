using System;
using System.Collections.Generic;

// ═══════════════════════════════════════════════════════════════════════════════
//  SAVE DATA — one player's state, as the local cache holds it.
//
//  The authored game moved to Assets/Scripts/Rules/Content/ContentData.cs, because
//  the server compiles that tree too and has to read the same content the client
//  reads. What is left here is per-player state, which the server will own outright:
//  these classes become the shape of an API response rather than the shape of a file
//  on disk, and account.json becomes a cache that can be deleted without loss.
//
//  Unity types are allowed here — Vector3Data at the bottom needs one.
// ═══════════════════════════════════════════════════════════════════════════════

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

    /// <summary>
    /// Which save format this account was last written by. Zero for anything written
    /// before versioning existed, which is exactly what a one-time migration needs to
    /// recognise. See SaveManager.CurrentSaveVersion.
    /// </summary>
    public int                  saveVersion;

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

    /// <summary>
    /// The five action-bar slots, as ability ids. An empty string is an empty slot.
    ///
    /// This is the indirection that makes the bar arrangeable. Abilities used to be
    /// resolved as ClassData.abilities[slot] in two places independently, so slot
    /// position WAS the ability's identity and there was nothing to rearrange. Now
    /// PlayerController.GetAbility is the single place a slot becomes an ability.
    /// </summary>
    public List<string> hotbar;

    /// <summary>The hotbar, always exactly HotbarSlots long.</summary>
    public List<string> Hotbar()
    {
        hotbar ??= new List<string>();

        while (hotbar.Count < HotbarSlots) hotbar.Add("");
        if (hotbar.Count > HotbarSlots) hotbar.RemoveRange(HotbarSlots, hotbar.Count - HotbarSlots);

        return hotbar;
    }

    public const int HotbarSlots = 5;

    public long     coins;

    /// <summary>
    /// How many times this character has changed class. Nothing gates on it; it exists
    /// so a future limit or price has somewhere to hook, the same way renameCount does.
    /// </summary>
    public int      classChangeCount;

    /// <summary>
    /// Every class this character has specced into, primary first.
    ///
    /// `classId` above is kept in sync as a legacy mirror — GhostSnapshot reads it and
    /// so does every save written before cross-speccing — but THIS is authoritative.
    /// CharacterManager is the only place both are written, so they cannot drift.
    ///
    /// Use ClassIds() rather than this field directly: a save from before cross-spec
    /// has an empty list and a populated classId, and the helper covers that.
    /// </summary>
    public List<string> classIds;

    /// <summary>
    /// The classes this character has, in order. Never null, never empty for a
    /// character that has a class at all.
    /// </summary>
    public List<string> ClassIds()
    {
        classIds ??= new List<string>();

        // Migration in the accessor rather than a load pass, so it also covers a
        // CharacterData built in code or arriving from a future server.
        if (classIds.Count == 0 && !string.IsNullOrEmpty(classId))
            classIds.Add(classId);

        return classIds;
    }

    /// <summary>How many classes this character may have, from their level.</summary>
    public int ClassSlots()
    {
        if (level >= ClassSlotThreeLevel) return 3;
        if (level >= ClassSlotTwoLevel)   return 2;
        return 1;
    }

    /// <summary>Character level at which a second class unlocks.</summary>
    public const int ClassSlotTwoLevel = 50;

    /// <summary>Character level at which a third class unlocks.</summary>
    public const int ClassSlotThreeLevel = 100;

    /// <summary>True when a class slot is unlocked and still empty.</summary>
    public bool HasUnusedClassSlot() => ClassIds().Count < ClassSlots();

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
        hotbar              = new List<string>();
        classIds            = new List<string>();
        storedDurability    = new List<ItemDurability>();
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

// ── Serialization helper for Vector3 (avoids Unity dependency in JSON) ─────────

[Serializable]
public class Vector3Data
{
    public float x, y, z;
    public UnityEngine.Vector3 ToVector3() => new UnityEngine.Vector3(x, y, z);
    public static Vector3Data From(UnityEngine.Vector3 v) => new Vector3Data { x = v.x, y = v.y, z = v.z };
}
