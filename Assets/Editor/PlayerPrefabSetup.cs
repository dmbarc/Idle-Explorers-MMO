using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Builds Assets/Prefabs/PlayerCharacter.prefab — the player, with its gameplay
/// components on a root that behaves like a transform.
///
/// ══ WHY THIS EXISTS ═══════════════════════════════════════════════════════════
///
/// The Hollow of the Fading Light shipped as a blank screen you could not move in,
/// and the cause was one line that looked correct:
///
///     player.transform.position = spawnPoint + Vector3.up * 0.5f;
///
/// "PlayerCharacter" was not a plain transform. It was the SPUM prefab's ROOT, and
/// that root carries a **RectTransform** on layer 5 (UI) — SPUM authors its rigs
/// inside a UI hierarchy. A RectTransform re-derives its local x and y from
/// anchoredPosition every time its rect is rebuilt, so of the three axes that
/// assignment wrote, only **z** survived. The scene still held
/// anchoredPosition.x = 337.5 from SampleScene, so the player loaded 337 units east
/// of a map that is 128 units wide, hovering over nothing: no ground in frame, no
/// NavMesh underfoot to walk on, and nowhere within the spawner's radius for a
/// monster to appear. Goblin Camp survived the identical bug only because its stored
/// position happens to land on a 2000-unit terrain.
///
/// No error, no warning, and the assignment reads as if it worked.
///
/// The fix is structural rather than another careful call site: gameplay components
/// move onto a plain GameObject that OWNS the SPUM rig, so nothing between a spawn
/// point and a world position can be a RectTransform again. The rig itself is left
/// exactly as SPUM authored it — its own root keeps its RectTransform and its
/// SPUM_Prefabs component, both of which the idle animation and the preview system
/// depend on — but it is now a child, and its anchored offset is neutralised once,
/// here, instead of leaking into every scene that instantiates it.
///
/// Generated rather than hand-authored for the same reason as MonsterPrefabSetup:
/// it is reproducible, reviewable as code, and reversible by deleting the file.
///
/// Menu: Idle Explorers → Build Player Prefab
/// </summary>
public static class PlayerPrefabSetup
{
    /// <summary>
    /// Deliberately NOT under Resources. Map scenes reference this prefab directly,
    /// so putting it in Resources would only force it into every build whether a
    /// scene wanted it or not.
    /// </summary>
    internal const string PREFAB_PATH = "Assets/Prefabs/PlayerCharacter.prefab";

    private const string MARKER_PREFAB = "Assets/Prefabs/MoveMarker.prefab";

    /// <summary>
    /// The art. The same Devil rig CharacterPreview instantiates, so the character in
    /// the world and the character on a class card cannot drift apart.
    /// </summary>
    private const string SPUM_RIG =
        "Assets/Imports/SPUM/Resources/Addons/BasicPack/2_Prefab/Devil/SPUM_20240911215637772.prefab";

    // Component values copied from the instance SampleScene has always carried, so
    // this changes where the components live and nothing about how they behave.
    private const float AgentRadius  = 0.2f;
    private const float AgentHeight  = 2f;
    private const float AgentSpeed   = 3.5f;
    private const float AgentAccel   = 8f;
    private const float AgentAngular = 120f;

    private static readonly Vector3 ColliderCentre = new Vector3(0f, 0.3058f, 0f);
    private const float ColliderRadius = 0.2416f;
    private const float ColliderHeight = 1.0187f;

    [MenuItem("Idle Explorers/Build Player Prefab")]
    public static void BuildMenu() => Build(showDialog: true);

