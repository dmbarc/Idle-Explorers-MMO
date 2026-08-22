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
    public float distance    = 18f;
    public float minDistance = 6f;
    public float maxDistance = 45f;
    public float zoomSpeed   = 4f;

    [Header("Angles")]
    public float pitch      = 50f;
    public float yaw        = 45f;
    public float orbitSpeed = 90f;

    [Header("Pan")]
    public float panSpeed = 18f;
    [Tooltip("How far WASD may drag the view from the player before it stops.")]
    public float maxPanDistance = 30f;

    /// <summary>Offset applied by WASD, reset when the camera re-centres.</summary>
    private Vector3 _panOffset;
    private Vector3 _lastKnownTargetPos;

    void Start()
    {
        AcquireTarget();
        if (target != null)
        {
            _lastKnownTargetPos = target.position;
            transform.position  = DesiredPosition();
            transform.rotation  = Quaternion.Euler(pitch, yaw, 0f);
        }
    }

    void LateUpdate()
    {
        if (target == null)
        {
            AcquireTarget();
            if (target == null) return;
        }

        HandleZoom();
        HandleOrbit();
        HandlePan();

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

        distance = Mathf.Clamp(distance - normalized * zoomSpeed, minDistance, maxDistance);
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

        if (Mathf.Approximately(x, 0f) && Mathf.Approximately(z, 0f)) return;

        // Pan relative to where the camera is looking, flattened to the ground
        Vector3 forward = Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized;
        Vector3 right   = Vector3.ProjectOnPlane(transform.right,   Vector3.up).normalized;

        _panOffset += (right * x + forward * z) * panSpeed * Time.deltaTime;
        _panOffset  = Vector3.ClampMagnitude(_panOffset, maxPanDistance);
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
