using System;
using System.IO;
using System.Text.RegularExpressions;
using IdleExplorers.Backend;

namespace IdleExplorersTests
{
    /// <summary>
    /// The OAuth flow, and the parts of it that are only wrong in production.
    ///
    /// ══ WHY PKCE IS TESTED AND NOT JUST TRUSTED ═══════════════════════════════════
    ///
    /// Because every way of getting it wrong still signs people in. A verifier that is
    /// reused, too short, or hashed with the wrong encoding produces a flow that works
    /// perfectly on a developer's machine and provides none of the protection it exists
    /// for. There is no failing symptom to notice.
    ///
    /// The one that matters most here: on desktop the redirect lands on a loopback port
    /// that any other local process could try to answer. Without a correct verifier,
    /// intercepting the code is enough to become the player.
    /// </summary>
    internal static class OAuthChecks
    {
        internal static void Run(Action<bool, string> check, string root)
        {
            Console.WriteLine("Google sign-in");

            Verifiers(check);
            Challenges(check);
            Wiring(check, root);
            WebStorage(check, root);
        }

        // ══ THE VERIFIER ══════════════════════════════════════════════════════

        private static void Verifiers(Action<bool, string> check)
        {
            string first  = Pkce.NewVerifier();
            string second = Pkce.NewVerifier();

            // Reuse would defeat the entire mechanism: this sign-in's code must only be
            // redeemable by this sign-in.
            check(first != second, "every sign-in gets a fresh verifier");

            // RFC 7636 says 43 to 128 characters. Below the floor is guessable; above
            // the ceiling is rejected by the server, which reads as a config problem.
            check(first.Length is >= 43 and <= 128,
                  $"a verifier is a legal length (this one is {first.Length})");

            // base64url only. A + or / or = in the value means it is being sent in the
            // wrong alphabet, and the server hashes something different from what we
            // hashed -- a mismatch with no useful message attached.
            check(Regex.IsMatch(first, @"^[A-Za-z0-9\-_]+$"),
                  "a verifier is base64url, so it survives being put in a URL");
        }

        // ══ THE CHALLENGE ═════════════════════════════════════════════════════

        private static void Challenges(Action<bool, string> check)
        {
            // The worked example from RFC 7636 appendix B. Checking against a known
            // answer rather than against our own implementation is the only way to
            // catch an encoding that is self-consistent and wrong -- which would work
            // against nothing but itself.
            const string verifier  = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
            const string expected  = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";

            check(Pkce.Challenge(verifier) == expected,
                  "the challenge matches the worked example in RFC 7636");

            check(Pkce.Challenge("a") != Pkce.Challenge("b"),
                  "different verifiers give different challenges");

            check(!Pkce.Challenge(verifier).Contains("="),
                  "the challenge carries no base64 padding, which a URL would mangle");

            check(Pkce.Challenge(verifier) != verifier,
                  "the challenge is a HASH -- sending the verifier itself would publish it");
        }

        // ══ THE WIRING ════════════════════════════════════════════════════════

