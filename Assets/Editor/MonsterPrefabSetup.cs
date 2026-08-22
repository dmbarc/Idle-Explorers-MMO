using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Builds Assets/Resources/Monsters/goblin.prefab from a SPUM character rig plus the
/// component configuration already proven on _default.prefab.
///
/// goblin_camp's defaultMonsterId has always been "goblin", and MonsterSpawner
/// already resolves Monsters/{id} before falling back to Monsters/_default — so
/// simply ADDING this prefab makes goblins appear with no code change at all. The
/// working _default is left untouched, which also makes this trivially reversible:
/// delete the generated file and the fallback takes over again.
///
/// Menu: Idle Explorers → Build Goblin Prefab
/// </summary>
public static class MonsterPrefabSetup
{
    private const string SOURCE_PREFAB = "Assets/Resources/Monsters/_default.prefab";
    private const string OUTPUT_PREFAB = "Assets/Resources/Monsters/goblin.prefab";

    /// <summary>
    /// SPUM's Devil race reads much closer to a goblin than the Skeleton rig that has
    /// been standing in. Any prefab from that folder works; this is simply the first.
    /// </summary>
    private const string SPUM_RIG =
        "Assets/Imports/SPUM/Resources/Addons/BasicPack/2_Prefab/Devil/SPUM_20240911215637878.prefab";

    [MenuItem("Idle Explorers/Build Goblin Prefab")]
    public static void Build()
    {
        var source = AssetDatabase.LoadAssetAtPath<GameObject>(SOURCE_PREFAB);
        if (source == null)
        {
            EditorUtility.DisplayDialog("Missing source",
                $"Expected {SOURCE_PREFAB}.\n\nIt supplies the component settings to copy.", "OK");
            return;
        }

        var rig = AssetDatabase.LoadAssetAtPath<GameObject>(SPUM_RIG);
        if (rig == null)
        {
            EditorUtility.DisplayDialog("Missing SPUM rig",
                $"Expected {SPUM_RIG}.\n\nPick another prefab from the Devil folder if this one moved.", "OK");
            return;
        }

        var sourceController = source.GetComponent<MonsterController>();
        if (sourceController == null)
        {
            Debug.LogError("[MonsterSetup] _default.prefab has no MonsterController to copy.");
            return;
        }

        // Work on an instance; the asset itself is never modified.
        var instance = (GameObject)PrefabUtility.InstantiatePrefab(rig);
        instance.name = "goblin";

        // Break the SPUM prefab link so the result is a standalone prefab rather than
        // a variant that would inherit future changes to the pack.
        PrefabUtility.UnpackPrefabInstance(instance, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);

        try
        {
            ConfigureNavigation(instance, source);
            ConfigureCollision(instance, source);
            ConfigureController(instance, sourceController);

            Directory.CreateDirectory(Path.GetDirectoryName(OUTPUT_PREFAB));
            PrefabUtility.SaveAsPrefabAsset(instance, OUTPUT_PREFAB, out bool saved);

            if (!saved)
            {
                Debug.LogError($"[MonsterSetup] Failed to save {OUTPUT_PREFAB}.");
                return;
            }

            AssetDatabase.Refresh();
            Debug.Log($"[MonsterSetup] Built {OUTPUT_PREFAB} from {Path.GetFileName(SPUM_RIG)}.");

            EditorUtility.DisplayDialog("Goblin Built",
                $"Saved {OUTPUT_PREFAB}.\n\n" +
                "MonsterSpawner already looks up Monsters/goblin before falling back to " +
                "_default, so goblin_camp will use it on the next Play with no code change.\n\n" +
                "Delete the file to go back to the skeleton stand-in.",
                "OK");
        }
        finally
        {
            Object.DestroyImmediate(instance);
        }
    }

    private static void ConfigureNavigation(GameObject target, GameObject source)
    {
        var sourceAgent = source.GetComponent<NavMeshAgent>();
        if (sourceAgent == null) return;

        var agent = target.GetComponent<NavMeshAgent>() ?? target.AddComponent<NavMeshAgent>();

        agent.speed                 = sourceAgent.speed;
        agent.angularSpeed          = sourceAgent.angularSpeed;
        agent.acceleration          = sourceAgent.acceleration;
        agent.stoppingDistance      = sourceAgent.stoppingDistance;
        agent.radius                = sourceAgent.radius;
        agent.height                = sourceAgent.height;
        agent.baseOffset            = sourceAgent.baseOffset;
        agent.obstacleAvoidanceType = sourceAgent.obstacleAvoidanceType;
        agent.areaMask              = sourceAgent.areaMask;
    }

    /// <summary>
    /// Copies the colliders. Without these the goblin is unclickable and the player's
    /// raycast targeting slides straight through it.
    /// </summary>
    private static void ConfigureCollision(GameObject target, GameObject source)
    {
        foreach (var existing in target.GetComponents<Collider>())
            Object.DestroyImmediate(existing);

        foreach (var sourceCollider in source.GetComponents<Collider>())
        {
            switch (sourceCollider)
            {
                case CapsuleCollider capsule:
                {
                    var copy = target.AddComponent<CapsuleCollider>();
                    copy.center    = capsule.center;
                    copy.radius    = capsule.radius;
                    copy.height    = capsule.height;
                    copy.direction = capsule.direction;
                    copy.isTrigger = capsule.isTrigger;
                    break;
                }
                case BoxCollider box:
                {
                    var copy = target.AddComponent<BoxCollider>();
                    copy.center    = box.center;
                    copy.size      = box.size;
                    copy.isTrigger = box.isTrigger;
                    break;
                }
                case SphereCollider sphere:
                {
                    var copy = target.AddComponent<SphereCollider>();
                    copy.center    = sphere.center;
                    copy.radius    = sphere.radius;
                    copy.isTrigger = sphere.isTrigger;
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Copies MonsterController wholesale, then re-points the references that must
    /// address the new rig rather than the old one.
    /// </summary>
    private static void ConfigureController(GameObject target, MonsterController source)
    {
        UnityEditorInternal.ComponentUtility.CopyComponent(source);

        var controller = target.GetComponent<MonsterController>();
        if (controller != null) UnityEditorInternal.ComponentUtility.PasteComponentValues(controller);
        else
        {
            UnityEditorInternal.ComponentUtility.PasteComponentAsNew(target);
            controller = target.GetComponent<MonsterController>();
        }

        if (controller == null)
        {
            Debug.LogError("[MonsterSetup] Could not attach MonsterController to the goblin.");
            return;
        }

        // These pointed at the skeleton's own components; left alone they would be
        // null on the goblin and it would neither animate nor path.
        controller.agent = target.GetComponent<NavMeshAgent>();
        controller.anim  = target.GetComponentInChildren<Animator>();

        // The health bar and drop prefab are asset/child references that survive the
        // copy, but the health UI belongs to the old rig's hierarchy.
        controller.healthUI     = null;
        controller.healthNumber = null;
        controller.healthSlider = null;

        if (controller.anim == null)
            Debug.LogWarning("[MonsterSetup] No Animator found on the SPUM rig — the goblin will not animate.");
    }
}
