using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Turns a billboarded character to face the way it is going.
///
/// ══ WHY THIS IS NOT JUST rotation ═════════════════════════════════════════════
///
/// A SPUM character is flat artwork. Billboard already turns the art subtree to face
/// the camera, which is what stops it going edge-on — but it also means the rig's
/// rotation no longer says anything about which way the character is looking. Turning
/// it any further would undo the billboard and put us back where we started.
///
/// So facing is a MIRROR, not a rotation: the art keeps facing the camera and gets
/// flipped left-to-right. Nothing in this project flipped a sprite before — flipX has
/// zero occurrences and no negative scale is ever written — so this is the whole
/// mechanism, in one place.
///
/// ══ WHY IT SITS BESIDE Billboard AND READS THE MAGNITUDE ══════════════════════
///
/// Same transform as the Billboard, which is the art subtree and not the prefab root.
/// The root carries the agent and the collider, and code there reasonably expects
/// transform.forward to mean "the way this character is facing".
///
/// The scale is written as `Abs(current) * sign` rather than `±1`, because
/// SpumRig.NormaliseHeight writes a uniform scale factor to this exact transform to
/// make every character the right height. Assuming 1 there would silently resize
/// every character in the game the first time they turned around.
///
/// ══ THE DEADZONE AND THE DEBOUNCE ARE BOTH REQUIRED ═══════════════════════════
///
/// Facing is decided by projecting movement onto the camera's right vector, and a
/// character walking almost exactly toward or away from the camera has a projection
/// hovering around zero. Without a deadzone it strobes; without a hold time it
/// strobes more slowly. Neither alone is enough, because a NavMeshAgent rounding a
/// corner produces real sideways motion in both directions within a few frames.
/// </summary>
public class SpriteFacing : MonoBehaviour
{
    /// <summary>
    /// Which way the artwork faces when unflipped.
    ///
    /// SPUM rigs are authored facing one way, and which way decides the sign of every
    /// flip below. It is deliberately one constant rather than a minus sign spread
    /// across the file.
    ///
    /// It is -1 because the rigs face LEFT. With +1 every character in the game walked
    /// backwards -- which is exactly the failure this constant exists to make a
    /// one-line fix.
    /// </summary>
    private const float DefaultFacing = -1f;

    /// <summary>
    /// Sideways speed below which the direction is not worth believing.
    ///
    /// In metres per second, measured along the camera's right vector — so a
    /// character sprinting straight at the camera is below it, which is correct: they
    /// are not turning, and the last facing is the honest answer.
    /// </summary>
    public const float SidewaysDeadzone = 0.15f;

    /// <summary>Minimum seconds between flips.</summary>
    public const float HoldSeconds = 0.12f;

    /// <summary>
    /// Something to look at while standing still — usually the current target.
    ///
    /// Set by whatever owns the character. An idle character with nothing to look at
    /// holds its last facing rather than snapping to a default, because snapping is
    /// visible and holding is not.
    /// </summary>
    public Transform LookTarget { get; set; }

    /// <summary>
    /// Says which way this rig is travelling, for one that is positioned rather than
    /// steered. See the note in SidewaysComponent.
    /// </summary>
    public void ReportVelocity(Vector3 velocity) => _reported = velocity;

    private NavMeshAgent _agent;
    private Camera       _camera;
    private float        _sign = DefaultFacing;

    /// <summary>
    /// Movement handed in from outside, for a rig with no agent of its own.
    ///
    /// Zero means "I have nothing to say", which is what every locally steered
    /// character leaves it at.
    /// </summary>
    private Vector3      _reported;
    private float        _lastFlipAt = float.NegativeInfinity;

    /// <summary>
    /// Makes a character rig face its direction of travel, and returns the component.
    ///
    /// Deliberately mirrors Billboard.CoverArt and attaches to the same transform:
    /// these two are a pair, and a rig with one and not the other is either edge-on
    /// or permanently facing the same way.
    /// </summary>
    public static SpriteFacing Attach(GameObject rigRoot)
    {
        if (rigRoot == null) return null;

        var billboard = Billboard.CoverArt(rigRoot);
        if (billboard == null) return null;

        var art = billboard.transform;

        var existing = art.GetComponent<SpriteFacing>();
        if (existing != null) return existing;

        var added = art.gameObject.AddComponent<SpriteFacing>();
        added._agent = rigRoot.GetComponentInParent<NavMeshAgent>()
                    ?? rigRoot.GetComponentInChildren<NavMeshAgent>();

        return added;
    }

