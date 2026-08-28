using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace IdleExplorers.Backend
{
    /// <summary>
    /// Runs both backends and reports where they disagree.
    ///
    /// ══ WHY THIS EXISTS ═══════════════════════════════════════════════════════════
    ///
    /// A migration from client-authoritative to server-authoritative has exactly one
    /// hard question: does the server compute the same numbers the game has always
    /// computed? Answering it by reading two implementations is how people miss an
    /// off-by-one in an XP curve for six months.
    ///
    /// So: the local backend answers, the game plays normally, and every call is sent
    /// to the server in parallel. Divergences are logged. Playing for a week turns the
    /// whole game into a fuzzer, and it costs one extra request per call.
    ///
    /// ══ WHICH ANSWER WINS ═════════════════════════════════════════════════════════
    ///
    /// The LOCAL one, always, for as long as shadow mode is on. That is what makes it
    /// safe to leave running: a server bug cannot break anyone's session, because
    /// nothing the server says is used. The moment its answers are trusted is the
    /// moment shadow mode ends and ApiBackend is wired up directly.
    ///
    /// ══ WHY THE SERVER CALL CANNOT THROW ══════════════════════════════════════════
    ///
    /// Every server failure is swallowed and counted. The whole point is to observe a
    /// server that is expected to be wrong; letting its exceptions reach the game
    /// would make shadow mode less stable than not running it, and nobody would.
    /// </summary>
    public class ShadowBackend : IGameBackend
    {
        private readonly IGameBackend _local;
        private readonly IGameBackend _remote;
        private readonly Divergences  _log;

        public bool IsAvailable => _local.IsAvailable;

        /// <summary>Everything seen so far. Read it from a debug panel or a test.</summary>
        public Divergences Log => _log;

        public ShadowBackend(IGameBackend local, IGameBackend remote, Divergences log = null)
        {
            _local  = local  ?? throw new ArgumentNullException(nameof(local));
            _remote = remote ?? throw new ArgumentNullException(nameof(remote));
            _log    = log    ?? new Divergences();
        }

        // ── Reads ─────────────────────────────────────────────────────────────

        public async Awaitable<AccountSnapshot> GetAccountAsync()
        {
            var local = await _local.GetAccountAsync();

            await CompareAsync("GetAccount", local,
                               () => _remote.GetAccountAsync(),
                               (a, b) => Compare.Accounts(a, b));

            return local;
        }

        /// <summary>Only the server keeps a class list that talents are checked against.</summary>
        public Awaitable AddClassAsync(string characterId, string classId) =>
            _remote.AddClassAsync(characterId, classId);

        public async Awaitable SaveAppearanceAsync(string characterId, SpumSaveData appearance)
        {
            await _local.SaveAppearanceAsync(characterId, appearance);
            await _remote.SaveAppearanceAsync(characterId, appearance);
        }

        /// <summary>Only the real server can grant. The local side has no wallet to move.</summary>
        public Awaitable<TestGrantResult> GrantTestPackAsync(string packId) =>
            _remote.GrantTestPackAsync(packId);

        /// <summary>Only the server holds the wallet the price comes out of.</summary>
        public Awaitable<PurchaseResult> BuyProductAsync(string characterId, string productId) =>
            _remote.BuyProductAsync(characterId, productId);

        /// <summary>The claim is the server's to hand out; the local side has no accounts.</summary>
        public Awaitable<SessionClaimResult> ClaimSessionAsync() => _remote.ClaimSessionAsync();

        public Awaitable ReleaseSessionAsync() => _remote.ReleaseSessionAsync();

        /// <summary>Only the server holds the credited-seconds balance.</summary>
        public Awaitable<UseItemResult> UseItemAsync(string characterId, string itemId) =>
            _remote.UseItemAsync(characterId, itemId);

        /// <summary>
        /// Straight through to the server. There is no local twin of other people.
        /// </summary>
        public Awaitable<PresenceSnapshot> ReportPresenceAsync(string characterId, string mapId,
                                                               float x, float z, string say) =>
            _remote.ReportPresenceAsync(characterId, mapId, x, z, say);

        // Straight through, like presence: there is no local population to compare
        // against, so a shadow of this would be shadowing nothing.
        public Awaitable<StrikeResult> StrikeAsync(string characterId, string monsterId,
                                                   double damage, double seconds) =>
            _remote.StrikeAsync(characterId, monsterId, damage, seconds);

        // Straight through. There is no local telemetry to disagree with.
        public Awaitable<Telemetry.Receipt> ReportTelemetryAsync(Telemetry.Event[] events) =>
            _remote.ReportTelemetryAsync(events);

        public Awaitable<PartySnapshot> GetPartyAsync(string characterId) =>
            _remote.GetPartyAsync(characterId);

        public Awaitable<PartySnapshot> FormPartyAsync(string characterId) =>
            _remote.FormPartyAsync(characterId);

        public Awaitable<PartySnapshot> JoinPartyAsync(string characterId, string leaderCharacterId) =>
            _remote.JoinPartyAsync(characterId, leaderCharacterId);

        public Awaitable<PartySnapshot> LeavePartyAsync(string characterId) =>
            _remote.LeavePartyAsync(characterId);

        public async Awaitable SaveLocationAsync(string characterId, string mapId, float x, float z)
        {
            await _local.SaveLocationAsync(characterId, mapId, x, z);
            await _remote.SaveLocationAsync(characterId, mapId, x, z);
        }

        public async Awaitable<CharacterSnapshot> GetCharacterAsync(string characterId)
        {
            var local = await _local.GetCharacterAsync(characterId);

            await CompareAsync("GetCharacter", local,
                               () => _remote.GetCharacterAsync(characterId),
                               (a, b) => Compare.Characters(a, b));

            return local;
        }

        public async Awaitable<BossGateSnapshot> GetBossGateAsync(string characterId)
        {
            var local = await _local.GetBossGateAsync(characterId);

            await CompareAsync("GetBossGate", local,
                               () => _remote.GetBossGateAsync(characterId),
                               (a, b) => Compare.Gates(a, b));

            return local;
        }

        // ── Writes ────────────────────────────────────────────────────────────
        //
        // Sent to BOTH. The server needs the same history the client has, or its
        // answers diverge for reasons that have nothing to do with its arithmetic --
        // a server that was never told the character started mining will correctly
        // report zero ore, and that is not the bug anyone is looking for.

        public async Awaitable<SettlementSnapshot> SettleAsync(string characterId)
        {
            var local = await _local.SettleAsync(characterId);

            await CompareAsync("Settle", local,
                               () => _remote.SettleAsync(characterId),
                               (a, b) => Compare.Settlements(a, b));

            return local;
        }

        public async Awaitable<SettlementSnapshot> StopActivityAsync(string characterId)
        {
            var local = await _local.StopActivityAsync(characterId);

            await CompareAsync("StopActivity", local,
                               () => _remote.StopActivityAsync(characterId),
                               (a, b) => Compare.Settlements(a, b));

            return local;
        }

        public async Awaitable<CharacterSnapshot> CreateCharacterAsync(string name, string classId,
                                                                       SpumSaveData appearance)
        {
            var local = await _local.CreateCharacterAsync(name, classId, appearance);

            // Not compared: the two will assign different ids, and that is correct
            // rather than a divergence. Mirrored so the server has the character.
            await MirrorAsync("CreateCharacter", () => _remote.CreateCharacterAsync(name, classId, appearance));

            return local;
        }

        public async Awaitable<ActivitySnapshot> SetGatheringAsync(string characterId, string nodeId)
        {
            var local = await _local.SetGatheringAsync(characterId, nodeId);
            await MirrorAsync("SetGathering", () => _remote.SetGatheringAsync(characterId, nodeId));

            return local;
        }

        public async Awaitable<ActivitySnapshot> SetCraftingAsync(string characterId, string recipeId)
        {
            var local = await _local.SetCraftingAsync(characterId, recipeId);
            await MirrorAsync("SetCrafting", () => _remote.SetCraftingAsync(characterId, recipeId));

            return local;
        }

        public async Awaitable<ActivitySnapshot> SetFightingAsync(string characterId, string monsterId)
        {
            var local = await _local.SetFightingAsync(characterId, monsterId);
            await MirrorAsync("SetFighting", () => _remote.SetFightingAsync(characterId, monsterId));

            return local;
        }

        public async Awaitable HeartbeatAsync(string characterId)
        {
            await _local.HeartbeatAsync(characterId);
            await MirrorAsync("Heartbeat", async () => { await _remote.HeartbeatAsync(characterId); return true; });
        }

        /// <summary>
        /// Mirrored, never compared.
        ///
        /// The local backend deliberately computes no bonus, so comparing the two
        /// would report a divergence on every single report -- which is noise that
        /// buries the findings shadow mode exists for. The server still receives the
        /// grades, so its side of the feature is exercised.
        /// </summary>
        public async Awaitable<MinigameSnapshot> ReportMinigameAsync(string characterId, string[] grades)
        {
            var local = await _local.ReportMinigameAsync(characterId, grades);

            await MirrorAsync("ReportMinigame", () => _remote.ReportMinigameAsync(characterId, grades));

            return local;
        }

        // ══ THE ONE PLACE THE SERVER'S ANSWER WINS ════════════════════════════
        //
        // Everywhere else in shadow mode the local answer is returned and the server
        // is only observed -- that is what makes it safe to leave running, because a
        // server bug cannot break a session.
        //
        // The boss is different, and the difference is not a compromise. There IS no
        // local answer: LocalBackend deliberately computes nothing, so returning it
        // would mean shadow mode makes the King unfightable. Passing the server's
        // answer through costs nothing that was working before, because nothing was.

        public Awaitable<EncounterSnapshot> EngageBossAsync(string characterId, string monsterId) =>
            _remote.EngageBossAsync(characterId, monsterId);

        public Awaitable<EncounterTick> ReportBossActionsAsync(string characterId,
                                                               BossActionReport[] actions) =>
            _remote.ReportBossActionsAsync(characterId, actions);

        public Awaitable<EncounterResult> ResolveBossAsync(string characterId) =>
            _remote.ResolveBossAsync(characterId);

        public Awaitable<LootClaim> ClaimLootAsync(string characterId) =>
            _remote.ClaimLootAsync(characterId);

        // Mirrored, not compared. The local side answers these from managers that are
        // still the authority in shadow mode, so every comparison would report a
        // divergence by construction -- noise that buries real findings.

        public async Awaitable<CharacterSnapshot> EquipAsync(string characterId, string itemId, string slotId)
        {
            var local = await _local.EquipAsync(characterId, itemId, slotId);
            await MirrorAsync("Equip", () => _remote.EquipAsync(characterId, itemId, slotId));

            return local;
        }

        public async Awaitable<CharacterSnapshot> UnequipAsync(string characterId, string slotId)
        {
            var local = await _local.UnequipAsync(characterId, slotId);
            await MirrorAsync("Unequip", () => _remote.UnequipAsync(characterId, slotId));

            return local;
        }

        public async Awaitable<BankMoveResult> DepositAsync(string characterId, string itemId, long quantity)
        {
            var local = await _local.DepositAsync(characterId, itemId, quantity);
            await MirrorAsync("Deposit", () => _remote.DepositAsync(characterId, itemId, quantity));

            return local;
        }

        public async Awaitable<BankMoveResult> WithdrawAsync(string characterId, string itemId, long quantity)
        {
            var local = await _local.WithdrawAsync(characterId, itemId, quantity);
            await MirrorAsync("Withdraw", () => _remote.WithdrawAsync(characterId, itemId, quantity));

            return local;
        }

        public Awaitable<BankSnapshot> GetBankAsync() => _local.GetBankAsync();

        public async Awaitable<TalentSnapshot> SpendTalentAsync(string characterId, string nodeId)
        {
            var local = await _local.SpendTalentAsync(characterId, nodeId);
            await MirrorAsync("SpendTalent", () => _remote.SpendTalentAsync(characterId, nodeId));

            return local;
        }

        public Awaitable<TalentSnapshot> GetTalentsAsync(string characterId) =>
            _local.GetTalentsAsync(characterId);

        public async Awaitable<BossGateSnapshot> UnlockBossAsync(string characterId)
        {
            var local = await _local.UnlockBossAsync(characterId);

            await CompareAsync("UnlockBoss", local,
                               () => _remote.UnlockBossAsync(characterId),
                               (a, b) => Compare.Gates(a, b));

            return local;
        }

        // ── Machinery ─────────────────────────────────────────────────────────

        private async Awaitable CompareAsync<T>(string call, T local,
                                                Func<Awaitable<T>> remote,
                                                Func<T, T, List<string>> compare) where T : class
        {
            try
            {
                T answer = await remote();

                if (answer == null)
                {
                    _log.RecordFailure(call, "the server returned nothing");
                    return;
                }

                List<string> differences = compare(local, answer);

                if (differences.Count == 0) { _log.RecordAgreement(call); return; }

                _log.RecordDivergence(call, differences);
            }
            catch (Exception e)
            {
                // Counted, never rethrown. Shadow mode must not be able to break a
                // session, or it gets switched off and stops finding anything.
                _log.RecordFailure(call, e.Message);
            }
        }

        private async Awaitable MirrorAsync<T>(string call, Func<Awaitable<T>> remote)
        {
            try
            {
                await remote();
                _log.RecordAgreement(call);
            }
            catch (Exception e)
            {
                _log.RecordFailure(call, e.Message);
            }
        }
    }

    /// <summary>
    /// What the two backends disagreed about.
    ///
    /// Counted as well as logged, because the useful question after a week of playing
    /// is "how often" rather than "did it ever" -- a divergence on one call in ten
    /// thousand is a rounding boundary, and one on every call is a formula.
    /// </summary>
    public class Divergences
    {
        private readonly Dictionary<string, int> _agreements = new();
        private readonly Dictionary<string, int> _divergences = new();
        private readonly Dictionary<string, int> _failures    = new();
        private readonly List<string>            _examples    = new();

        /// <summary>Most examples kept. Beyond this the counts tell the story.</summary>
        public const int MaxExamples = 50;

        public int TotalAgreements  => Sum(_agreements);
        public int TotalDivergences => Sum(_divergences);
        public int TotalFailures    => Sum(_failures);

        public IReadOnlyList<string> Examples => _examples;

        public void RecordAgreement(string call) => Bump(_agreements, call);

        public void RecordDivergence(string call, List<string> differences)
        {
            Bump(_divergences, call);

            string detail = $"{call}: {string.Join("; ", differences)}";

            if (_examples.Count < MaxExamples) _examples.Add(detail);

            // A warning rather than an error: during shadow mode a divergence is the
            // expected finding, and turning the console red for the thing being
            // looked for trains people to ignore it.
            Debug.LogWarning($"[Shadow] {detail}");
        }

        public void RecordFailure(string call, string reason)
        {
            Bump(_failures, call);

            if (_examples.Count < MaxExamples) _examples.Add($"{call} failed: {reason}");
        }

        /// <summary>A report worth pasting into a bug.</summary>
        public string Summary()
        {
            var report = new StringBuilder();

            report.AppendLine($"Shadow mode: {TotalAgreements} agreed, " +
                              $"{TotalDivergences} diverged, {TotalFailures} failed.");

            foreach (var call in _divergences)
                report.AppendLine($"  diverged  {call.Key}: {call.Value}");

            foreach (var call in _failures)
                report.AppendLine($"  failed    {call.Key}: {call.Value}");

            foreach (string example in _examples)
                report.AppendLine($"    {example}");

            return report.ToString();
        }

        public void Clear()
        {
            _agreements.Clear();
            _divergences.Clear();
            _failures.Clear();
            _examples.Clear();
        }

        private static void Bump(Dictionary<string, int> into, string key) =>
            into[key] = into.TryGetValue(key, out int n) ? n + 1 : 1;

        private static int Sum(Dictionary<string, int> counts)
        {
            int total = 0;
            foreach (var entry in counts) total += entry.Value;

            return total;
        }
    }
}
