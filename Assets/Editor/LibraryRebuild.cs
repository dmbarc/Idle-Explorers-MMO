using UnityEditor;
using UnityEngine;

/// <summary>
/// Rebuilds the two generated lookup assets, and nothing else.
///
/// ══ WHY THIS IS NOT JUST "SETUP EVERYTHING" ═══════════════════════════════════
///
/// SetupAll rebuilds both map scenes and rebakes their NavMeshes, which is minutes
/// of work and, more to the point, rewrites two scene files. When the change in hand
/// is four rows in an icon table and four in a VFX table, regenerating the world to
/// pick them up means every such change arrives as an unreviewable scene diff.
///
/// So this is the narrow door: the icon library, the VFX library, and the art
/// validator that reports what would still draw nothing.
///
///   Unity -quit -batchmode -nographics -projectPath . \
///         -executeMethod LibraryRebuild.RunFromCommandLine
///
/// ══ WHY THE VALIDATOR RUNS TOO ════════════════════════════════════════════════
///
/// Because it is the only thing in the project that can answer "does this address
/// actually resolve" with Unity's own loader rather than a guess about where files
/// live. A rebuild that reported success while every new address resolved to nothing
/// is precisely the outcome this is meant to rule out.
/// </summary>
public static class LibraryRebuild
{
    [MenuItem("Idle Explorers/Rebuild Icon + VFX Libraries")]
    public static void Run()
    {
        Debug.Log("[LibraryRebuild] 1/3 Icon library");
        IconLibrarySetup.Rebuild();

        Debug.Log("[LibraryRebuild] 2/3 VFX library");
        VFXLibrarySetup.Rebuild();

        Debug.Log("[LibraryRebuild] 3/3 Equipment art check");
        EquipmentArtValidator.Validate();

        AssetDatabase.SaveAssets();
        Debug.Log("[LibraryRebuild] Complete.");
    }

    /// <summary>
    /// The headless entry point.
    ///
    /// Separate from Run so a batch invocation can fail the process rather than log
    /// a warning nobody reads — a build script that always exits zero is the same
    /// mistake as a test that always passes.
    /// </summary>
    public static void RunFromCommandLine()
    {
        try
        {
            Run();
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[LibraryRebuild] Failed: {e}");
            EditorApplication.Exit(1);
            return;
        }

        EditorApplication.Exit(0);
    }
}
