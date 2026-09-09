using UnityEngine;

/// <summary>
/// Keeps a world-space object facing the camera — health bars, node name tags,
/// floating damage, dropped loot, and the sprite rigs of the characters themselves.
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

    // ── Attaching to a character rig ──────────────────────────────────────────

    /// <summary>
    /// The node inside a SPUM prefab that holds the artwork. Everything the character
    /// is made of hangs off it; the prefab root above it carries the gameplay
    /// components.
    /// </summary>
    private const string ArtRootName = "UnitRoot";

    /// <summary>
    /// Makes a character rig face the camera, and returns the transform now doing it.
    ///
    /// ══ WHY CHARACTERS WERE SOMETIMES EDGE-ON ═════════════════════════════════
    ///
    /// A SPUM character is flat artwork standing in a 3D world, and it faced whatever
    /// direction its transform did. The NavMeshAgent rotates that transform to face
    /// travel — so walking toward or away from the camera looked correct, and walking
    /// across it turned the character into a vertical line. It was never a rendering
    /// fault; the sprite was being shown exactly side-on, which is what a plane with
    /// no thickness looks like from the side.
    ///
    /// This is deliberately attached to the ART subtree rather than the prefab root.
    /// The root carries the agent and the collider, and a monster's root carries both
    /// AND the artwork — so billboarding the root would make transform.forward mean
    /// "toward the camera" for code that reasonably expects it to mean "the way this
    /// character is facing". The agent keeps steering; only the picture turns.
    ///
    /// Falls back to the root when a rig has no UnitRoot, and again if UnitRoot turns
    /// out not to contain every sprite — half a character facing the camera is worse
    /// than none of it, and is precisely the "not ALWAYS facing the camera" this fixes.
    /// </summary>
    public static Billboard CoverArt(GameObject rigRoot)
    {
        if (rigRoot == null) return null;

        int total = rigRoot.GetComponentsInChildren<SpriteRenderer>(true).Length;
        if (total == 0)
        {
            Debug.LogWarning($"[Billboard] '{rigRoot.name}' has no SpriteRenderer anywhere — " +
                             "nothing to turn toward the camera. Is this a SPUM rig?");
            return null;
        }

        var art = SpumRig.FindDeep(rigRoot.transform, ArtRootName);

        // Explicit null comparison, never `??`. UnityEngine.Object overloads ==, and
        // `??` cannot see that overload — this is the same operator that has already
        // cost this project a MissingComponentException it took a while to read.
        if (art == null)
        {
            art = rigRoot.transform;
        }
        else if (art.GetComponentsInChildren<SpriteRenderer>(true).Length < total)
        {
            Debug.LogWarning($"[Billboard] '{rigRoot.name}': {ArtRootName} holds only some of the " +
                             $"{total} sprite(s), so the whole rig is being turned instead.");
            art = rigRoot.transform;
        }

        var existing = art.GetComponent<Billboard>();
        if (existing != null) return existing;

        var added = art.gameObject.AddComponent<Billboard>();
        if (added == null)
            Debug.LogError($"[Billboard] Could not add a Billboard to '{art.name}'.");

        return added;
    }

    /// <summary>
    /// Every SpriteRenderer under <paramref name="root"/> that no Billboard turns.
    ///
    /// The prefab builders assert this is zero. A sprite nobody billboards does not
    /// fail, warn or look broken from the front — it simply disappears from certain
    /// angles, which reads as a flickering art bug rather than a missing component.
    /// </summary>
    public static int CountUnbillboardedSprites(GameObject root)
    {
        if (root == null) return 0;

        int uncovered = 0;

        foreach (var renderer in root.GetComponentsInChildren<SpriteRenderer>(true))
        {
            if (renderer == null) continue;
            if (renderer.GetComponentInParent<Billboard>(includeInactive: true) == null) uncovered++;
        }

        return uncovered;
    }
}
