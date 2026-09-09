using TMPro;
using UnityEngine;

/// <summary>
/// The way out of a room you were let into.
///
/// ══ WHY IT DOES NOT EXIST UNTIL IT IS EARNED ══════════════════════════════════
///
/// The throne is a one-way door. You get in by killing a thousand goblins, and the
/// only ways out were dying or opening the travel list — neither of which reads as
/// FINISHING the fight. A player who beats the King and then opens a menu to leave
/// has been told, by the interface, that nothing happened.
///
/// So the exit is a reward: it is not in the scene, it is not hidden, it is not
/// disabled. It does not exist, and then the King falls and it does.
///
/// ══ WHY IT IS BUILT IN CODE RATHER THAN PLACED ════════════════════════════════
///
/// Same reason as NamePlate, DamageNumber and the boss portal's beacon: the arena is
/// GENERATED from a recipe, and anything hand-placed in that scene is deleted the
/// next time somebody rebuilds it. A portal the fight spawns cannot be lost that way,
/// and needs no scene edit to ship.
///
/// ══ WHY IT ASKS NOTHING OF THE SERVER ═════════════════════════════════════════
///
/// Unlike BossPortalController, which is a GATE and therefore has to be told by the
/// server whether it may open. Leaving is not a privilege: nothing is granted by
/// walking through it, and a player who somehow conjured one has cheated their way
/// into the goblin camp. Making it a round trip would add a way for the exit to
/// fail, which is the one thing it must never do.
/// </summary>
public class ExitPortal : MonoBehaviour
{
    /// <summary>Where it goes. Empty is treated as the starting map.</summary>
    public string destinationMapId = GameManager.StartingMapId;

    /// <summary>
    /// Green, and deliberately not the boss portal's violet.
    ///
    /// The two are the same object to a player — a column of light you walk into — so
    /// the only thing telling them apart is the colour. One is the way in and one is
    /// the way out, and confusing them means walking back into the arena.
    /// </summary>
    private static readonly Color BeaconColor = new(0.42f, 1f, 0.58f, 0.40f);

    /// <summary>Tall enough to be seen from the far side of the arena.</summary>
    private const float BeaconHeight = 12f;

    /// <summary>
    /// Opens an exit at a point on the ground, replacing any already open.
    ///
    /// Returns the portal so a caller can move or relabel it.
    /// </summary>
    public static ExitPortal Open(Vector3 where, string destinationMapId, string label)
    {
        // One at a time. A fight that somehow reported ending twice would otherwise
        // leave two columns of light standing in the same spot.
        foreach (var existing in Object.FindObjectsByType<ExitPortal>(FindObjectsInactive.Exclude))
            if (existing != null) Destroy(existing.gameObject);

        var go = new GameObject("ExitPortal");
        go.transform.position = where;

        var portal = go.AddComponent<ExitPortal>();

        if (!string.IsNullOrEmpty(destinationMapId)) portal.destinationMapId = destinationMapId;

        portal.Build(label);
        return portal;
    }

    private void Build(string label)
    {
        // ══ SOMETHING TO CLICK, AND TO WALK INTO ══════════════════════════════
        //
        // A trigger, not a solid: the player walks INTO an exit, and a collider that
        // pushed them back out of it would be a door that shoves.
        var body = gameObject.AddComponent<CapsuleCollider>();

        body.isTrigger = true;
        body.radius    = 1.4f;
        body.height    = 4f;
        body.center    = Vector3.up * 2f;

        BuildBeam();
        BuildLabel(label);

        // Said out loud as well. The portal opens behind a player who is looking at
        // the corpse, and a column of light that appears off-camera is a column of
        // light nobody saw appear.
        GameEvents.FireToast("A way out opens.", ChatTone.Good);
    }

    private void BuildBeam()
    {
        var beam = GameObject.CreatePrimitive(PrimitiveType.Cylinder);

        beam.name = "ExitBeam";
        beam.transform.SetParent(transform, false);
        beam.transform.localPosition = Vector3.up * BeaconHeight * 0.5f;
        beam.transform.localScale    = new Vector3(0.9f, BeaconHeight * 0.5f, 0.9f);

        // The beam is scenery. The capsule above is what takes the click, and a second
        // collider here would swallow half of them.
        Destroy(beam.GetComponent<Collider>());

        // ══ THE CHAIN ENDS SOMEWHERE THAT ALWAYS SHIPS ════════════════════════
        //
        // Copied from BossPortalController, which shipped a hundred-metre magenta
        // column because Shader.Find returned null twice and the primitive kept its
        // built-in default material — and built-in shaders are not in a URP build.
        // Sprites/Default is always there, and the material is assigned
        // unconditionally rather than only when a shader was found.
        Shader unlit = Shader.Find("Universal Render Pipeline/Unlit")
                    ?? Shader.Find("Unlit/Color")
                    ?? Shader.Find("Sprites/Default");

        var beamRenderer = beam.GetComponent<Renderer>();

        beamRenderer.material = new Material(unlit) { name = "ExitBeam" };
        beamRenderer.material.color = BeaconColor;
        beamRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

        var glow = new GameObject("ExitGlow");

        glow.transform.SetParent(transform, false);
        glow.transform.localPosition = Vector3.up * 1.5f;

        var point = glow.AddComponent<Light>();

        point.type      = LightType.Point;
        point.color     = BeaconColor;
        point.range     = 14f;
        point.intensity = 3.5f;
    }

    private void BuildLabel(string text)
    {
        var go = new GameObject("ExitLabel");

        go.transform.SetParent(transform, false);
        go.transform.localPosition = Vector3.up * 4.2f;

        var label = go.AddComponent<TextMeshPro>();

        label.fontSize  = 3.2f;
        label.alignment = TextAlignmentOptions.Center;
        label.color     = new Color(0.72f, 1f, 0.80f);
        label.text      = "<mark=#00000059>" + (string.IsNullOrEmpty(text) ? "Leave" : text) + "</mark>";

        // fontMaterial rather than fontSharedMaterial: the shared one is every piece
        // of text in the game that uses this font.
        Material material = label.fontMaterial;

        material.EnableKeyword(ShaderUtilities.Keyword_Outline);
        material.SetFloat(ShaderUtilities.ID_OutlineWidth, 0.25f);
        material.SetColor(ShaderUtilities.ID_OutlineColor, Color.black);

        label.GetComponent<RectTransform>().sizeDelta = new Vector2(10f, 1.4f);

        // Parented to the portal ROOT, which nothing mirrors. Under an art subtree a
        // label turns into mirror writing the first time SpriteFacing flips it.
        go.AddComponent<Billboard>();
    }

    /// <summary>
    /// Walks through. Called by a click on the portal and by walking into it.
    ///
    /// Guarded against running twice, because both of those can happen in the same
    /// frame — a player who clicks the portal they are already standing in.
    /// </summary>
    public void Enter()
    {
        if (_entered) return;

        _entered = true;

        GameManager.Zone?.EnterMap(string.IsNullOrEmpty(destinationMapId)
                                       ? GameManager.StartingMapId
                                       : destinationMapId);
    }

    private bool _entered;

    private void OnTriggerEnter(Collider other)
    {
        if (other != null && other.CompareTag("Player")) Enter();
    }
}
