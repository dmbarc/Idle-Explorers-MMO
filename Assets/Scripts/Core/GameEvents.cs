using System;
using UnityEngine;

/// <summary>
/// Central static event bus. All systems fire and listen here — no direct references needed.
/// Usage: GameEvents.OnItemPickedUp?.Invoke("iron_ore", 1500);
///
/// These are plain static Action fields rather than C# events on purpose: an `event`
/// can only be invoked from inside its declaring type, which would force a Fire*
/// helper for every single one. The trade-off is that any caller could reassign a
/// delegate instead of subscribing — always use += / -=, never =.
/// </summary>
public static class GameEvents
{
    // ── Account & Character ──────────────────────────────────────────────────
    public static Action<AccountData>     OnAccountLoaded;
    public static Action<CharacterData>   OnCharacterSelected;
    public static Action<CharacterData>   OnCharacterCreated;
    public static Action<int>             OnAccountLevelUp;       // new level
    public static Action<int>             OnCharacterLevelUp;     // new level

    // ── Skills ───────────────────────────────────────────────────────────────
    public static Action<string, int>     OnSkillLevelUp;         // skillId, newLevel
    public static Action<string, long>    OnSkillXPGained;        // skillId, amount
    public static Action<string, int>     OnMilestoneUnlocked;    // skillId, milestone (99/200/etc)

    // ── Activity (AFK / current action) ──────────────────────────────────────
    public static Action<SkillActivityData>   OnActivityChanged;
    public static Action<long>                OnAFKRewardsCollected;  // elapsed seconds

    // ── Items & Loot ─────────────────────────────────────────────────────────
    public static Action<string, long>    OnItemPickedUp;         // itemId, quantity
    public static Action<int>             OnInventorySlotChanged; // slotIndex
    public static Action                  OnInventoryChanged;     // full refresh

    // ── Merge Board ──────────────────────────────────────────────────────────
    public static Action<int>             OnMergeSlotChanged;     // slotIndex
    public static Action<string, string>  OnMergeCompleted;       // fromItemId, toItemId

    // ── World / Zones ─────────────────────────────────────────────────────────
    public static Action<string>          OnMapEntered;           // mapId
    public static Action<string>          OnZoneEntered;          // zoneId
    public static Action<string>          OnSkillNodeInteracted;  // nodeId

    // ── Combat ───────────────────────────────────────────────────────────────
    public static Action<string>          OnMonsterKilled;        // monsterId
    public static Action<double>          OnPlayerDamageTaken;
    public static Action<double, double>  OnPlayerHealthChanged;  // current, max
    public static Action                  OnPlayerDied;

    // ── Economy ──────────────────────────────────────────────────────────────
    public static Action<long>            OnCoinsChanged;         // new total
    public static Action                  OnAuctionListingPosted;
    public static Action<string>          OnAuctionListingSold;   // listingId

    // ── Social ───────────────────────────────────────────────────────────────
    public static Action<string>          OnFriendRequestReceived;    // fromAccountId
    public static Action<string>          OnFriendRequestAccepted;
    public static Action<GuildData>       OnGuildUpdated;

    // ── Collection ────────────────────────────────────────────────────────────
    public static Action<string>          OnSpiritCollected;      // spiritId
    public static Action<string>          OnRelicCollected;       // relicId
    public static Action<string>          OnWardrobeItemUnlocked; // wardrobeId

    // ── UI ────────────────────────────────────────────────────────────────────
    public static Action<string>          OnToastRequested;       // message
    public static Action                  OnReturnToMainMenu;

    // ── Ghost System ──────────────────────────────────────────────────────────
    public static Action<GhostSnapshot[]> OnGhostSnapshotsReceived;  // for current map

    // ── Convenience fire helpers ──────────────────────────────────────────────
    public static void FireItemPickedUp(string itemId, long qty)     => OnItemPickedUp?.Invoke(itemId, qty);
    public static void FireInventoryChanged()                         => OnInventoryChanged?.Invoke();
    public static void FireSkillLevelUp(string skillId, int level)   => OnSkillLevelUp?.Invoke(skillId, level);
    public static void FireActivityChanged(SkillActivityData data)   => OnActivityChanged?.Invoke(data);
    public static void FireToast(string message)                     => OnToastRequested?.Invoke(message);
    public static void FireMonsterKilled(string monsterId)           => OnMonsterKilled?.Invoke(monsterId);
    public static void FireMapEntered(string mapId)                  => OnMapEntered?.Invoke(mapId);
    public static void FireMergeCompleted(string from, string to)    => OnMergeCompleted?.Invoke(from, to);

    // ── Domain reload guard ───────────────────────────────────────────────────
    /// <summary>
    /// Static delegates survive between play sessions when Fast Enter Play Mode is on
    /// (domain reload disabled), which silently accumulates duplicate handlers —
    /// every toast fires twice, then three times, then four. Clearing them here on
    /// subsystem registration makes each play session start from a clean bus.
    /// </summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        OnAccountLoaded          = null;
        OnCharacterSelected      = null;
        OnCharacterCreated       = null;
        OnAccountLevelUp         = null;
        OnCharacterLevelUp       = null;

        OnSkillLevelUp           = null;
        OnSkillXPGained          = null;
        OnMilestoneUnlocked      = null;

        OnActivityChanged        = null;
        OnAFKRewardsCollected    = null;

        OnItemPickedUp           = null;
        OnInventorySlotChanged   = null;
        OnInventoryChanged       = null;

        OnMergeSlotChanged       = null;
        OnMergeCompleted         = null;

        OnMapEntered             = null;
        OnZoneEntered            = null;
        OnSkillNodeInteracted    = null;

        OnMonsterKilled          = null;
        OnPlayerDamageTaken      = null;
        OnPlayerHealthChanged    = null;
        OnPlayerDied             = null;

        OnCoinsChanged           = null;
        OnAuctionListingPosted   = null;
        OnAuctionListingSold     = null;

        OnFriendRequestReceived  = null;
        OnFriendRequestAccepted  = null;
        OnGuildUpdated           = null;

        OnSpiritCollected        = null;
        OnRelicCollected         = null;
        OnWardrobeItemUnlocked   = null;

        OnToastRequested         = null;
        OnReturnToMainMenu       = null;

        OnGhostSnapshotsReceived = null;
    }
}
