using UnityEngine;

/// <summary>
/// The camera that renders the menus.
///
/// Bootstrap has no scene geometry, but Unity still needs *a* camera or it draws
/// "Display 1 — No cameras rendering" over the UI. This provides one, and then
/// gets out of the way: when a map scene loads it brings its own Main Camera, and
/// two enabled cameras fight over the display.
///
/// Created by UIManager, so no scene ever needs a hand-placed camera.
/// </summary>
[RequireComponent(typeof(Camera))]
public class BootstrapCamera : MonoBehaviour
{
    private Camera _camera;

    public static BootstrapCamera Create()
    {
        var go = new GameObject("BootstrapCamera");
        DontDestroyOnLoad(go);

        var cam = go.AddComponent<Camera>();
        cam.clearFlags      = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.06f, 0.05f, 0.08f, 1f);
        cam.cullingMask     = 0;      // renders nothing; the UI canvas is Screen Space Overlay
        cam.orthographic    = true;
        cam.depth           = -100;   // behind any gameplay camera
        go.AddComponent<AudioListener>();

        return go.AddComponent<BootstrapCamera>();
    }

    void Awake() => _camera = GetComponent<Camera>();

    void OnEnable()
    {
        GameEvents.OnMapEntered += OnMapEntered;
        GameEvents.OnReturnToMainMenu += OnReturnedToMenu;
    }

    void OnDisable()
    {
        GameEvents.OnMapEntered -= OnMapEntered;
        GameEvents.OnReturnToMainMenu -= OnReturnedToMenu;
    }

    /// <summary>
    /// Steps aside for the map's own camera — but only if there is one.
    ///
    /// This used to disable unconditionally, on the assumption in the class comment
    /// above. That assumption failed: MapSceneSetup deleted the player root, the Main
    /// Camera was a child of it, and both maps saved with no camera at all. This
    /// listener then switched off the last enabled camera in the game at the exact
    /// moment the map finished loading, which is why the fault presented as
    /// "Display 1 — No cameras rendering" on entering any map.
    ///
    /// Staying on does not make the map playable — cullingMask is 0, so nothing in the
    /// world draws. It keeps a legible background and a working overlay UI so the
    /// player can get back to the menu, and it puts a real error in the console instead
    /// of a black screen. A safety net, not a fix; the fix is that the map builder now
    /// creates the camera.
    /// </summary>
    private void OnMapEntered(string mapId)
    {
        if (!AnotherCameraIsRendering())
        {
            Debug.LogError($"[BootstrapCamera] Scene '{mapId}' has no camera of its own, so the " +
                           "menu camera is staying on. The map will not be visible. Run " +
                           "'Idle Explorers → Setup Everything' to rebuild the map scenes.");
            return;
        }

        SetActive(false);
    }

    private void OnReturnedToMenu() => SetActive(true);

    /// <summary>Any enabled camera that is not this one.</summary>
    private bool AnotherCameraIsRendering()
    {
        var cameras = FindObjectsByType<Camera>(FindObjectsInactive.Exclude);

        foreach (var cam in cameras)
        {
            if (cam == null || cam == _camera) continue;
            if (cam.enabled && cam.isActiveAndEnabled) return true;
        }

        return false;
    }

    private void SetActive(bool active)
    {
        if (_camera == null) return;
        _camera.enabled = active;

        // Only one AudioListener may be enabled at a time or Unity logs a warning
        // every frame; the map camera carries its own.
        var listener = GetComponent<AudioListener>();
        if (listener != null) listener.enabled = active;
    }
}
