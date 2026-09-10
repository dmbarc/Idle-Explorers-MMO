using System;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

namespace IdleExplorers.Backend
{
    /// <summary>
    /// Signing in, and staying signed in.
    ///
    /// ══ WHY THE CLIENT TALKS TO SUPABASE AND NOTHING ELSE DOES ════════════════════
    ///
    /// This is the ONE place the client is allowed to reach Supabase, and it reaches
    /// only /auth/v1. Identity is the single thing Supabase owns in this architecture:
    /// game state lives behind the ASP.NET server, and the client has no Postgres
    /// credentials at all.
    ///
    /// What comes back is a JWT. The game server verifies it against the project's JWT
    /// secret -- which the client never sees -- and takes the subject as the account
    /// id. So the token is the only thing that crosses, and forging one requires the
    /// secret.
    ///
    /// ══ WHY THE REFRESH TOKEN IS PERSISTED AND THE ACCESS TOKEN IS NOT ════════════
    ///
    /// An access token lives about an hour and is useless afterwards, so writing it
    /// down buys nothing and leaves a valid credential on disk. A refresh token is
    /// what lets somebody close the tab and come back tomorrow without typing a
    /// password, which is the difference between a game people return to and one they
    /// re-authenticate into.
    ///
    /// It goes in PlayerPrefs, which on WebGL is IndexedDB scoped to the origin. That
    /// is the same place every web app keeps one. Anybody with access to the machine
    /// can read it -- exactly as they could read a browser's saved session -- and
    /// SignOut clears it.
    ///
    /// ══ WHY IT REFRESHES EARLY ════════════════════════════════════════════════════
    ///
    /// A token that expires mid-request produces a 401 on something the player was in
    /// the middle of, and the retry path would have to understand authentication. Far
    /// simpler to renew before it matters.
    /// </summary>
    public class SupabaseAuthClient
    {
        /// <summary>
        /// How long before expiry a token is renewed.
        ///
        /// Five minutes. Long enough to cover a slow refresh on a bad connection, short
        /// enough that a session is not renewing constantly.
        /// </summary>
        public const double RenewWithinSeconds = 300d;

        /// <summary>Where the refresh token is kept between sessions.</summary>
        public const string RefreshTokenKey = "idle.auth.refresh";

        /// <summary>Seconds before a sign-in attempt is given up on.</summary>
        public const int TimeoutSeconds = 20;

        private readonly string _url;
        private readonly string _anonKey;

        private string   _accessToken = "";
        private DateTime _expiresAt   = DateTime.MinValue;

        /// <summary>Who is signed in, or empty. The Supabase user id, which the API uses as the account.</summary>
        public string UserId { get; private set; } = "";

        public string Email { get; private set; } = "";

        public bool IsSignedIn => !string.IsNullOrEmpty(_accessToken);

        /// <summary>True when a session can probably be restored without a password.</summary>
        public bool HasStoredSession => !string.IsNullOrEmpty(PlayerPrefs.GetString(RefreshTokenKey, ""));

        public SupabaseAuthClient(string supabaseUrl, string anonKey)
        {
            _url = (supabaseUrl ?? "").TrimEnd('/');

            // Refused rather than shipped. A service-role key here would authenticate
            // every request as a database superuser, and the leak has already happened
            // by the time this runs -- but failing loudly is how somebody finds out
            // before a player does.
            if (ServerConfig.LooksLikeServiceRole(anonKey))
            {
                Debug.LogError("[Auth] The configured key is a SERVICE-ROLE key. Refusing to use it. " +
                               "Rotate it in the Supabase dashboard and configure the anon key.");
                _anonKey = "";
                return;
            }

            _anonKey = anonKey ?? "";
        }

        /// <summary>
        /// The current access token, or empty.
        ///
        /// Read as a FUNCTION by ApiBackend rather than handed over as a string,
        /// because it changes: a backend holding a copy starts failing an hour into a
        /// session with no way to recover.
        /// </summary>
        public string AccessToken => _accessToken;

