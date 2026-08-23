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
    /// </summary>
    public static void Show(Layer layer, Sprite sprite)
    {
        if (layer?.Renderer == null || sprite == null) return;

        layer.Renderer.sprite  = sprite;
        layer.Renderer.enabled = true;

        if (!layer.Renderer.gameObject.activeSelf)
            layer.Renderer.gameObject.SetActive(true);
    }
}
