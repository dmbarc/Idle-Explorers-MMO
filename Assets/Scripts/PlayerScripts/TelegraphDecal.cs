using System.Collections.Generic;
using IdleExplorers.Rules;
using UnityEngine;

/// <summary>
/// The red shape on the ground that says where an attack is about to land.
///
/// ══ THE WIND-UP IS THE MECHANIC ═══════════════════════════════════════════════
///
/// A cleave with no telegraph is unavoidable damage. The same cleave with 1.2
/// seconds of a filling arc is a thing the player beat. That is the entire
/// difference between a boss fight and a damage race, and it is why the telegraph
/// duration is authored beside the damage rather than picked here.
///
/// ══ WHY IT READS THE SAME STRUCT THE HIT TEST DOES ════════════════════════════
///
/// It is built from an AreaShape, which is what AreaShape.Contains resolves. The
/// painted shape and the damaged shape are therefore the same shape by construction.
/// The usual way a telegraphed fight loses trust is two implementations that drift
/// by a few degrees, and a player who is hit outside the marker stops believing any
/// of them.
///
/// ══ NO PREFABS, NO ART ════════════════════════════════════════════════════════
///
/// Generated meshes, like GroundArrow and the water tiles. Four shapes, no importer
/// settings, no meta files, and a new shape is a method rather than an asset.
/// </summary>
public class TelegraphDecal : MonoBehaviour
{
    /// <summary>Sides used to approximate a curve. Enough to read as round at boss scale.</summary>
    private const int Sides = 48;

    /// <summary>Height above the ground. Enough to avoid z-fighting with the tiles.</summary>
    private const float Lift = 0.05f;

    private static readonly Color Warning = new Color(0.95f, 0.25f, 0.20f, 0.28f);
    private static readonly Color Imminent = new Color(1.00f, 0.85f, 0.35f, 0.55f);

    private MeshRenderer _renderer;
    private MaterialPropertyBlock _block;

    private float _bornAt;
    private float _windUp;

    /// <summary>
    /// Paints a shape and fills it over <paramref name="windUpSeconds"/>.
    ///
    /// The decal destroys itself on resolve. It is a warning, not a state: something
    /// else applies the damage, and a decal that outlived its attack would be telling
    /// the player about danger that has already passed.
    /// </summary>
    public static TelegraphDecal Show(AreaShape shape, float windUpSeconds, Transform parent = null)
    {
        var go = new GameObject("Telegraph");
        if (parent != null) go.transform.SetParent(parent, worldPositionStays: true);

        go.transform.position = new Vector3(shape.Origin.X, Lift, shape.Origin.Z);

        var filter = go.AddComponent<MeshFilter>();
        filter.sharedMesh = BuildMesh(shape);

        var renderer = go.AddComponent<MeshRenderer>();
        renderer.sharedMaterial    = SharedMaterial();
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows    = false;

        var decal = go.AddComponent<TelegraphDecal>();
        decal._renderer = renderer;
        decal._block    = new MaterialPropertyBlock();
        decal._bornAt   = Time.time;
        decal._windUp   = Mathf.Max(0.05f, windUpSeconds);

        return decal;
    }

    private void Update()
    {
        float progress = Mathf.Clamp01((Time.time - _bornAt) / _windUp);

        // Warning to imminent as it fills. A single flat colour reads as decoration;
        // a colour that changes reads as a countdown, which is what it is.
        Color colour = Color.Lerp(Warning, Imminent, progress);

        // Pulsing faster as it closes, so the last half second is unmistakeable even
        // in peripheral vision -- which is where it will be, because the player is
        // looking at the boss.
        colour.a *= 0.75f + 0.25f * Mathf.Sin(Time.time * Mathf.Lerp(6f, 22f, progress));

        _block.SetColor(ColourProperty, colour);
        _renderer.SetPropertyBlock(_block);

        if (progress >= 1f) Destroy(gameObject);
    }

    // ── Meshes ────────────────────────────────────────────────────────────────

    /// <summary>
    /// A flat mesh matching the shape, in world space around its own origin.
    ///
    /// Not shared between decals: a cone and a ring are different meshes, and even
    /// two cones differ by range and arc. Cheap enough -- a boss shows at most a
    /// handful at once, each a few dozen triangles.
    /// </summary>
    private static Mesh BuildMesh(AreaShape shape)
    {
        return shape.Kind switch
        {
            AreaKind.Circle => Fan(0f, shape.Range, 0f, 360f),
            AreaKind.Ring   => Band(shape.InnerRadius, shape.Range, 0f, 360f),
            AreaKind.Cone   => Fan(0f, shape.Range, Bearing(shape.Facing), shape.ArcDegrees),
            AreaKind.Line   => Rectangle(shape.Range, shape.HalfWidth, Bearing(shape.Facing)),
            _               => Fan(0f, AreaShape.SinglePointTolerance * 4f, 0f, 360f),
        };
    }

    /// <summary>Compass bearing of a facing, in degrees, so meshes can be built rotated.</summary>
    private static float Bearing(Ground facing) =>
        Mathf.Atan2(facing.X, facing.Z) * Mathf.Rad2Deg;

