using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>
/// The web build, with the settings decided rather than inherited.
///
/// ══ WHY A SCRIPT AND NOT THE BUILD DIALOG ═════════════════════════════════════
///
/// Because half a dozen player settings decide whether the result loads quickly, loads
/// slowly, or does not load at all behind a CDN -- and none of them is obvious from the
/// dialog. Left to whatever the project happened to have, a build works on a local file
/// server and fails on Cloudflare, which is the worst possible place to discover it.
///
/// Written down, they are reviewable, and CI can run the same build.
///
/// ══ COMPRESSION IS THE ONE THAT MATTERS ═══════════════════════════════════════
///
/// Unity defaults to Brotli, which means shipping .br files and telling the CDN to
/// serve them with Content-Encoding: br. Whether Cloudflare preserves that header for
/// pre-compressed static assets is genuinely unresolved -- their docs do not say and
/// community reports contradict each other across several years.
///
/// So compression is DISABLED here and the edge is left to compress on the fly.
/// application/wasm is on Cloudflare's default compressible list, streaming compilation
/// survives, and there is no header to argue about. The cost is that the 25 MiB
/// per-file limit applies to the UNCOMPRESSED size, which is why the build reports its
/// largest files at the end.
///
/// If the probe later shows Brotli works, this is one line.
/// </summary>
public static class WebGLBuild
{
    /// <summary>Where the build lands. Gitignored -- it is an artefact, not source.</summary>
    public const string OutputDirectory = "Build/WebGL";

    /// <summary>
    /// Cloudflare's per-file ceiling, uncompressed.
    ///
    /// Both Pages and Workers Static Assets cap a single file at this. A build that
    /// exceeds it does not fail here -- it fails at upload, later, with a message about
    /// a file rather than about a build -- so the size is reported at the end where
    /// somebody will read it.
    /// </summary>
    public const long CloudflareFileLimit = 25L * 1024L * 1024L;

    [MenuItem("Idle Explorers/Build WebGL")]
    public static void BuildFromMenu() => Run(exitOnFinish: false);

    /// <summary>Entry point for -batchmode -executeMethod.</summary>
    public static void BuildFromCommandLine() => Run(exitOnFinish: true);

