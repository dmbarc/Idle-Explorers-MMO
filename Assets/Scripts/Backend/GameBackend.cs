using UnityEngine;

namespace IdleExplorers.Backend
{
    /// <summary>
    /// Which backend the game is talking to.
    ///
    /// ══ WHY A STATIC ══════════════════════════════════════════════════════════════
    ///
    /// The same reason GameManager's managers are: the alternative is threading an
    /// IGameBackend through every screen, every node and every controller that might
    /// one day need one, and a constructor parameter cannot reach a MonoBehaviour
    /// Unity instantiated.
    ///
    /// It is deliberately the ONLY static here. Everything else takes the interface,
    /// so a test can hand a fake to a specific object without touching global state.
    ///
    /// ══ WHY IT DEFAULTS TO LOCAL ══════════════════════════════════════════════════
    ///
    /// A game with no configured server still runs. That is the whole point of the
    /// seam during the migration: opening the editor and pressing play must not
    /// require a container, or the iteration loop dies and people stop testing.
    ///
    /// ══ WHERE THE REAL ONE COMES FROM ═════════════════════════════════════════════
    ///
    /// Configure() is called at startup once the API base URL and access token are
    /// known. Until then, and whenever the server is not configured, this is
    /// LocalBackend and the game behaves exactly as it did before any of this.
    /// </summary>
    public static class GameBackend
    {
        private static IGameBackend _current;

        /// <summary>
        /// The backend in use. Never null.
        ///
        /// Never null on purpose: a null here would mean a null check at every call
        /// site, and the one that gets forgotten is in the code path nobody tests.
        /// </summary>
        public static IGameBackend Current => _current ??= new LocalBackend();

        /// <summary>Whether the game is talking to a server at all.</summary>
        public static bool IsRemote => _current is ApiBackend or ShadowBackend;

        /// <summary>
        /// Divergences seen so far, if shadow mode is running. Null otherwise.
        ///
        /// Exposed so a debug panel can show the count without knowing how shadow
        /// mode is wired -- and so the number is visible to whoever is playing, which
        /// is the only way a week of shadow mode gets looked at.
        /// </summary>
        public static Divergences Divergences => (_current as ShadowBackend)?.Log;

        /// <summary>
        /// Points the game at a server.
        /// </summary>
        /// <param name="baseUrl">Origin of the API. Empty stays local.</param>
        /// <param name="accessToken">Reads the current token. See ApiBackend.</param>
        /// <param name="shadow">
        /// True to run BOTH: local answers, server compared. The safe way to turn a
        /// server on for the first time, because nothing the server says is used.
        /// </param>
        public static void Configure(string baseUrl, System.Func<string> accessToken,
                                     bool shadow = true)
        {
            if (string.IsNullOrEmpty(baseUrl))
            {
                _current = new LocalBackend();
                Debug.Log("[Backend] No API configured — running locally.");
                return;
            }

            var api = new ApiBackend(baseUrl, accessToken);

            _current = shadow ? new ShadowBackend(new LocalBackend(), api) : api;

            Debug.Log($"[Backend] {(shadow ? "Shadow mode against" : "Live against")} {baseUrl}.");
        }

        /// <summary>Back to local. For tests, and for turning a bad server off.</summary>
        public static void GoLocal() => _current = new LocalBackend();

        /// <summary>
        /// Statics survive between play sessions when domain reload is disabled, so a
        /// backend configured in the last session would still be pointed at whatever
        /// it was pointed at then -- including a server that has since been stopped.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => _current = null;
    }
}
