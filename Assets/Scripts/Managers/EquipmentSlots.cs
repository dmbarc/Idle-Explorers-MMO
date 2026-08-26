using System.Collections.Generic;

/// <summary>
/// The 28 equipment slots, defined once so the paperdoll UI, the save format and
/// item validation cannot disagree about what exists.
///
/// Cosmetic slots change how the character looks; functional slots carry stats and
/// procs.
///
/// SPUM turns out to ship swappable layers for far more than the four this used to
/// claim. Reading the rig rather than guessing, the Devil prefab has P_Shoulder,
/// P_LFoot/P_RFoot, P_LCloth/P_RCloth and P_LCArm/P_RCArm as well as the obvious
/// head and body parts — and the Legacy sprite sheets are cut into Body/Left/Right
/// sub-sprites precisely so one armour set can fill all of them. So shoulders, boots,
/// legs and bracers all render now; only gloves, tabard and aura have no layer.
///
/// SpumParts is a LIST because several slots are two mirrored parts. See
/// CharacterAppearance for how a sheet's Left/Right sub-sprites reach the right side.
/// </summary>
public static class EquipmentSlots
{
    public class Slot
    {
        public string   SlotId;
        public string   DisplayName;
        public bool     IsCosmetic;

        /// <summary>
        /// SPUM part transforms this slot swaps. Empty when the rig has no layer for
        /// it. Two entries means a mirrored pair — left first, right second.
        /// </summary>
        public string[] SpumParts = System.Array.Empty<string>();

        /// <summary>
        /// Ancestor that disambiguates each part in SpumParts, index for index.
        ///
        /// Needed because the rig carries TWO P_Weapon transforms and TWO P_Shield
        /// transforms, one per arm, and a find-first lookup would dress whichever the
        /// hierarchy happened to list first. The main hand is the right arm and the
        /// off hand the left, which is also where SpumAppearance puts the cosmetic
        /// weapon — so an equipped weapon overrides the cosmetic one rather than
        /// fighting it for the same layer.
        ///
        /// Empty, or a null entry, accepts any match. That is right for every part
        /// that appears once, which is most of them.
        /// </summary>
        public string[] SpumAncestors = System.Array.Empty<string>();

        public bool RendersOnCharacter => SpumParts != null && SpumParts.Length > 0;

        /// <summary>The ancestor qualifier for SpumParts[index], or null for any.</summary>
        public string AncestorFor(int index) =>
            SpumAncestors != null && index < SpumAncestors.Length ? SpumAncestors[index] : null;
    }