        /// <summary>True when the token is close enough to expiry to be worth renewing.</summary>
        public bool NeedsRenewal =>
            IsSignedIn && (_expiresAt - DateTime.UtcNow).TotalSeconds < RenewWithinSeconds;

        // ── The calls ─────────────────────────────────────────────────────────

        /// <summary>Creates an account. The player may need to confirm by email first.</summary>
        public Awaitable<AuthResult> SignUpAsync(string email, string password) =>
            PostAsync("/auth/v1/signup", Credentials(email, password));

        public Awaitable<AuthResult> SignInAsync(string email, string password) =>
            PostAsync("/auth/v1/token?grant_type=password", Credentials(email, password));

        /// <summary>
        /// Signs in without an email, for a player who wants to try the game.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The same signup endpoint with no credentials in the body. GoTrue reads
        /// the absence of an email as a request for an anonymous user and issues a
        /// real session for one, carrying <c>is_anonymous: true</c> in the token.
        /// Every other call in this class works on it unchanged, which is the
        /// point: a guest is not a second kind of player the game has to reason
        /// about, it is an ordinary account whose token says how it was made.
        /// </para>
        /// <para>
        /// The refresh token is stored like any other, so a guest who reloads the
        /// page keeps their character. What ends a guest is silence — the sweep
        /// deletes accounts that stop sending heartbeats — not the tab closing,
        /// which cannot be detected reliably enough to build on.
        /// </para>
        /// </remarks>
        public Awaitable<AuthResult> SignInAsGuestAsync() =>
            PostAsync("/auth/v1/signup", "{}");

        /// <summary>
        /// Picks up where the last session left off.
        ///
        /// Called at startup before the login screen is shown, so a returning player
        /// sees the game rather than a form. A failure here is ordinary -- the token
        /// expired, or was revoked -- and simply means the form appears.
        /// </summary>
        public async Awaitable<AuthResult> RestoreAsync()
        {
            string refresh = PlayerPrefs.GetString(RefreshTokenKey, "");

            if (string.IsNullOrEmpty(refresh))
                return AuthResult.Failed("No stored session.");

            AuthResult result = await PostAsync("/auth/v1/token?grant_type=refresh_token",
                                                $"{{\"refresh_token\":{Quote(refresh)}}}");

            // A refresh token that no longer works is not worth keeping. Left in place
            // it would be retried on every startup forever, each one a failed request
            // before the login screen.
            if (!result.Ok) Forget();

            return result;
        }

        /// <summary>
        /// Swaps an OAuth authorisation code for a session.
        ///
        /// ══ WHY THE VERIFIER GOES IN THE BODY ═════════════════════════════════
        ///
        /// This is the second half of PKCE. The challenge went out with the
        /// authorisation request; the verifier it was derived from arrives here, and
        /// Supabase checks that hashing one gives the other. That is what proves the
        /// party redeeming the code is the party that asked for it -- which matters
        /// enormously on desktop, where the redirect lands on a loopback port any
        /// other local process could have tried to answer.
        ///
        /// In the BODY, never the query string. A verifier in a URL is a verifier in
        /// an access log.
        /// </summary>
        public Awaitable<AuthResult> ExchangeCodeAsync(string code, string verifier) =>
            PostAsync("/auth/v1/token?grant_type=pkce",
                      $"{{\"auth_code\":{Quote(code)},\"code_verifier\":{Quote(verifier)}}}");

        /// <summary>Renews before expiry. Safe to call often; does nothing when not needed.</summary>
        public async Awaitable RenewAsync()
        {
            if (!NeedsRenewal) return;

            await RestoreAsync();
        }

        /// <summary>
        /// Ends the session, locally and at Supabase.
        ///
        /// Local first, and unconditionally: a sign-out that leaves a token behind
        /// because the network was down is a sign-out that did not happen, and the
        /// player has already walked away from the machine.
        /// </summary>
        public async Awaitable SignOutAsync()
        {
            string token = _accessToken;

            _accessToken = "";
            _expiresAt   = DateTime.MinValue;
            UserId       = "";
            Email        = "";

            Forget();

            if (string.IsNullOrEmpty(token) || !Configured) return;

            try
            {
                using var request = Build("/auth/v1/logout", "{}");
                request.SetRequestHeader("Authorization", $"Bearer {token}");

                await request.SendWebRequest();
            }
            catch (Exception)
            {
                // Already signed out as far as this machine is concerned. The token
                // expires on its own within the hour.
            }
        }

