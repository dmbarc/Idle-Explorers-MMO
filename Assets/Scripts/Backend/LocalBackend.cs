using System;
using System.Collections.Generic;
using IdleExplorers.Rules;
using UnityEngine;

namespace IdleExplorers.Backend
{
    /// <summary>
    /// Today's game, behind the new seam.
    ///
    /// ══ THIS IS SCAFFOLDING AND IS MEANT TO BE DELETED ════════════════════════════
    ///
    /// It wraps the managers that currently own progression, so three things stay true
    /// during the migration:
    ///
    ///   · the editor works with no server running, which keeps the iteration loop fast
    ///   · every call site can be moved to IGameBackend before any of them change
    ///     behaviour, so a migration bug and a server bug never arrive together
    ///   · ShadowBackend has something to compare the server AGAINST
    ///
    /// The last one is the real reason. Without a local implementation there is nothing
    /// to check the server's arithmetic against except reading it, and reading is how
    /// an off-by-one in an XP curve survives for six months.
    ///
    /// At cutover this file is deleted and ApiBackend is wired up directly. Nothing
    /// here is a long-term design; it is a description of what the game does today.
    ///
    /// ══ WHY THE ASYNC METHODS DO NOT AWAIT ════════════════════════════════════════
    ///
    /// Everything here is synchronous, because the managers are. Returning a completed
    /// Awaitable rather than pretending otherwise keeps the interface honest and costs
    /// one allocation -- and it means a caller written against this cannot accidentally
    /// depend on the answer arriving in the same frame, which it will not once the
    /// server is answering.
    /// </summary>
    public class LocalBackend : IGameBackend
    {
        /// <summary>Always. There is nothing to be unavailable.</summary>
        public bool IsAvailable => true;

        // ── Reads ─────────────────────────────────────────────────────────────

        public Awaitable<AccountSnapshot> GetAccountAsync()
        {
            var account = AccountManager.Current;

            var snapshot = new AccountSnapshot
            {
                accountId      = account?.accountId ?? "",
                displayName    = account?.accountName ?? "",
                accountXp      = account?.accountXP ?? 0L,
                characters     = Summaries(account),
                wallets        = Wallets(),

                // The local backend IS the content it loaded, so its copy is never
                // stale by construction. Empty rather than invented: a fabricated hash
                // would be compared against the server's and always differ.
                contentVersion = "",
            };

            return Completed(snapshot);
        }

        /// <summary>
        /// Offline these are already saved -- the local save IS the character.
        ///
        /// Writing them again here would be the same bytes twice, and the whole point
        /// of this backend is that it wraps what the managers already do.
        /// </summary>
        public Awaitable SaveAppearanceAsync(string characterId, SpumSaveData appearance) =>
            Completed();

        public Awaitable SaveLocationAsync(string characterId, string mapId) => Completed();

        /// <summary>
        /// Offline the shop grants locally, as it always did, so this says nothing.
        ///
        /// Returning null rather than a fabricated result: ShopManager only calls this
        /// when a server is authoritative, and inventing a balance here would let a
        /// future caller believe an offline grant had been recorded somewhere.
        /// </summary>
        public Awaitable<TestGrantResult> GrantTestPackAsync(string packId) =>
            Completed<TestGrantResult>(null);

        /// <summary>Offline the item resolver already does this locally, so this says nothing.</summary>
        public Awaitable<UseItemResult> UseItemAsync(string characterId, string itemId) =>
            Completed<UseItemResult>(null);

        public Awaitable<CharacterSnapshot> GetCharacterAsync(string characterId)
        {
            var character = Find(characterId);
            if (character == null) return Completed<CharacterSnapshot>(null);

            return Completed(Describe(character));
        }

        // ══ THE BOSS NEEDS A SERVER ═══════════════════════════════════════════
        //
        // Every other call here has a local answer because the managers already
        // computed one -- this is a migration scaffold wrapping code that exists. The
        // encounter has no local twin at all: it was never client-side, and inventing
        // one would mean writing the fight twice and then deleting the harder copy.
        //
        // So these say NOTHING, loudly enough that the caller can tell. A local
        // session simply cannot fight the King, which is honest -- and the scaffold
        // is deleted at cutover anyway.

        public Awaitable<EncounterSnapshot> EngageBossAsync(string characterId, string monsterId) =>
            Completed(EncounterSnapshot.Nothing);

        public Awaitable<EncounterTick> ReportBossActionsAsync(string characterId,
                                                               BossActionReport[] actions) =>
            Completed(EncounterTick.Nothing);

        public Awaitable<EncounterResult> ResolveBossAsync(string characterId) =>
            Completed(EncounterResult.Nothing);

        public Awaitable<LootClaim> ClaimLootAsync(string characterId) =>
            Completed(LootClaim.Nothing);

