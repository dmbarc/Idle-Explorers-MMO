using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Builds one Resources/Monsters/{id}.prefab per entry in the table below, from a
/// SPUM character rig plus the component configuration already proven on
/// _default.prefab.
///
/// MonsterSpawner resolves Monsters/{id} before falling back to Monsters/_default,
/// so a monster appears purely by being built here. The working _default is left
/// untouched, which makes this reversible: delete a generated file and the fallback
/// takes over again.
///
/// ══ THIS WAS A MENU ITEM NOBODY RAN ═══════════════════════════════════════════
///
/// It built exactly one prefab and was not part of Setup Everything, so
/// Resources/Monsters held only _default and skeleton — meaning every goblin in the
/// game since the day this file was written has actually been the fallback stand-in.
/// A table plus a place in the setup sequence is what stops that recurring for the
/// next monster.
///
/// The project's design rule is ONE MONSTER PER AREA: a map names a single
/// defaultMonsterId, and a new map means authoring a new monster rather than reusing
/// one. Every id below is the sole inhabitant of somewhere.
///
/// Menu: Idle Explorers → Build Monster Prefabs
/// </summary>
public static class MonsterPrefabSetup
{
    private const string SOURCE_PREFAB = "Assets/Resources/Monsters/_default.prefab";
    private const string OUTPUT_DIR    = "Assets/Resources/Monsters";

    private const string SpumRoot = "Assets/Imports/SPUM/Resources/Addons/BasicPack/2_Prefab/";

    /// <summary>
    /// One entry per monster: which rig it wears and, optionally, what colour its skin
    /// is. The tint is applied to the body layers only — tinting the whole rig would
    /// take the eyes and the weapon with it and read as a lighting fault.
    /// </summary>
    private readonly struct Recipe
    {
        public readonly string MonsterId;
        public readonly string RigPath;
        public readonly string SkinTint;
        public readonly string Note;

        /// <summary>
        /// How tall this monster stands, as a fraction of a person.
        ///
        /// The absolute number lives in SpumRig.CharacterHeight, so a monster is
        /// described the way anybody would describe one — "a head shorter than you" —
        /// rather than in world units nobody can picture.
        /// </summary>
        public readonly float HeightVsPlayer;

        public Recipe(string monsterId, string rigPath, string skinTint, float heightVsPlayer,
                      string note)
        {
            MonsterId      = monsterId;
            RigPath        = rigPath;
            SkinTint       = skinTint;
            HeightVsPlayer = heightVsPlayer;
            Note           = note;
        }
    }

    private static readonly Recipe[] Recipes =
    {
        new Recipe("goblin", SpumRoot + "Devil/SPUM_20240911215637878.prefab", null, 0.82f,
                   "Goblin Camp. The Devil race reads much closer to a goblin than the " +
                   "Skeleton rig that stood in for it, and a goblin should be shorter " +
                   "than the person fighting it."),

        new Recipe("bramblekin", SpumRoot + "Elf/SPUM_20240911215638048.prefab", "#4E7A38", 0.95f,
                   "Hollow of the Fading Light. An elf silhouette under a mossy green " +
                   "reads as something that grew rather than something that arrived."),
    };

    [MenuItem("Idle Explorers/Build Monster Prefabs")]
    public static void BuildMenu() => BuildAll(showDialog: true);

    public static int BuildAll(bool showDialog)
    {
        var source = AssetDatabase.LoadAssetAtPath<GameObject>(SOURCE_PREFAB);
        if (source == null)
        {
            Debug.LogError($"[MonsterSetup] Missing {SOURCE_PREFAB} — it supplies the component " +
                           "settings every monster copies.");
            if (showDialog)
                EditorUtility.DisplayDialog("Missing source",
                    $"Expected {SOURCE_PREFAB}.\n\nIt supplies the component settings to copy.", "OK");
            return 0;
        }

        var sourceController = source.GetComponent<MonsterController>();
        if (sourceController == null)
        {
            Debug.LogError("[MonsterSetup] _default.prefab has no MonsterController to copy.");
            return 0;
        }

        int built = 0;
        foreach (var recipe in Recipes)
            if (Build(recipe, source, sourceController)) built++;

        Debug.Log($"[MonsterSetup] Built {built} of {Recipes.Length} monster prefab(s) in {OUTPUT_DIR}.");

        if (showDialog)
            EditorUtility.DisplayDialog("Monster Prefabs Built",
                $"{built} of {Recipes.Length} built in {OUTPUT_DIR}.\n\n" +
                "MonsterSpawner resolves Monsters/{id} before falling back to _default, so each " +
                "map picks its own up on the next Play with no code change.",
                "OK");

        return built;
    }

