using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// A live character, rendered into a UI panel.
///
/// SPUM characters are sprite rigs in world space, so they cannot simply be parented
/// to a Canvas — the UI here is Screen Space Overlay, which draws nothing from the
/// scene. Instead each preview builds a tiny private stage: a rig, a camera pointed at
/// it, and a RenderTexture the camera draws into, shown through a RawImage.
///
/// Stages are parked far outside the playable world and separated from each other, so
/// several previews (five class cards, plus the editor) can exist at once without
/// seeing each other or being seen by the game camera. That is cheaper and far less
/// fragile than adding a project layer, which would need a ProjectSettings edit that
/// every future contributor would have to know about.
///
/// Every preview owns its RenderTexture and releases it in OnDestroy. Leaking those is
/// how a screen that rebuilds on every show quietly eats video memory.
/// </summary>
public class CharacterPreview : MonoBehaviour
{
    /// <summary>Where preview stages live — far from anything the player can reach.</summary>
    private static readonly Vector3 StageOrigin = new Vector3(10000f, 10000f, 10000f);

    /// <summary>Gap between stages, wide enough that no camera sees a neighbour.</summary>
    private const float StageSpacing = 100f;

    private static int _nextStageIndex;

    private const int TextureWidth  = 256;
    private const int TextureHeight = 320;

    private RenderTexture _texture;
    private Camera        _camera;
    private GameObject    _stage;
    private GameObject    _rig;
    private RawImage      _image;

    /// <summary>
    /// Builds a preview filling <paramref name="parent"/>. Returns null when the SPUM
    /// prefab is missing, so callers can fall back to a plain icon rather than crash.
    /// </summary>
    public static CharacterPreview Create(Transform parent, SpumSaveData look, string name = "Preview")
    {
        var prefab = Resources.Load<GameObject>(PlayerRigAddress);
        if (prefab == null)
        {
            Debug.LogWarning($"[CharacterPreview] No rig prefab at Resources/{PlayerRigAddress} — " +
                             "preview skipped.");
            return null;
        }

        var host = new GameObject(name, typeof(RectTransform), typeof(RawImage), typeof(CharacterPreview));
        host.transform.SetParent(parent, false);
        UIFactory.FillParent(host.GetComponent<RectTransform>());

        var preview = host.GetComponent<CharacterPreview>();
        preview.Build(prefab, look);
        return preview;
    }

    /// <summary>
    /// The rig used for previews. The same Devil prefab the player uses, so a preview
    /// and the character it is previewing cannot drift apart — the body sheet is
    /// swapped by SpumAppearance, not the prefab.
    /// </summary>
    public const string PlayerRigAddress = "Addons/BasicPack/2_Prefab/Devil/SPUM_20240911215637772";

    private void Build(GameObject prefab, SpumSaveData look)
    {
        Vector3 origin = StageOrigin + Vector3.right * (StageSpacing * _nextStageIndex++);

        _stage = new GameObject($"PreviewStage_{name}");
        _stage.transform.position = origin;

        _rig = Instantiate(prefab, origin, Quaternion.identity, _stage.transform);
        _rig.name = "Rig";

        // An idle SPUM rig runs its animator, which is most of what makes a preview
        // read as a character rather than a paper doll. But the controller also drives
        // a blink that swaps eye sprites, so the eyes are set after it is running.
        StripGameplayComponents(_rig);

        _camera = new GameObject("PreviewCamera", typeof(Camera)).GetComponent<Camera>();
        _camera.transform.SetParent(_stage.transform, false);

        // Framed on the upper body: the rig's feet are near its origin and the head is
        // roughly 1.3 units up, so a slightly raised, slightly tight shot fills the
        // panel with the character rather than the empty space around them.
        _camera.transform.localPosition = new Vector3(0f, 0.85f, -10f);
        _camera.orthographic     = true;
        _camera.orthographicSize = 1.05f;
        _camera.clearFlags       = CameraClearFlags.SolidColor;
        _camera.backgroundColor  = new Color(0f, 0f, 0f, 0f);   // transparent, so the card shows through
        _camera.nearClipPlane    = 0.1f;
        _camera.farClipPlane     = 50f;

        _texture = new RenderTexture(TextureWidth, TextureHeight, 16, RenderTextureFormat.ARGB32)
        {
            name       = $"PreviewRT_{name}",
            antiAliasing = 1,
        };
        _camera.targetTexture = _texture;

        _image = GetComponent<RawImage>();
        _image.texture       = _texture;
        _image.raycastTarget = false;

        SetLook(look);
    }

    /// <summary>Redraws the preview with a different look. Cheap — no rebuild.</summary>
    public void SetLook(SpumSaveData look)
    {
        if (_rig == null) return;
        SpumAppearance.Apply(_rig.transform, look ?? SpumAppearance.Default());
    }

    /// <summary>
    /// Dresses the preview in a piece of equipment, so a class card can show its
    /// starting gear. Equipment art follows the same addressing as appearance art.
    /// </summary>
    public void SetEquipment(string equipSlotId, string equipSpriteAddress)
    {
        var slot = EquipmentSlots.Get(equipSlotId);
        if (_rig == null || slot == null || !slot.RendersOnCharacter) return;

        foreach (var partName in slot.SpumParts)
        {
            var layers = SpumRig.Collect(_rig.transform, partName);

            bool anyDrawn = false;
            foreach (var layer in layers) if (layer.WasDrawn) { anyDrawn = true; break; }

            foreach (var layer in layers)
            {
                if (anyDrawn && !layer.WasDrawn) { layer.Renderer.enabled = false; continue; }

                var sprite = SpriteLoader.Load(equipSpriteAddress, layer.Side);
                if (sprite != null) SpumRig.Show(layer, sprite);
            }
        }
    }

    /// <summary>
    /// Removes anything that would try to play the game inside a preview.
    ///
    /// The rig prefab is only art, but it is instantiated far outside the NavMesh and
    /// with no ground under it, so any gameplay component that woke up there would
    /// spend the preview's life logging failures about where it is.
    /// </summary>
    private static void StripGameplayComponents(GameObject rig)
    {
        foreach (var behaviour in rig.GetComponentsInChildren<MonoBehaviour>(true))
        {
            if (behaviour == null) continue;

            // SPUM's own components stay — they drive the idle animation.
            string type = behaviour.GetType().Name;
            if (type.StartsWith("SPUM") || type == "SpritePos") continue;

            Destroy(behaviour);
        }

        foreach (var collider in rig.GetComponentsInChildren<Collider>(true))
            Destroy(collider);
    }

    void OnDestroy()
    {
        // A RenderTexture is not garbage collected — releasing it is the whole reason
        // this component exists rather than a handful of loose GameObjects.
        if (_camera != null) _camera.targetTexture = null;

        if (_texture != null)
        {
            _texture.Release();
            Destroy(_texture);
            _texture = null;
        }

        if (_stage != null) Destroy(_stage);
    }
}
