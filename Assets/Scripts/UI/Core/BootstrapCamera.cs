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

    private void OnMapEntered(string mapId) => SetActive(false);
    private void OnReturnedToMenu()         => SetActive(true);

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
