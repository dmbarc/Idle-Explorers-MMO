using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// RuneScape-style camera: follows the player, WASD pans away from them,
/// scroll wheel zooms, Q/E orbit.
///
/// Added to the map scene's Main Camera by MapSceneSetup.
/// </summary>
public class CameraController : MonoBehaviour
{
    [Header("Follow")]
    [Tooltip("Object to orbit. Found by tag at runtime when left empty.")]
    public Transform target;
    public float followSmoothing = 8f;

    [Header("Zoom")]
    public float distance    = 30f;
    public float minDistance = 4f;
    public float maxDistance = 300f;

    /// <summary>
    /// Multiplier per scroll notch. Zoom is proportional, not linear: a fixed number
    /// of units per notch that feels right up close takes seventy-five notches to
    /// cross the range from here, and one that crosses the range quickly is unusable
    /// when you are stood next to something.
    /// </summary>
    public float zoomStep = 1.18f;

    [Header("Angles")]
    public float pitch      = 50f;
    public float yaw        = 45f;
    public float orbitSpeed = 90f;

    [Header("Pan")]
    public float panSpeed = 18f;
    [Tooltip("How far WASD may drag the view from the player before it stops.")]
    public float maxPanDistance = 30f;

    [Tooltip("Seconds of no WASD input before the view eases back to the player.")]
    public float panHoldSeconds = 1.5f;

    [Tooltip("How quickly the pan offset decays once the hold expires. Higher is snappier.")]
    public float panRecentreSpeed = 3.5f;

    /// <summary>Offset applied by WASD, reset when the camera re-centres.</summary>
    private Vector3 _panOffset;
    private Vector3 _lastKnownTargetPos;
    private float   _lastPanInputAt = -999f;

    void Start()
    {
        DetachFromParent();

        AcquireTarget();
        if (target != null)
        {
            _lastKnownTargetPos = target.position;
            transform.position  = DesiredPosition();
            transform.rotation  = Quaternion.Euler(pitch, yaw, 0f);
        }
    }

    /// <summary>
    /// A follow camera must not be a CHILD of what it follows.
    ///
    /// The map scene had this camera parented to PlayerCharacter. LateUpdate computes
    /// a world position and assigns transform.position/rotation — but by the time it
    /// runs, the parent has already moved the camera and, because the NavMeshAgent
    /// rotates the player to face travel, swung it around them. The smoothed follow
    /// then hauls it back, every frame. That is what "the camera moves in odd
    /// directions, sometimes behind the character" was.
    ///
    /// MapSceneSetup detaches it at authoring time; this is the runtime guarantee for
    /// any scene that has not been regenerated.
    /// </summary>
    private void DetachFromParent()
    {
        if (transform.parent == null) return;

        Debug.Log($"[CameraController] Detaching from '{transform.parent.name}' — " +
                  "a follow camera parented to its own target fights itself every frame.");
        transform.SetParent(null, worldPositionStays: true);
    }

    void LateUpdate()
    {
        if (target == null)
        {
            AcquireTarget();
            if (target == null) return;
        }

        // A panel is open, or the player is typing. Zoom, orbit and pan all read
        // their devices directly and would otherwise fire THROUGH the interface: the
        // scroll wheel zoomed the map while scrolling a crafting list at the anvil,
        // and WASD would walk the view off the character mid-sentence. The follow
        // below still runs, so the camera keeps tracking a character who is moving.
        if (!UIManager.WorldInputBlocked)
        {
            HandleZoom();
            HandleOrbit();
            HandlePan();
        }
        else
        {
            // Without this the offset counts as "held" for the whole time a panel is
            // open and then snaps back the instant it closes.
            RecentreAfterPanning();
        }

        _lastKnownTargetPos = target.position;

        // Smoothed follow so the camera does not judder with the agent's steps
        transform.position = Vector3.Lerp(transform.position, DesiredPosition(),
                                           1f - Mathf.Exp(-followSmoothing * Time.deltaTime));
        transform.rotation = Quaternion.Euler(pitch, yaw, 0f);
    }