        // ── Machinery ─────────────────────────────────────────────────────────

        private bool Configured => !string.IsNullOrEmpty(_url) && !string.IsNullOrEmpty(_anonKey);

        private static string Credentials(string email, string password) =>
            $"{{\"email\":{Quote(email)},\"password\":{Quote(password)}}}";

        private async Awaitable<AuthResult> PostAsync(string path, string body)
        {
            if (!Configured) return AuthResult.Failed("No Supabase project is configured.");

            try
            {
                using var request = Build(path, body);

                await request.SendWebRequest();

                string text = request.downloadHandler?.text ?? "";

                if (request.result != UnityWebRequest.Result.Success ||
                    request.responseCode is < 200 or >= 300)
                {
                    return Rejected(request.responseCode, text)
                         ? AuthResult.WrongCredentials(Explain(request.responseCode, text))
                         : AuthResult.Failed(Explain(request.responseCode, text));
                }

                var session = JsonUtility.FromJson<SessionResponse>(text);

                if (session == null || string.IsNullOrEmpty(session.access_token))
                {
                    // Sign-up on a project that requires email confirmation answers 200
                    // with a user and no session. That is a success, and telling the
                    // player "something went wrong" would be a lie they act on.
                    return AuthResult.NeedsConfirmation();
                }

                Adopt(session);

                return AuthResult.Succeeded(UserId);
            }
            catch (Exception e)
            {
                return AuthResult.Failed(e.Message);
            }
        }

        private UnityWebRequest Build(string path, string body)
        {
            var request = new UnityWebRequest($"{_url}{path}", UnityWebRequest.kHttpVerbPOST)
            {
                uploadHandler   = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body)),
                downloadHandler = new DownloadHandlerBuffer(),
                timeout         = TimeoutSeconds,
            };

            request.SetRequestHeader("Content-Type", "application/json");

            // Supabase wants the anon key on every auth call, in both headers. Public by
            // design -- see ServerConfig.
            request.SetRequestHeader("apikey", _anonKey);
            request.SetRequestHeader("Authorization", $"Bearer {_anonKey}");

