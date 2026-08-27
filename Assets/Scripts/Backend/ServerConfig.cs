using UnityEngine;

namespace IdleExplorers.Backend
{
    /// <summary>
    /// Where the server is, and what may safely be shipped to reach it.
    ///
    /// ══ WHY THIS IS AN ASSET AND NOT A CONSTANT ═══════════════════════════════════
    ///
    /// Because the three values differ per environment -- local stack, staging, live --
    /// and the alternative is a #if ladder that somebody eventually ships pointed at
    /// the wrong one. An asset can also be swapped in a build machine without a code
    /// change.
    ///
    /// ══ WHAT IS SAFE TO PUT HERE, AND WHAT IS NOT ═════════════════════════════════
    ///
    /// EVERYTHING IN A WEBGL BUILD IS PUBLIC. Not "hard to read" -- public. The whole
    /// build is downloaded by the browser and can be read by anybody who cares. So:
    ///
    ///   SAFE      the API base URL, the Supabase project URL, the Supabase ANON key
    ///   NEVER     the service-role key, the database connection string, any secret
    ///
    /// The anon key is designed to be public -- it is in the page source of every
    /// Supabase web app in existence, and it grants nothing on its own because row
    /// level security is enabled and FORCED on every table with no policies. It is an
    /// identifier for the project, not a credential for the data.
    ///
    /// The service-role key is the opposite of that, and it lives only in the game
    /// server's own configuration. If one ever appears in this file, the fix is not to
    /// obfuscate it -- it is to rotate it, because it has already leaked.
    ///
    /// ══ WHY EMPTY MEANS LOCAL ═════════════════════════════════════════════════════
    ///
    /// A checkout with no asset, or an asset nobody has filled in, plays offline
    /// against LocalBackend. Opening the project and pressing play must never require
    /// a container, a network or an account, or the iteration loop dies and people
    /// stop testing.
    /// </summary>
    [CreateAssetMenu(fileName = "ServerConfig", menuName = "Idle Explorers/Server Config")]
    public class ServerConfig : ScriptableObject
    {
        /// <summary>Where the asset lives, so a build can find it with no scene reference.</summary>
        public const string ResourcePath = "ServerConfig";

        [Header("The game server")]
        [Tooltip("Origin of the ASP.NET API, no trailing slash. Empty plays offline.")]
        public string apiBaseUrl = "";

        [Header("Supabase — identity only")]
        [Tooltip("https://<project-ref>.supabase.co — the project URL, not the database host.")]
        public string supabaseUrl = "";

        [Tooltip("The ANON / publishable key. Public by design. NEVER the service-role key.")]
        [TextArea(2, 4)]
        public string supabaseAnonKey = "";

        [Header("Migration")]
        [Tooltip("Answer locally, ask the server in parallel, log every disagreement. " +
                 "The safe way to point at a server for the first time.")]
        public bool shadowMode = true;

        /// <summary>True when there is a server to talk to at all.</summary>
        public bool HasServer => !string.IsNullOrWhiteSpace(apiBaseUrl);

        /// <summary>True when players can actually sign in.</summary>
        public bool HasAuth =>
            !string.IsNullOrWhiteSpace(supabaseUrl) && !string.IsNullOrWhiteSpace(supabaseAnonKey);

        /// <summary>
        /// The asset, or null.
        ///
        /// Loaded from Resources rather than wired into a scene, because the very first
        /// thing that needs it runs before any scene the designer edits -- and a
        /// missing scene reference would be a null in the login path, which is the
        /// worst place to discover one.
        /// </summary>
        public static ServerConfig Load() => Resources.Load<ServerConfig>(ResourcePath);

        /// <summary>
        /// What is wrong with this configuration, or empty.
        ///
        /// Checked at startup and printed once. The two mistakes worth catching are a
        /// trailing slash -- which turns every path into a double slash and a 404 -- and
        /// somebody pasting a service-role key, which is a leak rather than a typo.
        /// </summary>
        public string Problem()
        {
            if (apiBaseUrl.EndsWith("/"))
                return "apiBaseUrl has a trailing slash; every request would carry a double slash.";

            if (HasServer && !apiBaseUrl.StartsWith("http"))
                return "apiBaseUrl needs a scheme, e.g. https://api.example.com";

            if (!string.IsNullOrWhiteSpace(supabaseUrl) && supabaseUrl.Contains("db."))
            {
                return "supabaseUrl looks like the DATABASE host. It should be the project " +
                       "URL: https://<project-ref>.supabase.co";
            }

            // A service-role JWT carries this claim in its payload, and the payload is
            // base64 -- so the substring survives into the encoded token.
            if (LooksLikeServiceRole(supabaseAnonKey))
            {
                return "supabaseAnonKey looks like a SERVICE-ROLE key. Do not ship it. " +
                       "Rotate it in the Supabase dashboard and use the anon key instead.";
            }

            if (HasServer && !HasAuth)
                return "An API is configured but Supabase is not, so nobody can sign in.";

            return "";
        }

        /// <summary>
        /// Whether a key is the one that must never leave the server.
        ///
        /// A Supabase JWT is three base64url segments; the middle one holds
        /// {"role":"service_role",...}. Rather than decoding, this checks for the
        /// encoding of that fragment, which is stable because base64 is
        /// position-dependent only on three-byte boundaries -- so all three alignments
        /// are checked.
        /// </summary>
        public static bool LooksLikeServiceRole(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return false;

            if (key.Contains("service_role")) return true;

            // "service_role" encoded at each of the three byte alignments.
            foreach (string marker in new[] { "c2VydmljZV9yb2xl", "NlcnZpY2Vfcm9sZ", "zZXJ2aWNlX3JvbG" })
                if (key.Contains(marker)) return true;

            return false;
        }
    }
}
