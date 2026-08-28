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

        /// <summary>Saves the look. Stored as given -- the server has no opinion about hair.</summary>
        Awaitable SaveAppearanceAsync(string characterId, SpumSaveData appearance);

        /// <summary>Remembers where the character is, so they load in there next time.</summary>
        Awaitable SaveLocationAsync(string characterId, string mapId);

        /// <summary>
        /// Consumes an item whose effect the SERVER owns, and applies it.
        ///
        /// Only mystic gems today. The seconds come from the server's own catalogue,
        /// never from the request, or a gem is worth whatever the caller claims.
        /// </summary>
        Awaitable<UseItemResult> UseItemAsync(string characterId, string itemId);

        /// <summary>
        /// Grants a coin pack WITHOUT a payment, for testing.
        ///
        /// Refused unless the server has the flag explicitly on. Nothing about this is
        /// a purchase; see ShopEndpoints for why it is a server call at all.
        /// </summary>
        Awaitable<TestGrantResult> GrantTestPackAsync(string packId);

        Awaitable<CharacterSnapshot> CreateCharacterAsync(string name, string classId,
                                                          SpumSaveData appearance);

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

        /// <summary>
        /// Reports how well the player did at a minigame.
        ///
        /// GRADES, and nothing else -- no reward, no action count, no timing. The
        /// server converts them against the actions its own clock produced, which is
        /// what stops a scripted client claiming more than the design cap. See
        /// IdleExplorers.Rules.Minigame.
        /// </summary>
        Awaitable<MinigameSnapshot> ReportMinigameAsync(string characterId, string[] grades);

        // ── The boss ──────────────────────────────────────────────────────
        //
        // The one fight the server validates per action. Everything else in the game
        // settles from two timestamps; this reports what the player DID, because in
        // this one place what they did inside the window is meant to matter.
        //
        // Note what is not sent: no damage, no health, no position, no timestamp.
        // The client says "I swung", and the server decides what that was worth
        // against a snapshot frozen at engage and its own clock.

        /// <summary>
        /// Start the fight. The answer carries the WHOLE attack timeline.
        ///
        /// That is what makes a telegraphed boss playable over the internet: every
        /// wind-up is drawn from this schedule with no further network involvement,
        /// so a 200 ms connection does not turn a 1.2 second telegraph into a 1.0
        /// second one.
        /// </summary>
        Awaitable<EncounterSnapshot> EngageBossAsync(string characterId, string monsterId);

        /// <summary>
        /// "I swung these times." Returns the authoritative health.
        ///
        /// Batched, at most a couple of times a second. The client predicts damage
        /// locally from the shared rules and reconciles to what comes back.
        /// </summary>
        Awaitable<EncounterTick> ReportBossActionsAsync(string characterId, BossActionReport[] actions);

        /// <summary>Ask the server how it went. It decides, from its own health value.</summary>
        Awaitable<EncounterResult> ResolveBossAsync(string characterId);

        /// <summary>Move earned loot from pending into the bag and the wallet.</summary>
        Awaitable<LootClaim> ClaimLootAsync(string characterId);

        // ── The rest of what persists ─────────────────────────────────────
        //
        // Equipment, the bank and talents. Each was the last of its kind still being
        // decided on the client: a stat, an item and a stat respectively, all three of
        // which feed damage per second and therefore how fast somebody farms and
        // whether they beat the enrage timer.
        //
        // Every one of these RETURNS the character, rather than a success flag. The
        // server has just changed the thing the client draws, and asking for it again
        // afterwards is a second round trip and a window where the two disagree.

        /// <summary>Wear an item. The server checks ownership, the slot and the requirement.</summary>
        Awaitable<CharacterSnapshot> EquipAsync(string characterId, string itemId, string slotId);

        Awaitable<CharacterSnapshot> UnequipAsync(string characterId, string slotId);

        /// <summary>Bag to bank. Refused rather than truncated when the bank is full.</summary>
        Awaitable<BankMoveResult> DepositAsync(string characterId, string itemId, long quantity);

        Awaitable<BankMoveResult> WithdrawAsync(string characterId, string itemId, long quantity);

        /// <summary>What is in the bank. Account-wide, so it takes no character.</summary>
        Awaitable<BankSnapshot> GetBankAsync();

        /// <summary>Put a point into a talent. The server owns the tier and prerequisite rules.</summary>
        Awaitable<TalentSnapshot> SpendTalentAsync(string characterId, string nodeId);

        Awaitable<TalentSnapshot> GetTalentsAsync(string characterId);

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

        /// <summary>
        /// What is switched on, as the server sees it.
        ///
        /// COURTESY, not enforcement. It lets the client hide a disabled feature
        /// rather than showing it broken; every flag with an effect is checked again
        /// in the handler that would do the thing, so a client that ignores this gets
        /// a refusal rather than a reward.
        /// </summary>
        public FeatureFlagRow[] flags;

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

        /// <summary>
        /// What this character looks like, so the select screen can draw the face.
        ///
        /// Carried on the ROSTER rather than fetched per card: one request that
        /// renders the whole list beats one request per character to render it.
        /// </summary>
        public SpumSaveData appearance;
    }

    [Serializable]
    public class CharacterSnapshot
    {
        public string          characterId;
        public string          name;
        public long            xp;
        public int             level;

        /// <summary>
        /// The look, and where they left off.
        ///
        /// Both used to live only in the client's local save, which under an
        /// authoritative server is not the source of anything. A player customised a
        /// character, went to character select, and came back to the default face;
        /// and every character loaded into the starting map however far they had
        /// walked.
        /// </summary>
        public SpumSaveData    appearance;
        public string          lastMapId;
        public SkillSnapshot[] skills;
        public SlotSnapshot[]  inventory;
        public EquipSnapshot[] equipment;
        public KillSnapshot[]  kills;
    }

    [Serializable] public class FeatureFlagRow { public string flag; public bool enabled; }

    /// <summary>What using a server-owned item produced.</summary>
    [Serializable]
    public class UseItemResult
    {
        public string characterId;
        public string itemId;
        public long   grantedSeconds;
        public long   creditedSeconds;
    }

    /// <summary>What an unpaid shop grant produced. See ShopEndpoints.</summary>
    [Serializable]
    public class TestGrantResult
    {
        public string packId;
        public long   granted;
        public string currency;
        public long   balance;

        /// <summary>Always false. Said out loud so this cannot be shown as a receipt.</summary>
        public bool   paid;
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

    /// <summary>What a minigame report earned, on top of the window it was played in.</summary>
    [Serializable]
    public class MinigameSnapshot
    {
        public SettlementSnapshot settled;
        public SettlementSnapshot bonus;

        /// <summary>Extra actions the grades were worth. Zero when they were not.</summary>
        public long bonusActions;

        public static readonly MinigameSnapshot Nothing = new();
    }

    // ══ The boss fight ════════════════════════════════════════════════════════

    /// <summary>
    /// One swing, as the client reports it.
    ///
    /// A sequence number and optionally an ability, and deliberately nothing else. A
    /// damage field would be a claim the server has to either trust or ignore, and a
    /// field that is always ignored is one somebody eventually starts trusting.
    /// </summary>
    [Serializable]
    public class BossActionReport
    {
        public long   sequence;
        public string abilityId;
    }

    [Serializable]
    public class EncounterSnapshot
    {
        public string encounterId;
        public string monsterId;
        public string name;

        public long   bossHp;
        public long   bossMaxHp;
        public float  armor;

        /// <summary>
        /// The snapshot the server froze. Echoed so the client can predict its own
        /// numbers from the shared rules -- it is a COPY of the authority, not the
        /// authority, and the health that comes back from an action report wins.
        /// </summary>
        public double frozenDps;
        public double frozenAttackSeconds;

        public double enrageSeconds;
        public double elapsedSeconds;

        public EncounterPhase[] phases;

        public static readonly EncounterSnapshot Nothing = new();

        public bool Started => !string.IsNullOrEmpty(encounterId);
    }

    [Serializable]
    public class EncounterPhase
    {
        public int    index;
        public string name;
        public float  fromHealthFraction;
        public float  hasteMultiplier;
        public int    addsPerWave;
        public float  secondsBetweenWaves;

        /// <summary>
        /// The schedule, from the moment this phase begins.
        ///
        /// IdleExplorers.Rules.BossCast, the same class the server writes -- so a
        /// field renamed on one side is renamed on both.
        /// </summary>
        public IdleExplorers.Rules.BossCast[] casts;
    }

    [Serializable]
    public class EncounterTick
    {
        public string encounterId;

        /// <summary>The authoritative health. Reconcile to this.</summary>
        public long   bossHp;
        public long   bossMaxHp;
        public int    phase;

        public double elapsedSeconds;
        public double remainingSeconds;

        public int    accepted;
        public int    rejected;

        /// <summary>
        /// True when the server credited less than the actions were nominally worth.
        ///
        /// Honest clients see this in the first moments of a fight, when the whole
        /// budget is the opening tolerance. A client seeing it repeatedly has a bug or
        /// is being tampered with, and either way it is worth being able to see.
        /// </summary>
        public bool   clamped;

        public bool   dead;
        public bool   enraged;

        public static readonly EncounterTick Nothing = new();
    }

    [Serializable]
    public class EncounterResult
    {
        public string      encounterId;
        public bool        won;
        public long        xpGained;
        public double      seconds;
        public long        damageDealt;
        public long        bossMaxHp;

        /// <summary>Earned, and waiting to be claimed. Not in the bag yet.</summary>
        public ItemStack[] pending;

        public static readonly EncounterResult Nothing = new();
    }

    [Serializable]
    public class LootClaim
    {
        public ItemStack[] claimed;
        public long        stillWaiting;

        /// <summary>
        /// Said out loud, because the reason nothing arrived is almost always a full
        /// bag -- and a silent no-op is indistinguishable from a broken button.
        /// </summary>
        public bool        bagWasFull;

        public static readonly LootClaim Nothing = new();
    }

    // ══ Equipment, bank and talents ═══════════════════════════════════════════

    [Serializable]
    public class BankSnapshot
    {
        public int          capacity;
        public int          used;
        public BankSlot[]   slots;

        public static readonly BankSnapshot Nothing = new();
    }

    [Serializable]
    public class BankSlot
    {
        public int    slot;
        public string itemId;
        public long   quantity;
    }

    /// <summary>
    /// What a deposit or withdrawal actually moved.
    ///
    /// `moved` rather than a boolean, because a full container moves SOME of a stack
    /// and reporting that as success loses the rest silently -- a player who banks
    /// 4,000 ore and finds 1,200 will not notice until much later.
    /// </summary>
    [Serializable]
    public class BankMoveResult
    {
        public string itemId;
        public long   moved;
        public long   requested;
        public bool   partial;

        public static readonly BankMoveResult Nothing = new();
    }

    [Serializable]
    public class TalentSnapshot
    {
        public int           level;
        public int           total;
        public int           spent;
        public int           available;
        public TalentRankRow[] ranks;

        public static readonly TalentSnapshot Nothing = new();
    }

    [Serializable] public class TalentRankRow { public string nodeId; public int rank; }

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
