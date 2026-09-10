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
        private static bool                _ready;
        private static string              _resumeProblem = "";

        /// <summary>The auth client, or null when the game is running offline.</summary>
        public static SupabaseAuthClient Auth => _auth;

        /// <summary>Whether a server is configured at all. False means offline play.</summary>
        public static bool HasServer => _config != null && _config.HasServer;

        /// <summary>Whether somebody is signed in right now.</summary>
        public static bool IsSignedIn => _auth is { IsSignedIn: true };

        /// <summary>
        /// Raised when the signed-in state changes.
        ///
        /// ══ DO NOT RELY ON THIS FOR THE STARTUP SIGN-IN ═══════════════════════
        ///
        /// It is useless for that, and believing otherwise cost a whole round of
        /// debugging. Begin() runs at BeforeSceneLoad, so a Google redirect coming
        /// back, or a stored session being restored, raises this BEFORE ANY SCENE
        /// EXISTS. There is nothing subscribed yet and there cannot be.
        ///
        /// The symptom is a sign-in that works perfectly and a player looking at the
        /// login screen: token obtained, account created, event fired into an empty
        /// room. Use Ready and IsSignedIn -- a state a screen can ASK about whenever
        /// it happens to start -- rather than a moment it has to be present for.
        /// </summary>
        public static event Action<bool> SignedInChanged;

        /// <summary>
        /// Whether startup has finished deciding whether somebody is signed in.
        ///
        /// False for the first moments of the process, which matters because the login
        /// screen can appear before the token exchange has come back.
        /// </summary>
        public static bool Ready => _ready;

        /// <summary>
        /// Why a sign-in that was in progress at startup did not complete, or empty.
        ///
        /// Kept rather than only logged, so the login screen can say it. A silent
        /// return to the login form after a successful trip through Google is
        /// indistinguishable from the game ignoring the button.
        /// </summary>
        public static string ResumeProblem => _resumeProblem;

        /// <summary>
        /// Waits until startup has settled, then returns.
        ///
        /// A loop rather than a completion source because it is correct when called
        /// AFTER the fact as well -- which is the common case, since content loading
        /// usually outlasts the token exchange -- and needs nothing reset between
        /// play sessions.
        /// </summary>
        public static async Awaitable UntilReadyAsync()
        {
            while (!_ready) await Awaitable.NextFrameAsync();
        }

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

            // ══ AND AT RUNTIME, BELT AND BRACES ═════════════════════════════════
            //
            // The player setting is the real one; this covers a build made before it
            // was set and the editor, where the setting is a separate checkbox nobody
            // remembers. An online game that stops when the tab loses focus stops
            // being online.
            Application.runInBackground = true;

            try
            {
                _config = ServerConfig.Load();

                if (_config == null)
                {
                    // Not a warning. A checkout with no config asset is the normal state
                    // for somebody working on presentation, and it plays fine.
                    Debug.Log("[Session] No ServerConfig asset — playing offline.");
                    GameBackend.GoLocal();
                    _ready = true;
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
                    _ready = true;
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

                // ══ A WEB SIGN-IN COMING BACK ═════════════════════════════════
                //
                // Tried BEFORE the stored session, because this page load may be the
                // return leg of a Google redirect -- and a stale refresh token from a
                // previous account would otherwise win and sign the player into the
                // wrong one. Does nothing on every other page load, and nothing at all
                // off the web.
                AuthResult resumed = await GoogleSignIn.ResumeAsync();

                if (resumed.Ok)
                {
                    Debug.Log("[Session] Completed a Google sign-in.");
                    await ClaimAsync();
                    SignedInChanged?.Invoke(true);
                }
                else if (!string.IsNullOrEmpty(resumed.Message))
                {
                    _resumeProblem = resumed.Message;

                    // A resume that FAILED with something to say. The empty-message case
                    // is the ordinary page load with no code on it, which is almost every
                    // page load and must stay quiet -- but a real refusal from the token
                    // endpoint used to be discarded here, and a sign-in that dropped the
                    // player back on the login screen left nothing behind to look at.
                    Debug.LogError($"[Session] Google sign-in did not complete: {resumed.Message}");
                }

                if (!resumed.Ok && _auth is { HasStoredSession: true })
                {
                    AuthResult restored = await _auth.RestoreAsync();

                    if (restored.Ok)
                    {
                        Debug.Log("[Session] Signed in from a stored session.");
                        await ClaimAsync();
                        SignedInChanged?.Invoke(true);
                    }
                }

                Keep();

                _ready = true;
            }
            catch (Exception e)
            {
                // Nothing here is worth taking the game down for. Offline is a working
                // game; a failed launcher is not.
                Debug.LogError($"[Session] Could not start against the server: {e.Message}");
                GameBackend.GoLocal();

                // Set even on the failure path, or every screen waiting on it hangs
                // forever on the one morning the server is down.
                _ready = true;
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

            if (result.Ok)
            {
                await ClaimAsync();
                SignedInChanged?.Invoke(true);
            }

            return result;
        }

        public static async Awaitable<AuthResult> SignUpAsync(string email, string password)
        {
            if (_auth == null) return AuthResult.Failed("This build is not connected to a server.");

            AuthResult result = await _auth.SignUpAsync(email, password);

            if (result.Ok)
            {
                await ClaimAsync();
                SignedInChanged?.Invoke(true);
            }

            return result;
        }

        /// <summary>
        /// Sign in as a guest, with no email and no password.
        ///
        /// The account is real in every way the server cares about -- it owns
        /// characters, it settles, it is subject to the same rules -- and it is
        /// temporary: the server deletes guests that stop sending heartbeats for half
        /// an hour, and everything they own goes with them.
        ///
        /// Nothing here marks the account as a guest. The token says so, because
        /// Supabase issues it with is_anonymous set, and a flag of ours would be a
        /// second source of truth that could disagree with the first.
        /// </summary>
        public static async Awaitable<AuthResult> SignInAsGuestAsync()
        {
            if (_auth == null) return AuthResult.Failed("This build is not connected to a server.");

            AuthResult result = await _auth.SignInAsGuestAsync();

            if (result.Ok)
            {
                await ClaimAsync();
                SignedInChanged?.Invoke(true);
            }

            return result;
        }

        /// <summary>
        /// Sign in with Google.
        ///
        /// On the web this does not return in the ordinary sense -- the page navigates
        /// away mid-call and a later page load finishes the job through ResumeAsync.
        /// On desktop it waits for the browser and comes back with a session.
        /// </summary>
        public static async Awaitable<AuthResult> SignInWithGoogleAsync()
        {
            if (_auth == null) return AuthResult.Failed("This build is not connected to a server.");

            AuthResult result = await GoogleSignIn.SignInAsync();

            if (result.Ok)
            {
                await ClaimAsync();
                SignedInChanged?.Invoke(true);
            }

            return result;
        }

        /// <summary>
        /// Takes this client's claim on the account.
        ///
        /// ══ WHY EVERY SIGN-IN PATH CALLS IT ═════════════════════════════════
        ///
        /// Because there are five of them -- email, sign-up, Google, a returning Google
        /// redirect and a restored stored session -- and an account is only protected
        /// from being played twice if all five claim. One that forgot would be a way
        /// in that quietly skipped the rule.
        ///
        /// Failure is not fatal. A client with no claim is treated as an older build
        /// and allowed through, which is the deliberate soft edge in SessionGuard: the
        /// guard is against holding the WRONG claim, not against holding none.
        /// </summary>
        private static async Awaitable ClaimAsync()
        {
            if (!ServerState.IsAuthoritative) return;

            try
            {
                SessionClaimResult claim = await GameBackend.Current.ClaimSessionAsync();

                ApiBackend.SessionClaim = claim?.sessionId ?? "";

                Debug.Log("[Session] Holding the account.");
            }
            catch (BackendException e)
            {
                Debug.LogWarning($"[Session] Could not claim the account: {e.Message}");
            }
        }

        public static async Awaitable SignOutAsync()
        {
            if (_auth == null) return;

            // Released first, while the token still works. A claim left behind is
            // harmless -- the next sign-in displaces it -- but releasing means the
            // next one is a clean claim rather than a displacement.
            if (ServerState.IsAuthoritative && !string.IsNullOrEmpty(ApiBackend.SessionClaim))
            {
                try   { await GameBackend.Current.ReleaseSessionAsync(); }
                catch (BackendException) { /* the next sign-in takes it anyway */ }
            }

            ApiBackend.SessionClaim = "";

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
            _started       = false;
            _ready         = false;
            _resumeProblem = "";
            _config        = null;
            _auth          = null;

            SignedInChanged = null;
        }
    }
}