        // ══ THE LOCAL MANAGERS ALREADY DO ALL OF THIS ═════════════════════════
        //
        // Offline, EquipmentManager and BankManager ARE the game and this scaffold has
        // nothing to add -- returning a snapshot here would mean describing state the
        // caller is about to be handed anyway. Nothing, so the caller keeps using them.
        //
        // Deleted at cutover along with the rest of this file.

        public Awaitable<CharacterSnapshot> EquipAsync(string characterId, string itemId, string slotId) =>
            Completed<CharacterSnapshot>(null);

        public Awaitable<CharacterSnapshot> UnequipAsync(string characterId, string slotId) =>
            Completed<CharacterSnapshot>(null);

        public Awaitable<BankMoveResult> DepositAsync(string characterId, string itemId, long quantity) =>
            Completed(BankMoveResult.Nothing);

        public Awaitable<BankMoveResult> WithdrawAsync(string characterId, string itemId, long quantity) =>
            Completed(BankMoveResult.Nothing);

        public Awaitable<BankSnapshot> GetBankAsync() => Completed(BankSnapshot.Nothing);

        public Awaitable<TalentSnapshot> SpendTalentAsync(string characterId, string nodeId) =>
            Completed(TalentSnapshot.Nothing);

        public Awaitable<TalentSnapshot> GetTalentsAsync(string characterId) =>
            Completed(TalentSnapshot.Nothing);

        public Awaitable<BossGateSnapshot> GetBossGateAsync(string characterId)
        {
            var character = Find(characterId);

            long active = KillsOf(character, BossGate.Monster, supervised: true);

            return Completed(new BossGateSnapshot
            {
                monsterId   = BossGate.Monster,
                required    = BossGate.RequiredActiveKills,
                activeKills = active,
                afkKills    = KillsOf(character, BossGate.Monster, supervised: false),
                remaining   = Math.Max(0L, BossGate.RequiredActiveKills - active),
                open        = active >= BossGate.RequiredActiveKills,
            });
        }

        // ── Writes ────────────────────────────────────────────────────────────

        public Awaitable<CharacterSnapshot> CreateCharacterAsync(string name, string classId,
                                                                 SpumSaveData appearance)
        {
            // CreateCharacter fills in the id rather than returning one, so the object
            // is built here and read back after.
            var character = new CharacterData
            {
                characterName = name,
                classId       = classId ?? "",
            };

            GameManager.Character?.CreateCharacter(character);

            return Completed(string.IsNullOrEmpty(character.characterId) ? null : Describe(character));
        }

        public Awaitable<ActivitySnapshot> SetGatheringAsync(string characterId, string nodeId)
        {
            SkillNodeEntry node = FindNode(nodeId);
            if (node == null) return Completed<ActivitySnapshot>(null);

            float seconds = Mathf.Max(RateMath.MinSecondsPerAction, node.baseSecondsPerAction);

            GameManager.Activity?.SetActivity(
                node.skillId, node.targetItemId, node.targetItemId,
                GameManager.Zone?.CurrentMapId ?? "",
                node.activeRateMulti, node.afkRateMulti,
                node.specialChance, node.specialLabel,
                xpPerHour: node.xpPerAction * ActivityManager.ActionsPerHour(seconds, node.activeRateMulti),
                recipeId: null,
                secondsPerAction: seconds,
                announce: false);

            return Completed(new ActivitySnapshot
            {
                characterId = characterId,
                kind        = "gather",
                nodeId      = nodeId,
                skillId     = node.skillId,
            });
        }

        public Awaitable<ActivitySnapshot> SetCraftingAsync(string characterId, string recipeId)
        {
            var recipe = GameManager.Content?.GetRecipe(recipeId);
            if (recipe == null) return Completed<ActivitySnapshot>(null);

            int level = GameManager.Skills?.GetSkillLevel(recipe.skillId) ?? 1;

            GameManager.Activity?.SetActivity(
                recipe.skillId, recipe.outputItemId, recipe.DisplayName,
                GameManager.Zone?.CurrentMapId ?? "",
                activeRate: 1f, afkRate: 0.5f, specialChance: 0f, specialLabel: "",
                xpPerHour: 0f,
                recipeId: recipe.id,
                secondsPerAction: recipe.SecondsPerCraft(level),
                announce: false);

            return Completed(new ActivitySnapshot
            {
                characterId = characterId,
                kind        = "craft",
                recipeId    = recipe.id,
                skillId     = recipe.skillId,
            });
        }

