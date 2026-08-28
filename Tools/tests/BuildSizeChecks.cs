using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace IdleExplorersTests
{
    /// <summary>
    /// What a WebGL build will contain whether anybody meant it to or not.
    ///
    /// ══ THE RULE THAT CATCHES PEOPLE ══════════════════════════════════════════════
    ///
    /// Unity ships the contents of EVERY folder named Resources, anywhere in the
    /// project, referenced or not. That is the whole rule, and it is why an imported
    /// asset pack can silently add tens of megabytes to a download: the pack's author
    /// put their files in a folder with that name, and nothing in this project has to
    /// mention them for them to ship.
    ///
    /// It happened here. An orchestral music pack contributed 29 MB to the build --
    /// nothing referenced it, not a script, not a scene, not even the AudioLibrary.
    /// The first attempt to measure "how big is Resources" looked only at
    /// Assets/Resources, found 990 KB, and concluded the bucket was tiny.
    ///
    /// ══ WHY A SIZE BUDGET RATHER THAN A LIST ══════════════════════════════════════
    ///
    /// A list of allowed folders is a list somebody has to update, and the failure of
    /// forgetting is silent. A budget fails the moment an import pushes the total up,
    /// which is exactly when somebody should look.
    /// </summary>
    internal static class BuildSizeChecks
    {
        /// <summary>
        /// What all the Resources folders together may weigh.
        ///
        /// Currently about 24 MB, nearly all of it SPUM's character rigs and
        /// TextMeshPro, both genuinely used. The headroom is deliberately small: this
        /// is a number that only ever grows by accident.
        /// </summary>
        public const long BudgetBytes = 32L * 1024L * 1024L;

        /// <summary>
        /// The per-file ceiling both Cloudflare Pages and Workers Static Assets impose,
        /// uncompressed. Not a build failure, but the thing that decides whether the
        /// result can be hosted at all.
        /// </summary>
        public const long HostFileLimit = 25L * 1024L * 1024L;

        internal static void Run(Action<bool, string> check, string root)
        {
            Console.WriteLine("Build payload");

            string assets = Path.Combine(root, "Assets");

            if (!Directory.Exists(assets)) return;

            var folders = new List<(string Path, long Bytes)>();

            foreach (string folder in Directory.EnumerateDirectories(assets, "Resources", SearchOption.AllDirectories))
            {
                // A folder ending in ~ is invisible to Unity, which is how the music
                // pack was excluded without deleting anything.
                if (folder.EndsWith("~")) continue;

                folders.Add((Relative(root, folder), Weigh(folder)));
            }

            long total = folders.Sum(entry => entry.Bytes);

            foreach (var (path, bytes) in folders.OrderByDescending(entry => entry.Bytes))
                Console.WriteLine($"    {bytes / (1024f * 1024f),7:N1} MB  {path}");

            check(total <= BudgetBytes,
                  $"everything in a Resources folder fits the {BudgetBytes / (1024 * 1024)} MB " +
                  $"budget (currently {total / (1024f * 1024f):N1} MB) -- remember that Unity ships " +
                  "EVERY folder with that name, referenced or not");

            // A single file over the host's cap cannot be uploaded at all, whatever the
            // total. Worth catching in the project rather than at deploy time.
            foreach (var (path, _) in folders)
            {
                string full = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));

                foreach (string file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
                {
                    var info = new FileInfo(file);

                    if (info.Length <= HostFileLimit) continue;

                    check(false, $"{Relative(root, file)} is {info.Length / (1024f * 1024f):N1} MB, " +
                                 "over the 25 MiB per-file limit the host enforces");
                }
            }
        }

        private static long Weigh(string folder)
        {
            long bytes = 0L;

            foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                // .meta files are editor bookkeeping and never ship.
                if (file.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;

                try   { bytes += new FileInfo(file).Length; }
                catch (IOException) { /* vanished mid-scan; not worth failing over */ }
            }

            return bytes;
        }

        private static string Relative(string root, string path) =>
            path.StartsWith(root) ? path.Substring(root.Length).TrimStart('\\', '/').Replace('\\', '/')
                                  : path;
    }
}
