using System;

/// <summary>
/// Central static event bus. All systems fire and listen here — no direct references needed.
/// Usage: GameEvents.OnItemPickedUp?.Invoke("iron_ore", 1500);
/// </summary>
public static class GameEvents
{
    // ── Account & Character ──────────────────────────────────────────────────
    public static event Action<AccountData>     OnAccountLoaded;
    public static event Action<CharacterData>   OnCharacterSelected;
    public static event Action<CharacterData>   OnCharacterCreated;
    public static event Action<int>             OnAccountLevelUp;       // new level
    public static event Action<int>             OnCharacterLevelUp;     // new level

    // ── Skills ───────────────────────────────────────────────────────────────
    public static event Action<string, int>     OnSkillLevelUp;         // skillId, newLevel
    public static event Action<string, long>    OnSkillXPGained;        // skillId, amount
    public static event Action<string, int>     OnMilestoneUnlocked;    // skillId, milestone (99/200/etc)

    // ── Activity (AFK / current action) ──────────────────────────────────────
    public static event Action<SkillActivityData>   OnActivityChanged;
    public static event Action<long>                OnAFKRewardsCollected;  // total gold value

    // ── Items & Loot ─────────────────────────────────────────────────────────
    public static event Action<string, long>    OnItemPickedUp;         // itemId, quantity
    public static event Action<int>             OnInventorySlotChanged; // slotIndex
    public static event Action                  OnInventoryChanged;     // full refresh

    // ── Merge Board ──────────────────────────────────────────────────────────
    public static event Action<int>             OnMergeSlotChanged;     // slotIndex
    public static event Action<string, string>  OnMergeCompleted;       // fromItemId, toItemId

    // ── World / Zones ─────────────────────────────────────────────────────────
    public static event Action<string>          OnMapEntered;           // mapId
    public static event Action<string>          OnZoneEntered;          // zoneId
    public static event Action<string>          OnSkillNodeInteracted;  // nodeId

    // ── Combat ───────────────────────────────────────────────────────────────
    public static event Action<string>          OnMonsterKilled;        // monsterId
    public static event Action<double>          OnPlayerDamageTaken;

    // ── Economy ──────────────────────────────────────────────────────────────
    public static event Action<long>            OnCoinsChanged;         // new total
    public static event Action                  OnAuctionListingPosted;
    public static event Action<string>          OnAuctionListingSold;   // listingId

    // ── Social ───────────────────────────────────────────────────────────────
    public static event Action<string>          OnFriendRequestReceived;    // fromAccountId
    public static event Action<string>          OnFriendRequestAccepted;
    public static event Action<GuildData>       OnGuildUpdated;

    // ── Collection ────────────────────────────────────────────────────────────
    public static event Action<string>          OnSpiritCollected;      // spiritId
    public static event Action<string>          OnRelicCollected;       // relicId
    public static event Action<string>          OnWardrobeItemUnlocked; // wardrobeId

    // ── UI ────────────────────────────────────────────────────────────────────
    public static event Action<string>          OnToastRequested;       // message
    public static event Action                  OnReturnToMainMenu;

    // ── Ghost System ──────────────────────────────────────────────────────────
    public static event Action<GhostSnapshot[]> OnGhostSnapshotsReceived;  // for current map

    // ── Convenience fire helpers ──────────────────────────────────────────────
    public static void FireItemPickedUp(string itemId, long qty)     => OnItemPickedUp?.Invoke(itemId, qty);
    public static void FireInventoryChanged()                         => OnInventoryChanged?.Invoke();
    public static void FireSkillLevelUp(string skillId, int level)   => OnSkillLevelUp?.Invoke(skillId, level);
    public static void FireActivityChanged(SkillActivityData data)   => OnActivityChanged?.Invoke(data);
    public static void FireToast(string message)                     => OnToastRequested?.Invoke(message);
    public static void FireMonsterKilled(string monsterId)           => OnMonsterKilled?.Invoke(monsterId);
    public static void FireMapEntered(string mapId)                  => OnMapEntered?.Invoke(mapId);
    public static void FireMergeCompleted(string from, string to)    => OnMergeCompleted?.Invoke(from, to);
}