    // ── Input ─────────────────────────────────────────────────────────────────

    private void HandleZoom()
    {
        var mouse = Mouse.current;
        if (mouse == null) return;

        float scroll = mouse.scroll.ReadValue().y;
        if (Mathf.Abs(scroll) < 0.01f) return;

        // Scroll deltas are ~120 per notch on Windows, ~1 on other setups
        float normalized = Mathf.Clamp(scroll / 120f, -1f, 1f);
        if (Mathf.Abs(normalized) < 0.01f) normalized = Mathf.Sign(scroll);

        // Proportional: each notch scales the distance rather than subtracting from
        // it, so a notch moves you the same *fraction* whether you are at 5 units or
        // 250. A linear step cannot serve both ends of a 4-to-300 range.
        distance = Mathf.Clamp(distance * Mathf.Pow(Mathf.Max(1.01f, zoomStep), -normalized),
                                minDistance, maxDistance);
    }

    private void HandleOrbit()
    {
        var kb = Keyboard.current;
        if (kb == null) return;

        if (kb.qKey.isPressed) yaw -= orbitSpeed * Time.deltaTime;
        if (kb.eKey.isPressed) yaw += orbitSpeed * Time.deltaTime;
    }

    private void HandlePan()
    {
        var kb = Keyboard.current;
        if (kb == null) return;

        // Number keys drive abilities, so panning is WASD only.
        float x = (kb.dKey.isPressed ? 1f : 0f) - (kb.aKey.isPressed ? 1f : 0f);
        float z = (kb.wKey.isPressed ? 1f : 0f) - (kb.sKey.isPressed ? 1f : 0f);

        // Space snaps back to the player
        if (kb.spaceKey.wasPressedThisFrame) _panOffset = Vector3.zero;

        if (Mathf.Approximately(x, 0f) && Mathf.Approximately(z, 0f))
        {
            RecentreAfterPanning();
            return;
        }

        _lastPanInputAt = Time.time;

        // Pan relative to where the camera is looking, flattened to the ground.
        // Scaled by zoom: at 250 units out, an 18-units-per-second pan is barely
        // perceptible, and up close it would be a lurch.
        Vector3 forward = Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized;
        Vector3 right   = Vector3.ProjectOnPlane(transform.right,   Vector3.up).normalized;
        float   speed   = panSpeed * Mathf.Max(0.35f, distance / 30f);

        _panOffset += (right * x + forward * z) * speed * Time.deltaTime;
        _panOffset  = Vector3.ClampMagnitude(_panOffset, maxPanDistance * Mathf.Max(1f, distance / 30f));
    }

    /// <summary>
    /// Eases the pan offset back to zero once WASD has been idle for a moment.
    ///
    /// The offset is world-space and used to persist until Space was pressed — an
    /// undiscoverable key. Panning once left the camera permanently off-centre, so as
    /// the player walked about it appeared ahead of them, then beside, then behind,
    /// with no obvious cause.
    /// </summary>
    private void RecentreAfterPanning()
    {
        if (_panOffset == Vector3.zero) return;
        if (Time.time - _lastPanInputAt < panHoldSeconds) return;

        _panOffset = Vector3.Lerp(_panOffset, Vector3.zero,
                                   1f - Mathf.Exp(-panRecentreSpeed * Time.deltaTime));

        // Snap the last fraction of a unit, or it creeps toward zero forever.
        if (_panOffset.sqrMagnitude < 0.01f) _panOffset = Vector3.zero;
    }

    // ── Positioning ───────────────────────────────────────────────────────────

    private Vector3 DesiredPosition()
    {
        Vector3 focus  = (target != null ? target.position : _lastKnownTargetPos) + _panOffset;
        Vector3 offset = Quaternion.Euler(pitch, yaw, 0f) * Vector3.back * distance;
        return focus + offset;
    }

    private void AcquireTarget()
    {
        var player = GameObject.FindWithTag("Player");
        if (player == null)
        {
            var controller = FindAnyObjectByType<PlayerController>();
            if (controller != null) player = controller.gameObject;
        }
        if (player != null) target = player.transform;
    }
}
