using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace IdleExplorers.Backend
{
    /// <summary>
    /// Sign in with Google, without a plugin, a Firebase project or a client secret.
    ///
    /// ══ HOW IT WORKS ══════════════════════════════════════════════════════════════
    ///
    ///   1. invent a PKCE verifier, send its hash to Supabase
    ///   2. send the player's BROWSER to Supabase, which sends them to Google
    ///   3. Google returns them to Supabase, which redirects back with a code
    ///   4. swap the code plus the verifier for a session
    ///
    /// The game never sees a Google password and never handles one. Supabase holds the
    /// Google client secret; this side holds nothing durable at all, which is the whole
    /// reason PKCE is used -- see Pkce.
    ///
    /// ══ THE ONLY THING THAT DIFFERS PER PLATFORM ══════════════════════════════════
    ///
    /// Where step 3 lands.
    ///
    ///   desktop and editor   a loopback HTTP server this process runs for a minute
    ///   WebGL                the page itself, with the code read back off its own URL
    ///
    /// Everything else -- the verifier, the URL, the exchange -- is identical, which is
    /// why they share this file rather than being two implementations that drift.
    ///
    /// ══ WHAT HAS TO BE SET UP ONCE, OUTSIDE THE GAME ══════════════════════════════
    ///
    ///   · a Google Cloud OAuth client, whose authorised redirect is
    ///     https://PROJECT.supabase.co/auth/v1/callback
    ///   · that client's id and secret pasted into Supabase's Google provider
    ///   · this game's redirect URL added to Supabase's allow-list
    ///
    /// None of it can be done from code, and all of it fails visibly rather than
    /// silently -- Supabase refuses an unlisted redirect outright.
    /// </summary>
    public static class GoogleSignIn
    {
        /// <summary>
        /// Where the verifier waits while the browser is away.
        ///
        /// On WebGL the page NAVIGATES AWAY and the process ends -- so the verifier has
        /// to outlive it, or the code comes back and there is nothing left to redeem it
        /// with.
        ///
        /// PLAYERPREFS CANNOT DO THIS ON THE WEB, which cost a debugging round. Unity
        /// backs it with IndexedDB and persists through setTimeout(..., 0) followed by
        /// an async syncfs -- so a Save() immediately followed by a navigation is one
        /// JavaScript task, and the document dies before the timeout fires. The value
        /// is simply never written. See IdleOAuth.jslib.
        ///
        /// So the web uses localStorage, which is synchronous, and every other platform
        /// keeps PlayerPrefs, where there is no navigation to lose the race to.
        ///
        /// It is deleted the moment it is used. A verifier left behind is a spent key
        /// lying around, and the next sign-in would find a stale one.
        /// </summary>
        private const string VerifierKey = "idle.auth.pkce";

        /// <summary>Whether a Google button should be shown at all.</summary>
        public static bool IsAvailable => Session.HasServer && Session.Auth != null;

        /// <summary>
        /// Runs the whole flow and returns the result.
        ///
        /// On WebGL this NEVER RETURNS on the way out -- the page navigates to Google
        /// mid-call. The return value only matters on the way back, when Resume picks
        /// the code off the URL at startup.
        /// </summary>
        public static async Awaitable<AuthResult> SignInAsync()
        {
            var config = ServerConfig.Load();

            if (config == null || !config.HasAuth)
                return AuthResult.Failed("This build is not connected to a sign-in service.");

            string verifier  = Pkce.NewVerifier();
            string challenge = Pkce.Challenge(verifier);

            Remember(verifier);

#if UNITY_WEBGL && !UNITY_EDITOR
            // The page goes to Google and this build stops existing. Resume() finishes
            // the job when it comes back.
            Navigate(Authorize(config, challenge, CurrentPageUrl()));

            return AuthResult.Failed("Redirecting to Google…");
#else
            using var catcher = new LoopbackCatcher();

            if (!catcher.Start())
            {
                return AuthResult.Failed(
                    $"Could not open the sign-in listener on port {LoopbackCatcher.Port}. " +
                    "Something else is using it.");
            }

            Application.OpenURL(Authorize(config, challenge, LoopbackCatcher.RedirectUri));

            string code = await catcher.AwaitCodeAsync();

            if (string.IsNullOrEmpty(code))
            {
                Forget();
                return AuthResult.Failed("Sign-in was cancelled or timed out.");
            }

            return await RedeemAsync(verifier, code);
#endif
        }

        /// <summary>
        /// Finishes a WebGL sign-in, if this page load is one coming back from Google.
        ///
        /// Called at startup. Does nothing at all on the ordinary path, which is most
        /// page loads -- so it is cheap to call unconditionally rather than making
        /// somebody remember when it applies.
        /// </summary>
        public static async Awaitable<AuthResult> ResumeAsync()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            string code = QueryParameter("code");

            if (string.IsNullOrEmpty(code)) return AuthResult.Failed("");

            string verifier = Recall();

            if (string.IsNullOrEmpty(verifier))
            {
                // A code with no verifier: a bookmarked callback, a refresh, or a
                // second tab. Not worth a toast -- but it is logged, because this is
                // also exactly what a broken store looks like, and last time it looked
                // like nothing at all.
                Debug.LogWarning("[GoogleSignIn] Came back with a code but no verifier. " +
                                 "A stale callback URL, or browser storage is unavailable.");
                ClearCodeFromUrl();
                return AuthResult.Failed("");
            }

            AuthResult result = await RedeemAsync(verifier, code);

            // ══ THE CODE COMES OFF THE ADDRESS BAR EITHER WAY ═════════════════
            //
            // It is single-use and already spent. Leaving it there means it lands in
            // history and in anything the player pastes the URL into, and a refresh
            // retries an exchange that can only fail.
            ClearCodeFromUrl();

            return result;
#else
            await Awaitable.NextFrameAsync();

            return AuthResult.Failed("");
#endif
        }

        // ── The exchange ──────────────────────────────────────────────────────

        private static async Awaitable<AuthResult> RedeemAsync(string verifier, string code)
        {
            Forget();

            if (Session.Auth == null)
                return AuthResult.Failed("No sign-in service is configured.");

            return await Session.Auth.ExchangeCodeAsync(code, verifier);
        }

        /// <summary>
        /// Puts the verifier somewhere that outlives this page.
        ///
        /// Synchronously on the web. Anything deferred loses to the navigation on the
        /// very next line -- that is not a tuning question, it is the whole reason this
        /// is not one call to PlayerPrefs.
        /// </summary>
        private static void Remember(string verifier)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            IdleOAuthStore(VerifierKey, verifier);
#else
            PlayerPrefs.SetString(VerifierKey, verifier);
            PlayerPrefs.Save();
#endif
        }

        private static string Recall()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return IdleOAuthLoad(VerifierKey) ?? "";
