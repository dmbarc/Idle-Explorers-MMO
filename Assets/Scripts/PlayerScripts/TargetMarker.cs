using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// How the game shows what the player has just told the character to go and do.
///
/// There used to be one answer to three different questions. Clicking the ground,
/// clicking a goblin and clicking the anvil all dropped the same green cylinder, and
/// for the latter two it was parented to the thing so it hung in the middle of it —
/// a floating capsule with no relationship to what had been selected.
///
/// Three questions, three answers:
///
///   • A point on the ground has nothing to highlight, so it gets a marker: an arrow
///     bouncing above the spot, pointing down at it.
///   • A monster is a sprite, and the sprite itself is the thing that should look
///     picked. See <see cref="TargetHighlight"/>.
///   • A skilling station is a 3D model, and the same is true of it — so the same
///     highlight covers both, tinting whichever kind of renderer it finds.
/// </summary>
public class GroundArrow : MonoBehaviour
{
    // ── Shape ─────────────────────────────────────────────────────────────────

    // ══ TWICE THE SIZE IT WAS ═════════════════════════════════════════════════
    //
    // The marker is the only thing on screen that says WHICH tree, rock or goblin the
    // character is working on, and at the old size it was a thumbnail-sized cone on a
    // map seen from thirty units up. Doubled on every axis, so it reads at the camera
    // distance the game is actually played at rather than the one it was authored at.
    private const int   Sides       = 8;
    private const float HeadRadius  = 0.84f;
    private const float HeadHeight  = 1.24f;
    private const float ShaftRadius = 0.28f;
    private const float ShaftHeight = 1.24f;

    /// <summary>How far the tip floats above the point at the bottom of the bounce.</summary>
    private const float HoverHeight = 0.35f;

    private const float BounceHeight  = 0.45f;
    private const float BouncesPerSec = 1.3f;
    private const float SpinDegPerSec = 55f;

    private static readonly Color ArrowColor = new Color(1f, 0.83f, 0.29f);

    /// <summary>
    /// One mesh for every arrow ever built. Marked DontSave so entering and leaving
    /// play mode does not leave a mesh asset behind, and rebuilt if Unity does
    /// collect it between scene loads.
    /// </summary>
    private static Mesh _sharedMesh;

    private Vector3 _groundPoint;

    /// <summary>Builds an arrow and points it at a spot on the ground.</summary>
    public static GroundArrow ShowAt(Vector3 groundPoint)
    {
        var go = new GameObject("TargetArrow");

        var filter = go.AddComponent<MeshFilter>();
        filter.sharedMesh = SharedMesh();

        var renderer = go.AddComponent<MeshRenderer>();
        renderer.sharedMaterial   = ArrowMaterial();
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows    = false;

        // No collider, deliberately. Click-to-move raycasts against world geometry,
        // and a marker that could be clicked would let the player target the thing
        // telling them where they had already clicked.
        var arrow = go.AddComponent<GroundArrow>();
        arrow.MoveTo(groundPoint);
        return arrow;
    }

    public void MoveTo(Vector3 groundPoint)
    {
        _groundPoint = groundPoint;
        transform.position = groundPoint + Vector3.up * (HoverHeight + BounceHeight);
    }

    void Update()
    {
        // Sine over [0,1] rather than [-1,1], so the bottom of the bounce is the
        // hover height and the arrow never dips into the ground it is pointing at.
        float phase  = Mathf.Sin(Time.time * BouncesPerSec * Mathf.PI * 2f) * 0.5f + 0.5f;
        float height = HoverHeight + BounceHeight * phase;

        transform.position = _groundPoint + Vector3.up * height;
        transform.Rotate(Vector3.up, SpinDegPerSec * Time.deltaTime, Space.World);
    }

    // ── Mesh ──────────────────────────────────────────────────────────────────

    private static Mesh SharedMesh()
    {
        if (_sharedMesh != null) return _sharedMesh;

        _sharedMesh = BuildArrowMesh();
        _sharedMesh.hideFlags = HideFlags.DontSave;
        return _sharedMesh;
    }

