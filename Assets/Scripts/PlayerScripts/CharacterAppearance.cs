using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Draws worn cosmetics on the SPUM rig.
///
/// ══ WHY THE TIN HELMET WAS INVISIBLE ══════════════════════════════════════════
///
/// P_Helmet has TWO child renderers, 11_Helmet1 (sorting order 11) and 12_Helmet2
/// (order 12). The Devil prefab this project uses for the player ships Helmet_7 —
/// its horns — already assigned to 12_Helmet2. The old lookup took
/// GetComponentInChildren, which returns the FIRST child, so every helmet ever
/// equipped was written to 11_Helmet1 and drawn one layer BEHIND the horns that were
/// already there. The sprite loaded, the renderer was enabled, and nothing appeared.
///
/// The second half of the same bug: Legacy's armour, pant and cloth sheets are
/// Multiple-mode textures cut into Body / Left / Right sub-sprites, and
/// Resources.Load&lt;Sprite&gt; returns NULL for those. Every chest piece in
/// item_data.json pointed at one. So chest armour never rendered either, and the log
/// line that said so was Debug.Log rather than a warning.
///
/// Both are fixed by treating a slot as a GROUP of layers:
///
///   • The layers that already had art are the ones the rig actually draws, so those
///     are the ones an item replaces. Layers that shipped empty get blanked.
///   • Sub-sprites are addressed as "path#Name", and a bare path auto-resolves to
///     #Left / #Right / #Body from which part of the rig it is being written to — so
///     one address in item_data.json dresses the body AND both shoulders.
///   • Originals are captured before the first overwrite, so taking a helmet off puts
///     the character's own head back instead of leaving them bald.
/// </summary>
[DisallowMultipleComponent]
public class CharacterAppearance : MonoBehaviour
{
    /// <summary>Sorting offsets for slots SPUM has no layer for, relative to the body.</summary>
    private static readonly Dictionary<string, int> SyntheticSortOrder = new()
    {
        { "gloves", 3 },
        { "tabard", 4 },
    };

    /// <summary>One renderer we may write to, plus what it looked like before we did.</summary>
    private class Layer
    {
        public SpriteRenderer Renderer;
        public Sprite         OriginalSprite;
        public bool           OriginalEnabled;

        /// <summary>Which half of a Left/Right/Body sheet belongs on this renderer.</summary>
        public string         Side;

        /// <summary>True when the rig shipped art here — the layer it actually draws.</summary>
        public bool           WasDrawn => OriginalSprite != null;
    }

    private readonly Dictionary<string, List<Layer>> _layers = new();
    private GameObject _auraInstance;
    private string     _auraItemId;

    void OnEnable()
    {
        GameEvents.OnEquipmentChanged += Refresh;
        Refresh();
    }

    void OnDisable() => GameEvents.OnEquipmentChanged -= Refresh;

    // ── Applying ──────────────────────────────────────────────────────────────

    /// <summary>Redraws every cosmetic slot from what the character is wearing.</summary>
    public void Refresh()
    {
        var equipment = GameManager.Equipment;
        if (equipment == null) return;

        foreach (var slot in EquipmentSlots.Cosmetic())
        {
            var item = equipment.GetEquippedItem(slot.SlotId);

            if (slot.SlotId == "aura") { ApplyAura(item); continue; }

            ApplySlot(slot, item);
        }
    }

    private void ApplySlot(EquipmentSlots.Slot slot, ItemData item)
    {
        var layers = ResolveLayers(slot);
        if (layers == null || layers.Count == 0) return;

        // Nothing worn, or worn art that does not exist: put the rig back exactly as
        // it shipped. Leaving the last item's sprite behind would read as phantom
        // equipment, and blanking everything would strip the character's own body.
        if (item == null || string.IsNullOrEmpty(item.equipSpriteAddress))
        {
            foreach (var layer in layers) Restore(layer);
            return;
        }

        // Only the layers the rig already draws get the item. A helmet written to the
        // spare layer under the one carrying art is invisible — that is the whole bug
        // this class exists to have fixed. When NO layer shipped art (both shoulders,
        // for instance) they are all fair game.
        bool anyDrawn = false;
        foreach (var layer in layers) if (layer.WasDrawn) { anyDrawn = true; break; }

        bool appliedAny = false;

        foreach (var layer in layers)
        {
            if (anyDrawn && !layer.WasDrawn)
            {
                // A spare layer beneath a real one. Hide it so a previous item cannot
                // peek out from under the current one.
                layer.Renderer.enabled = false;
                continue;
            }

            var sprite = SpriteLoader.Load(item.equipSpriteAddress, layer.Side);
            if (sprite == null) continue;

            layer.Renderer.sprite  = sprite;
            layer.Renderer.enabled = true;

            // enabled = true does nothing for a renderer on a deactivated object, and
            // a silently-inactive layer looks exactly like a failed sprite load.
            if (!layer.Renderer.gameObject.activeSelf)
                layer.Renderer.gameObject.SetActive(true);

            appliedAny = true;
        }

        if (!appliedAny)
        {
            // Loud, not Debug.Log. A cosmetic that resolves to nothing is invisible by
            // definition, so the console message is the only evidence it went wrong.
            Debug.LogWarning($"[CharacterAppearance] '{item.id}' has equipSpriteAddress " +
                             $"'{item.equipSpriteAddress}' but no sprite loaded for slot " +
                             $"'{slot.SlotId}'. Check the path is under a Resources folder, and " +
                             "that a Multiple-mode sheet names its sub-sprite (path#Body).");

            foreach (var layer in layers) Restore(layer);
        }
    }

    private static void Restore(Layer layer)
    {
        layer.Renderer.sprite  = layer.OriginalSprite;
        layer.Renderer.enabled = layer.OriginalEnabled;
    }

