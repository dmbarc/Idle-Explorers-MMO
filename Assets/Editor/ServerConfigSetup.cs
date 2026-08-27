using System.IO;
using IdleExplorers.Backend;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Creates the asset that tells the game where the server is.
///
/// ══ WHY IT IS CREATED RATHER THAN COMMITTED ═══════════════════════════════════
///
/// A ScriptableObject asset stores the GUID of the script it instantiates, and that
/// GUID is minted by Unity when it first imports the script. Hand-writing the .asset
/// file means guessing it, and a wrong guess produces an asset that loads as null --
/// which shows up as "the game is offline" with nothing in the console.
///
/// So the asset is made by the editor, once, and then committed like any other.
///
/// ══ WHY IT IS EMPTY ═══════════════════════════════════════════════════════════
///
/// An empty config plays offline, which is the right default for a checkout: opening
/// the project and pressing play must not require a server. Filling it in is a
/// deliberate act, and the field tooltips say which key is safe to put there.
/// </summary>
public static class ServerConfigSetup
{
    private const string Folder = "Assets/Resources";
    private const string Path   = Folder + "/" + ServerConfig.ResourcePath + ".asset";

    [MenuItem("Idle Explorers/Server Config")]
    public static void OpenOrCreate()
    {
        ServerConfig config = Ensure(announce: true);

        // Selected as well as created, because the whole reason somebody opened this
        // menu is to type into it.
        Selection.activeObject = config;
        EditorGUIUtility.PingObject(config);
    }

    /// <summary>
    /// The asset, created if it is missing. Safe to call from Setup Everything.
    /// </summary>
    public static ServerConfig Ensure(bool announce)
    {
        var existing = AssetDatabase.LoadAssetAtPath<ServerConfig>(Path);

        if (existing != null)
        {
            if (announce) Report(existing);
            return existing;
        }

        if (!Directory.Exists(Folder)) Directory.CreateDirectory(Folder);

        var config = ScriptableObject.CreateInstance<ServerConfig>();

        AssetDatabase.CreateAsset(config, Path);
        AssetDatabase.SaveAssets();

        Debug.Log($"[ServerConfig] Created {Path}. Empty, so the game plays offline. " +
                  "Fill in the API URL and the Supabase project URL + ANON key to connect.");

        return config;
    }

    /// <summary>
    /// Says what the current configuration will actually do.
    ///
    /// Worth printing rather than leaving somebody to infer it from a blank inspector:
    /// "offline" and "connected but nobody can sign in" look identical from the outside
    /// and are very different problems.
    /// </summary>
    private static void Report(ServerConfig config)
    {
        string problem = config.Problem();

        if (!string.IsNullOrEmpty(problem))
        {
            Debug.LogError($"[ServerConfig] {problem}");
            return;
        }

        if (!config.HasServer)
        {
            Debug.Log("[ServerConfig] No API URL — the game plays offline against LocalBackend.");
            return;
        }

        string mode = config.shadowMode
            ? "SHADOW mode: local answers, the server asked in parallel, disagreements logged"
            : "LIVE: the server's answers are used";

        Debug.Log($"[ServerConfig] {config.apiBaseUrl} — {mode}.");
    }
}