#else
            return PlayerPrefs.GetString(VerifierKey, "");
#endif
        }

        private static void Forget()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            IdleOAuthForget(VerifierKey);
#else
            PlayerPrefs.DeleteKey(VerifierKey);
            PlayerPrefs.Save();
#endif
        }

        /// <summary>
        /// The URL that starts the whole thing.
        ///
        /// Everything is escaped, because the redirect contains a colon and slashes and
        /// the challenge is base64url -- an unescaped redirect is one Supabase reads as
        /// truncated and rejects as not on the allow-list, which reads as a
        /// configuration problem rather than an encoding one.
        /// </summary>
        private static string Authorize(ServerConfig config, string challenge, string redirect) =>
            $"{config.supabaseUrl.TrimEnd('/')}/auth/v1/authorize" +
            $"?provider=google" +
            $"&redirect_to={Uri.EscapeDataString(redirect)}" +
            $"&code_challenge={Uri.EscapeDataString(challenge)}" +
            $"&code_challenge_method=s256";

        // ── The web platform ──────────────────────────────────────────────────

#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")] private static extern void IdleOAuthNavigate(string url);
        [DllImport("__Internal")] private static extern string IdleOAuthQuery(string key);
        [DllImport("__Internal")] private static extern string IdleOAuthPageUrl();
        [DllImport("__Internal")] private static extern void IdleOAuthClearQuery();
        [DllImport("__Internal")] private static extern void IdleOAuthStore(string key, string value);
        [DllImport("__Internal")] private static extern string IdleOAuthLoad(string key);
        [DllImport("__Internal")] private static extern void IdleOAuthForget(string key);

        private static void Navigate(string url)   => IdleOAuthNavigate(url);
        private static string QueryParameter(string key) => IdleOAuthQuery(key) ?? "";
        private static string CurrentPageUrl()     => IdleOAuthPageUrl() ?? "";
        private static void ClearCodeFromUrl()     => IdleOAuthClearQuery();
#endif
    }
}