    /// <summary>The aura is a looping particle effect parented to the rig, not a sprite.</summary>
    private void ApplyAura(ItemData item)
    {
        string wanted = item?.equipSpriteAddress;

        // Already showing the right one — rebuilding it every refresh would restart
        // the effect each time anything else was equipped.
        if (_auraInstance != null && _auraItemId == item?.id) return;

        if (_auraInstance != null)
        {
            Destroy(_auraInstance);
            _auraInstance = null;
            _auraItemId   = null;
        }

        if (item == null || string.IsNullOrEmpty(wanted)) return;

        // lifetime 0: it persists until the item comes off.
        _auraInstance = AbilityVFX.PlayAttached(wanted, transform, lifetime: 0f);
        _auraItemId   = item.id;
    }

    // ── Rig lookup ────────────────────────────────────────────────────────────

    private List<Layer> ResolveLayers(EquipmentSlots.Slot slot)
    {
        if (_layers.TryGetValue(slot.SlotId, out var cached) && cached != null && cached.Count > 0)
        {
            // A destroyed rig invalidates the cache — rebuild rather than dereference.
            bool stale = false;
            foreach (var layer in cached) if (layer.Renderer == null) { stale = true; break; }
            if (!stale) return cached;
        }

        var layers = new List<Layer>();

        if (slot.RendersOnCharacter)
        {
            foreach (var partName in slot.SpumParts)
                CollectPart(partName, layers);

            if (layers.Count == 0)
                Debug.LogWarning($"[CharacterAppearance] No SPUM part for slot '{slot.SlotId}' " +
                                 $"({string.Join(", ", slot.SpumParts)}) on '{name}'. " +
                                 "This rig may be a different SPUM prefab than the code expects.");
        }
        else
        {
            var synthetic = CreateSyntheticLayer(slot.SlotId);
            if (synthetic != null)
                layers.Add(new Layer { Renderer = synthetic, Side = "Body" });
        }

        _layers[slot.SlotId] = layers;
        return layers;
    }

    /// <summary>
    /// Adds every renderer belonging to one SPUM part, recording what it looked like
    /// first.
    ///
    /// Two things about this rig make the obvious one-liner wrong:
    ///
    ///   • A part name can appear TWICE. There are two P_Shoulder transforms, one on
    ///     each arm, so a FindDeep that returns the first would dress one shoulder and
    ///     leave the other bare.
    ///   • Parts NEST. P_LCloth — the left legguard — is a child of P_LFoot, so
    ///     GetComponentsInChildren on the boot slot would sweep up the leg layer and
    ///     boots would silently overwrite legguards.
    ///
    /// Hence: find every transform with the name, and walk each subtree by hand,
    /// stopping at any nested "P_" part because that layer belongs to another slot.
    /// </summary>
    private void CollectPart(string partName, List<Layer> into)
    {
        var parts = new List<Transform>();
        FindAllDeep(transform, partName, parts);

        // P_LFoot / P_RCloth name their own side; P_Shoulder does not, and its child
        // (25_L_Shoulder or -15_R_Shoulder) names it instead.
        string partSide = SideFromName(partName);

        foreach (var part in parts)
            WalkPart(part, partSide, into, isRoot: true);
    }

    private void WalkPart(Transform node, string partSide, List<Layer> into, bool isRoot)
    {
        // A nested part is a different slot's layer. Descending into it is how boots
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
            WalkPart(child, partSide, into, isRoot: false);
    }

    /// <summary>
    /// "Left", "Right" or null, from a rig transform name.
    ///
    /// Deliberately narrow: it matches the L/R marker SPUM uses (P_LFoot, _3L_Foot,
    /// 25_L_Shoulder) and nothing else. A looser test would read the "l" in "Helmet"
    /// or "Cloth" and hand every layer a side it does not have.
    /// </summary>
    private static string SideFromName(string name)
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
    /// Creates the placeholder layer for a slot SPUM has no art category for. It
    /// draws nothing until an item supplies a sprite, but it exists, is correctly
    /// ordered, and is found by name — so adding art later is purely a data change.
    /// </summary>
    private SpriteRenderer CreateSyntheticLayer(string slotId)
    {
        string childName = $"Equip_{slotId}";

        var existing = FindDeep(transform, childName);
        if (existing != null) return existing.GetComponent<SpriteRenderer>();

        // Parent to the body so the layer inherits the rig's facing and flipping.
        var anchor = FindDeep(transform, "P_Body") ?? transform;

        var go = new GameObject(childName);
        go.transform.SetParent(anchor, false);
        go.transform.localPosition = Vector3.zero;

        var renderer = go.AddComponent<SpriteRenderer>();
        renderer.enabled = false;

        var reference = anchor.GetComponent<SpriteRenderer>();
        if (reference != null)
        {
            renderer.sortingLayerID = reference.sortingLayerID;
            renderer.sortingOrder   = reference.sortingOrder +
                                      (SyntheticSortOrder.TryGetValue(slotId, out int offset) ? offset : 1);
        }

        return renderer;
    }

    /// <summary>Depth-first search by exact name through the whole rig. First match.</summary>
    private static Transform FindDeep(Transform root, string targetName)
    {
        if (root.name == targetName) return root;

        foreach (Transform child in root)
        {
            var found = FindDeep(child, targetName);
            if (found != null) return found;
        }
        return null;
    }

    /// <summary>Every transform with the name — P_Shoulder appears once per arm.</summary>
    private static void FindAllDeep(Transform root, string targetName, List<Transform> into)
    {
        if (root.name == targetName) into.Add(root);

        foreach (Transform child in root)
            FindAllDeep(child, targetName, into);
    }
}