        public Awaitable<ActivitySnapshot> SetFightingAsync(string characterId, string monsterId)
        {
            var monster = GameManager.Content?.GetMonster(monsterId);
            if (monster == null) return Completed<ActivitySnapshot>(null);

            GameManager.Activity?.SetActivity(
                "combat", monster.id, monster.DisplayName,
                GameManager.Zone?.CurrentMapId ?? "",
                activeRate: 1f, afkRate: 0.6f, specialChance: 0f, specialLabel: "",
                xpPerHour: 0f,
                recipeId: null,
                secondsPerAction: 1f,
                announce: false);

            return Completed(new ActivitySnapshot
            {
                characterId = characterId,
                kind        = "combat",
                monsterId   = monster.id,
                skillId     = "combat",
            });
        }

        public Awaitable<SettlementSnapshot> SettleAsync(string characterId)
        {
            var character = Find(characterId);
            if (character == null) return Completed(SettlementSnapshot.Nothing);

            AFKRewardSummary summary = GameManager.Activity?.ProcessAFKRewards(character);

            return Completed(Describe(summary));
        }

        public Awaitable<SettlementSnapshot> StopActivityAsync(string characterId)
        {
            var character = Find(characterId);
            AFKRewardSummary summary = character == null
                ? null
                : GameManager.Activity?.ProcessAFKRewards(character);

            GameManager.Activity?.ClearActivity();

            return Completed(Describe(summary));
        }

        /// <summary>
        /// Nothing. Presence is only meaningful to a server deciding a rate, and here
        /// the client IS the authority -- there is nobody to tell.
        /// </summary>
        public Awaitable HeartbeatAsync(string characterId) => Completed();

        /// <summary>
        /// Nothing.
        ///
        /// The local backend has no concept of a settlement window to grade against,
        /// so it cannot compute a bonus without inventing one -- and an invented
        /// bonus would be a divergence shadow mode reported forever. Returning
        /// nothing is the honest answer: the minigame is a server feature.
        /// </summary>
        public Awaitable<MinigameSnapshot> ReportMinigameAsync(string characterId, string[] grades) =>
            Completed(MinigameSnapshot.Nothing);

        public Awaitable<BossGateSnapshot> UnlockBossAsync(string characterId) =>
            GetBossGateAsync(characterId);

        // ── Describing what the managers hold ─────────────────────────────────

        private static CharacterSnapshot Describe(CharacterData character)
        {
            var skills = new List<SkillSnapshot>();

            if (character.skills != null)
            {
                foreach (var progress in character.skills)
                {
                    if (progress == null) continue;

                    skills.Add(new SkillSnapshot
                    {
                        skillId = progress.skillId,
                        xp      = progress.xp,
                        // Derived, matching the server. Never the stored level: the two
                        // drifted once already and BackfillCharacterXP exists because
                        // of it.
                        level   = Levelling.SkillLevel(progress.xp),
                    });
                }
            }

            var inventory = new List<SlotSnapshot>();

            if (character.inventory != null)
            {
                for (int slot = 0; slot < character.inventory.Count; slot++)
                {
                    var entry = character.inventory[slot];
                    if (SlotContainer.IsEmpty(entry)) continue;

                    inventory.Add(new SlotSnapshot
                    {
                        slot     = slot,
                        itemId   = entry.itemId,
                        quantity = entry.quantity,
                    });
                }
            }

            var equipment = new List<EquipSnapshot>();

            if (character.equipment != null)
            {
                foreach (var worn in character.equipment)
                {
                    if (worn == null || string.IsNullOrEmpty(worn.itemId)) continue;

                    equipment.Add(new EquipSnapshot
                    {
                        slotId     = worn.slotId,
                        itemId     = worn.itemId,
                        durability = worn.durability,
                    });
                }
            }

            return new CharacterSnapshot
            {
                characterId = character.characterId,
                name        = character.characterName,
                xp          = character.xp,
                level       = Levelling.CharacterLevel(character.xp),
                skills      = skills.ToArray(),
                inventory   = inventory.ToArray(),
                equipment   = equipment.ToArray(),

                kills       = Kills(character),
            };
        }

        private static SettlementSnapshot Describe(AFKRewardSummary summary)
        {
            if (summary == null) return SettlementSnapshot.Nothing;

            var items = new List<ItemStack>();
            long xp = 0L;

            foreach (var gained in summary.itemsGained)
            {
                if (gained == null || gained.quantity <= 0) continue;

                items.Add(new ItemStack { itemId = gained.itemId, quantity = gained.quantity });
            }

            foreach (var gained in summary.xpGained)
                if (gained != null) xp += gained.quantity;

            return new SettlementSnapshot
            {
                // The old summary counts KILLS but not actions for gathering, so this
                // is the closest honest figure. Shadow mode compares it against the
                // server's action count, and a divergence here is expected until the
                // local path is retired.
                actions             = summary.kills,
                supervisedActions   = summary.kills,
                xpGained            = xp,
                elapsedSeconds      = summary.elapsedSeconds,
                supervisedSeconds   = 0d,
                items               = items.ToArray(),
                currency            = Array.Empty<CurrencyAmount>(),
                stoppedForRoom      = false,
                lostToFullInventory = 0L,
                ranOutOfInputs      = summary.WasTruncated,
            };
        }

