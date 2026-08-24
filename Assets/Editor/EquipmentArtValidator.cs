using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Checks that every piece of equipment can actually be seen.
///
/// This exists because of a bug that was invisible in every way that matters: the tin
/// helmet equipped, saved, showed in the paperdoll, granted its stats — and did not
/// appear on the character. Two separate causes, neither of which produced an error:
///
///   1. Resources.Load&lt;Sprite&gt; returns NULL for a Multiple-mode texture, and every
///      chest piece pointed at one. No exception, no warning, no sprite.
///   2. The renderer being written to was one layer BEHIND the one the rig already
///      drew art on, so the helmet was rendered and then covered up.
///
/// Both are now handled at runtime, but a check that runs on demand and prints a
/// table beats trusting that. It reports, per item: whether the address resolves,
/// which sub-sprite it picked, and whether the slot has any layer on the rig at all.
///
/// Menu: Idle Explorers → Validate Equipment Art
/// </summary>
public static class EquipmentArtValidator
{
    private const string ITEM_DATA = "Assets/StreamingAssets/item_data.json";
    private const string SET_DATA  = "Assets/StreamingAssets/set_data.json";

    [System.Serializable] private class ItemFile { public ItemData[] items; }
    [System.Serializable] private class SetFile  { public ItemSetData[] sets; }

    [MenuItem("Idle Explorers/Validate Equipment Art")]
    public static void Validate()
    {
        var items = LoadItems();
        if (items == null)
        {
            Debug.LogError($"[ArtCheck] Could not read {ITEM_DATA}.");
            return;
        }

        var ok       = new List<string>();
        var broken   = new List<string>();
        var noArt    = new List<string>();
        var noLayer  = new List<string>();

        foreach (var item in items)
        {
            if (item == null || !item.IsEquippable) continue;

            // By FAMILY, not by exact id. An item declares "ring" and the paperdoll
            // has ring1..ring10; asking for an exact match reported all six rings,
            // amulets and trinkets in the game as broken when they equip perfectly
            // well — EquipmentManager has always resolved families. Both now go
            // through EquipmentSlots so they cannot disagree again.
            var slot = EquipmentSlots.Representative(item.equipSlot);
            if (slot == null)
            {
                broken.Add($"{item.id}: equipSlot '{item.equipSlot}' matches no slot or slot family");
                continue;
            }

            // The inventory icon. Every item needs one; this is the check that would
            // have caught nine pieces of armour silently drawing coloured squares.
            if (string.IsNullOrEmpty(item.iconAddress))
                noArt.Add($"{item.id}: no iconAddress (falls back to a generated placeholder)");
            else if (SpriteLoader.Load(item.iconAddress) == null)
                broken.Add($"{item.id}: iconAddress '{item.iconAddress}' resolves to nothing");

            // The worn art.
            if (string.IsNullOrEmpty(item.equipSpriteAddress))
            {
                noArt.Add($"{item.id}: no equipSpriteAddress — equips but is not drawn on the character");
                continue;
            }

            if (!slot.RendersOnCharacter)
            {
                noLayer.Add($"{item.id}: slot '{slot.SlotId}' has no SPUM layer, so its art cannot show");
                continue;
            }

            var names = SpriteLoader.SubSpriteNames(item.equipSpriteAddress);
            if (names.Length == 0)
            {
                broken.Add($"{item.id}: equipSpriteAddress '{item.equipSpriteAddress}' resolves to nothing " +
                           "— check the path is under a Resources folder");
                continue;
            }

            // A mirrored slot needs Left and Right, or one side will silently borrow
            // the other's art (or the sheet's Body piece).
            bool mirrored = slot.SpumParts.Length > 1 || slot.SlotId == "shoulders";
            if (mirrored && !Has(names, "Left") && !Has(names, "Right") && names.Length == 1)
                noArt.Add($"{item.id}: '{item.equipSpriteAddress}' has one sprite but " +
                          $"'{slot.SlotId}' draws two sides — both will use the same art");

            ok.Add($"{item.id} → {item.equipSpriteAddress} [{string.Join("/", names)}]");
        }

        Report(ok, broken, noArt, noLayer);
        ValidateSets(items);
        ValidateAppearance();
    }

