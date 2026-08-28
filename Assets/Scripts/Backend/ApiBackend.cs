using System;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace IdleExplorers.Backend
{
    /// <summary>
    /// The real backend, over HTTP.
    ///
    /// ══ WHY UnityWebRequest AND NOT HttpClient ════════════════════════════════════
    ///
    /// Not preference. WebGL excludes System.Net entirely: HttpClient does not exist
    /// in that build, and neither does ClientWebSocket. The WebSocket case is the nasty
    /// one — it compiles and then hangs forever at runtime rather than failing — which
    /// is why there is no realtime connection anywhere in this design.
    ///
    /// UnityWebRequest is the only transport on the web, so it is the transport
    /// everywhere. One code path that works in three builds beats two that work in one
    /// each.
    ///
    /// ══ EVERY MUTATION CARRIES A KEY ══════════════════════════════════════════════
    ///
    /// A fresh idempotency key per logical operation, reused across retries of that
    /// same operation. That is the whole contract: the key identifies the INTENT, so
    /// retrying with the same one is safe and retrying with a new one is a second
    /// craft. Retry generates no new key for exactly that reason.
    /// </summary>
    public class ApiBackend : IGameBackend
    {
        private readonly string _baseUrl;
        private readonly Func<string> _accessToken;

        /// <summary>
        /// How many times a transient failure is retried.
        ///
        /// Three, with a short backoff. A WebGL tab gets suspended mid-request whenever
        /// a phone locks or a laptop lid closes, and the server sheds load as 503 under
        /// a spike -- both are worth riding out silently rather than showing a player
        /// an error for something that fixes itself.
        /// </summary>
        public const int MaxAttempts = 3;

        /// <summary>Seconds before a request is given up on.</summary>
        public const int TimeoutSeconds = 20;

        public bool IsAvailable => !string.IsNullOrEmpty(_baseUrl);

        /// <param name="baseUrl">Origin only, no trailing slash.</param>
        /// <param name="accessToken">
        /// Reads the current Supabase access token. A FUNCTION rather than a string,
        /// because tokens expire and a backend holding a stale copy would start failing
        /// an hour into a session with no way to recover.
        /// </param>
        public ApiBackend(string baseUrl, Func<string> accessToken)
        {
            _baseUrl     = (baseUrl ?? "").TrimEnd('/');
            _accessToken = accessToken ?? (() => null);
        }

        // ── The calls ─────────────────────────────────────────────────────────

        public Awaitable<AccountSnapshot> GetAccountAsync() =>
            GetAsync<AccountSnapshot>("/account/");

        public Awaitable<CharacterSnapshot> GetCharacterAsync(string characterId) =>
            GetAsync<CharacterSnapshot>($"/character/{characterId}");

        public async Awaitable SaveAppearanceAsync(string characterId, SpumSaveData appearance) =>
            await SendAsync<EmptyResponse>("PUT", $"/character/{characterId}/appearance",
                                           new AppearanceBody { appearance = appearance });

        public async Awaitable SaveLocationAsync(string characterId, string mapId) =>
            await SendAsync<EmptyResponse>("PUT", $"/character/{characterId}/location",
                                           new MapBody { mapId = mapId });

        public Awaitable<CharacterSnapshot> CreateCharacterAsync(string name, string classId,
                                                                 SpumSaveData appearance) =>
            PostAsync<CharacterSnapshot>("/character/", new CreateCharacterBody
            {
                name       = name,
                classId    = classId ?? "",

                // Sent WITH the creation rather than saved after it. A second call
                // that can fail on its own is a character created with a default face
                // and no obvious way for the player to tell why.
                appearance = appearance,
            });

        public Awaitable<ActivitySnapshot> SetGatheringAsync(string characterId, string nodeId) =>
            PostAsync<ActivitySnapshot>($"/activity/{characterId}", new NodeBody { nodeId = nodeId });

        public Awaitable<ActivitySnapshot> SetCraftingAsync(string characterId, string recipeId) =>
            PostAsync<ActivitySnapshot>($"/activity/{characterId}/craft", new RecipeBody { recipeId = recipeId });

        public Awaitable<ActivitySnapshot> SetFightingAsync(string characterId, string monsterId) =>
            PostAsync<ActivitySnapshot>($"/activity/{characterId}/fight", new MonsterBody { monsterId = monsterId });

        public Awaitable<SettlementSnapshot> StopActivityAsync(string characterId) =>
            SendAsync<SettlementSnapshot>("DELETE", $"/activity/{characterId}", null);

        public async Awaitable HeartbeatAsync(string characterId) =>
            await PostAsync<EmptyResponse>($"/activity/{characterId}/beat", null);

        public Awaitable<SettlementSnapshot> SettleAsync(string characterId) =>
            PostAsync<SettlementSnapshot>($"/activity/{characterId}/settle", null);

        public Awaitable<MinigameSnapshot> ReportMinigameAsync(string characterId, string[] grades) =>
            PostAsync<MinigameSnapshot>($"/activity/{characterId}/minigame",
                                        new GradesBody { grades = grades });

        public Awaitable<EncounterSnapshot> EngageBossAsync(string characterId, string monsterId) =>
            PostAsync<EncounterSnapshot>($"/encounter/{characterId}",
                                         new MonsterBody { monsterId = monsterId });

        public Awaitable<EncounterTick> ReportBossActionsAsync(string characterId,
                                                               BossActionReport[] actions) =>
            PostAsync<EncounterTick>($"/encounter/{characterId}/actions",
                                     new ActionsBody { actions = actions });

        public Awaitable<EncounterResult> ResolveBossAsync(string characterId) =>
            PostAsync<EncounterResult>($"/encounter/{characterId}/resolve", null);

        public Awaitable<LootClaim> ClaimLootAsync(string characterId) =>
            PostAsync<LootClaim>($"/encounter/{characterId}/loot", null);

        public Awaitable<CharacterSnapshot> EquipAsync(string characterId, string itemId, string slotId) =>
            PostAsync<CharacterSnapshot>($"/equipment/{characterId}/equip",
                                         new EquipBody { itemId = itemId, slotId = slotId });

        public Awaitable<CharacterSnapshot> UnequipAsync(string characterId, string slotId) =>
            PostAsync<CharacterSnapshot>($"/equipment/{characterId}/unequip",
                                         new SlotBody { slotId = slotId });

        public Awaitable<BankMoveResult> DepositAsync(string characterId, string itemId, long quantity) =>
            PostAsync<BankMoveResult>($"/bank/{characterId}/deposit",
                                      new MoveBody { itemId = itemId, quantity = quantity });

        public Awaitable<BankMoveResult> WithdrawAsync(string characterId, string itemId, long quantity) =>
            PostAsync<BankMoveResult>($"/bank/{characterId}/withdraw",
                                      new MoveBody { itemId = itemId, quantity = quantity });

        public Awaitable<BankSnapshot> GetBankAsync() => GetAsync<BankSnapshot>("/bank/");

        public Awaitable<TalentSnapshot> SpendTalentAsync(string characterId, string nodeId) =>
            PostAsync<TalentSnapshot>($"/talent/{characterId}", new NodeIdBody { nodeId = nodeId });

        public Awaitable<TalentSnapshot> GetTalentsAsync(string characterId) =>
            GetAsync<TalentSnapshot>($"/talent/{characterId}");

        public Awaitable<BossGateSnapshot> GetBossGateAsync(string characterId) =>
            GetAsync<BossGateSnapshot>($"/boss/{characterId}/gate");

        public Awaitable<BossGateSnapshot> UnlockBossAsync(string characterId) =>
            PostAsync<BossGateSnapshot>($"/boss/{characterId}/unlock", null);

        // ── Transport ─────────────────────────────────────────────────────────

        private Awaitable<T> GetAsync<T>(string path) where T : class =>
            SendAsync<T>(UnityWebRequest.kHttpVerbGET, path, null);

        private Awaitable<T> PostAsync<T>(string path, object body) where T : class =>
            SendAsync<T>(UnityWebRequest.kHttpVerbPOST, path, body);

        /// <summary>
        /// One logical operation, retried as needed.
        ///
        /// The idempotency key is generated ONCE, outside the retry loop. That is the
        /// entire safety property: a retry carrying the same key is collapsed by the
        /// server into the original, and a retry carrying a new one is a second craft.
        /// </summary>
        private async Awaitable<T> SendAsync<T>(string method, string path, object body)
            where T : class
        {
            string requestId = Guid.NewGuid().ToString("N");
            BackendException last = null;

            for (int attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                try
                {
                    return await AttemptAsync<T>(method, path, body, requestId);
                }
                catch (BackendException e) when (e.IsTransient && attempt < MaxAttempts)
                {
                    last = e;

                    // Backing off rather than hammering. A server shedding load
                    // recovers faster when the clients that were shed wait.
                    await Awaitable.WaitForSecondsAsync(0.4f * attempt);
                }
            }

            throw last ?? new BackendException(0, "unreachable", $"{method} {path} failed.");
        }

        private async Awaitable<T> AttemptAsync<T>(string method, string path, object body,
                                                   string requestId) where T : class
        {
            using var request = new UnityWebRequest($"{_baseUrl}{path}", method)
            {
                downloadHandler = new DownloadHandlerBuffer(),
                timeout         = TimeoutSeconds,
            };

            if (body != null)
            {
                byte[] payload = Encoding.UTF8.GetBytes(JsonUtility.ToJson(body));

                request.uploadHandler = new UploadHandlerRaw(payload);
                request.SetRequestHeader("Content-Type", "application/json");
            }
            else if (method != UnityWebRequest.kHttpVerbGET)
            {
                // An empty body still needs a content type, or some proxies will
                // reject the request before it reaches the server.
                request.uploadHandler = new UploadHandlerRaw(Array.Empty<byte>());
                request.SetRequestHeader("Content-Type", "application/json");
            }

            string token = _accessToken();
            if (!string.IsNullOrEmpty(token))
                request.SetRequestHeader("Authorization", $"Bearer {token}");

            // Every mutating request, always. The server refuses one without it, which
            // is deliberate: a missing key is a client bug and should be loud.
            if (method != UnityWebRequest.kHttpVerbGET)
                request.SetRequestHeader("Idempotency-Key", requestId);

            await request.SendWebRequest();

            long   status = request.responseCode;
            string text   = request.downloadHandler?.text ?? "";

            if (request.result != UnityWebRequest.Result.Success || status is < 200 or >= 300)
                throw Failure(request, status, text);

            if (typeof(T) == typeof(EmptyResponse)) return null;

            if (string.IsNullOrWhiteSpace(text))
                throw new BackendException((int)status, "empty response", $"{method} {path} returned nothing.");

            try
            {
                return JsonUtility.FromJson<T>(text);
            }
            catch (Exception e)
            {
                // A parse failure here is almost always a shape mismatch between the
                // server's JSON and the [Serializable] class -- most likely somebody
                // returned a Dictionary, which JsonUtility reads as empty rather than
                // as an error. Worth naming, because the symptom is a player being
                // told they earned nothing.
                throw new BackendException((int)status, "unreadable response",
                                           $"{method} {path}: {e.Message}");
            }
        }

        /// <summary>
        /// Turns a failed request into something with a title worth reading.
        ///
        /// The server answers problems as JSON with a title and detail, so those are
        /// preferred over UnityWebRequest's own error string -- "HTTP/1.1 409 Conflict"
        /// tells a player nothing, and "Both your hands are on that weapon" tells them
        /// everything.
        /// </summary>
        private static BackendException Failure(UnityWebRequest request, long status, string text)
        {
            string title  = request.error ?? "request failed";
            string detail = null;

            if (!string.IsNullOrWhiteSpace(text))
            {
                try
                {
                    var problem = JsonUtility.FromJson<ProblemResponse>(text);

                    if (problem != null && !string.IsNullOrEmpty(problem.title))
                    {
                        title  = problem.title;
                        detail = problem.detail;
                    }
                }
                catch (Exception)
                {
                    // Not a problem document. The status and Unity's own error still
                    // say enough to act on.
                }
            }

            return new BackendException((int)status, title, detail);
        }

        // ── Request bodies ────────────────────────────────────────────────────

        [Serializable] private class CreateCharacterBody
        {
            public string       name;
            public string       classId;
            public SpumSaveData appearance;
        }
        [Serializable] private class NodeBody            { public string nodeId; }
        [Serializable] private class RecipeBody          { public string recipeId; }
        [Serializable] private class MonsterBody         { public string monsterId; }
        [Serializable] private class GradesBody          { public string[] grades; }
        [Serializable] private class EquipBody           { public string itemId; public string slotId; }
        [Serializable] private class SlotBody            { public string slotId; }
        [Serializable] private class MoveBody            { public string itemId; public long quantity; }
        [Serializable] private class NodeIdBody          { public string nodeId; }
        [Serializable] private class ActionsBody         { public BossActionReport[] actions; }
        [Serializable] private class AppearanceBody      { public SpumSaveData appearance; }
        [Serializable] private class MapBody             { public string mapId; }

        /// <summary>For calls whose answer nobody reads.</summary>
        private sealed class EmptyResponse { }

        [Serializable] private class ProblemResponse { public string title; public string detail; public int status; }
    }
}
