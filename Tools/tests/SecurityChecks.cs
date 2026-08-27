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
            DeployConfig(check, root);
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
            // The CURRENT format. Listed first because new Supabase projects are issued
            // this one, and the sweep originally knew only the legacy shape below --
            // which meant it would have passed an sb_secret_ key shipped in an asset.
            "sb_secret_",

            // The legacy JWT: unencoded, then at each of the three base64 alignments.
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

        // ══ THE DEPLOY CONFIG POINTS AT REAL FILES ════════════════════════════

        /// <summary>
        /// Every path in fly.toml resolves from the REPOSITORY ROOT.
        ///
        /// ══ WHY THIS IS WORTH A CHECK ═════════════════════════════════════════
        ///
        /// The Dockerfile copies Assets/Scripts/Rules and Assets/StreamingAssets, which
        /// live outside server/ -- so the build context must be the repo root, and
        /// flyctl takes its context from the directory you run it in.
        ///
        /// fly.toml lives in server/, which makes "relative to the root" look wrong to
        /// anybody editing it, and the natural correction breaks the build. The first
        /// version of that file said dockerfile = "Dockerfile" and
        /// ignorefile = "../.dockerignore" -- correct-looking, and impossible: from
        /// server/ the context has no Assets/ in it.
        ///
        /// That fails on deploy day with a COPY error naming a path that plainly
        /// exists, which is a genuinely confusing hour.
        /// </summary>
        internal static void DeployConfig(Action<bool, string> check, string root)
        {
            string path = Path.Combine(root, "server", "fly.toml");

            // COMMENTS STRIPPED FIRST. fly.toml explains at length what its paths used
            // to say and why that was wrong -- and Regex.Match returns the FIRST match,
            // which was the sentence describing the bug rather than the setting. The
            // check failed against a file that was already correct.
            //
            // Third time this exact trap has appeared in this suite. Prose that names
            // the thing it warns about will always be found before the code.
            string toml = StripToml(Safely(path));

            check(toml.Length > 0, "server/fly.toml exists");

            if (toml.Length == 0) return;

            foreach (var (key, mustExist) in new[]
                     {
                         ("dockerfile", "server/Dockerfile"),
                         ("ignorefile", ".dockerignore"),
                     })
            {
                var match = Regex.Match(toml, key + @"\s*=\s*""([^""]+)""");

                check(match.Success, $"fly.toml names a {key}");

                if (!match.Success) continue;

                string named = match.Groups[1].Value;

                check(!named.StartsWith(".."),
                      $"fly.toml {key} does not climb out of the repo -- the context IS the root");

                check(File.Exists(Path.Combine(root, named.Replace('/', Path.DirectorySeparatorChar))),
                      $"fly.toml {key} '{named}' resolves from the repository root");

                check(named == mustExist,
                      $"fly.toml {key} is '{mustExist}', so `fly deploy --config server/fly.toml` " +
                      "from the root finds it");
            }

            // The port the container listens on and the port Fly routes to are two
            // numbers that must agree, in two files, neither of which mentions the
            // other. A mismatch is a deploy that passes its build and fails every
            // health check.
            var port = Regex.Match(toml, @"internal_port\s*=\s*(\d+)");

            check(port.Success && port.Groups[1].Value == "8080",
                  "fly.toml routes to 8080, which is what the container exposes");

            // 256 MB is the Fly default and it is not enough for a .NET server holding
            // a 25-connection pool and the whole content catalogue.
            var memory = Regex.Match(toml, @"memory\s*=\s*""(\d+)mb""", RegexOptions.IgnoreCase);

            check(memory.Success && int.Parse(memory.Groups[1].Value) >= 512,
                  "fly.toml asks for at least 512mb");

            check(Regex.IsMatch(toml, @"min_machines_running\s*=\s*1"),
                  "one machine stays warm, so nobody meets a cold .NET start mid-fight");
        }

        /// <summary>TOML with its # comments removed. See DeployConfig.</summary>
        private static string StripToml(string source) =>
            Regex.Replace(source, @"^\s*#.*$", "", RegexOptions.Multiline);

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