    /// <summary>
    /// Checks the appearance catalogue and every class's preview look.
    ///
    /// The same class of bug as the equipment art, and just as silent: an address that
    /// resolves to nothing produces a character with no hair, or a class card showing
    /// a blank rig, with no error anywhere. Multiple-mode sheets are the usual cause —
    /// Resources.Load returns null for them without complaint.
    /// </summary>
    private static void ValidateAppearance()
    {
        var broken = new List<string>();
        int checkedAddresses = 0;

        void CheckOne(string owner, string address, params string[] requiredSubs)
        {
            if (string.IsNullOrEmpty(address)) return;   // empty is a real choice
            checkedAddresses++;

            var names = SpriteLoader.SubSpriteNames(address);
            if (names.Length == 0)
            {
                broken.Add($"{owner}: '{address}' resolves to nothing");
                return;
            }

            foreach (var sub in requiredSubs)
            {
                if (System.Array.Exists(names, n =>
                        string.Equals(n, sub, System.StringComparison.OrdinalIgnoreCase)))
                    continue;

                broken.Add($"{owner}: '{address}' has no '{sub}' sub-sprite " +
                           $"(it has: {string.Join("/", names)})");
            }
        }

        // Bodies must supply all six pieces, or a character loses an arm.
        foreach (var (address, name) in SpumAppearance.Bodies)
            CheckOne($"body '{name}'", address, "Head", "Body", "Arm_L", "Arm_R", "Foot_L", "Foot_R");

        foreach (var (address, name) in SpumAppearance.Eyes)
            CheckOne($"eyes '{name}'", address, "Front", "Back");

        foreach (var (address, name) in SpumAppearance.Hairs)     CheckOne($"hair '{name}'", address);
        foreach (var (address, name) in SpumAppearance.FaceHairs) CheckOne($"facial hair '{name}'", address);
        foreach (var (address, name) in SpumAppearance.Weapons)   CheckOne($"weapon '{name}'", address);
        foreach (var (address, name) in SpumAppearance.Backs)     CheckOne($"cloak '{name}'", address);

        // Every class's card preview.
        string path = "Assets/StreamingAssets/class_data.json";
        if (File.Exists(path))
        {
            string raw = File.ReadAllText(path).Trim();
            if (raw.StartsWith("["))
            {
                var parsed = JsonUtility.FromJson<ClassFile>("{\"classes\":" + raw + "}");
                if (parsed?.classes != null)
                {
                    foreach (var cls in parsed.classes)
                    {
                        var look = cls?.previewLook;
                        if (look == null || look.IsEmpty)
                        {
                            broken.Add($"class '{cls?.id}' has no previewLook — its card shows a default figure");
                            continue;
                        }

                        CheckOne($"class '{cls.id}' body", look.bodyAddress,
                                 "Head", "Body", "Arm_L", "Arm_R", "Foot_L", "Foot_R");
                        CheckOne($"class '{cls.id}' hair",   look.hairAddress);
                        CheckOne($"class '{cls.id}' eyes",   look.eyeAddress, "Front", "Back");
                        CheckOne($"class '{cls.id}' weapon", look.weaponAddress);
                        CheckOne($"class '{cls.id}' cloak",  look.backAddress);

                        // The armour each class card wears. Same silent failure as
                        // everything else here: an address that resolves to nothing
                        // leaves the class looking undressed, which is now a valid
                        // look and therefore indistinguishable from working.
                        if (cls.previewEquipment == null) continue;

                        foreach (var piece in cls.previewEquipment)
                        {
                            if (piece == null) continue;

                            if (!EquipmentSlots.Exists(piece.slot))
                            {
                                broken.Add($"class '{cls.id}' previewEquipment names slot " +
                                           $"'{piece.slot}', which is not a real slot");
                                continue;
                            }

                            CheckOne($"class '{cls.id}' {piece.slot}", piece.sprite);
                        }
                    }
                }
            }
        }

        // The rig the previews instantiate. Without it every preview silently falls
        // back to a text label.
        if (Resources.Load<GameObject>(CharacterPreview.PlayerRigAddress) == null)
            broken.Add($"no preview rig at Resources/{CharacterPreview.PlayerRigAddress}");

        if (broken.Count == 0)
            Debug.Log($"[ArtCheck] {checkedAddresses} appearance address(es) all resolve.");
        else
            Debug.LogError($"[ArtCheck] {broken.Count} appearance problem(s):\n  • " +
                           string.Join("\n  • ", broken));
    }