    private static void Run(bool exitOnFinish)
    {
        string[] scenes = EditorBuildSettings.scenes
            .Where(scene => scene.enabled)
            .Select(scene => scene.path)
            .ToArray();

        if (scenes.Length == 0)
        {
            Fail("No scenes are enabled in Build Settings.", exitOnFinish);
            return;
        }

        // Bootstrap owns the Managers object and every other scene loads additively on
        // top of it. Anywhere but first and the game starts with no managers at all.
        if (!scenes[0].EndsWith("Bootstrap.unity"))
        {
            Fail($"Bootstrap must be the first scene, not '{scenes[0]}'.", exitOnFinish);
            return;
        }

        Configure();

        Debug.Log($"[WebGL] Building {scenes.Length} scene(s) to {OutputDirectory}");

        BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes           = scenes,
            locationPathName = OutputDirectory,
            target           = BuildTarget.WebGL,
            targetGroup      = BuildTargetGroup.WebGL,
            options          = BuildOptions.None,
        });

        BuildSummary summary = report.summary;

        if (summary.result != BuildResult.Succeeded)
        {
            Fail($"Build {summary.result} with {summary.totalErrors} error(s).", exitOnFinish);
            return;
        }

        Debug.Log($"[WebGL] Succeeded in {summary.totalTime.TotalMinutes:N1} min, " +
                  $"{summary.totalSize / (1024f * 1024f):N1} MB total.");

        ReportLargestFiles();

        if (exitOnFinish) EditorApplication.Exit(0);
    }

    /// <summary>
    /// Every setting that decides whether this loads behind a CDN.
    /// </summary>
    private static void Configure()
    {
        // ══ THE WEBGL CONTENT-PATH BUG ════════════════════════════════════════
        //
        // Not a setting -- a note. ContentManager special-cases WebGL because
        // Application.streamingAssetsPath is ALREADY an absolute URL there, and the
        // desktop path prefixes file:/// to it. Without that branch every one of the
        // thirteen content files fails and the game boots to an empty catalogue.
        //
        // Fixed in ContentManager; recorded here because this is where somebody looks
        // when a web build has no items.

        PlayerSettings.WebGL.linkerTarget = WebGLLinkerTarget.Wasm;

        // Disabled, so Cloudflare compresses at the edge. See the class comment.
        PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Disabled;

        // Only meaningful WITH compression, and actively harmful without it: it ships
        // a JavaScript decompressor that costs streaming compilation for nothing.
        PlayerSettings.WebGL.decompressionFallback = false;

        // The build is cached in the browser's IndexedDB, so a returning player does
        // not re-download tens of megabytes. Free, and the single biggest improvement
        // to a second visit.
        PlayerSettings.WebGL.dataCaching = true;

        // Explicitly-thrown only. Full exception support inserts checks around every
        // array access and costs real frame time; this keeps the exceptions the game
        // actually throws while dropping the ones the runtime would synthesise.
        PlayerSettings.WebGL.exceptionSupport = WebGLExceptionSupport.ExplicitlyThrownExceptionsOnly;

        // Size over speed. This is a download before it is a program, and an idle game
        // is not CPU-bound -- settlement happens on the server.
        PlayerSettings.SetIl2CppCompilerConfiguration(
            NamedBuildTarget.WebGL, Il2CppCompilerConfiguration.Master);

        PlayerSettings.SetManagedStrippingLevel(NamedBuildTarget.WebGL, ManagedStrippingLevel.Low);

        // ══ WHY STRIPPING IS LOW AND NOT HIGH ═════════════════════════════════
        //
        // JsonUtility is reflection, and the stripper cannot see it. A stripped field
        // is not an error -- JsonUtility leaves it at its default -- so an
        // over-stripped build deserialises every wire type as an object full of zeros
        // and tells the player they earned nothing.
        //
        // Assets/link.xml preserves the game assembly for exactly this reason. Low
        // stripping is belt and braces on a build whose size is dominated by engine
        // and art rather than by our IL.
    }

    /// <summary>
    /// The biggest files, and whether any of them will be refused by the host.
    ///
    /// Reported because the limit is enforced at UPLOAD, long after the build, with a
    /// message about a file rather than about a setting -- and by then whoever ran the
    /// build has moved on.
    /// </summary>
    private static void ReportLargestFiles()
    {
        if (!Directory.Exists(OutputDirectory)) return;

        var files = new DirectoryInfo(OutputDirectory)
            .GetFiles("*", SearchOption.AllDirectories)
            .OrderByDescending(file => file.Length)
            .Take(6)
            .ToArray();

        Debug.Log("[WebGL] Largest files:");

        bool oversized = false;

        foreach (FileInfo file in files)
        {
            bool tooBig = file.Length > CloudflareFileLimit;
            oversized |= tooBig;

            Debug.Log($"[WebGL]   {file.Length / (1024f * 1024f),8:N1} MB  {file.Name}" +
                      (tooBig ? "   ← OVER the 25 MiB per-file limit" : ""));
        }

        if (oversized)
        {
            Debug.LogWarning(
                "[WebGL] At least one file exceeds Cloudflare's 25 MiB per-file limit. " +
                "Serve the large blobs from R2 behind a Worker, which is the one place " +
                "header control is unambiguous, or turn Brotli back on if the encoding " +
                "probe shows it survives.");
        }
    }

    private static void Fail(string message, bool exitOnFinish)
    {
        Debug.LogError($"[WebGL] {message}");

        // A non-zero exit, so a build that failed cannot look like one that worked.
        // Batch builds are read by scripts far more often than by people.
        if (exitOnFinish) EditorApplication.Exit(1);
    }
}
