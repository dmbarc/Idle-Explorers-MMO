using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Rebuilds Resources/ItemDrops/GenericDrop.prefab — the world pickup every dropped
/// item uses.
///
/// The prefab is generated rather than hand-authored because the version it replaces
/// had three faults that are invisible in the Inspector and obvious in motion:
///
///  1. A Billboard on the SAME object as a dynamic Rigidbody. Billboard writes
///     transform.rotation every LateUpdate; the physics engine writes it too. The
///     result is a permanent argument that reads as loot sliding across the ground.
///  2. A non-trigger BoxCollider, so drops shoved each other and the terrain.
///  3. Root scale 0.1 on a 128px icon at 100 pixels-per-unit — a 0.13-unit sprite in
///     a world where the player is roughly 2 units tall, which is why the icons were
///     there but far too small to identify.
///
/// The replacement is kinematic, triggers only, and scales the sprite rather than the
/// root so the pickup radius does not change with stack size.
///
/// Menu: Idle Explorers → Rebuild Item Drop Prefab
/// </summary>
public static class ItemDropSetup
{
    private const string DIRECTORY   = "Assets/Resources/ItemDrops";
    private const string PREFAB_PATH = DIRECTORY + "/GenericDrop.prefab";

    /// <summary>
    /// World radius the player must come within to collect a drop. Constant, because
    /// the trigger lives on the unscaled root.
    /// </summary>
    private const float PickupRadius = 1.2f;

    [MenuItem("Idle Explorers/Rebuild Item Drop Prefab")]
    public static void Rebuild() => Rebuild(showDialog: true);

    public static void Rebuild(bool showDialog)
    {
        if (!Directory.Exists(DIRECTORY)) Directory.CreateDirectory(DIRECTORY);

        var root = new GameObject("GenericDrop");

        // Root stays at scale 1 for the lifetime of the drop.
        var trigger = root.AddComponent<SphereCollider>();
        trigger.isTrigger = true;
        trigger.radius    = PickupRadius;
        trigger.center    = Vector3.zero;

        // OnTriggerEnter needs a Rigidbody on one of the two parties, and the player
        // has a CharacterController and a CapsuleCollider but no Rigidbody — so this
        // one is load-bearing even though it never simulates.
        var body = root.AddComponent<Rigidbody>();
        body.isKinematic = true;
        body.useGravity  = false;

        // Sprite on a child, so the item can be scaled and bobbed without touching
        // the trigger — and so Billboard rotates only the artwork.
        var spriteGo = new GameObject("Sprite");
        spriteGo.transform.SetParent(root.transform, false);

        var renderer = spriteGo.AddComponent<SpriteRenderer>();
        renderer.sprite       = null;      // assigned per item by DropPickup.Setup
        renderer.sortingOrder = 10;        // above terrain decals

        spriteGo.AddComponent<Billboard>();

        var pickup = root.AddComponent<DropPickup>();
        pickup.spriteRenderer = renderer;

        bool existed = File.Exists(PREFAB_PATH);
        PrefabUtility.SaveAsPrefabAsset(root, PREFAB_PATH);
        Object.DestroyImmediate(root);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Debug.Log($"[ItemDrop] {(existed ? "Rebuilt" : "Created")} {PREFAB_PATH} — " +
                  $"kinematic, trigger radius {PickupRadius}, Billboard on the sprite child.");

        if (showDialog)
            EditorUtility.DisplayDialog("Item Drop Prefab Rebuilt",
                $"{PREFAB_PATH}\n\n" +
                "• Kinematic — drops cannot slide, roll or push each other\n" +
                "• Billboard moved onto the sprite child, off the physics body\n" +
                "• Sprite scales with stack size; pickup radius stays constant\n\n" +
                "Existing drops already lying in a running scene keep the old behaviour " +
                "until they are picked up.",
                "OK");
    }
}
