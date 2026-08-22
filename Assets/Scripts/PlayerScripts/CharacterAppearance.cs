using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Draws worn cosmetics on the SPUM rig.
///
/// SPUM ships swappable sprite layers for exactly four of the ten cosmetic slots —
/// helmet, cape, chest and shirt map to P_Helmet, P_Back, P_ArmorBody and
/// P_ClothBody. There is no sprite category anywhere in the pack for shoulders,
/// tabard, gloves, bracers or boots, and aura is a particle effect rather than a
/// sprite at all.
///
/// Rather than pretend otherwise, the uncovered slots still get a named child
/// SpriteRenderer created at a sensible sort order. They render nothing today; the
/// day art exists, filling in equipSpriteAddress lights them up with no code change.
/// </summary>
[DisallowMultipleComponent]
public class CharacterAppearance : MonoBehaviour
{
    /// <summary>Sorting offsets for slots SPUM has no layer for, relative to the body.</summary>
    private static readonly Dictionary<string, int> SyntheticSortOrder = new()
    {
        { "boots",     1 },
        { "bracers",   2 },
        { "gloves",    3 },
        { "tabard",    4 },
        { "shoulders", 5 },
    };

    private readonly Dictionary<string, SpriteRenderer> _layers = new();
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

            ApplyLayer(slot, item);
        }
    }

    private void ApplyLayer(EquipmentSlots.Slot slot, ItemData item)
    {
        var renderer = ResolveRenderer(slot);
        if (renderer == null) return;

        // No item, or an item with no worn art: hide the layer rather than leaving
        // whatever the prefab shipped with, which would read as phantom equipment.
        if (item == null || string.IsNullOrEmpty(item.equipSpriteAddress))
        {
            renderer.enabled = false;
            return;
        }

        var sprite = Resources.Load<Sprite>(item.equipSpriteAddress);
        if (sprite == null)
        {
            Debug.Log($"[CharacterAppearance] No sprite at Resources/{item.equipSpriteAddress} " +
                      $"for '{item.id}' — slot '{slot.SlotId}' left blank.");
            renderer.enabled = false;
            return;
        }

        renderer.sprite  = sprite;
        renderer.enabled = true;
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

    private SpriteRenderer ResolveRenderer(EquipmentSlots.Slot slot)
    {
        if (_layers.TryGetValue(slot.SlotId, out var cached) && cached != null) return cached;

        SpriteRenderer renderer = slot.RendersOnCharacter
            ? FindSpumRenderer(slot.SpumPart)
            : CreateSyntheticLayer(slot.SlotId);

        if (renderer != null) _layers[slot.SlotId] = renderer;
        return renderer;
    }

    /// <summary>
    /// Finds the SpriteRenderer for a SPUM part. The P_ transforms are pivots, so the
    /// renderer is usually on a child rather than the part itself.
    /// </summary>
    private SpriteRenderer FindSpumRenderer(string partName)
    {
        var part = FindDeep(transform, partName);
        if (part == null)
        {
            Debug.Log($"[CharacterAppearance] SPUM part '{partName}' not found on {name}.");
            return null;
        }

        return part.GetComponent<SpriteRenderer>() ?? part.GetComponentInChildren<SpriteRenderer>(true);
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

    /// <summary>Breadth-first search by exact name through the whole rig.</summary>
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
}