        private static void Wiring(Action<bool, string> check, string root)
        {
            string google = Strip(Read(root, "Assets/Scripts/Backend/GoogleSignIn.cs"));
            string auth   = Strip(Read(root, "Assets/Scripts/Backend/SupabaseAuthClient.cs"));
            string jslib  = Read(root, "Assets/Plugins/WebGL/IdleOAuth.jslib");

            check(google.Length > 0, "GoogleSignIn exists");
            check(jslib.Length > 0,  "the WebGL bridge exists, so the web build can redirect");

            if (google.Length == 0) return;

            check(google.Contains("code_challenge_method=s256"),
                  "the S256 method, not plain -- plain puts the verifier in the URL");

            // A URL is not the place for a verifier: it lands in browser history and in
            // every access log between here and the server.
            check(!Regex.IsMatch(google, @"code_verifier=[^\"" ]*\{"),
                  "the verifier is never put in a query string");

            check(auth.Contains("grant_type=pkce"),
                  "the code is exchanged through the PKCE grant");

            check(Regex.IsMatch(auth, @"auth_code.*code_verifier", RegexOptions.Singleline),
                  "the exchange sends the verifier in the BODY");

            // The verifier has to outlive a page navigation on the web, or the code
            // comes back with nothing left to redeem it.
            check(google.Contains("PlayerPrefs.SetString") && google.Contains("PlayerPrefs.DeleteKey"),
                  "the verifier is stored across the redirect and deleted once spent");

            // A spent code left on the address bar lands in history and in anything the
            // player pastes, and a refresh retries an exchange that can only fail.
            check(jslib.Contains("replaceState"),
                  "the spent code is taken off the URL without reloading the page");

            check(!jslib.Contains("window.location.search = ") && !jslib.Contains("location.reload"),
                  "clearing the URL does not reload -- that would restart Unity mid-sign-in");

            string login = Strip(Read(root, "Assets/Scripts/UI/Screens/LoginScreen.cs"));

            check(login.Contains("SignInWithGoogleAsync"),
                  "the login screen can actually start a Google sign-in");

            check(login.Contains("GoogleSignIn.IsAvailable"),
                  "the button is hidden offline rather than shown and broken");

            // ══ THE BUG THAT SENT A REAL PLAYER A MESSAGE THEY COULD NOT ACT ON ══
            //
            // OnShow used to pre-fill the field from AccountManager, which offline
            // holds a name like "Adventurer". Connected, that put a non-email in the
            // email box -- so the email check passed, the password check failed, and
            // the player was told "Enter your password" while looking at a form that
            // appeared to be filled in.
            //
            // Asserted by ORDER rather than by absence, because both branches
            // legitimately exist in this method: the connected one reads the remembered
            // email and returns, and only then does the offline one touch accountName.
            // A cruder "these two words never appear near each other" check flagged the
            // correct code, which is how this comment came to be written.
            int connected = login.IndexOf("if (Connected)", StringComparison.Ordinal);
            int lastEmail = login.IndexOf("LastEmailKey", StringComparison.Ordinal);
            int accountNm = login.IndexOf("accountName", StringComparison.Ordinal);

            check(connected >= 0 && lastEmail > connected,
                  "the connected branch pre-fills from the remembered email");

            check(accountNm < 0 || accountNm > lastEmail,
                  "the local account name is only reached AFTER the connected branch returns");

            check(Regex.IsMatch(login, @"if \(Connected\)[\s\S]{0,300}?return;", RegexOptions.None),
                  "the connected branch returns before the offline pre-fill can run");
        }

        // == THE VERIFIER HAS TO SURVIVE A PAGE NAVIGATION =====================

        /// <summary>
        /// The bug this exists to prevent, which shipped once.
        ///
        /// On WebGL the sign-in stores a PKCE verifier and then immediately sends the
        /// page to Google. PlayerPrefs cannot carry a value across that: Unity backs it
        /// with IndexedDB and persists via setTimeout(..., 0) followed by an async
        /// syncfs, so Save() and the navigation land in ONE JavaScript task and the
        /// document dies before the write starts.
        ///
        /// What made it expensive is that every visible sign of success was present.
        /// Google accepted the login, Supabase returned a code, the page came home with
        /// ?code= on the address bar -- and then the flow silently fell back to the
        /// login screen, because the verifier needed to redeem that code was gone.
        /// Nothing was logged and nothing was shown.
        ///
        /// Checked structurally rather than by proximity: the WebGL arm of Remember is
        /// extracted and inspected on its own. A file-wide "PlayerPrefs must not appear"
        /// would be wrong -- desktop uses it correctly, and should.
        /// </summary>
        private static void WebStorage(Action<bool, string> check, string root)
        {
            string signIn = Strip(Read(root, "Assets/Scripts/Backend/GoogleSignIn.cs"));
            string bridge = Read(root, "Assets/Plugins/WebGL/IdleOAuth.jslib");

            if (signIn.Length == 0 || bridge.Length == 0)
            {
                check(false, "GoogleSignIn.cs and IdleOAuth.jslib both exist");
                return;
            }

            string web = WebGLArm(signIn, "Remember");

            check(web.Length > 0, "GoogleSignIn.Remember has a WebGL-specific arm");

            check(!web.Contains("PlayerPrefs"),
                  "the web verifier is NOT stored through PlayerPrefs -- it is asynchronous " +
                  "there and loses the race to the navigation on the next line");

            check(web.Contains("IdleOAuthStore"),
                  "the web verifier goes through the synchronous localStorage bridge");

            // A DllImport naming a function the bridge does not export is a symbol that
            // resolves to nothing. Cheap to check here; awkward to diagnose in a browser.
            foreach (Match import in Regex.Matches(
                         signIn, @"DllImport\(""__Internal""\)\]\s*private static extern \S+ (\w+)"))
            {
                string name = import.Groups[1].Value;

                check(bridge.Contains(name + ":"),
                      $"IdleOAuth.jslib exports {name}, which GoogleSignIn imports");
            }

            // The desktop arm keeps PlayerPrefs, and should: there is no navigation to
            // lose to, and it is the store that persists across a restart.
            check(signIn.Contains("PlayerPrefs.SetString"),
                  "desktop still remembers the verifier through PlayerPrefs");
        }

        /// <summary>
        /// The body of one method's #if UNITY_WEBGL arm, and nothing beyond that method.
        ///
        /// The brace counting is the point. A first version searched forward from the
        /// method name to the next #if UNITY_WEBGL in the file -- which, when the arm
        /// under test was deleted, happily returned the NEXT method's arm and reported
        /// two of three assertions passing on code that no longer existed. Verified by
        /// reinstating the original bug and watching it half-pass.
        ///
        /// That is the same mistake this suite has now made in four separate places:
        /// checking what is nearby instead of what is being asked about. Bounding the
        /// slice to the method is what makes the answer exact rather than probable.
        /// </summary>
        private static string WebGLArm(string source, string method)
        {
            Match at = Regex.Match(source, @"\b" + Regex.Escape(method) + @"\s*\([^)]*\)\s*\{");

            if (!at.Success) return "";

            string body = Body(source, source.IndexOf('{', at.Index));

            Match arm = Regex.Match(
                body, @"#if UNITY_WEBGL && !UNITY_EDITOR([\s\S]*?)#(?:else|endif)");

            return arm.Success ? arm.Groups[1].Value : "";
        }

        /// <summary>Everything between a brace and its partner.</summary>
        private static string Body(string source, int open)
        {
            if (open < 0) return "";

            int depth = 0;

            for (int at = open; at < source.Length; at++)
            {
                if (source[at] == '{') depth++;
                else if (source[at] == '}' && --depth == 0)
                    return source.Substring(open + 1, at - open - 1);
            }

            return "";
        }

        private static string Read(string root, string relative)
        {
            string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

            return File.Exists(path) ? File.ReadAllText(path) : "";
        }

        private static string Strip(string source)
        {
            source = Regex.Replace(source, @"/\*.*?\*/", "", RegexOptions.Singleline);
            source = Regex.Replace(source, @"^\s*///.*$", "", RegexOptions.Multiline);
            source = Regex.Replace(source, @"^\s*//.*$",  "", RegexOptions.Multiline);

            return source;
        }
    }
}
