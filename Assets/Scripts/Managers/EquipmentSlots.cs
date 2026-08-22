using System.Collections.Generic;

/// <summary>
/// The 25 equipment slots, defined once so the paperdoll UI, the save format and
/// item validation cannot disagree about what exists.
///
/// Cosmetic slots change how the character looks; functional slots carry stats and
/// procs. Only four cosmetic slots can currently be rendered — SPUM ships swappable
/// sprite layers for helmet, cape, chest and shirt and nothing for the rest — so the
/// remainder are marked RendersOnCharacter = false and shown as "cosmetic pending
/// art" rather than silently doing nothing.
/// </summary>
public static class EquipmentSlots
{
    public class Slot
    {
        public string SlotId;
        public string DisplayName;
        public bool   IsCosmetic;

        /// <summary>The SPUM part transform this slot swaps, or null when no layer exists.</summary>
        public string SpumPart;

        public bool RendersOnCharacter => !string.IsNullOrEmpty(SpumPart);
    }

    public static readonly Slot[] All =
    {
        // ── Cosmetic ──────────────────────────────────────────────────────────
        new Slot { SlotId = "helmet",    DisplayName = "Helmet",    IsCosmetic = true, SpumPart = "P_Helmet"    },
        new Slot { SlotId = "cape",      DisplayName = "Cape",      IsCosmetic = true, SpumPart = "P_Back"      },
        new Slot { SlotId = "chest",     DisplayName = "Chest",     IsCosmetic = true, SpumPart = "P_ArmorBody" },
        new Slot { SlotId = "shirt",     DisplayName = "Shirt",     IsCosmetic = true, SpumPart = "P_ClothBody" },

        // No SPUM sprite category exists for these. They equip, save and reserve a
        // render slot; supplying art later needs no code change.
        new Slot { SlotId = "shoulders", DisplayName = "Shoulders", IsCosmetic = true },
        new Slot { SlotId = "tabard",    DisplayName = "Tabard",    IsCosmetic = true },
        new Slot { SlotId = "gloves",    DisplayName = "Gloves",    IsCosmetic = true },
        new Slot { SlotId = "bracers",   DisplayName = "Bracers",   IsCosmetic = true },
        new Slot { SlotId = "boots",     DisplayName = "Boots",     IsCosmetic = true },

        // Aura is a particle effect parented to the rig, not a sprite swap.
        new Slot { SlotId = "aura",      DisplayName = "Aura",      IsCosmetic = true },

        // ── Functional ────────────────────────────────────────────────────────
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

    public static IEnumerable<Slot> Cosmetic()
    {
        foreach (var s in All) if (s.IsCosmetic) yield return s;
    }

    public static IEnumerable<Slot> Functional()
    {
        foreach (var s in All) if (!s.IsCosmetic) yield return s;
    }
}