    /// <summary>
    /// A downward arrow whose tip sits at the object's own origin.
    ///
    /// Generated rather than imported so the marker needs no asset, no importer
    /// settings and no meta file — and so the tip is exactly at the origin, which is
    /// what lets MoveTo treat the transform position as "the height of the point
    /// above the spot" instead of an offset somebody has to remember.
    ///
    /// Unity treats a triangle as front-facing when its winding is clockwise from the
    /// viewer, which for these rings means (bottom[i], top[i], bottom[i+1]) —
    /// getting it backwards produces an arrow that is invisible from outside and
    /// perfectly solid from within.
    /// </summary>
    private static Mesh BuildArrowMesh()
    {
        var vertices  = new List<Vector3>();
        var triangles = new List<int>();

        // 0: the tip, at the origin, pointing down.
        vertices.Add(Vector3.zero);

        int headRing  = vertices.Count;
        AddRing(vertices, HeadRadius,  HeadHeight);

        int shaftLow  = vertices.Count;
        AddRing(vertices, ShaftRadius, HeadHeight);

        int shaftHigh = vertices.Count;
        AddRing(vertices, ShaftRadius, HeadHeight + ShaftHeight);

        int capCentre = vertices.Count;
        vertices.Add(new Vector3(0f, HeadHeight + ShaftHeight, 0f));

        for (int i = 0; i < Sides; i++)
        {
            int next = (i + 1) % Sides;

            // Cone: the tip stands in for the collapsed lower ring.
            triangles.Add(0);
            triangles.Add(headRing + i);
            triangles.Add(headRing + next);

            // The flat top of the head, from the wide ring in to the shaft.
            triangles.Add(shaftLow + i);
            triangles.Add(headRing + next);
            triangles.Add(headRing + i);

            triangles.Add(shaftLow + i);
            triangles.Add(shaftLow + next);
            triangles.Add(headRing + next);

            // Shaft.
            triangles.Add(shaftLow  + i);
            triangles.Add(shaftHigh + i);
            triangles.Add(shaftLow  + next);

            triangles.Add(shaftLow  + next);
            triangles.Add(shaftHigh + i);
            triangles.Add(shaftHigh + next);

            // Cap, so the arrow is closed when seen from above — which, in a camera
            // pitched fifty degrees down, is most of the time.
            triangles.Add(capCentre);
            triangles.Add(shaftHigh + next);
            triangles.Add(shaftHigh + i);
        }

        var mesh = new Mesh { name = "TargetArrow" };
        mesh.SetVertices(vertices);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }

    private static void AddRing(List<Vector3> into, float radius, float y)
    {
        for (int i = 0; i < Sides; i++)
        {
            float angle = i / (float)Sides * Mathf.PI * 2f;
            into.Add(new Vector3(Mathf.Cos(angle) * radius, y, Mathf.Sin(angle) * radius));
        }
    }

    private static Material _material;

    /// <summary>
    /// Unlit, so the marker reads the same in the Hollow's gloom as in open daylight.
    /// A lit marker is a marker that disappears exactly when the map is dark enough
    /// for the player to need it.
    /// </summary>
    private static Material ArrowMaterial()
    {
        if (_material != null) return _material;

        var shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (shader == null) shader = Shader.Find("Unlit/Color");
        if (shader == null) shader = Shader.Find("Sprites/Default");

        _material = new Material(shader) { name = "TargetArrow", hideFlags = HideFlags.DontSave };

        // URP names it _BaseColor, the built-in pipeline _Color. Setting whichever
        // exists costs nothing and means this does not depend on which is installed.
        if (_material.HasProperty("_BaseColor")) _material.SetColor("_BaseColor", ArrowColor);
        if (_material.HasProperty("_Color"))     _material.SetColor("_Color",     ArrowColor);

        return _material;
    }
}

/// <summary>
/// Makes a targeted thing look targeted, by pulsing its own renderers toward a warm
/// gold rather than putting an object next to it.
///
/// It handles both kinds of target because the player does not distinguish them: a
/// goblin is a stack of thirty SpriteRenderers and the anvil is a MeshRenderer with a
/// Kenney atlas on it, and "the thing I clicked is lit up" has to mean the same in
/// both cases.
///
/// Everything it changes, it changes reversibly:
///
///   • Sprites keep their colour cached and have it written back. Monsters are tinted
///     at prefab-build time — the bramblekin is a green elf — so overwriting the
///     colour outright would strip the tint and put it back as white.
///   • Meshes are tinted through a MaterialPropertyBlock, never through
///     renderer.material. Touching .material instantiates a private copy of a shared
///     material per renderer, which is a leak that only shows up as memory.
///
/// Restores on disable as well as on Remove, so a monster that dies mid-highlight
/// does not need anybody to have remembered it was lit.
/// </summary>
public class TargetHighlight : MonoBehaviour
{
    private static readonly Color HighlightTint = new Color(1f, 0.87f, 0.42f);

