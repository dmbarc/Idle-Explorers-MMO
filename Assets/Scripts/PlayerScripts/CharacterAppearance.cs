using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Draws worn EQUIPMENT on the SPUM rig. The character underneath it — body, hair,
/// eyes, weapon — belongs to CharacterBaseAppearance.
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
/// Multiple-mode textures, and Resources.Load&lt;Sprite&gt; returns NULL for those.
/// Every chest piece in item_data.json pointed at one.
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

    private readonly Dictionary<string, List<SpumRig.Layer>> _layers = new();
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

        // Drawn(), not Cosmetic(). The hands are on the FUNCTIONAL side of the
        // paperdoll and still have rig layers of their own -- reading IsCosmetic here
        // meant a weapon equipped, hit for its damage, granted its ability, and left
        // the character holding nothing.
        foreach (var slot in EquipmentSlots.Drawn())
        {
            var item = equipment.GetEquippedItem(slot.SlotId);

            if (slot.SlotId == "aura") { ApplyAura(item); continue; }

            ApplySlot(slot, item);
        }

        // Last, because it depends on what the helmet slot just did. Hair is a layer
        // of the CHARACTER, drawn by CharacterBaseAppearance, but whether it shows is
        // a fact about their EQUIPMENT — so the decision has to live on this side.
        var helmet = equipment.GetEquippedItem("helmet");
        SpumAppearance.ApplyHelmetRule(
            transform,
            CharacterManager.Current?.spumConfig?.hairAddress,
            helmet != null && !string.IsNullOrEmpty(helmet.equipSpriteAddress));
    }

    /// <summary>
    /// Forgets which sprites the rig "originally" had, so the next Refresh re-reads them.
    ///
    /// Load-bearing for the appearance editor. A slot's original sprite is captured
    /// the first time that slot is drawn, and taking an item off restores it — but the
    /// boots slot writes to the same two renderers that hold the BODY's bare feet. So
    /// changing race while wearing boots, then taking them off, would restore the old
    /// race's feet and leave a character with mismatched legs. CharacterBaseAppearance
    /// calls this after writing the body; ordering alone cannot fix it, because
    /// appearance can change at any time.
    /// </summary>
    public void InvalidateLayerCache() => _layers.Clear();

    private void ApplySlot(EquipmentSlots.Slot slot, ItemData item)
    {
        var layers = ResolveLayers(slot);
        if (layers == null || layers.Count == 0) return;

        // Nothing worn, or worn art that does not exist: put the rig back exactly as
        // it was. Leaving the last item's sprite behind would read as phantom
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

            SpumRig.Show(layer, sprite);
            layer.Renderer.color = Color.white;
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

    private static void Restore(SpumRig.Layer layer)
    {
        layer.Renderer.sprite  = layer.OriginalSprite;
        layer.Renderer.enabled = layer.OriginalEnabled;
        layer.Renderer.color   = Color.white;
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

    private List<SpumRig.Layer> ResolveLayers(EquipmentSlots.Slot slot)
    {
        if (_layers.TryGetValue(slot.SlotId, out var cached) && cached != null && cached.Count > 0)
        {
            // A destroyed rig invalidates the cache — rebuild rather than dereference.
            bool stale = false;
            foreach (var layer in cached) if (layer.Renderer == null) { stale = true; break; }
            if (!stale) return cached;
        }

        var layers = new List<SpumRig.Layer>();

        if (slot.RendersOnCharacter)
        {
            // The ancestor is load-bearing for the hands: P_Weapon and P_Shield each
            // appear twice, once per arm, and Collect without a qualifier returns
            // both — so a sword would be drawn into the shield hand as well.
            for (int i = 0; i < slot.SpumParts.Length; i++)
                layers.AddRange(SpumRig.Collect(transform, slot.SpumParts[i], slot.AncestorFor(i)));

            if (layers.Count == 0)
                Debug.LogWarning($"[CharacterAppearance] No SPUM part for slot '{slot.SlotId}' " +
                                 $"({string.Join(", ", slot.SpumParts)}) on '{name}'. " +
                                 "This rig may be a different SPUM prefab than the code expects.");
        }
        else
        {
            var synthetic = CreateSyntheticLayer(slot.SlotId);
            if (synthetic != null)
                layers.Add(new SpumRig.Layer
                {
                    Renderer        = synthetic,
                    Side            = "Body",
                    OriginalSprite  = synthetic.sprite,
                    OriginalEnabled = synthetic.enabled,
                });
        }

        _layers[slot.SlotId] = layers;
        return layers;
    }

    /// <summary>
    /// Creates the placeholder layer for a slot SPUM has no art category for. It
    /// draws nothing until an item supplies a sprite, but it exists, is correctly
    /// ordered, and is found by name — so adding art later is purely a data change.
    /// </summary>
    private SpriteRenderer CreateSyntheticLayer(string slotId)
    {
        string childName = $"Equip_{slotId}";

        var existing = SpumRig.FindDeep(transform, childName);
        if (existing != null) return existing.GetComponent<SpriteRenderer>();

        // Parent to the body so the layer inherits the rig's facing and flipping.
        var anchor = SpumRig.FindDeep(transform, "P_Body") ?? transform;

        var go = new GameObject(childName);
        go.transform.SetParent(anchor, false);
        go.transform.localPosition = Vector3.zero;

        var renderer = go.AddComponent<SpriteRenderer>();
        renderer.enabled = false;

        var reference = anchor.GetComponentInChildren<SpriteRenderer>();
        if (reference != null)
        {
            renderer.sortingLayerID = reference.sortingLayerID;
            renderer.sortingOrder   = reference.sortingOrder +
                                      (SyntheticSortOrder.TryGetValue(slotId, out int offset) ? offset : 1);
        }

        return renderer;
    }
}
