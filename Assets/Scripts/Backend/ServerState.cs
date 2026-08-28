using System;
using System.Collections.Generic;
using UnityEngine;

namespace IdleExplorers.Backend
{
    /// <summary>
    /// Makes the client's own data a CACHE of the server's, rather than the truth.
    ///
    /// ══ THE GAP THIS CLOSES ═══════════════════════════════════════════════════════
    ///
    /// Every endpoint existed, every rule was shared, the seam was built and tested and
    /// deployed -- and the game called none of it. Gathering, crafting, combat,
    /// settlement, experience, inventory, equipment and currency all still ran on the
    /// client and were written to a file the player owns. The one thing the whole
    /// re-architecture exists to prevent was still true.
    ///
    /// ══ WHY A CACHE RATHER THAN A REWRITE ═════════════════════════════════════════
    ///
    /// InventoryPanel, CharacterSheet, the skill bars and a hundred other things read
    /// CharacterData. Rewriting them all to read a snapshot type would be weeks of
    /// churn in code that is not wrong.
    ///
    /// So CharacterData stays, and stops being AUTHORED locally. The server's answer is
    /// written into it on every sync, and anything the client computed in between is
    /// overwritten without ceremony. Reads keep working; writes stop mattering.
    ///
    /// That is the honest shape of the migration: the client may believe whatever it
    /// likes for a few hundred milliseconds, and then it is told.
    ///
    /// ══ WHY IT DOES NOTHING UNLESS THE SERVER IS AUTHORITATIVE ════════════════════
    ///
    /// Offline and in shadow mode the local managers ARE the game, and overwriting
    /// their state from a server whose answers are deliberately not being used would
    /// replace a working session with a half-migrated one. Authority is all or nothing,
    /// and which one is in force is a property of the backend rather than a flag
    /// anybody can leave in the wrong position.
    /// </summary>
    public static class ServerState
    {
        /// <summary>
        /// Whether the server's answers are the ones that count.
        ///
        /// True only for a direct ApiBackend. Shadow mode is deliberately excluded: it
        /// exists to OBSERVE a server whose answers are not trusted yet, and a shadow
        /// session that overwrote local state from the server would not be a shadow at
        /// all.
        /// </summary>
        public static bool IsAuthoritative => GameBackend.Current is ApiBackend;

        /// <summary>The last account the server described. Null before the first pull.</summary>
        public static AccountSnapshot Account { get; private set; }

        /// <summary>Raised after any pull that changed character state, so panels refresh.</summary>
        public static event Action Changed;

        /// <summary>Raised when a settle paid something, carrying what it paid.</summary>
        public static event Action<SettlementSnapshot> Settled;

        // ── Pulling ───────────────────────────────────────────────────────────