            return request;
        }

        private void Adopt(SessionResponse session)
        {
            _accessToken = session.access_token;

            // Renewed against a real clock rather than a countdown, so a suspended tab
            // wakes up knowing its token is stale instead of believing it has 50
            // minutes left.
            _expiresAt = DateTime.UtcNow.AddSeconds(session.expires_in > 0 ? session.expires_in : 3600);

            UserId = session.user?.id ?? UserId;
            Email  = session.user?.email ?? Email;

            if (!string.IsNullOrEmpty(session.refresh_token))
            {
                PlayerPrefs.SetString(RefreshTokenKey, session.refresh_token);
                PlayerPrefs.Save();
            }
        }

        private static void Forget()
        {
            PlayerPrefs.DeleteKey(RefreshTokenKey);
            PlayerPrefs.Save();
        }

        /// <summary>
        /// Turns a failure into something worth showing a player.
        ///
        /// Supabase answers with a JSON body naming the problem; the status alone says
        /// "400", which tells somebody who typed their password wrong nothing at all.
        /// </summary>
        /// <summary>
        /// Whether the email and password themselves were refused, as opposed to
        /// anything else that can go wrong on the way.
        ///
        /// Worth telling apart because it is the only failure where clearing the
        /// password box helps. Every other one -- a rate limit, an unreachable
        /// service, a game server that did not answer -- leaves a player retyping a
        /// password that was correct, which is a small insult repeated at the worst
        /// moment.
        ///
        /// Read from error_code rather than the prose, because the prose is Supabase's
        /// to reword and has been reworded.
        /// </summary>
        private static bool Rejected(long status, string text)
        {
            if (status is not (400 or 401)) return false;

            try
            {
                var problem = JsonUtility.FromJson<AuthError>(text);

                if (!string.IsNullOrEmpty(problem?.error_code))
                    return problem.error_code is "invalid_credentials" or "invalid_grant";
            }
            catch (Exception)
            {
                // Not a problem document; fall through to the status.
            }

            // A 400 from the token endpoint with nothing else to go on is a bad
            // password far more often than it is anything else.
            return status == 400;
        }

        private static string Explain(long status, string text)
        {
            if (!string.IsNullOrWhiteSpace(text))
            {
                try
                {
                    var problem = JsonUtility.FromJson<AuthError>(text);

                    string message = !string.IsNullOrEmpty(problem?.error_description) ? problem.error_description
                                   : !string.IsNullOrEmpty(problem?.msg)               ? problem.msg
                                   : problem?.message;

                    if (!string.IsNullOrEmpty(message)) return message;
                }
                catch (Exception)
                {
                    // Not a problem document. The status still says something.
                }
            }

            return status switch
            {
                0   => "Could not reach the sign-in service.",
                400 => "That email and password do not match.",
                422 => "That email or password is not acceptable.",
                429 => "Too many attempts. Wait a minute and try again.",
                _   => $"Sign-in failed ({status}).",
            };
        }

        private static string Quote(string value)
        {
            var text = new StringBuilder("\"");

            foreach (char c in value ?? "")
            {
                switch (c)
                {
                    case '"':  text.Append("\\\""); break;
                    case '\\': text.Append("\\\\"); break;
                    case '\n': text.Append("\\n");  break;
                    case '\r': text.Append("\\r");  break;
                    case '\t': text.Append("\\t");  break;
                    default:
                        // Control characters would produce invalid JSON, and an email
                        // field is a place somebody can paste anything.
                        if (c < ' ') text.Append("\\u").Append(((int)c).ToString("x4"));
                        else         text.Append(c);
                        break;
                }
            }

            return text.Append('"').ToString();
        }

        // ── Wire shapes ───────────────────────────────────────────────────────

        [Serializable] private class SessionResponse
        {
            public string access_token;
            public string refresh_token;
            public int    expires_in;
            public User   user;
        }

        [Serializable] private class User   { public string id; public string email; }
        [Serializable] private class AuthError { public string message; public string msg; public string error_description; public string error_code; }
    }

    /// <summary>What happened when somebody tried to sign in.</summary>
    public readonly struct AuthResult
    {
        public readonly bool   Ok;
        public readonly string UserId;
        public readonly string Message;

        /// <summary>
        /// True when the account exists but the email has not been confirmed.
        ///
        /// Its own state rather than a failure, because the player did nothing wrong
        /// and the next step is in their inbox rather than on the screen.
        /// </summary>
        public readonly bool   AwaitingConfirmation;

        /// <summary>
        /// True when the email and password themselves were refused.
        ///
        /// Distinct from a plain failure because it is the only one where the password
        /// box should be emptied. A sign-in that succeeded and then could not reach the
        /// game server must leave the field alone -- the password was right, and making
        /// somebody retype it to retry is a penalty for the server's problem.
        /// </summary>
        public readonly bool   CredentialsRejected;

        private AuthResult(bool ok, string userId, string message, bool awaiting,
                           bool rejected = false)
        {
            Ok                   = ok;
            UserId               = userId;
            Message              = message;
            AwaitingConfirmation = awaiting;
            CredentialsRejected  = rejected;
        }

        public static AuthResult Succeeded(string userId) => new(true, userId, "", false);
        public static AuthResult Failed(string message)   => new(false, "", message, false);

        /// <summary>The email and password were refused. The only failure worth clearing a field over.</summary>
        public static AuthResult WrongCredentials(string message) =>
            new(false, "", message, false, rejected: true);

        public static AuthResult NeedsConfirmation() =>
            new(false, "", "Check your email to confirm the account, then sign in.", true);
    }
}
