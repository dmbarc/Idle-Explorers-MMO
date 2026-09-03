using UnityEngine;

/// <summary>
/// A thing thrown at a monster.
///
/// ══ WHAT IT IS AND IS NOT ═════════════════════════════════════════════════════
///
/// It is presentation with a delay. The damage was decided the moment the shot was
/// fired — by the stat block and the weapon, and eventually by the server — and this
/// carries it to the target so the number appears when the arrow lands rather than
/// when the bow twangs. Nothing about the outcome depends on the flight.
///
/// That distinction is what keeps a ranged weapon honest over a network. If arrival
/// decided the hit, a client could decide it too, by moving the arrow.
///
/// ══ WHY IT HOMES ══════════════════════════════════════════════════════════════
///
/// It tracks the target rather than flying to where the target was. A monster that
/// steps aside mid-flight should not dodge a hit that has already been rolled — the
/// server has no idea where anything is standing, so a miss the server did not
/// authorise would be a divergence the client invented. Homing makes the picture
/// agree with the arithmetic.
///
/// A target that dies first is the one exception: the projectile fades where it is,
/// because a hit landing on a corpse looks like a bug even when it is bookkeeping.
///
/// ══ NO PREFAB ═════════════════════════════════════════════════════════════════
///
/// Built in code, like GroundArrow and the water tiles: no asset, no importer
/// settings, no meta file to keep in step. The mesh and material are shared statics,
/// so a volley of three costs three transforms rather than three meshes.
/// </summary>
public class Projectile : MonoBehaviour
{
    /// <summary>Metres per second. Fast enough to feel like a shot, slow enough to see.</summary>
    public const float Speed = 22f;

    /// <summary>
    /// Seconds after which an arrow gives up.
    ///
    /// A projectile whose target is destroyed without dying — a despawn, a scene
    /// change — would otherwise chase a null reference forever, and an idle game
    /// leaves things running for hours.
    /// </summary>
    private const float MaxLifetime = 5f;

    private const float Size = 0.28f;

    private ICombatTarget _target;
    private double            _damage;
    private bool              _wasCrit;
    private float             _bornAt;
    private Vector3           _drift;

    /// <summary>
    /// Launches one projectile carrying an already-rolled hit.
    /// </summary>
    /// <param name="from">Muzzle position.</param>
    /// <param name="target">What it is chasing.</param>
    /// <param name="damage">Decided before launch. This only delivers it.</param>
    /// <param name="wasCrit">Drives the damage number's colour on arrival.</param>
    /// <param name="spread">
    /// Sideways offset for a multi-shot fan, in metres at the muzzle. The Trisong Bow
    /// fires three, and three arrows on identical paths read as one arrow.
    /// </param>
    public static Projectile Launch(Vector3 from, ICombatTarget target,
                                    double damage, bool wasCrit, float spread = 0f)
    {
        if (target == null) return null;

        var go = new GameObject("Projectile");
        go.transform.position = from;

        var filter = go.AddComponent<MeshFilter>();
        filter.sharedMesh = SharedMesh();

        var renderer = go.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = SharedMaterial();
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows    = false;

        var projectile = go.AddComponent<Projectile>();
        projectile._target  = target;
        projectile._damage  = damage;
        projectile._wasCrit = wasCrit;
        projectile._bornAt  = Time.time;

        // Perpendicular to the shot, so a fan spreads across the line of fire rather
        // than along it. Flattened to the ground plane: a volley that fans vertically
        // reads as one arrow with a rendering fault.
        Vector3 toTarget = target.transform.position - from;
        toTarget.y = 0f;

        Vector3 sideways = Vector3.Cross(Vector3.up, toTarget.sqrMagnitude > 0.001f
                                                     ? toTarget.normalized
                                                     : Vector3.forward);
        projectile._drift = sideways * spread;

        return projectile;
    }

    private void Update()
    {
        // Gone, rather than dead: despawned, or the scene changed underneath it.
        if (_target == null) { Destroy(gameObject); return; }

        if (Time.time - _bornAt > MaxLifetime) { Destroy(gameObject); return; }

        // A hit landing on a corpse looks like a bug even when it is bookkeeping.
        if (!_target.IsAlive()) { Destroy(gameObject); return; }

        Vector3 aim = _target.transform.position + Vector3.up * 0.9f;

        // The fan converges as it flies, so three arrows leave the bow apart and
        // arrive together. Scaling the drift by remaining distance does that for
        // free, with no separate convergence to tune.
        float remaining = Vector3.Distance(transform.position, aim);
        Vector3 goal    = aim + _drift * Mathf.Clamp01(remaining / 6f);

        transform.position = Vector3.MoveTowards(transform.position, goal, Speed * Time.deltaTime);

        if (goal != transform.position)
            transform.rotation = Quaternion.LookRotation((goal - transform.position).normalized);

        if (Vector3.Distance(transform.position, aim) > 0.35f) return;

        _target.TakeDamage(_damage, _wasCrit);
        AbilityVFX.Play("impact", transform.position);
        Destroy(gameObject);
    }

    // ── Shared art ────────────────────────────────────────────────────────────

    private static Mesh     _mesh;
    private static Material _material;

    /// <summary>
    /// A stubby four-sided spike pointing down +Z, so LookRotation aims it.
    /// </summary>
    private static Mesh SharedMesh()
    {
        if (_mesh != null) return _mesh;

        float w = Size * 0.25f;
        float l = Size;

        var vertices = new[]
        {
            new Vector3( 0f,  0f,  l),    // 0 tip
            new Vector3(-w,   0f, -l),    // 1
            new Vector3( 0f,  w,  -l),    // 2
            new Vector3( w,   0f, -l),    // 3
            new Vector3( 0f, -w,  -l),    // 4
        };

        var triangles = new[]
        {
            0, 1, 2,
            0, 2, 3,
            0, 3, 4,
            0, 4, 1,
            1, 4, 3,
            1, 3, 2,
        };

        _mesh = new Mesh { name = "Projectile", hideFlags = HideFlags.DontSave };
        _mesh.SetVertices(vertices);
        _mesh.SetTriangles(triangles, 0);
        _mesh.RecalculateNormals();
        _mesh.RecalculateBounds();

        return _mesh;
    }

    /// <summary>
    /// Unlit, for the same reason the target arrow is: a lit projectile disappears
    /// exactly in the maps dark enough for the player to need to see it.
    /// </summary>
    private static Material SharedMaterial()
    {
        if (_material != null) return _material;

        var shader = Shader.Find("Universal Render Pipeline/Unlit")
                  ?? Shader.Find("Unlit/Color")
                  ?? Shader.Find("Sprites/Default");

        _material = new Material(shader) { name = "Projectile", hideFlags = HideFlags.DontSave };

        var colour = new Color(0.98f, 0.88f, 0.55f);

        // URP names it _BaseColor and the built-in pipeline _Color. Setting whichever
        // exists costs nothing and means this does not care which is installed.
        if (_material.HasProperty("_BaseColor")) _material.SetColor("_BaseColor", colour);
        if (_material.HasProperty("_Color"))     _material.SetColor("_Color",     colour);

        return _material;
    }

    /// <summary>
    /// Shared statics survive between play sessions when domain reload is disabled,
    /// and a Mesh from a previous session is a destroyed native object.
    /// </summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        _mesh     = null;
        _material = null;
    }
}
