using UnityEditor;
using UnityEngine;

/// <summary>
/// The rebuild this round of content needs, and nothing else.
///
/// ══ WHY NOT "SETUP EVERYTHING" ════════════════════════════════════════════════
///
/// SetupAll also rebuilds the Hollow and the Bootstrap scene, neither of which
/// changed. This touches exactly what did: the monster prefabs (the King had none at
/// all), the two lookup libraries, and the Goblin Camp, whose layout gained four
/// gatherable trees.
///
///   Unity -quit -batchmode -nographics -projectPath . \
///         -executeMethod PlaytestRebuild.RunFromCommandLine
/// </summary>
public static class PlaytestRebuild
{
    [MenuItem("Idle Explorers/Rebuild For Playtest")]
    public static void Run()
    {
        Debug.Log("[PlaytestRebuild] 1/4 Monster prefabs");
        MonsterPrefabSetup.BuildAll(showDialog: false);

        Debug.Log("[PlaytestRebuild] 2/4 Icon library");
        IconLibrarySetup.Rebuild();

        Debug.Log("[PlaytestRebuild] 3/4 VFX library");
        VFXLibrarySetup.Rebuild();

        // Last, and it rebakes its own NavMesh -- the map has to be final first.
        Debug.Log("[PlaytestRebuild] 4/4 Goblin Camp + NavMesh");
        GoblinCampSetup.Execute(showDialog: false);

        Debug.Log("[PlaytestRebuild] 5/5 Equipment art check");
        EquipmentArtValidator.Validate();

        AssetDatabase.SaveAssets();
        Debug.Log("[PlaytestRebuild] Complete.");
    }

    public static void RunFromCommandLine()
    {
        try { Run(); }
        catch (System.Exception e)
        {
            Debug.LogError($"[PlaytestRebuild] Failed: {e}");
            EditorApplication.Exit(1);
            return;
        }

        EditorApplication.Exit(0);
    }
}