    private static bool Build(Recipe recipe, GameObject source, MonsterController sourceController)
    {
        var rig = AssetDatabase.LoadAssetAtPath<GameObject>(recipe.RigPath);
        if (rig == null)
        {
            Debug.LogError($"[MonsterSetup] '{recipe.MonsterId}': rig missing at {recipe.RigPath}.");
            return false;
        }

        string outputPath = $"{OUTPUT_DIR}/{recipe.MonsterId}.prefab";

        // Work on an instance; the asset itself is never modified.
        var instance = (GameObject)PrefabUtility.InstantiatePrefab(rig);
        instance.name = recipe.MonsterId;

        // Break the SPUM prefab link so the result is a standalone prefab rather than
        // a variant that would inherit future changes to the pack.
        PrefabUtility.UnpackPrefabInstance(instance, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);

        try
        {
            ConfigureNavigation(instance, source);
            ConfigureCollision(instance, source);
            ConfigureController(instance, sourceController);

            // Monsters are the same flat artwork the player is, and turned edge-on for
            // the same reason: the agent rotates them to face where they are walking.
            Billboard.CoverArt(instance);

            // Same reason the player is scaled: the sprite is authored at 32 pixels to
            // the unit, and the world was built for a two-unit person.
            SpumRig.NormaliseHeight(instance.transform,
                                    SpumRig.CharacterHeight * recipe.HeightVsPlayer);

            int unbillboarded = Billboard.CountUnbillboardedSprites(instance);
            if (unbillboarded > 0)
                Debug.LogWarning("[MonsterSetup] " + recipe.MonsterId + ": " + unbillboarded +
                                 " sprite(s) no Billboard turns — they will go edge-on as it walks.");

            if (!string.IsNullOrEmpty(recipe.SkinTint))
            {
                int tinted = SpumAppearance.TintBody(instance.transform, recipe.SkinTint);
                if (tinted == 0)
                    Debug.LogWarning($"[MonsterSetup] '{recipe.MonsterId}': tint {recipe.SkinTint} " +
                                     "matched no body layer — the rig may not be a SPUM unit.");
            }

            Directory.CreateDirectory(OUTPUT_DIR);
            PrefabUtility.SaveAsPrefabAsset(instance, outputPath, out bool saved);

            if (!saved)
            {
                Debug.LogError($"[MonsterSetup] Failed to save {outputPath}.");
                return false;
            }

            Debug.Log($"[MonsterSetup] {recipe.MonsterId} → {outputPath} " +
                      $"({Path.GetFileNameWithoutExtension(recipe.RigPath)}). {recipe.Note}");
            return true;
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

        // Not `GetComponent() ?? AddComponent()`. `??` compares by reference and cannot
        // see Unity's overloaded ==, so the live-wrapper-around-nothing that
        // GetComponent returns for an absent component reads as "not null", the
        // AddComponent never runs, and the next line throws MissingComponentException
        // against the object instead of against the operator that skipped it.
        var agent = target.GetComponent<NavMeshAgent>();
        if (agent == null) agent = target.AddComponent<NavMeshAgent>();
        if (agent == null)
        {
            Debug.LogError($"[MonsterSetup] Could not add a NavMeshAgent to '{target.name}'.");
            return;
        }

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
            Debug.LogError($"[MonsterSetup] Could not attach MonsterController to '{target.name}'.");
            return;
        }

        // These pointed at the source prefab's own components; left alone they would
        // be null on the new rig and it would neither animate nor path.
        controller.agent = target.GetComponent<NavMeshAgent>();
        controller.anim  = target.GetComponentInChildren<Animator>();

        // The health bar and drop prefab are asset/child references that survive the
        // copy, but the health UI belongs to the old rig's hierarchy.
        controller.healthUI     = null;
        controller.healthNumber = null;
        controller.healthSlider = null;

        if (controller.anim == null)
            Debug.LogWarning($"[MonsterSetup] No Animator on '{target.name}' — it will not animate.");
    }
}