    public static bool Build(bool showDialog)
    {
        var rig = AssetDatabase.LoadAssetAtPath<GameObject>(SPUM_RIG);
        if (rig == null)
        {
            Debug.LogError($"[PlayerPrefab] SPUM rig missing: {SPUM_RIG}");
            if (showDialog)
                EditorUtility.DisplayDialog("Missing rig",
                    $"Expected {SPUM_RIG}.\n\nPick another prefab from the Devil folder if it moved.", "OK");
            return false;
        }

        // The root is a bare GameObject — this is the entire point of the exercise.
        var root = new GameObject("PlayerCharacter");
        root.layer = 0;                       // Default. The rig's art keeps SPUM's own layer.

        try
        {
            root.tag = "Player";              // DropPickup's trigger and the camera both find it by tag
        }
        catch (UnityException)
        {
            Debug.LogWarning("[PlayerPrefab] No 'Player' tag in this project — loot pickup will not work.");
        }

        try
        {
            AttachRig(root, rig);
            var animator = root.GetComponentInChildren<Animator>(includeInactive: true);
            ConfigureNavigation(root);
            ConfigureCollision(root);
            ConfigureController(root, animator);

            Directory.CreateDirectory(Path.GetDirectoryName(PREFAB_PATH));
            PrefabUtility.SaveAsPrefabAsset(root, PREFAB_PATH, out bool saved);

            if (!saved)
            {
                Debug.LogError($"[PlayerPrefab] Failed to save {PREFAB_PATH}.");
                return false;
            }

            AssetDatabase.Refresh();
            Debug.Log($"[PlayerPrefab] Built {PREFAB_PATH} — gameplay components on a plain " +
                      $"Transform root, rig nested underneath" +
                      (animator == null ? " (NO ANIMATOR FOUND)" : "") + ".");

            if (showDialog)
                EditorUtility.DisplayDialog("Player Prefab Built",
                    $"Saved {PREFAB_PATH}.\n\n" +
                    "Rebuild the maps (Prepare Map Scene / Build Hollow Map, or Setup Everything) " +
                    "so they instantiate it.",
                    "OK");

            return true;
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    /// <summary>
    /// Nests the SPUM rig under the new root and neutralises its RectTransform.
    ///
    /// The neutralising is the load-bearing half. SPUM ships the rig root anchored at
    /// (0, -170) — harmless inside a Canvas, and 170 units of vertical offset in a
    /// world-space hierarchy. Zeroing it here means every scene that instantiates this
    /// prefab starts from a clean transform instead of inheriting whatever anchored
    /// offset the last scene happened to save.
    /// </summary>
    private static void AttachRig(GameObject root, GameObject rigPrefab)
    {
        var art = (GameObject)PrefabUtility.InstantiatePrefab(rigPrefab);

        // Unpacked so this is a standalone prefab rather than a nested variant that
        // would inherit future changes to the SPUM package.
        PrefabUtility.UnpackPrefabInstance(art, PrefabUnpackMode.Completely,
                                            InteractionMode.AutomatedAction);

        art.name = "Rig";
        art.transform.SetParent(root.transform, worldPositionStays: false);

        if (art.transform is RectTransform rect)
        {
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta         = Vector2.zero;
            rect.anchoredPosition3D = Vector3.zero;
        }

        art.transform.localPosition = Vector3.zero;
        art.transform.localRotation = Quaternion.identity;
        art.transform.localScale    = Vector3.one;
    }

    private static void ConfigureNavigation(GameObject root)
    {
        var agent = root.GetComponent<NavMeshAgent>() ?? root.AddComponent<NavMeshAgent>();

        agent.radius       = AgentRadius;
        agent.height       = AgentHeight;
        agent.speed        = AgentSpeed;
        agent.acceleration = AgentAccel;
        agent.angularSpeed = AgentAngular;

        // PlayerController drives stopping distance per target — a chase and a walk to
        // a dropped item want different values.
        agent.stoppingDistance = 0f;
        agent.autoBraking      = true;
    }

    /// <summary>
    /// The collider a dropped item's trigger has to touch.
    ///
    /// Both a CapsuleCollider and a CharacterController, matching what the scene has
    /// always carried. DropPickup's Rigidbody is what actually raises the trigger
    /// callback — the player has none — so this only needs to be a collider that is
    /// tagged Player, which the root now is.
    /// </summary>
    private static void ConfigureCollision(GameObject root)
    {
        var capsule = root.GetComponent<CapsuleCollider>() ?? root.AddComponent<CapsuleCollider>();
        capsule.radius    = ColliderRadius;
        capsule.height    = ColliderHeight;
        capsule.direction = 1;                // Y
        capsule.center    = ColliderCentre;
        capsule.isTrigger = false;

        var controller = root.GetComponent<CharacterController>() ?? root.AddComponent<CharacterController>();
        controller.height = AgentHeight;
        controller.radius = AgentRadius;
        controller.center = Vector3.zero;
    }

    private static void ConfigureController(GameObject root, Animator animator)
    {
        var player = root.GetComponent<PlayerController>() ?? root.AddComponent<PlayerController>();

        // The animator lives on the rig's UnitRoot, one level down. Wiring it here is
        // what keeps PlayerController free of a rig lookup of its own.
        player.anim = animator;

        var marker = AssetDatabase.LoadAssetAtPath<GameObject>(MARKER_PREFAB);
        if (marker == null)
            Debug.LogWarning($"[PlayerPrefab] No move marker at {MARKER_PREFAB} — " +
                             "click-to-move will work but show nothing where you clicked.");
        player.destinationMarker = marker;

        // Inspector fallbacks only. StatsManager overwrites all of these in Start via
        // RefreshFromStats; they matter solely when a map scene is played directly
        // with no GameManager, which happens while iterating on a map.
        player.maxHealthPoints   = 100;
        player.healthRegenAmount = 1;
        player.healthRegenSpeed  = 1f;
        player.attackDistance    = 2f;
        player.attackSpeed       = 0.5f;
        player.autoAttack        = false;
        player.dropMultiplier    = 1;
        player.movementSpeed     = 5f;
    }
}
