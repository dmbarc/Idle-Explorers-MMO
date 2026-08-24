using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Finding and reading the sprite layers of a SPUM character rig.
///
/// Extracted from CharacterAppearance because two systems now need it: equipment
/// draws onto helmet/chest/boot layers, and the appearance editor draws onto the
/// body/hair/eye/weapon layers underneath. Duplicating the traversal would mean
/// duplicating the three things about this rig that are not obvious:
///
///   1. **Part names repeat.** There are two P_Shoulder transforms (one per arm), two
///      P_Arm, two P_Weapon and two P_Shield. A find-first lookup dresses one side and
///      leaves the other bare, so callers get every match and disambiguate by ancestor.
///   2. **Parts nest.** P_LCloth (the left legguard) is a CHILD of P_LFoot, so a plain
///      GetComponentsInChildren on the boot part sweeps up the leg layer too — boots
///      would silently overwrite legguards.
///   3. **Renderer names are not unique.** The eyes' renderers are called "Front" and
///      "Back", and the cape's renderer is ALSO called "Back". Only a part-plus-ancestor
///      address is unambiguous.
/// </summary>
public static class SpumRig
{
    /// <summary>One renderer belonging to a part, and which half of a sheet it wants.</summary>
    public class Layer
    {
        public SpriteRenderer Renderer;

        /// <summary>"Left", "Right" or "Body" — the sub-sprite name for a Multiple sheet.</summary>
        public string Side;

        /// <summary>What the rig shipped here, captured before anything overwrote it.</summary>
        public Sprite OriginalSprite;
        public bool   OriginalEnabled;

        /// <summary>True when the rig shipped art here — the layer it actually draws.</summary>
        public bool WasDrawn => OriginalSprite != null;
    }

    /// <summary>
    /// Every renderer belonging to a named part.
    ///
    /// <paramref name="requiredAncestor"/> disambiguates repeated part names: pass
    /// "P_LArm" to get the left arm's P_Arm rather than the right one. Null accepts
    /// any match, which is what you want for parts that appear once.
    /// </summary>
    public static List<Layer> Collect(Transform root, string partName, string requiredAncestor = null)
    {
        var layers = new List<Layer>();
        if (root == null || string.IsNullOrEmpty(partName)) return layers;

        var parts = new List<Transform>();
        FindAllDeep(root, partName, parts);

        // A part name that carries its own side (P_LFoot) beats a child's name; one
        // that does not (P_Shoulder) falls through to the child.
        string partSide = SideFromName(partName);

        foreach (var part in parts)
        {
            if (requiredAncestor != null && !HasAncestor(part, requiredAncestor)) continue;
            Walk(part, partSide, layers, isRoot: true);
        }
        return layers;
    }

    private static void Walk(Transform node, string partSide, List<Layer> into, bool isRoot)
    {
        // A nested part belongs to a different slot. Descending into it is how boots
        // end up drawing over legguards.
        if (!isRoot && node.name.StartsWith("P_", System.StringComparison.Ordinal)) return;

        var renderer = node.GetComponent<SpriteRenderer>();
        if (renderer != null)
        {
            into.Add(new Layer
            {
                Renderer        = renderer,
                OriginalSprite  = renderer.sprite,
                OriginalEnabled = renderer.enabled,
                Side            = partSide ?? SideFromName(node.name) ?? "Body",
            });
        }

        foreach (Transform child in node)
            Walk(child, partSide, into, isRoot: false);
    }

    private static bool HasAncestor(Transform node, string ancestorName)
    {
        for (var t = node.parent; t != null; t = t.parent)
            if (t.name == ancestorName) return true;
        return false;
    }

    /// <summary>Depth-first search by exact name. First match.</summary>
    public static Transform FindDeep(Transform root, string targetName)
    {
        if (root == null) return null;
        if (root.name == targetName) return root;

        foreach (Transform child in root)
        {
            var found = FindDeep(child, targetName);
            if (found != null) return found;
        }
        return null;
    }

    /// <summary>Every transform with the name — P_Shoulder appears once per arm.</summary>
    public static void FindAllDeep(Transform root, string targetName, List<Transform> into)
    {
        if (root == null) return;
        if (root.name == targetName) into.Add(root);

        foreach (Transform child in root)
            FindAllDeep(child, targetName, into);
    }

    /// <summary>
    /// "Left", "Right" or null, from a rig transform name.
    ///
    /// Deliberately narrow: it matches the L/R marker SPUM uses (P_LFoot, _3L_Foot,
    /// 25_L_Shoulder) and nothing else. A looser test would read the "l" in "Helmet"
    /// or "Cloth" and hand every layer a side it does not have.
    /// </summary>
    public static string SideFromName(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;

        for (int i = 0; i < name.Length - 1; i++)
        {
            char c = name[i];
            if (c != 'L' && c != 'R') continue;

            // Preceded by a separator, a digit, or nothing; followed by a capital or
            // an underscore. That is exactly SPUM's convention and not much else.
            bool startOk = i == 0 || name[i - 1] == '_' || name[i - 1] == '-' || char.IsDigit(name[i - 1]);
            char next    = name[i + 1];
            bool endOk   = next == '_' || char.IsUpper(next);

            if (startOk && endOk) return c == 'L' ? "Left" : "Right";
        }
        return null;
    }

