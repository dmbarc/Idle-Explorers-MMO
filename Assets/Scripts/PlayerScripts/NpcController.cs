using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Somebody standing in a map who is not trying to kill you.
///
/// ══ WHY THEY ARE SPAWNED FROM DATA RATHER THAN PLACED IN THE SCENE ════════════
///
/// Both map scenes are GENERATED from a recipe, and regenerating one deletes anything
/// hand-placed in it. An NPC in the scene file survives exactly until the next time
/// somebody edits the layout string — which happens often, because the layout is how
/// the map is authored.
///
/// So the shopkeeper is four numbers in zone_data.json, and this builds him on
/// arrival. Same reasoning as ExitPortal, NamePlate and DamageNumber.
///
/// ══ WHY THE RIG IS STRIPPED ═══════════════════════════════════════════════════
///
/// A SPUM prefab from the Resources pack ships whatever components its author put on
/// it, and this one has to be furniture: no agent steering it, no audio listener
/// competing with the player's, no camera. RemotePlayerView learned the same lesson
/// with the same list, and the two are kept deliberately similar so a component that
/// has to be removed is removed in both.
/// </summary>
public class NpcController : MonoBehaviour
{
    /// <summary>What this person is for. Matches MapNpc.kind.</summary>
    public string kind = "shop";

    /// <summary>Their name, shown over their head and on the panel they open.</summary>
    public string displayName = "";

    /// <summary>How close the player must be to be served.</summary>
    public float interactionRange = 4f;

    /// <summary>
    /// Builds one from its content row, or returns null when the art is missing.
    ///
    /// Null rather than a placeholder capsule: an NPC that failed to load should be
    /// absent and complained about, not a grey pill standing in a camp looking like a
    /// deliberate choice.
    /// </summary>
    public static NpcController Spawn(MapNpc row, Transform parent)
    {
        if (row == null || string.IsNullOrEmpty(row.prefabAddress)) return null;

        var prefab = Resources.Load<GameObject>(row.prefabAddress);

        if (prefab == null)
        {
            Debug.LogWarning($"[Npc] '{row.id}' has no prefab at '{row.prefabAddress}'. " +
                             "The path is relative to a Resources folder and has no extension.");
            return null;
        }

        Vector3 where = Ground(new Vector3(row.x, 0f, row.z));

        var go = Object.Instantiate(prefab, where, Quaternion.identity, parent);

        go.name = "Npc_" + row.id;

        Strip(go);

        // ══ SIZED LIKE A PERSON ═══════════════════════════════════════════════
        //
        // The SPUM packs are authored at different scales, so a shopkeeper next to the
        // player is whatever height his source prefab happened to be unless somebody
        // measures. SpumRig already owns that measurement for the player and the
        // monsters, so it owns it here too.
        SpumRig.NormaliseHeight(go.transform, PersonHeight);

        // ══ OR HE STANDS EDGE-ON ══════════════════════════════════════════════
        //
        // A SPUM character is flat artwork in a 3D world and faces whatever direction
        // its transform does. The player and the monsters get a Billboard from their
        // prefab builders; a rig loaded straight out of Resources has none, so without
        // this the shopkeeper is a vertical line from most camera angles.
        //
        // CoverArt attaches to the ART subtree rather than the root, which is what
        // keeps the collider and the name plate pointing where they were put.
        Billboard.CoverArt(go);

        var npc = go.AddComponent<NpcController>();

        npc.kind        = string.IsNullOrEmpty(row.kind) ? "shop" : row.kind;
        npc.displayName = row.DisplayName;

        npc.Face(row.facing);
        npc.AddCollider();

        NamePlate.Attach(go, npc.displayName, NamePlate.Role.OtherPlayer);

        return npc;
    }

    /// <summary>A shopkeeper is a person, so they are a person's height.</summary>
    private const float PersonHeight = 2f;

    /// <summary>
    /// Removes everything that would make the rig behave like a player or a monster.
    ///
    /// The list is the one RemotePlayerView uses. An AudioListener is the loudest
    /// omission -- two in a scene makes Unity warn every frame and the sound field
    /// collapse -- and a stray NavMeshAgent would have the shopkeeper wandering off.
    /// </summary>
    private static void Strip(GameObject go)
    {
        foreach (var behaviour in go.GetComponentsInChildren<MonoBehaviour>(true))
        {
            if (behaviour == null) continue;

            if (behaviour is PlayerController or CameraController or MonsterController or
                              SkillNodeController)
                Destroy(behaviour);
        }

        foreach (var agent in go.GetComponentsInChildren<NavMeshAgent>(true)) Destroy(agent);
        foreach (var ear   in go.GetComponentsInChildren<AudioListener>(true)) Destroy(ear);
        foreach (var eye   in go.GetComponentsInChildren<Camera>(true))        Destroy(eye);
    }

    /// <summary>
    /// Something to click on.
    ///
    /// A trigger, so the player can walk through a shopkeeper standing in a doorway
    /// rather than being wedged against one. The raycast in PlayerController asks for
    /// triggers explicitly, which is what makes that safe.
    /// </summary>
    private void AddCollider()
    {
        var body = gameObject.AddComponent<CapsuleCollider>();

        body.isTrigger = true;
        body.radius    = 0.6f;
        body.height    = 2f;
        body.center    = Vector3.up * 1f;
    }

    /// <summary>
    /// Turns them to face a direction, once.
    ///
    /// Written on the ART subtree rather than the root, because that is where
    /// SpriteFacing writes and where Billboard yaws — flipping the root would turn the
    /// collider and the name plate with it, and a mirrored name plate is mirror
    /// writing.
    /// </summary>
    private void Face(float facing)
    {
        // UnitRoot by name, not "the first child". A SPUM prefab's first child is
        // whatever the pack's author put first, and SpriteFacing, Billboard and
        // SpumRig.NormaliseHeight all agree that the art lives under UnitRoot —
        // writing scale onto a different node would flip something else.
        Transform art = SpumRig.FindDeep(transform, "UnitRoot") ??
                        (transform.childCount > 0 ? transform.GetChild(0) : null);

        if (art == null) return;

        Vector3 scale = art.localScale;

        art.localScale = new Vector3(Mathf.Abs(scale.x) * (facing < 0f ? -1f : 1f),
                                     scale.y, scale.z);
    }

    /// <summary>
    /// Drops a point onto the ground, so an NPC authored with a rough position does
    /// not end up buried in a hillside or standing in the air above one.
    /// </summary>
    private static Vector3 Ground(Vector3 where)
    {
        if (Physics.Raycast(where + Vector3.up * 20f, Vector3.down,
                            out RaycastHit hit, 60f,
                            Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            return hit.point;

        if (NavMesh.SamplePosition(where, out NavMeshHit nav, 8f, NavMesh.AllAreas))
            return nav.position;

        return where;
    }

    /// <summary>Opens whatever this person is for. Called by a click on them.</summary>
    public void Talk()
    {
        switch (kind)
        {
            case "shop":
                GoldShopPanel.Open(this);
                break;

            default:
                GameEvents.FireToast($"{displayName} has nothing to say.", ChatTone.Info);
                break;
        }
    }
}