    private void Awake()
    {
        if (_agent == null) _agent = GetComponentInParent<NavMeshAgent>();
    }

    /// <summary>
    /// After LateUpdate, so the Billboard has already turned the art this frame and
    /// the camera's right vector is the one actually being rendered against.
    /// </summary>
    private void LateUpdate()
    {
        if (_camera == null)
        {
            _camera = Camera.main;
            if (_camera == null) return;
        }

        float sideways = SidewaysComponent();

        if (Mathf.Abs(sideways) >= SidewaysDeadzone)
        {
            float wanted = sideways > 0f ? DefaultFacing : -DefaultFacing;
            if (!Mathf.Approximately(wanted, _sign) && Time.time - _lastFlipAt >= HoldSeconds)
            {
                _sign       = wanted;
                _lastFlipAt = Time.time;
            }
        }

        Apply();
    }

    /// <summary>
    /// How fast the character is moving across the screen, signed right-positive.
    ///
    /// Movement first, then the look target: a character walking away from something
    /// it is fighting should face where it is going, which is also what a player
    /// reads as "retreating" rather than "moonwalking".
    /// </summary>
    private float SidewaysComponent()
    {
        Vector3 right = _camera.transform.right;

        // ══ A RIG THAT STEERS ITSELF, AND ONE THAT IS TOLD ═════════════════
        //
        // Local characters and monsters have a NavMeshAgent whose velocity says which
        // way they are going. A REMOTE player has none -- their position comes from
        // the server and the agent is stripped, precisely so they do not wander off
        // on their own opinion -- so their view reports the direction instead.
        //
        // Checked first: a rig that is being told where it is going has no agent to
        // disagree with, and one that has an agent never sets this.
        if (_reported.sqrMagnitude > 0.0001f)
            return Vector3.Dot(_reported, right);

        if (_agent != null && _agent.isActiveAndEnabled && _agent.velocity.sqrMagnitude > 0.0001f)
            return Vector3.Dot(_agent.velocity, right);

        if (LookTarget == null) return 0f;

        Vector3 toTarget = LookTarget.position - transform.position;
        toTarget.y = 0f;

        // Scaled past the deadzone so a distant target still decides the facing: this
        // is a direction, not a speed, and comparing it against a speed threshold
        // would leave a character ignoring anything it was not almost touching.
        float dot = Vector3.Dot(toTarget.normalized, right);

        return Mathf.Abs(dot) < 0.15f ? 0f : dot * (SidewaysDeadzone * 2f / 0.15f);
    }

    /// <summary>
    /// Writes the mirror, preserving whatever magnitude the rig was scaled to.
    /// </summary>
    private void Apply()
    {
        Vector3 scale = transform.localScale;
        float   wanted = Mathf.Abs(scale.x) * _sign;

        if (Mathf.Approximately(scale.x, wanted)) return;

        scale.x = wanted;
        transform.localScale = scale;
    }

    /// <summary>
    /// Every TextMeshPro object that a SpriteFacing would mirror.
    ///
    /// Mirrored glyphs are the failure mode of this whole approach, and they are
    /// obvious to a player and invisible to a compiler. Chat bubbles parent to the
    /// speaker root and damage numbers are unparented, so this should be zero — the
    /// prefab builders assert it, in the same shape as
    /// Billboard.CountUnbillboardedSprites.
    /// </summary>
    public static int CountMirroredText(GameObject root)
    {
        if (root == null) return 0;

        int mirrored = 0;

        foreach (var text in root.GetComponentsInChildren<TMPro.TMP_Text>(includeInactive: true))
        {
            if (text == null) continue;
            if (text.GetComponentInParent<SpriteFacing>(includeInactive: true) != null) mirrored++;
        }

        return mirrored;
    }
}