    [System.Serializable] private class ClassFile { public ClassData[] classes; }

    private static bool Has(string[] names, string wanted)
    {
        foreach (var n in names)
            if (string.Equals(n, wanted, System.StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static void Report(List<string> ok, List<string> broken,
                               List<string> noArt, List<string> noLayer)
    {
        Debug.Log($"[ArtCheck] {ok.Count} equippable item(s) have working worn art:\n  " +
                  string.Join("\n  ", ok));

        if (noLayer.Count > 0)
            Debug.Log($"[ArtCheck] {noLayer.Count} item(s) in slots the rig cannot draw " +
                      "(shown as 'art pending' in the paperdoll):\n  " + string.Join("\n  ", noLayer));

        if (noArt.Count > 0)
            Debug.LogWarning($"[ArtCheck] {noArt.Count} gap(s) — these work, but look worse than they " +
                             "should:\n  " + string.Join("\n  ", noArt));

        if (broken.Count > 0)
            Debug.LogError($"[ArtCheck] {broken.Count} BROKEN reference(s) — these will draw nothing:\n  " +
                           string.Join("\n  ", broken));
        else
            Debug.Log("[ArtCheck] No broken art references.");
    }

    /// <summary>
    /// The set half: every piece exists, points back at its set, and every bonus names
    /// an action SetBonusResolver implements. Same checks ItemSetManager runs at
    /// startup, available here without entering play mode.
    /// </summary>
    private static void ValidateSets(ItemData[] items)
    {
        if (!File.Exists(SET_DATA)) return;

        string raw = File.ReadAllText(SET_DATA).Trim();
        if (!raw.StartsWith("[")) return;

        var parsed = JsonUtility.FromJson<SetFile>("{\"sets\":" + raw + "}");
        if (parsed?.sets == null) return;

        var byId = new Dictionary<string, ItemData>();
        foreach (var item in items) if (item != null) byId[item.id] = item;

        var problems = new List<string>();

        foreach (var set in parsed.sets)
        {
            if (set?.itemIds == null) continue;

            foreach (var pieceId in set.itemIds)
            {
                if (!byId.TryGetValue(pieceId, out var piece))
                {
                    problems.Add($"set '{set.id}' names '{pieceId}', which is not in item_data.json");
                    continue;
                }

                if (piece.setId != set.id)
                    problems.Add($"'{pieceId}' is in set '{set.id}' but its setId reads '{piece.setId}'");

                if (!piece.HasDurability)
                    problems.Add($"'{pieceId}' has no maxDurability — the durability set bonuses " +
                                 "cannot affect it");
            }

            if (set.bonuses == null) continue;

            foreach (var bonus in set.bonuses)
            {
                if (bonus == null) continue;

                if (bonus.piecesRequired > set.PieceCount)
                    problems.Add($"set '{set.id}' has a {bonus.piecesRequired}-piece bonus but only " +
                                 $"{set.PieceCount} pieces — unreachable");

                if (!SetBonusResolver.Handles(bonus.action))
                    problems.Add($"set '{set.id}' bonus action '{bonus.action}' is not implemented");
            }
        }

        if (problems.Count == 0)
            Debug.Log($"[ArtCheck] {parsed.sets.Length} armour set(s) validated.");
        else
            Debug.LogError($"[ArtCheck] {problems.Count} set problem(s):\n  • " +
                           string.Join("\n  • ", problems));
    }

    private static ItemData[] LoadItems()
    {
        if (!File.Exists(ITEM_DATA)) return null;

        string raw = File.ReadAllText(ITEM_DATA).Trim();
        if (!raw.StartsWith("[")) return null;

        return JsonUtility.FromJson<ItemFile>("{\"items\":" + raw + "}")?.items;
    }
}
