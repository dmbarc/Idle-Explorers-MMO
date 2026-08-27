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
        /// The deploy config points at files that exist, from the right place.
        ///
        /// ══ WHY THIS IS WORTH A CHECK ═════════════════════════════════════════
        ///
        /// The Dockerfile copies Assets/Scripts/Rules and Assets/StreamingAssets, which
        /// live outside server/. So the build context must be the repository ROOT, and
        /// every host takes its context from where the config sits or where you run the
        /// command.
        ///
        /// The previous host's config got this exactly wrong -- it named "Dockerfile"
        /// and "../.dockerignore", which reads as "build from server/", and from there
        /// the context has no Assets/ in it. That fails on deploy day with a COPY error
        /// naming a path that plainly exists, which is a genuinely confusing hour.
        ///
        /// The config has since moved hosts. The trap did not move, so neither did the
        /// check.
        /// </summary>
        internal static void DeployConfig(Action<bool, string> check, string root)
        {
            string path = Path.Combine(root, "railway.json");
            string json = Safely(path);

            check(json.Length > 0, "railway.json is at the repository root, which is the build context");

            if (json.Length == 0) return;

            // ══ THE PATHS ═════════════════════════════════════════════════════

            var dockerfile = Regex.Match(json, @"""dockerfilePath""\s*:\s*""([^""]+)""");

            check(dockerfile.Success, "railway.json names a dockerfilePath");

            if (dockerfile.Success)
            {
                string named = dockerfile.Groups[1].Value;

                check(!named.StartsWith(".."),
                      "the dockerfilePath does not climb out of the repo -- the context IS the root");

                check(File.Exists(Path.Combine(root, named.Replace('/', Path.DirectorySeparatorChar))),
                      $"dockerfilePath '{named}' resolves from the repository root");
            }

            check(File.Exists(Path.Combine(root, ".dockerignore")),
                  "a .dockerignore sits at the root, so Library/ and obj/ stay out of the context");

            // ══ TWO IGNORE FILES, TWO DIFFERENT STEPS ═════════════════════════
            //
            // .dockerignore governs what enters the IMAGE once the build starts.
            // .railwayignore governs what gets UPLOADED to Railway at all. Having only
            // the first means every deploy tars the whole working tree -- here about
            // 1.2 GB of art, a Unity Library/ cache and the whole of .git, none of
            // which the Dockerfile reads.
            //
            // They may legitimately diverge in one direction: the upload can exclude
            // MORE than the build. A rule present only in .dockerignore is a file
            // uploaded for nothing, which is the case worth catching.
            string[] dockerRules  = Rules(Safely(Path.Combine(root, ".dockerignore")));
            string[] railwayRules = Rules(Safely(Path.Combine(root, ".railwayignore")));

            check(railwayRules.Length > 0, "a .railwayignore exists, so deploys do not upload the whole tree");

            var uploaded = new List<string>();

            foreach (string rule in dockerRules)
                if (Array.IndexOf(railwayRules, rule) < 0) uploaded.Add(rule);

            check(uploaded.Count == 0,
                  uploaded.Count == 0
                      ? "everything the build ignores is also left out of the upload"
                      : $"these are excluded from the image but still uploaded: {string.Join(", ", uploaded)}");

            // ══ WHAT IT ASKS THE PLATFORM FOR ═════════════════════════════════

            var replicas = Regex.Match(json, @"""numReplicas""\s*:\s*(\d+)");

            check(replicas.Success && replicas.Groups[1].Value == "1",
                  "exactly one replica -- more would multiply a bill this host was chosen to bound");

            check(Regex.IsMatch(json, @"""healthcheckPath""\s*:\s*""/readyz"""),
                  "the platform polls /readyz, which touches the database, not /healthz which does not");

            // An unbounded restart against a bad connection string is a container that
            // burns billed compute forever while never becoming healthy.
            check(Regex.IsMatch(json, @"""restartPolicyMaxRetries""\s*:\s*\d+"),
                  "restarts are bounded, so a misconfiguration cannot bill indefinitely");

            // ══ WHERE IT RUNS ═════════════════════════════════════════════════
            //
            // The API and its database have to be on the same continent. The service
            // defaulted to EU West with Supabase in Ohio, which is the Atlantic on
            // every query -- roughly 85ms instead of 10-15, several times per request.
            //
            // That presents as slow code rather than as a misplaced container, which
            // is why it is worth a check rather than a memory.
            var region = Regex.Match(json, @"""region""\s*:\s*""([^""]+)""");

            check(region.Success, "railway.json pins a region rather than taking the default");

            check(region.Success && region.Groups[1].Value.StartsWith("us-east"),
                  "railway.json asks for US East, alongside the Supabase project in us-east-2");

            // ══ AND THE FIELD THAT ACTUALLY DECIDES ═══════════════════════════
            //
            // multiRegionConfig OVERRIDES region. The service was created in EU West,
            // which set multiRegionConfig to ams, and every subsequent deploy accepted
            // region: us-east4 into the manifest and ignored it. The container ran in
            // Amsterdam for three deploys with the config insisting it was in Virginia.
            //
            // Checking `region` alone passed the whole time, which is why this second
            // assertion exists: a config that disagrees with itself is how the first
            // one went unnoticed.
            var multi = Regex.Match(json, @"""multiRegionConfig""\s*:\s*\{\s*""([^""]+)""");

            check(multi.Success,
                  "railway.json pins multiRegionConfig, which is what actually places the service");

            check(multi.Success && region.Success && multi.Groups[1].Value == region.Groups[1].Value,
                  "region and multiRegionConfig name the SAME place, so neither can silently win");

            // ══ THE PORT ══════════════════════════════════════════════════════
            //
            // Railway, Render and Cloud Run all assign a port through PORT. ASP.NET
            // reads ASPNETCORE_URLS instead, so an image that hard-codes the URL binds
            // to the wrong port and every health check fails against a server that is
            // running perfectly.

            string dockerfileText = Safely(Path.Combine(root, "server", "Dockerfile"));

            check(!Regex.IsMatch(StripHash(dockerfileText), @"ASPNETCORE_URLS\s*="),
                  "the Dockerfile does not pin ASPNETCORE_URLS, so the platform's PORT wins");

            string program = Safely(Path.Combine(root, "server", "src", "IdleExplorers.Api", "Program.cs"));

            check(program.Contains("UseUrls") && program.Contains("\"PORT\""),
                  "the app binds to PORT when the host assigns one");
        }

        /// <summary>The meaningful lines of an ignore file: no comments, no blanks.</summary>
        private static string[] Rules(string source)
        {
            var rules = new List<string>();

            foreach (string line in source.Split('\n'))
            {
                string trimmed = line.Trim();

                if (trimmed.Length > 0 && !trimmed.StartsWith("#")) rules.Add(trimmed);
            }

            return rules.ToArray();
        }

        /// <summary>Text with its # comments removed, so prose cannot answer for code.</summary>
        private static string StripHash(string source) =>
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