    /// <summary>A filled wedge: a circle when the arc is 360.</summary>
    private static Mesh Fan(float _, float radius, float centreDegrees, float arcDegrees)
    {
        var vertices  = new List<Vector3> { Vector3.zero };
        var triangles = new List<int>();

        float arc   = Mathf.Clamp(arcDegrees, 0f, 360f);
        float start = centreDegrees - arc * 0.5f;

        // At least two edge points even for a zero arc, or there is no triangle at
        // all and the decal is invisible -- which for a zero-arc cone is exactly
        // wrong, since it still hits things directly ahead.
        int segments = Mathf.Max(2, Mathf.CeilToInt(Sides * arc / 360f));

        for (int i = 0; i <= segments; i++)
        {
            float degrees = start + arc * (i / (float)segments);
            float radians = degrees * Mathf.Deg2Rad;

            vertices.Add(new Vector3(Mathf.Sin(radians) * radius, 0f, Mathf.Cos(radians) * radius));
        }

        for (int i = 1; i < vertices.Count - 1; i++)
        {
            triangles.Add(0);
            triangles.Add(i);
            triangles.Add(i + 1);
        }

        return Finish("TelegraphFan", vertices, triangles);
    }

    /// <summary>An annulus: the shockwave, with its hole.</summary>
    private static Mesh Band(float inner, float outer, float centreDegrees, float arcDegrees)
    {
        var vertices  = new List<Vector3>();
        var triangles = new List<int>();

        float arc   = Mathf.Clamp(arcDegrees, 0f, 360f);
        float start = centreDegrees - arc * 0.5f;

        int segments = Mathf.Max(2, Mathf.CeilToInt(Sides * arc / 360f));

        for (int i = 0; i <= segments; i++)
        {
            float radians = (start + arc * (i / (float)segments)) * Mathf.Deg2Rad;
            float sin = Mathf.Sin(radians), cos = Mathf.Cos(radians);

            vertices.Add(new Vector3(sin * inner, 0f, cos * inner));
            vertices.Add(new Vector3(sin * outer, 0f, cos * outer));
        }

        for (int i = 0; i < segments; i++)
        {
            int a = i * 2, b = a + 1, c = a + 2, d = a + 3;

            triangles.Add(a); triangles.Add(b); triangles.Add(c);
            triangles.Add(c); triangles.Add(b); triangles.Add(d);
        }

        return Finish("TelegraphBand", vertices, triangles);
    }

    /// <summary>The charge lane: constant width from the caster to its full reach.</summary>
    private static Mesh Rectangle(float length, float halfWidth, float bearingDegrees)
    {
        float radians = bearingDegrees * Mathf.Deg2Rad;

        var forward = new Vector3(Mathf.Sin(radians), 0f, Mathf.Cos(radians));
        var side    = new Vector3(forward.z, 0f, -forward.x) * halfWidth;

        var vertices = new List<Vector3>
        {
            -side,
             side,
            forward * length - side,
            forward * length + side,
        };

        var triangles = new List<int> { 0, 2, 1, 1, 2, 3 };

        return Finish("TelegraphLine", vertices, triangles);
    }

    private static Mesh Finish(string name, List<Vector3> vertices, List<int> triangles)
    {
        var mesh = new Mesh { name = name, hideFlags = HideFlags.DontSave };

        mesh.SetVertices(vertices);
        mesh.SetTriangles(triangles, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();

        return mesh;
    }

    // ── Material ──────────────────────────────────────────────────────────────

    private static Material _material;
    private static int _colourProperty = -1;

    /// <summary>
    /// URP calls it _BaseColor and the built-in pipeline _Color.
    ///
    /// Resolved once against the shader that was actually found, rather than setting
    /// both: a MaterialPropertyBlock with a property the shader does not have is
    /// silently ignored, and "the telegraph is invisible" would be the symptom.
    /// </summary>
    private static int ColourProperty
    {
        get
        {
            if (_colourProperty < 0) SharedMaterial();
            return _colourProperty;
        }
    }

    private static Material SharedMaterial()
    {
        if (_material != null) return _material;

        var shader = Shader.Find("Universal Render Pipeline/Unlit")
                  ?? Shader.Find("Unlit/Color")
                  ?? Shader.Find("Sprites/Default");

        _material = new Material(shader) { name = "Telegraph", hideFlags = HideFlags.DontSave };

        // Transparent, and NOT writing depth: several telegraphs overlap during a
        // phase change, and depth-writing transparents flicker against each other.
        _material.SetFloat("_Surface", 1f);
        _material.SetFloat("_ZWrite", 0f);
        _material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;

        _colourProperty = _material.HasProperty("_BaseColor")
            ? Shader.PropertyToID("_BaseColor")
            : Shader.PropertyToID("_Color");

        return _material;
    }

    /// <summary>
    /// Statics survive a play session when domain reload is disabled, and a Material
    /// from the previous one is a destroyed native object.
    /// </summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        _material = null;
        _colourProperty = -1;
    }
}
