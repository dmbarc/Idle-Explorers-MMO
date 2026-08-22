using UnityEngine;

/// <summary>
/// Keeps a world-space object facing the camera — health bars, node name tags.
/// </summary>
public class Billboard : MonoBehaviour
{
    private Camera _camera;

    void LateUpdate()
    {
        // Camera.main does a tagged scene search; cache it, and re-resolve if the
        // map scene reloads and replaces the camera.
        if (_camera == null)
        {
            _camera = Camera.main;
            if (_camera == null) return;
        }

        // Align with the camera's forward rather than LookAt(camera). LookAt points
        // +Z *at* the camera, which renders text mirrored — correct for a sprite,
        // wrong for anything with glyphs on it.
        transform.rotation = Quaternion.LookRotation(_camera.transform.forward, _camera.transform.up);
    }
}
