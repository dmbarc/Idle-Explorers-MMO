using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace IdleExplorersTests
{
    /// <summary>
    /// The things that must be true about what a WebGL build contains, and about the
    /// wires that are supposed to be connected.
    ///
    /// ══ THE TWO CLASSES OF BUG HERE ═══════════════════════════════════════════════
    ///
    /// A SECRET IN THE BUILD. Everything shipped to WebGL is public -- not obscure,
    /// public -- and a service-role key in a client asset is a total compromise of the
    /// database that nothing else in this architecture can contain. It is also the
    /// easiest mistake in the world to make: the two Supabase keys sit next to each
    /// other in the dashboard and look identical.
    ///
    /// A SEAM NOBODY CALLS. The entire client backend -- an interface, three
    /// implementations, shadow mode, a comparison log, tests -- was built and finished
    /// and nothing ever called GameBackend.Configure. The game ran on LocalBackend and
    /// there was no code path by which it could not. Everything compiled, every test
    /// passed, and the feature did not exist.
    ///
    /// That second one is why these checks look for CALLS rather than definitions. A
    /// definition proves somebody wrote it; a call proves it runs.
    /// </summary>
    internal static class SecurityChecks
    {
        internal static void Run(Action<bool, string> check, string root)
        {
            Console.WriteLine("Secrets and wiring");

            NoSecretsInTheBuild(check, root);
            TheSeamIsConnected(check, root);
            PasswordsGoNowhere(check, root);
        }

        // ══ NOTHING SECRET SHIPS ══════════════════════════════════════════════

        /// <summary>
        /// Fragments that identify a credential which must never leave the server.
        ///
        /// "service_role" appears verbatim in a Supabase key's JWT payload, and base64
        /// is only self-aligning on three-byte boundaries -- so the three encodings
        /// below are the same string at each alignment. One of them survives into any
        /// encoded token regardless of where the claim sits.
        /// </summary>
        private static readonly string[] ServiceRoleMarkers =
        {
            "service_role", "c2VydmljZV9yb2xl", "NlcnZpY2Vfcm9sZ", "zZXJ2aWNlX3JvbG",
        };

        /// <summary>Other things that have no business in a client build.</summary>
        private static readonly (string Pattern, string What)[] Forbidden =
        {
            (@"postgres(ql)?://[^\s""']+",              "a database connection URL"),
            (@"Password\s*=\s*[^;""\s]{4,};",           "a database password"),
            (@"SUPABASE_JWT_SECRET",                    "the JWT signing secret"),
            (@"sk_live_[A-Za-z0-9]+",                   "a live payment secret key"),
            (@"-----BEGIN [A-Z ]*PRIVATE KEY-----",     "a private key"),
        };

        private static void NoSecretsInTheBuild(Action<bool, string> check, string root)
        {
            // Everything that gets compiled into or loaded by a player's copy of the
            // game. Deliberately NOT the server tree, which is where these things
            // belong and where finding one is correct.
            string[] shipped =
            {
                Path.Combine(root, "Assets", "Scripts"),
                Path.Combine(root, "Assets", "Resources"),
                Path.Combine(root, "Assets", "StreamingAssets"),
            };

            var offenders = new List<string>();

            foreach (string directory in shipped)
            {
                if (!Directory.Exists(directory)) continue;

                foreach (string file in Files(directory))
                {
                    string text = Safely(file);
                    if (text.Length == 0) continue;

                    // The checks themselves and the code that refuses these keys have to
                    // name them. Excluded by path rather than by cleverness.
                    if (file.Replace('\\', '/').EndsWith("Backend/ServerConfig.cs")) continue;
                    if (file.Replace('\\', '/').EndsWith("Backend/SupabaseAuthClient.cs")) continue;

                    foreach (string marker in ServiceRoleMarkers)
                        if (text.Contains(marker))
                            offenders.Add($"{Short(root, file)} contains a service-role key");

                    foreach (var (pattern, what) in Forbidden)
                        if (Regex.IsMatch(text, pattern))
                            offenders.Add($"{Short(root, file)} contains {what}");
                }
            }

            foreach (string offender in offenders) check(false, offender);

            check(offenders.Count == 0,
                  "nothing shipped to a player contains a server secret");

            // The config asset is the one somebody actually fills in, so it gets its
            // own named check rather than being one file among hundreds.
            string config = Path.Combine(root, "Assets", "Resources", "ServerConfig.asset");

            if (File.Exists(config))
            {
                string text = Safely(config);
                bool clean = true;

                foreach (string marker in ServiceRoleMarkers)
                    if (text.Contains(marker)) clean = false;

                check(clean, "ServerConfig.asset holds the anon key, not the service-role key");
            }
        }

        // ══ THE SEAM IS ACTUALLY CONNECTED ════════════════════════════════════

        private static void TheSeamIsConnected(Action<bool, string> check, string root)
        {
            // STRIPPED. Session.cs opens by explaining at length that nothing used to
            // call GameBackend.Configure -- so an unstripped check passes on the prose
            // describing the bug even after somebody reintroduces it. Found exactly
            // that way, by deliberately unplugging the call and watching this pass.
            string session = Strip(Safely(
                Path.Combine(root, "Assets", "Scripts", "Backend", "Session.cs")));

            check(session.Length > 0, "something exists whose job is to configure the backend");

            if (session.Length == 0) return;

            // The gap this whole file was written for: the seam existed, was tested,
            // and was never called.
            check(session.Contains("GameBackend.Configure"),
                  "the backend is actually pointed at a server somewhere");

            check(session.Contains("RuntimeInitializeOnLoadMethod"),
                  "it runs on its own at startup rather than waiting for a scene reference");

            check(session.Contains("BeforeSceneLoad"),
                  "it runs BEFORE the first scene, so nothing reads an account first");

            // A configured backend with no way to obtain a token authenticates nothing.
            check(session.Contains("AccessToken"),
                  "the backend is given a way to read the CURRENT token, not a copy of one");

            check(session.Contains("GoLocal"),
                  "a missing or broken configuration falls back to offline play");

            string login = Strip(Safely(
                Path.Combine(root, "Assets", "Scripts", "UI", "Screens", "LoginScreen.cs")));

            check(login.Contains("Session.SignInAsync") || login.Contains("SignInAsync"),
                  "the login screen signs in against the server when there is one");

            check(login.Contains("Connected"),
                  "the login screen still works offline, so the editor needs no server");
        }

        // ══ A PASSWORD IS NOT STATE ═══════════════════════════════════════════

        private static void PasswordsGoNowhere(Action<bool, string> check, string root)
        {
            string login = Strip(Safely(Path.Combine(root, "Assets", "Scripts", "UI", "Screens", "LoginScreen.cs")));
            string auth  = Strip(Safely(Path.Combine(root, "Assets", "Scripts", "Backend", "SupabaseAuthClient.cs")));

            foreach (var (file, name) in new[] { (login, "LoginScreen"), (auth, "SupabaseAuthClient") })
            {
                check(!Regex.IsMatch(file, @"PlayerPrefs\.SetString\s*\([^)]*[Pp]assword"),
                      $"{name} never writes a password to PlayerPrefs");

                check(!Regex.IsMatch(file, @"Debug\.Log\w*\s*\([^)]*[Pp]assword"),
                      $"{name} never logs a password");

                // A password in a query string ends up in server logs, in proxy logs and
                // in browser history, none of which anybody remembers to clear.
                check(!Regex.IsMatch(file, @"[?&]password="),
                      $"{name} never puts a password in a URL");
            }

            // The refresh token is what persists, deliberately -- see the class comment
            // on SupabaseAuthClient.
            check(auth.Contains("RefreshTokenKey"),
                  "the refresh token is what is remembered between sessions, not the password");

            check(auth.Contains("PlayerPrefs.DeleteKey"),
                  "signing out actually removes the stored session");
        }

        // ── Machinery ─────────────────────────────────────────────────────────

        private static IEnumerable<string> Files(string directory)
        {
            foreach (string pattern in new[] { "*.cs", "*.json", "*.asset", "*.txt" })
                foreach (string file in Directory.EnumerateFiles(directory, pattern, SearchOption.AllDirectories))
                    yield return file;
        }

        private static string Safely(string path)
        {
            try   { return File.Exists(path) ? File.ReadAllText(path) : ""; }
            catch { return ""; }
        }

        private static string Short(string root, string path) =>
            path.StartsWith(root) ? path.Substring(root.Length).TrimStart('\\', '/') : path;

        private static string Strip(string source)
        {
            source = Regex.Replace(source, @"/\*.*?\*/", "", RegexOptions.Singleline);
            source = Regex.Replace(source, @"^\s*///.*$", "", RegexOptions.Multiline);
            source = Regex.Replace(source, @"^\s*//.*$",  "", RegexOptions.Multiline);

            return source;
        }
    }
}