        private static KillSnapshot[] Kills(CharacterData character)
        {
            if (character.kills == null) return Array.Empty<KillSnapshot>();

            var rows = new List<KillSnapshot>();

            foreach (var kill in character.kills)
            {
                if (kill == null || string.IsNullOrEmpty(kill.monsterId)) continue;

                rows.Add(new KillSnapshot
                {
                    monsterId   = kill.monsterId,
                    activeKills = kill.activeKills,
                    afkKills    = kill.afkKills,
                });
            }

            return rows.ToArray();
        }

        private static CharacterSummary[] Summaries(AccountData account)
        {
            if (account?.characters == null) return Array.Empty<CharacterSummary>();

            var summaries = new List<CharacterSummary>();

            foreach (var character in account.characters)
            {
                if (character == null) continue;

                summaries.Add(new CharacterSummary
                {
                    characterId = character.characterId,
                    name        = character.characterName,
                    classId     = character.classId,
                    xp          = character.xp,
                    level       = Levelling.CharacterLevel(character.xp),
                    lastMapId   = character.lastMapId,
                });
            }

            return summaries.ToArray();
        }

        /// <summary>
        /// Coins are an inventory item here and a wallet on the server.
        ///
        /// That IS the migration, so the two are reported in the same shape and shadow
        /// mode can see whether they agree on the number. A local backend that reported
        /// no wallet at all would look like agreement with a server that had not
        /// credited anything.
        /// </summary>
        private static WalletBalance[] Wallets() => new[]
        {
            new WalletBalance
            {
                currency = Currency.Coins,
                balance  = GameManager.Inventory?.GetQuantity(Currency.CoinsItemId) ?? 0L,
            },
            new WalletBalance
            {
                currency = Currency.RelicCoins,
                balance  = AccountManager.Current?.relicCoins ?? 0L,
            },
        };

        private static CharacterData Find(string characterId)
        {
            var account = AccountManager.Current;
            if (account?.characters == null) return CharacterManager.Current;

            foreach (var character in account.characters)
                if (character != null && character.characterId == characterId) return character;

            return CharacterManager.Current;
        }

        private static SkillNodeEntry FindNode(string nodeId)
        {
            var zones = GameManager.Content?.Zones;
            if (zones == null || string.IsNullOrEmpty(nodeId)) return null;

            foreach (var zone in zones.Values)
            {
                if (zone?.maps == null) continue;

                foreach (var map in zone.maps)
                {
                    if (map?.skillNodes == null) continue;

                    foreach (var node in map.skillNodes)
                        if (node != null && node.nodeId == nodeId) return node;
                }
            }

            return null;
        }

        /// <summary>
        /// From the client's own mirror, which KillTracker keeps.
        ///
        /// A mirror rather than the truth: it exists so the portal can draw a number
        /// without a round trip. The server's kill_counter decides whether the portal
        /// opens, and shadow mode compares the two.
        /// </summary>
        private static long KillsOf(CharacterData character, string monsterId, bool supervised)
        {
            if (character?.kills == null) return 0L;

            foreach (var row in character.kills)
            {
                if (row == null || row.monsterId != monsterId) continue;

                return supervised ? row.activeKills : row.afkKills;
            }

            return 0L;
        }

        // ── Completed awaitables ──────────────────────────────────────────────

        private static Awaitable<T> Completed<T>(T value) where T : class
        {
            var source = new AwaitableCompletionSource<T>();
            source.SetResult(value);

            return source.Awaitable;
        }

        private static Awaitable Completed()
        {
            var source = new AwaitableCompletionSource();
            source.SetResult();

            return source.Awaitable;
        }
    }

    /// <summary>
    /// The portal's terms.
    ///
    /// MOVED to the shared rules tree as IdleExplorers.Rules.BossGate, because the
    /// comment this replaces was right about the danger and wrong about the fix: a
    /// constant "in one place both BACKENDS read" is still only the client. The server
    /// had its own copy in BossEndpoints, and nothing connected them.
    ///
    /// This alias stays so the existing call sites keep reading, and so anybody who
    /// comes looking here finds the reason rather than a deleted symbol.
    /// </summary>
    public static class BossGate
    {
        public const string Monster             = IdleExplorers.Rules.BossGate.Monster;
        public const long   RequiredActiveKills = IdleExplorers.Rules.BossGate.RequiredActiveKills;
    }
}
