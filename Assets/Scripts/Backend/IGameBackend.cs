using System;
using UnityEngine;

namespace IdleExplorers.Backend
{
    /// <summary>
    /// Everything the game asks for that it is not allowed to decide.
    ///
    /// ══ WHY THERE IS A SEAM AT ALL ════════════════════════════════════════════════
    ///
    /// The client currently computes its own rewards and writes them to a JSON file in
    /// persistentDataPath. The server computes them instead. Swapping one for the other
    /// in a single change would mean the game is broken for however long that takes,
    /// with no way to tell a migration bug from a server bug.
    ///
    /// So the calls go through an interface with three implementations:
    ///
    ///   LocalBackend    today's managers, so the editor still works with no network
    ///   ApiBackend      the real one
    ///   ShadowBackend   answers from Local, asks Api in parallel, logs every
    ///                   disagreement — the game becomes a fuzzer against the server
    ///
    /// Shadow mode is the point of the exercise. Playing normally for a week finds the
    /// off-by-one in the XP curve before a player does, and it costs nothing but a
    /// second request.
    ///
    /// ══ WHY Awaitable AND NOT Task ════════════════════════════════════════════════
    ///
    /// WebGL has no threads, and Unity's Awaitable resumes on the main thread by
    /// construction. A Task-based API works in the editor and then deadlocks or throws
    /// on the web the first time somebody touches a transform in a continuation. Using
    /// the type that cannot do that is cheaper than remembering not to.
    ///
    /// ══ WHAT IS NOT HERE ══════════════════════════════════════════════════════════
    ///
    /// Nothing presentational. No animation, no camera, no VFX, no sprite facing, no
    /// bar tweening. Those stay entirely client-side, and if a cheater draws a dragon
    /// in slot one the next read replaces it.
    /// </summary>
    public interface IGameBackend
    {
        /// <summary>Whether this backend can reach whatever it needs. Local is always true.</summary>
        bool IsAvailable { get; }

        /// <summary>The bootstrap call: who am I, what characters do I have, what do I own.</summary>
        Awaitable<AccountSnapshot> GetAccountAsync();

        Awaitable<CharacterSnapshot> GetCharacterAsync(string characterId);

        Awaitable<CharacterSnapshot> CreateCharacterAsync(string name, string classId);

        /// <summary>Start working a gathering node.</summary>
        Awaitable<ActivitySnapshot> SetGatheringAsync(string characterId, string nodeId);

        /// <summary>Start a recipe at a station.</summary>
        Awaitable<ActivitySnapshot> SetCraftingAsync(string characterId, string recipeId);

        /// <summary>Start fighting a monster.</summary>
        Awaitable<ActivitySnapshot> SetFightingAsync(string characterId, string monsterId);

        /// <summary>Stop, and collect whatever the last window earned.</summary>
        Awaitable<SettlementSnapshot> StopActivityAsync(string characterId);

        /// <summary>
        /// "I am still here."
        ///
        /// The only thing a heartbeat asserts, and it asserts it by arriving. There is
        /// nothing to send.
        /// </summary>
        Awaitable HeartbeatAsync(string characterId);

        /// <summary>
        /// "What have I earned?"
        ///
        /// Safe to call as often as the client likes: the answer is a function of
        /// elapsed time, so calling twice in an instant pays nothing the second time.
        /// </summary>
        Awaitable<SettlementSnapshot> SettleAsync(string characterId);

        /// <summary>How close this character is to the boss portal.</summary>
        Awaitable<BossGateSnapshot> GetBossGateAsync(string characterId);

        Awaitable<BossGateSnapshot> UnlockBossAsync(string characterId);
    }

    // ══ The wire shapes ═══════════════════════════════════════════════════════════
    //
    // Plain [Serializable] classes with public fields, and arrays where a dictionary
    // would be natural. Both constraints come from JsonUtility, which is the only
    // serialiser in a WebGL build unless a package is added:
    //
    //   · it cannot deserialise a Dictionary, and does not say so -- it produces an
    //     empty one, which reads as "you earned nothing"
    //   · it cannot parse a bare top-level array, so every response is an object
    //
    // The server was shaped to match rather than the client being given a JSON library,
    // because a dependency avoided in a WebGL build is download size, IL2CPP stripping
    // risk and a link.xml nobody has to maintain.

