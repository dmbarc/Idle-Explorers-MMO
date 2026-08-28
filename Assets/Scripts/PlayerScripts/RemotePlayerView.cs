using IdleExplorers.Backend;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Somebody else, standing in your world.
///
/// ══ WHY IT IS A COPY OF THE LOCAL RIG ═════════════════════════════════════════
///
/// The player prefab lives in Assets/Prefabs, not in Resources, so it cannot be
/// loaded by name at runtime. Rather than move it — and pay for it in every build
/// whether anybody is online or not — a remote player is cloned from the rig already
/// standing in the scene, then stripped of everything that makes it yours.
///
/// That has a property worth keeping on purpose: another player cannot look like
/// something a player could not be, because they are literally made from the same
/// object. A separate remote prefab is a second thing to keep in step with the first,
/// and the day they drift the difference is other people rendering wrongly.
///
/// ══ WHY IT INTERPOLATES ═══════════════════════════════════════════════════════
///
/// Positions arrive about every two seconds, because Unity WebGL has no usable
/// socket and the world is polled. Snapping to each one would make everybody
/// teleport in a way that reads as broken rather than as latency.
///
/// So the reported position is a TARGET and the body walks toward it. Two seconds of
/// smooth walking to a place somebody has already left is a much better lie than the
/// truth arriving in jumps.
/// </summary>
public class RemotePlayerView : MonoBehaviour
{
    /// <summary>
    /// How quickly the body closes on the last reported position.
    ///
    /// Faster than a character walks, so a view that has fallen behind catches up
    /// rather than trailing further with every report. Not instant, or it snaps.
    /// </summary>
    private const float CatchUpSpeed = 6f;

    /// <summary>Below this, the walk animation stops. Stops a standing player shuffling.</summary>
    private const float StillThreshold = 0.05f;

    /// <summary>Who this is. The server's character id, never a local one.</summary>
    public string CharacterId { get; private set; }

    public string Name  { get; private set; }
    public int    Level { get; private set; }
    public string ClassId { get; private set; }

    /// <summary>True when they are in a group. Drawn differently; see NamePlate.</summary>
    public bool Grouped { get; private set; }

    private Vector3      _target;
    private NamePlate    _plate;
    private SpriteFacing _facing;
    private Animator     _anim;

    /// <summary>
    /// Builds a view of another player next to the local one.
    ///
    /// Returns null when there is no local rig to copy — which happens for a frame or
    /// two while a map loads, and is not worth an error: the next report builds it.
    /// </summary>
    public static RemotePlayerView Spawn(GameObject localRig, RemotePlayer data)
    {
        if (localRig == null || data == null || string.IsNullOrEmpty(data.characterId)) return null;

        GameObject copy = Instantiate(localRig, new Vector3(data.x, localRig.transform.position.y, data.z),
                                      localRig.transform.rotation);

        copy.name = $"RemotePlayer_{data.name}";

        Strip(copy);

        var view = copy.AddComponent<RemotePlayerView>();

        view.CharacterId = data.characterId;
        view._target     = copy.transform.position;
        view._anim       = copy.GetComponentInChildren<Animator>();
        view._facing     = SpriteFacing.Attach(copy);

        float head = SpumRig.MeasureCharacterHeight(copy.transform);

        view._plate = NamePlate.Attach(copy, data.name, NamePlate.Role.OtherPlayer,
                                       heightAbove: (head > 0.1f ? head : 2f) + NamePlate.ClearOfHead);

        view.Apply(data);

        return view;
    }

    /// <summary>
    /// Everything that makes the local rig YOURS, removed from the copy.
    ///
    /// ══ WHY THIS LIST IS EXPLICIT ═════════════════════════════════════════════
    ///
    /// Because the failure of missing one is loud and confusing. A second
    /// PlayerController would run a second attack loop and a second set of
    /// activity calls; a second CameraController would fight the real one for the
    /// view every frame; a second AudioListener makes Unity log a warning and mute
    /// positional audio outright.
    ///
    /// The agent goes too. A remote player's position is decided by the server, not
    /// by a NavMeshAgent's own opinion of where it should walk — leaving one on
    /// would give every other player a mind of their own.
    /// </summary>
    private static void Strip(GameObject copy)
    {
        foreach (var controller in copy.GetComponentsInChildren<PlayerController>(true))  Destroy(controller);
        foreach (var camera in copy.GetComponentsInChildren<CameraController>(true))      Destroy(camera);
        foreach (var listener in copy.GetComponentsInChildren<AudioListener>(true))       Destroy(listener);
        foreach (var appearance in copy.GetComponentsInChildren<CharacterAppearance>(true)) Destroy(appearance);
        foreach (var facing in copy.GetComponentsInChildren<SpriteFacing>(true))          Destroy(facing);
        foreach (var plate in copy.GetComponentsInChildren<NamePlate>(true))              Destroy(plate);

        foreach (var agent in copy.GetComponentsInChildren<NavMeshAgent>(true))
        {
            agent.enabled = false;
            Destroy(agent);
        }

        // A camera parented under the rig comes along with the clone and renders a
        // second view over the top of the real one.
        foreach (var camera in copy.GetComponentsInChildren<Camera>(true)) Destroy(camera.gameObject);

        // Kept, and made a trigger: this is what a click has to hit to inspect
        // somebody. A solid one would let other players push the local one around.
        foreach (var collider in copy.GetComponentsInChildren<Collider>(true))
            collider.isTrigger = true;
    }

    /// <summary>Takes the latest report. Cheap enough to call every poll.</summary>
    public void Apply(RemotePlayer data)
    {
        if (data == null) return;

        Name    = data.name;
        Level   = data.level;
        ClassId = data.classId;
        Grouped = !string.IsNullOrEmpty(data.partyId);

        _target = new Vector3(data.x, transform.position.y, data.z);

        if (_plate != null)
        {
            _plate.SetText(data.name);
            _plate.SetRole(NamePlate.Role.OtherPlayer);
        }

        // ══ NEVER LEAVE THEM WEARING YOUR FACE ═════════════════════════════
        //
        // This rig is a CLONE of the local player, so skipping the appearance does not
        // leave it blank -- it leaves it looking exactly like you. Walking up to
        // somebody and seeing a copy of yourself standing on top of them is precisely
        // that, and it happens to every character created before the appearance column
        // existed, because their stored look is empty.
        //
        // A default face is not right either, but it is somebody else's, which is the
        // property that matters. The real repair is the backfill in ServerState, which
        // gives those characters a stored appearance the first time they play.
        SpumAppearance.Apply(transform,
                             data.appearance is { IsEmpty: false }
                                 ? data.appearance
                                 : SpumAppearance.Default());
    }

    private void Update()
    {
        Vector3 here = transform.position;
        float   gap  = Vector3.Distance(here, _target);

        if (gap > StillThreshold)
        {
            transform.position = Vector3.MoveTowards(here, _target, CatchUpSpeed * Time.deltaTime);

            // Face the way they are travelling, which is what SpriteFacing does for
            // everyone else -- it reads an agent's velocity, and this rig has none.
            if (_facing != null) _facing.ReportVelocity((_target - here).normalized * CatchUpSpeed);
        }
        else if (_facing != null)
        {
            _facing.ReportVelocity(Vector3.zero);
        }

        SpumAnim.SetMoving(_anim, gap > StillThreshold);
    }
}
