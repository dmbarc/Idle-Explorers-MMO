using System;
using UnityEngine;

namespace IdleExplorers.Backend
{
    /// <summary>
    /// The thing that was missing: something that actually points the game at the server.
    ///
    /// ══ WHY THIS FILE EXISTS ══════════════════════════════════════════════════════
    ///
    /// Every piece of the client seam was built -- the interface, three backends,
    /// shadow mode, the comparison log -- and nothing ever called
    /// GameBackend.Configure. So the whole apparatus sat there, compiling, tested, and
    /// unreachable: the game ran entirely on LocalBackend and there was no code path by
    /// which it could not.
    ///
    /// That is the most expensive kind of gap, because everything looks finished.
    ///
    /// ══ WHAT IT DOES ══════════════════════════════════════════════════════════════
    ///
    /// At startup, before any scene that needs an account:
    ///
    ///   1. reads ServerConfig, and stays local if there is nothing to connect to
    ///   2. restores the previous session, so a returning player skips the form
    ///   3. wires GameBackend to ApiBackend -- through ShadowBackend while migrating
    ///   4. keeps the token fresh for as long as the game is running
    ///
    /// ══ WHY IT NEVER THROWS ═══════════════════════════════════════════════════════
    ///
    /// A configuration mistake, an unreachable server, an expired token: every one of
    /// them leaves the game playable offline rather than stuck on a black screen. The
    /// reason is logged once and the player gets a login screen. A launcher that can
    /// fail closed is a launcher that will, on the morning of the playtest.
    /// </summary>
    public static class Session
    {
        /// <summary>How often the token's remaining life is checked. Cheap; it usually does nothing.</summary>
        public const float RenewalCheckSeconds = 60f;

        private static ServerConfig        _config;
        private static SupabaseAuthClient  _auth;
        private static bool                _started;

        /// <summary>The auth client, or null when the game is running offline.</summary>
        public static SupabaseAuthClient Auth => _auth;

        /// <summary>Whether a server is configured at all. False means offline play.</summary>
        public static bool HasServer => _config != null && _config.HasServer;

        /// <summary>Whether somebody is signed in right now.</summary>
        public static bool IsSignedIn => _auth is { IsSignedIn: true };

        /// <summary>Raised when the signed-in state changes, so screens can react.</summary>
        public static event Action<bool> SignedInChanged;

        /// <summary>
        /// Runs before the first scene loads.
        ///
        /// BeforeSceneLoad rather than a MonoBehaviour in the bootstrap scene, because
        /// the backend has to be configured before anything reads an account -- and a
        /// component's Awake ordering against every other Awake is not something worth
        /// depending on.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static async void Begin()
        {
            if (_started) return;
            _started = true;

            try
            {
                _config = ServerConfig.Load();

                if (_config == null)
                {
                    // Not a warning. A checkout with no config asset is the normal state
                    // for somebody working on presentation, and it plays fine.
                    Debug.Log("[Session] No ServerConfig asset — playing offline.");
                    GameBackend.GoLocal();
                    return;
                }

                string problem = _config.Problem();

                if (!string.IsNullOrEmpty(problem))
                {
                    // Loud, because a misconfigured build fails in a way that looks like
                    // a server outage, and somebody will spend an afternoon on it.
                    Debug.LogError($"[Session] ServerConfig: {problem}");
                }

                if (!_config.HasServer)
                {
                    Debug.Log("[Session] No API configured — playing offline.");
                    GameBackend.GoLocal();
                    return;
                }

                _auth = _config.HasAuth
                    ? new SupabaseAuthClient(_config.supabaseUrl, _config.supabaseAnonKey)
                    : null;

                // Configured BEFORE the sign-in attempt, and with a function rather than
                // a string. Requests made while signed out simply carry no token and get
                // a 401, which is correct -- the alternative is a backend that has to be
                // rebuilt the moment somebody logs in.
                GameBackend.Configure(_config.apiBaseUrl,
                                      () => _auth?.AccessToken ?? "",
                                      _config.shadowMode);

                if (_auth is { HasStoredSession: true })
                {
                    AuthResult restored = await _auth.RestoreAsync();

                    if (restored.Ok)
                    {
                        Debug.Log("[Session] Signed in from a stored session.");
                        SignedInChanged?.Invoke(true);
                    }
                }

                Keep();
            }
            catch (Exception e)
            {
                // Nothing here is worth taking the game down for. Offline is a working
                // game; a failed launcher is not.
                Debug.LogError($"[Session] Could not start against the server: {e.Message}");
                GameBackend.GoLocal();
            }
        }

        // ── Signing in ────────────────────────────────────────────────────────

        /// <summary>
        /// Signs in and tells anybody listening.
        ///
        /// Returns the result rather than throwing, because every failure here is
        /// something a player needs to read: a wrong password, an unconfirmed email, a
        /// server that is down.
        /// </summary>
        public static async Awaitable<AuthResult> SignInAsync(string email, string password)
        {
            if (_auth == null) return AuthResult.Failed("This build is not connected to a server.");

            AuthResult result = await _auth.SignInAsync(email, password);

            if (result.Ok) SignedInChanged?.Invoke(true);

            return result;
        }

        public static async Awaitable<AuthResult> SignUpAsync(string email, string password)
        {
            if (_auth == null) return AuthResult.Failed("This build is not connected to a server.");

            AuthResult result = await _auth.SignUpAsync(email, password);

            if (result.Ok) SignedInChanged?.Invoke(true);

            return result;
        }

        public static async Awaitable SignOutAsync()
        {
            if (_auth == null) return;

            await _auth.SignOutAsync();

            SignedInChanged?.Invoke(false);
        }

        // ── Staying signed in ─────────────────────────────────────────────────

        /// <summary>
        /// Renews the token in the background for as long as the game runs.
        ///
        /// A loop rather than a MonoBehaviour, because there is no natural object to
        /// hang it on and a hidden DontDestroyOnLoad GameObject is a thing people find
        /// in the hierarchy and delete.
        /// </summary>
        private static async void Keep()
        {
            while (Application.isPlaying)
            {
                await Awaitable.WaitForSecondsAsync(RenewalCheckSeconds);

                if (_auth == null) return;

                try
                {
                    // Does nothing unless expiry is close. A renewal that fails is not
                    // fatal on its own -- the current token is still valid for minutes,
                    // and the next pass tries again.
                    await _auth.RenewAsync();
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[Session] Token renewal failed: {e.Message}");
                }
            }
        }

        /// <summary>
        /// Statics survive between play sessions when domain reload is disabled, so
        /// without this the second run would believe it had already started and skip
        /// configuring anything.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            _started = false;
            _config  = null;
            _auth    = null;

            SignedInChanged = null;
        }
    }
}