    /// <summary>
    /// Writes a sprite to a layer and makes sure it can actually be seen.
    ///
    /// `enabled = true` does nothing for a renderer on a deactivated GameObject, and a
    /// silently-inactive layer looks exactly like a failed sprite load — which is a
    /// bug this project has already paid for once.
    ///
    /// The maskInteraction reset is the third way this rig has found to draw nothing
    /// without reporting anything. SPUM ships P_Hair's renderer (7_Hair) set to
    /// VisibleInsideMask, and the only SpriteMask in the hierarchy — on 5_Head — has
    /// no sprite, so it masks zero pixels. A renderer visible only inside a mask that
    /// covers nothing is invisible everywhere, with the sprite correctly assigned and
    /// enabled true. That is why hair could be chosen but never seen, on the class
    /// cards, in the appearance editor and on the character in the world alike.
    ///
    /// Cleared here rather than in the prefab because this is the one place every
    /// appearance and equipment write already passes through, so it holds for whichever
    /// rig a future character uses. Safe because no SpriteMask in the rig has a sprite:
    /// nothing is masking anything today.
    /// </summary>
    public static void Show(Layer layer, Sprite sprite)
    {
        if (layer?.Renderer == null || sprite == null) return;

        layer.Renderer.sprite          = sprite;
        layer.Renderer.enabled         = true;
        layer.Renderer.maskInteraction = SpriteMaskInteraction.None;

        if (!layer.Renderer.gameObject.activeSelf)
            layer.Renderer.gameObject.SetActive(true);
    }

    // ── Size ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// How tall a person is in this world, in world units.
    ///
    /// Not an arbitrary choice — it is the NavMeshAgent height the project has always
    /// used, which is also what the camera distance, the walk speed and the Hollow's
    /// four-unit floor tiles were all tuned against. The one thing that never agreed
    /// with it was the artwork.
    /// </summary>
    public const float CharacterHeight = 2f;

    /// <summary>
    /// Scales a rig so the character stands <paramref name="targetHeight"/> units tall,
    /// and returns the factor applied.
    ///
    /// ══ WHY THE ANVIL WAS TWICE THE SIZE OF THE PLAYER ════════════════════════
    ///
    /// A SPUM sprite is authored at 32 pixels to the unit and comes out well under a
    /// metre tall, while everything around it was built for the two-unit person the
    /// NavMeshAgent describes. Nothing was scaled wrongly on purpose; the art and the
    /// world were simply never introduced to each other. The result is a character who
    /// is a quarter of one floor tile and half the height of a blacksmith's anvil.
    ///
    /// Fixing it here rather than shrinking the map means one number decides it for
    /// both maps, every monster and every prop at once — and the props can then be
    /// authored as multiples of a person, which is how somebody actually thinks about
    /// whether an anvil is the right size.
    ///
    /// Measured from the FEET to the TOP OF THE HEAD, not from the whole silhouette.
    /// SPUM prefabs ship dressed, and a rig holding a spear has a bounding box half
    /// again as tall as the character in it — so scaling to total bounds would make
    /// every character a different height depending on what they happened to be
    /// carrying when the prefab was built.
    /// </summary>
    public static float NormaliseHeight(Transform rigRoot, float targetHeight)
    {
        if (rigRoot == null || targetHeight <= 0f) return 1f;

        var art = ArtRoot(rigRoot);

        // Measured at a known scale, so re-running the builder on an already-scaled
        // rig converges instead of compounding.
        art.localScale = Vector3.one;

        float measured = MeasureCharacterHeight(rigRoot);
        if (measured <= 0.0001f)
        {
            Debug.LogWarning($"[SpumRig] Could not measure '{rigRoot.name}' — no sprite with " +
                             "any height. Leaving it at its authored scale.");
            return 1f;
        }

        float factor = targetHeight / measured;
        art.localScale = Vector3.one * factor;
        return factor;
    }

    /// <summary>
    /// Foot to crown, in world units, at the rig's current scale.
    ///
    /// Falls back to the full sprite bounds when the named parts are missing, so a rig
    /// that is not laid out the way SPUM lays one out still gets a sensible number
    /// rather than zero.
    /// </summary>
    public static float MeasureCharacterHeight(Transform rigRoot)
    {
        if (rigRoot == null) return 0f;

        float footBottom = float.MaxValue;
        float headTop    = float.MinValue;

        foreach (var part in new[] { "P_LFoot", "P_RFoot" })
            foreach (var layer in Collect(rigRoot, part))
                if (Drawable(layer.Renderer)) footBottom = Mathf.Min(footBottom, layer.Renderer.bounds.min.y);

        foreach (var part in new[] { "P_Head", "P_Hair", "P_Helmet" })
            foreach (var layer in Collect(rigRoot, part))
                if (Drawable(layer.Renderer)) headTop = Mathf.Max(headTop, layer.Renderer.bounds.max.y);

        if (footBottom < float.MaxValue && headTop > float.MinValue && headTop > footBottom)
            return headTop - footBottom;

        // Whole silhouette, as a last resort.
        Bounds? total = null;
        foreach (var renderer in rigRoot.GetComponentsInChildren<SpriteRenderer>(true))
        {
            if (!Drawable(renderer)) continue;

            if (total == null) { total = renderer.bounds; continue; }

            var grown = total.Value;
            grown.Encapsulate(renderer.bounds);
            total = grown;
        }

        return total?.size.y ?? 0f;
    }

    private static bool Drawable(SpriteRenderer renderer)
        => renderer != null && renderer.sprite != null && renderer.enabled;

    /// <summary>
    /// The node holding the artwork — UnitRoot inside a SPUM prefab, or the rig root
    /// when there is none.
    ///
    /// Scaling this rather than the prefab root matters for monsters, whose root
    /// carries the NavMeshAgent AND the art: scaling that would take the capsule
    /// collider with it and quietly change what the player can click on.
    /// </summary>
    private static Transform ArtRoot(Transform rigRoot)
    {
        var art = FindDeep(rigRoot, "UnitRoot");
        return art == null ? rigRoot : art;
    }
}