        /// <summary>
        /// Loads the account and every character summary.
        ///
        /// Called once after sign-in. Everything else hangs off knowing which
        /// characters exist.
        /// </summary>
        public static async Awaitable<bool> PullAccountAsync()
        {
            if (!IsAuthoritative) return false;

            try
            {
                Account = await GameBackend.Current.GetAccountAsync();

                if (Account == null) return false;

                var local = AccountManager.Current ?? new AccountData();

                local.accountId   = Account.accountId;
                local.accountName = Account.displayName;
                local.accountXP   = Account.accountXp;

                // ══ WHERE THE COINS GO ════════════════════════════════════════
                //
                // Relic coins are account-wide on both sides, so that one is a
                // straight copy -- and it is the number this whole architecture was
                // built to take off the player's machine.
                //
                // Ordinary coins are account-wide on the SERVER and per-character on
                // the client. Rather than reconcile two models, the account balance is
                // written onto whichever character is loaded, because that is what the
                // HUD reads and the server's number is the true one either way. The
                // divergence is worth removing eventually; inventing a second local
                // balance to hold it would not remove it.
                local.relicCoins = Account.BalanceOf(Rules.Currency.RelicCoins);

                if (CharacterManager.Current != null)
                    CharacterManager.Current.coins = Account.BalanceOf(Rules.Currency.Coins);

                MergeCharacterList(local, Account.characters);

                GameManager.Account?.LoadAccount(local);

                WarnIfContentIsStale(Account.contentVersion);

                Changed?.Invoke();
                return true;
            }
            catch (BackendException e)
            {
                Debug.LogError($"[ServerState] Could not load the account: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// Overwrites one character's progression with the server's version.
        ///
        /// Skills, inventory, equipment and kills. Not appearance or the map -- those
        /// are presentation and the client owns them.
        /// </summary>
        public static async Awaitable<bool> PullCharacterAsync(string characterId)
        {
            if (!IsAuthoritative || string.IsNullOrEmpty(characterId)) return false;

            try
            {
                CharacterSnapshot snapshot = await GameBackend.Current.GetCharacterAsync(characterId);

                if (snapshot == null) return false;

                Apply(snapshot);

                Changed?.Invoke();
                return true;
            }
            catch (BackendException e)
            {
                Debug.LogError($"[ServerState] Could not load the character: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// Writes a snapshot into the local CharacterData.
        ///
        /// Wholesale, not merged. A merge would need a rule for every field about which
        /// side wins, and the answer is always the same one -- so the rule is the
        /// assignment.
        /// </summary>
        public static void Apply(CharacterSnapshot snapshot)
        {
            var character = CharacterManager.Current;

            if (character == null || snapshot == null) return;
            if (character.characterId != snapshot.characterId) return;

            character.xp    = snapshot.xp;
            character.level = snapshot.level;

            // ══ ONLY WHEN THE SERVER HAS ONE ═════════════════════════════════
            //
            // A character created before the appearance column existed comes back with
            // an empty face. Writing that over a good local one would reproduce, on
            // every pull, exactly the bug this column was added to fix -- so an empty
            // answer means "no opinion", not "make them bald".
            if (snapshot.appearance is { IsEmpty: false })
                character.spumConfig = snapshot.appearance;

            if (!string.IsNullOrEmpty(snapshot.lastMapId))
                character.lastMapId = snapshot.lastMapId;

            ApplySkills(character, snapshot.skills);
            ApplyInventory(character, snapshot.inventory);
            ApplyEquipment(character, snapshot.equipment);
            ApplyKills(character, snapshot.kills);
        }

        private static void ApplySkills(CharacterData character, SkillSnapshot[] skills)
        {
            if (skills == null) return;

            character.skills ??= new List<SkillProgress>();
            character.skills.Clear();

            foreach (var skill in skills)
            {
                if (skill == null) continue;

                character.skills.Add(new SkillProgress
                {
                    skillId = skill.skillId,
                    xp      = skill.xp,
                    level   = skill.level,
                });
            }
        }

        private static void ApplyInventory(CharacterData character, SlotSnapshot[] slots)
        {
            if (slots == null) return;

            character.inventory ??= new List<InventoryEntry>();

            // Rebuilt at full capacity rather than sized to what came back: the panel
            // draws a fixed grid, and a short list is a grid with missing cells.
            character.inventory.Clear();

            for (int i = 0; i < InventoryManager.MaxSlots; i++)
                character.inventory.Add(new InventoryEntry());

            foreach (var slot in slots)
            {
                if (slot == null || slot.slot < 0 || slot.slot >= character.inventory.Count) continue;

                character.inventory[slot.slot].itemId   = slot.itemId;
                character.inventory[slot.slot].quantity = slot.quantity;
            }
        }

        private static void ApplyEquipment(CharacterData character, EquipSnapshot[] worn)
        {
            if (worn == null) return;

            character.equipment ??= new List<EquipmentEntry>();
            character.equipment.Clear();

            foreach (var piece in worn)
            {
                if (piece == null || string.IsNullOrEmpty(piece.itemId)) continue;

                character.equipment.Add(new EquipmentEntry
                {
                    slotId     = piece.slotId,
                    itemId     = piece.itemId,
                    durability = piece.durability,
                });
            }
        }

        private static void ApplyKills(CharacterData character, KillSnapshot[] kills)
        {
            if (kills == null) return;

            character.kills ??= new List<KillCount>();
            character.kills.Clear();

            foreach (var kill in kills)
            {
                if (kill == null) continue;

                character.kills.Add(new KillCount
                {
                    monsterId   = kill.monsterId,
                    activeKills = kill.activeKills,
                    afkKills    = kill.afkKills,
                });
            }
        }

        // ── Pushing intent ────────────────────────────────────────────────────

        /// <summary>
        /// "I am working this node now."
        ///
        /// Intent, not a reward. What it earns is decided by the server from its own
        /// clock, and arrives at the next settle.
        /// </summary>
        public static async Awaitable SetGatheringAsync(string nodeId)
        {
            await SetAsync(backend => backend.SetGatheringAsync(CharacterId, nodeId));
        }

        public static async Awaitable SetCraftingAsync(string recipeId)
        {
            await SetAsync(backend => backend.SetCraftingAsync(CharacterId, recipeId));
        }

        public static async Awaitable SetFightingAsync(string monsterId)
        {
            await SetAsync(backend => backend.SetFightingAsync(CharacterId, monsterId));
        }

        private static async Awaitable SetAsync(Func<IGameBackend, Awaitable<ActivitySnapshot>> call)
        {
            if (!IsAuthoritative || string.IsNullOrEmpty(CharacterId)) return;

            try
            {
                // Settled first by the SERVER, inside the same lock that changes the
                // activity -- so the window that just ended is paid at the old rate
                // rather than the new one. Nothing to do here but ask.
                await call(GameBackend.Current);

                await PullCharacterAsync(CharacterId);
            }
            catch (BackendException e)
            {
                GameEvents.FireToast(e.Title, ChatTone.Bad);
                Debug.LogWarning($"[ServerState] Could not change activity: {e.Message}");
            }
        }

        /// <summary>Stops, and collects whatever the last window earned.</summary>
        public static async Awaitable StopAsync()
        {
            if (!IsAuthoritative || string.IsNullOrEmpty(CharacterId)) return;

            try
            {
                SettlementSnapshot settled =
                    await GameBackend.Current.StopActivityAsync(CharacterId);

                Announce(settled);

                await PullCharacterAsync(CharacterId);
            }
            catch (BackendException e)
            {
                Debug.LogWarning($"[ServerState] Could not stop: {e.Message}");
            }
        }

        // ── Settling ──────────────────────────────────────────────────────────

        /// <summary>
        /// "What have I earned?"
        ///
        /// Safe to call as often as the client likes -- the answer is a function of
        /// elapsed time, so twice in an instant pays nothing the second time. That
        /// property is what lets this run on a timer without any bookkeeping.
        /// </summary>
        public static async Awaitable<SettlementSnapshot> SettleAsync()
        {
            if (!IsAuthoritative || string.IsNullOrEmpty(CharacterId))
                return SettlementSnapshot.Nothing;

            try
            {
                SettlementSnapshot settled = await GameBackend.Current.SettleAsync(CharacterId);

                if (settled == null || settled.IsEmpty) return SettlementSnapshot.Nothing;

                Announce(settled);

                // Pulled AFTER, so the panels show what the settle produced rather than
                // the state before it.
                await PullCharacterAsync(CharacterId);

                return settled;
            }
            catch (BackendException e)
            {
                // Swallowed and logged. A settle runs on a timer, so a failed one is
                // retried within seconds and the elapsed time it did not claim is still
                // there to claim -- nothing is lost by staying quiet.
                Debug.LogWarning($"[ServerState] Settle failed: {e.Message}");

                return SettlementSnapshot.Nothing;
            }
        }

        /// <summary>
        /// Remembers the look, so it survives a character select and follows the player.
        /// </summary>
        public static async Awaitable SaveAppearanceAsync(string characterId, SpumSaveData look)
        {
            if (!IsAuthoritative || string.IsNullOrEmpty(characterId) || look == null) return;

            try
            {
                await GameBackend.Current.SaveAppearanceAsync(characterId, look);
            }
            catch (BackendException e)
            {
                // Not worth a toast. The face is already correct on screen, and the
                // next save will carry it.
                Debug.LogWarning($"[ServerState] Could not save appearance: {e.Message}");
            }
        }

        /// <summary>
        /// Remembers where the character is standing, so they load in there next time.
        /// </summary>
        public static async Awaitable SaveLocationAsync(string mapId)
        {
            if (!IsAuthoritative || string.IsNullOrEmpty(CharacterId) || string.IsNullOrEmpty(mapId))
                return;

            try
            {
                await GameBackend.Current.SaveLocationAsync(CharacterId, mapId);
            }
            catch (BackendException e)
            {
                Debug.LogWarning($"[ServerState] Could not save location: {e.Message}");
            }
        }

        public static async Awaitable HeartbeatAsync()
        {
            if (!IsAuthoritative || string.IsNullOrEmpty(CharacterId)) return;

            try   { await GameBackend.Current.HeartbeatAsync(CharacterId); }
            catch (BackendException) { /* the next one is twenty seconds away */ }
        }

        // ── Machinery ─────────────────────────────────────────────────────────

        private static string CharacterId => CharacterManager.Current?.characterId ?? "";

        /// <summary>
        /// Tells the player what a window paid.
        ///
        /// The same event the local path fired, so every existing listener -- the toast,
        /// the AFK summary, the floating numbers -- keeps working without knowing that
        /// the number now comes from somewhere else.
        /// </summary>
        private static void Announce(SettlementSnapshot settled)
        {
            if (settled == null || settled.IsEmpty) return;

            Settled?.Invoke(settled);

            if (settled.stoppedForRoom)
                GameEvents.FireToast("Your bag is full.", ChatTone.Warning);

            if (settled.ranOutOfInputs)
                GameEvents.FireToast("You ran out of materials.", ChatTone.Warning);
        }

        /// <summary>
        /// Keeps the local character list in step with the server's.
        ///
        /// Matched on id rather than rebuilt, so a CharacterData the game is currently
        /// pointing at survives -- replacing the list wholesale would leave
        /// CharacterManager.Current referencing an object nothing else can see.
        /// </summary>
        private static void MergeCharacterList(AccountData local, CharacterSummary[] summaries)
        {
            local.characters ??= new List<CharacterData>();

            if (summaries == null) return;

            var kept = new List<CharacterData>();

            foreach (var summary in summaries)
            {
                if (summary == null) continue;

                CharacterData existing =
                    local.characters.Find(c => c != null && c.characterId == summary.characterId);

                if (existing == null)
                {
                    existing = new CharacterData
                    {
                        characterId   = summary.characterId,
                        characterName = summary.name,
                        classId       = summary.classId,
                    };
                }

                existing.characterName = summary.name;
                existing.classId       = summary.classId;
                existing.xp            = summary.xp;
                existing.level         = summary.level;
                existing.lastMapId     = summary.lastMapId;

                // Same reasoning as Apply: an empty face from the server is silence,
                // not an instruction.
                if (summary.appearance is { IsEmpty: false })
                    existing.spumConfig = summary.appearance;

                kept.Add(existing);
            }

            // The server's list is the list. A character deleted on another device is
            // gone here too, and one this client invented and never saved never existed.
            local.characters = kept;
        }

        /// <summary>
        /// Records what the server says its content hashes to.
        ///
        /// ══ WHY IT IS ONLY RECORDED AND NOT COMPARED ══════════════════════════
        ///
        /// The client has no hash of its own. GameContent knows what it loaded but
        /// never fingerprints it, so there is nothing to compare against -- and a
        /// comparison against a value invented here would differ every time and train
        /// everybody to ignore the warning.
        ///
        /// Kept because it is the one piece of evidence available when a player is
        /// told a chestplate costs one thing and charged another. Being a version
        /// behind makes a TOOLTIP wrong rather than a reward wrong, since the server
        /// prices everything -- so this is diagnosis, not enforcement.
        ///
        /// TODO(Phase 1): hash the loaded catalogue on the client and compare, which
        /// turns this into a real staleness check. GameContent is where it belongs.
        /// </summary>
        private static void WarnIfContentIsStale(string serverVersion)
        {
            if (string.IsNullOrEmpty(serverVersion)) return;

            Debug.Log($"[ServerState] Server content version {serverVersion}.");
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            Account = null;
            Changed = null;
            Settled = null;
        }
    }
}