    public static readonly Slot[] All =
    {
        // ── Cosmetic ──────────────────────────────────────────────────────────
        new Slot { SlotId = "helmet",    DisplayName = "Helmet",    IsCosmetic = true,
                   SpumParts = new[] { "P_Helmet" } },
        new Slot { SlotId = "cape",      DisplayName = "Cape",      IsCosmetic = true,
                   SpumParts = new[] { "P_Back" } },
        new Slot { SlotId = "chest",     DisplayName = "Chest",     IsCosmetic = true,
                   SpumParts = new[] { "P_ArmorBody" } },
        new Slot { SlotId = "shirt",     DisplayName = "Shirt",     IsCosmetic = true,
                   SpumParts = new[] { "P_ClothBody" } },
        new Slot { SlotId = "shoulders", DisplayName = "Shoulders", IsCosmetic = true,
                   SpumParts = new[] { "P_Shoulder" } },
        new Slot { SlotId = "legs",      DisplayName = "Legguards", IsCosmetic = true,
                   SpumParts = new[] { "P_LCloth", "P_RCloth" } },
        new Slot { SlotId = "boots",     DisplayName = "Boots",     IsCosmetic = true,
                   SpumParts = new[] { "P_LFoot", "P_RFoot" } },
        new Slot { SlotId = "bracers",   DisplayName = "Bracers",   IsCosmetic = true,
                   SpumParts = new[] { "P_LCArm", "P_RCArm" } },

        // No SPUM layer exists for these. They equip, save, carry stats and reserve a
        // render slot; supplying art later needs no code change.
        new Slot { SlotId = "gloves",    DisplayName = "Gloves",    IsCosmetic = true },
        new Slot { SlotId = "tabard",    DisplayName = "Tabard",    IsCosmetic = true },

        // Aura is a particle effect parented to the rig, not a sprite swap.
        new Slot { SlotId = "aura",      DisplayName = "Aura",      IsCosmetic = true },

        // ── Functional ────────────────────────────────────────────────────────

        // The hands. Both render, because the rig ships a P_Weapon and a P_Shield on
        // each arm — verified against the prefab rather than assumed, since a find-
        // first lookup on either name silently dresses the wrong side.
        new Slot { SlotId = "mainhand",  DisplayName = "Main Hand",
                   SpumParts     = new[] { "P_Weapon" },
                   SpumAncestors = new[] { "P_RArm" } },
        new Slot { SlotId = "offhand",   DisplayName = "Off Hand",
                   SpumParts     = new[] { "P_Shield" },
                   SpumAncestors = new[] { "P_LArm" } },

        new Slot { SlotId = "ring1",     DisplayName = "Ring 1"  },
        new Slot { SlotId = "ring2",     DisplayName = "Ring 2"  },
        new Slot { SlotId = "ring3",     DisplayName = "Ring 3"  },
        new Slot { SlotId = "ring4",     DisplayName = "Ring 4"  },
        new Slot { SlotId = "ring5",     DisplayName = "Ring 5"  },
        new Slot { SlotId = "ring6",     DisplayName = "Ring 6"  },
        new Slot { SlotId = "ring7",     DisplayName = "Ring 7"  },
        new Slot { SlotId = "ring8",     DisplayName = "Ring 8"  },
        new Slot { SlotId = "ring9",     DisplayName = "Ring 9"  },
        new Slot { SlotId = "ring10",    DisplayName = "Ring 10" },
        new Slot { SlotId = "amulet1",   DisplayName = "Amulet 1"  },
        new Slot { SlotId = "amulet2",   DisplayName = "Amulet 2"  },
        new Slot { SlotId = "trinket1",  DisplayName = "Trinket 1" },
        new Slot { SlotId = "trinket2",  DisplayName = "Trinket 2" },
        new Slot { SlotId = "companion", DisplayName = "Companion" },
    };

    private static Dictionary<string, Slot> _byId;

    public static Slot Get(string slotId)
    {
        if (string.IsNullOrEmpty(slotId)) return null;

        if (_byId == null)
        {
            _byId = new Dictionary<string, Slot>();
            foreach (var s in All) _byId[s.SlotId] = s;
        }

        return _byId.TryGetValue(slotId, out var slot) ? slot : null;
    }

    public static bool Exists(string slotId) => Get(slotId) != null;

    /// <summary>
    /// Every slot an item declaring <paramref name="declared"/> could go in.
    ///
    /// An item names a FAMILY, not a numbered slot: item_data.json says "ring", and
    /// the character has ring1 through ring10. Authoring "ring7 of the glutton" would
    /// be absurd, so the numbering is the paperdoll's business and never the item's.
    ///
    /// One implementation, because there were two readings of this rule and they
    /// disagreed: EquipmentManager resolved families correctly while the art check
    /// asked EquipmentSlots.Get and reported all six rings, amulets and trinkets in
    /// the game as broken items that would draw nothing. They equip perfectly well.
    /// </summary>
    public static IEnumerable<Slot> Family(string declared)
    {
        if (string.IsNullOrEmpty(declared)) yield break;

        // An exact id is its own family of one.
        var exact = Get(declared);
        if (exact != null) { yield return exact; yield break; }

        foreach (var slot in All)
            if (slot.SlotId.StartsWith(declared, System.StringComparison.Ordinal))
                yield return slot;
    }

    /// <summary>
    /// Whether an item's declared slot names anything real, exactly or as a family.
    /// This is the question content validation should be asking.
    /// </summary>
    public static bool IsEquippableSlot(string declared)
    {
        foreach (var _ in Family(declared)) return true;
        return false;
    }

    /// <summary>
    /// The slot whose art an item in this family renders on. Every member of a family
    /// draws the same way, so the first is representative.
    /// </summary>
    public static Slot Representative(string declared)
    {
        foreach (var slot in Family(declared)) return slot;
        return null;
    }

    /// <summary>Display name for a slot id, falling back to the raw id.</summary>
    public static string NameOf(string slotId) => Get(slotId)?.DisplayName ?? slotId;

    public static IEnumerable<Slot> Cosmetic()
    {
        foreach (var s in All) if (s.IsCosmetic) yield return s;
    }

    public static IEnumerable<Slot> Functional()
    {
        foreach (var s in All) if (!s.IsCosmetic) yield return s;
    }
}