    private const float PulsesPerSecond = 1.6f;
    private const float MinBlend        = 0.20f;
    private const float MaxBlend        = 0.65f;

    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    private static readonly int ColorId     = Shader.PropertyToID("_Color");

    private SpriteRenderer[] _sprites;
    private Color[]          _spriteColors;

    private Renderer[] _meshes;
    private Color[][]  _meshColors;

    private MaterialPropertyBlock _block;
    private bool _captured;

    /// <summary>
    /// Lights up a target. Idempotent — asking twice returns the highlight already
    /// running rather than capturing a second, already-tinted set of originals, which
    /// would bake the tint in permanently.
    /// </summary>
    public static TargetHighlight Apply(GameObject target)
    {
        if (target == null) return null;

        var existing = target.GetComponent<TargetHighlight>();
        if (existing != null) return existing;

        return target.AddComponent<TargetHighlight>();
    }

    /// <summary>Puts everything back and removes itself.</summary>
    public void Remove()
    {
        Restore();
        Destroy(this);
    }

    void OnEnable()  => Capture();
    void OnDisable() => Restore();

    void LateUpdate()
    {
        // After the animator, which drives the sprite swaps on a SPUM rig. Tinting in
        // Update would be overwritten on any frame the animation changed a sprite.
        float wave  = Mathf.Sin(Time.time * PulsesPerSecond * Mathf.PI * 2f) * 0.5f + 0.5f;
        Paint(Mathf.Lerp(MinBlend, MaxBlend, wave));
    }

    // ── Capture and restore ───────────────────────────────────────────────────

    private void Capture()
    {
        if (_captured) return;

        var sprites = GetComponentsInChildren<SpriteRenderer>(true);
        _sprites      = sprites;
        _spriteColors = new Color[sprites.Length];
        for (int i = 0; i < sprites.Length; i++)
            _spriteColors[i] = sprites[i] != null ? sprites[i].color : Color.white;

        var meshes = new List<Renderer>();
        foreach (var renderer in GetComponentsInChildren<Renderer>(true))
        {
            if (renderer == null || renderer is SpriteRenderer) continue;
            if (renderer is ParticleSystemRenderer) continue;   // its own colour over time
            meshes.Add(renderer);
        }

        _meshes     = meshes.ToArray();
        _meshColors = new Color[_meshes.Length][];

        for (int i = 0; i < _meshes.Length; i++)
        {
            var materials = _meshes[i].sharedMaterials;
            var colors    = new Color[materials.Length];

            for (int m = 0; m < materials.Length; m++) colors[m] = ColorOf(materials[m]);

            _meshColors[i] = colors;
        }

        _block    = new MaterialPropertyBlock();
        _captured = true;
    }

    private void Paint(float blend)
    {
        if (!_captured) return;

        for (int i = 0; i < _sprites.Length; i++)
        {
            if (_sprites[i] == null) continue;
            _sprites[i].color = Color.Lerp(_spriteColors[i], HighlightTint, blend);
        }

        for (int i = 0; i < _meshes.Length; i++)
        {
            var renderer = _meshes[i];
            if (renderer == null) continue;

            var colors = _meshColors[i];
            for (int m = 0; m < colors.Length; m++)
            {
                var lit = Color.Lerp(colors[m], HighlightTint, blend);

                renderer.GetPropertyBlock(_block, m);
                _block.SetColor(BaseColorId, lit);
                _block.SetColor(ColorId,     lit);
                renderer.SetPropertyBlock(_block, m);
            }
        }
    }

    private void Restore()
    {
        if (!_captured) return;

        for (int i = 0; i < _sprites.Length; i++)
        {
            if (_sprites[i] == null) continue;
            _sprites[i].color = _spriteColors[i];
        }

        for (int i = 0; i < _meshes.Length; i++)
        {
            var renderer = _meshes[i];
            if (renderer == null) continue;

            // Clearing the override entirely, rather than writing the original colour
            // back into the block. A block that merely holds the right value still
            // breaks batching for the rest of the object's life.
            for (int m = 0; m < _meshColors[i].Length; m++)
                renderer.SetPropertyBlock(null, m);
        }

        _captured = false;
    }

    private static Color ColorOf(Material material)
    {
        if (material == null) return Color.white;
        if (material.HasProperty(BaseColorId)) return material.GetColor(BaseColorId);
        if (material.HasProperty(ColorId))     return material.GetColor(ColorId);
        return Color.white;
    }
}