    /// <summary>One item and how many of it.</summary>
    [Serializable]
    public class ItemStack
    {
        public string itemId;
        public long   quantity;
    }

    /// <summary>Money earned by a window. An amount, not a balance.</summary>
    [Serializable]
    public class CurrencyAmount
    {
        public string currency;
        public long   amount;
    }

    /// <summary>Money held. A balance, not an amount.</summary>
    [Serializable]
    public class WalletBalance
    {
        public string currency;
        public long   balance;
    }

    [Serializable]
    public class AccountSnapshot
    {
        public string              accountId;
        public string              displayName;
        public long                accountXp;
        public CharacterSummary[]  characters;
        public WalletBalance[]     wallets;

        /// <summary>
        /// Hash of the twelve content files the SERVER loaded.
        ///
        /// The client ships its own copy so it can draw tooltips and sweep cooldowns
        /// without a round trip. This is how it learns that copy is stale, rather than
        /// quietly disagreeing about what a chestplate costs.
        /// </summary>
        public string contentVersion;

        public long BalanceOf(string currency)
        {
            if (wallets == null) return 0L;

            foreach (var wallet in wallets)
                if (wallet != null && wallet.currency == currency) return wallet.balance;

            return 0L;
        }
    }

    [Serializable]
    public class CharacterSummary
    {
        public string characterId;
        public string name;
        public string classId;
        public long   xp;
        public int    level;
        public string lastMapId;
    }

    [Serializable]
    public class CharacterSnapshot
    {
        public string          characterId;
        public string          name;
        public long            xp;
        public int             level;
        public SkillSnapshot[] skills;
        public SlotSnapshot[]  inventory;
        public EquipSnapshot[] equipment;
        public KillSnapshot[]  kills;
    }

    [Serializable] public class SkillSnapshot { public string skillId; public long xp; public int level; }
    [Serializable] public class SlotSnapshot  { public int slot; public string itemId; public long quantity; }
    [Serializable] public class EquipSnapshot { public string slotId; public string itemId; public int durability; }
    [Serializable] public class KillSnapshot  { public string monsterId; public long activeKills; public long afkKills; }

    [Serializable]
    public class ActivitySnapshot
    {
        public string characterId;
        public string kind;
        public string nodeId;
        public string recipeId;
        public string monsterId;
        public string skillId;
    }

    [Serializable]
    public class SettlementSnapshot
    {
        public long   actions;

        /// <summary>Of those, how many happened while somebody was watching.</summary>
        public long   supervisedActions;

        public long   xpGained;
        public double elapsedSeconds;
        public double supervisedSeconds;

        public ItemStack[]      items;
        public CurrencyAmount[] currency;

        public bool stoppedForRoom;
        public long lostToFullInventory;
        public bool ranOutOfInputs;

        public static readonly SettlementSnapshot Nothing = new();

        public long QuantityOf(string itemId)
        {
            if (items == null) return 0L;

            foreach (var stack in items)
                if (stack != null && stack.itemId == itemId) return stack.quantity;

            return 0L;
        }

        public long EarnedOf(string wallet)
        {
            if (currency == null) return 0L;

            foreach (var earned in currency)
                if (earned != null && earned.currency == wallet) return earned.amount;

            return 0L;
        }

        /// <summary>True when this window produced nothing at all.</summary>
        public bool IsEmpty => actions <= 0L && xpGained <= 0L;
    }

    [Serializable]
    public class BossGateSnapshot
    {
        public string monsterId;
        public long   required;
        public long   activeKills;
        public long   afkKills;
        public long   remaining;
        public bool   open;
    }

    /// <summary>
    /// Something the backend refused or could not do.
    ///
    /// Carries the status so a caller can tell "you cannot do that" from "try again in
    /// a moment" -- 503 is the server shedding load and IS worth retrying, while 409
    /// means the answer will be the same next time.
    /// </summary>
    public class BackendException : Exception
    {
        public int    StatusCode { get; }
        public string Title      { get; }

        public BackendException(int statusCode, string title, string detail)
            : base(string.IsNullOrEmpty(detail) ? title : detail)
        {
            StatusCode = statusCode;
            Title      = title;
        }

        /// <summary>Worth trying again. Capacity and transport, never a refusal.</summary>
        public bool IsTransient => StatusCode is 0 or 408 or 429 or >= 500;
    }
}
