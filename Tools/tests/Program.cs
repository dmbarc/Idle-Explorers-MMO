using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;

/// <summary>
/// Checks for the parts of this change that can be checked without Unity: the stat
/// registry, the Hollow's layout, and the drop-size normalisation.
///
/// Real project source is compiled in (StatBlock.cs and Stats.cs are copied verbatim),
/// so these test the shipping code rather than a paraphrase of it.
/// </summary>
internal static class Program
{
    private static int _passed, _failed;

    private static void Check(bool condition, string what)
    {
        if (condition) { _passed++; return; }
        _failed++;
        Console.WriteLine($"  FAIL  {what}");
    }

    private static void CheckNear(float actual, float expected, float tolerance, string what)
        => Check(Math.Abs(actual - expected) <= tolerance,
                 $"{what}  (expected ~{expected}, got {actual})");

    private static int Main()
    {
        Console.WriteLine("Idle Explorers — standalone checks\n");

        StatRegistry();
        MoveSpeedStat();
        MapLayouts();
        ContentFiles();
        DropSizing();
        SlotFamilies();

        Console.WriteLine("Crafting economy");
        EconomyChecks.Run(Check, RepoRoot());

        Console.WriteLine("Armour sets");
        SetBonusChecks.Run(Check, RepoRoot());

        Console.WriteLine("Shared rules purity");
        RulesPurity.Run(Check, RepoRoot());

        IdleExplorersTests.SecurityChecks.Run(Check, RepoRoot());

        IdleExplorersTests.BossClientChecks.Run(Check, RepoRoot());

        IdleExplorersTests.TelemetryChecks.Run(Check);

        AssetChecks.RigMasking(Check, RepoRoot());
        AssetChecks.MapCameras(Check, RepoRoot());

        Console.WriteLine($"\n{_passed} passed, {_failed} failed.");
        return _failed == 0 ? 0 : 1;
    }

    // ── Stats ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every stat in the registry must round-trip through StatBlock.
    ///
    /// This is the check that would have caught moveSpeed being declared in Stats.All
    /// and forgotten in StatBlock's switch — a stat that displays on the character
    /// sheet, is grantable from item_data.json, and silently does nothing.
    /// </summary>
    private static void StatRegistry()
    {
        Console.WriteLine("Stat registry");

        foreach (var info in Stats.All)
        {
            var block = new StatBlock();
            TestLog.Clear();

            block.Add(info.Id, 3.5f);

            Check(TestLog.Warnings.Count == 0,
                  $"'{info.Id}' is accepted by StatBlock.Add (it warned: " +
                  $"{string.Join("; ", TestLog.Warnings)})");

            CheckNear(block.Get(info.Id), 3.5f, 0.0001f,
                      $"'{info.Id}' reads back what Add wrote");
        }

        // Composition has to carry every field, not just the ones anyone remembered.
        var a = new StatBlock();
        var b = new StatBlock();
        foreach (var info in Stats.All) { a.Add(info.Id, 2f); b.Add(info.Id, 5f); }
        a.Add(b);

        foreach (var info in Stats.All)
            CheckNear(a.Get(info.Id), 7f, 0.0001f, $"'{info.Id}' survives Add(StatBlock)");

        // Clone must be deep enough that editing the copy cannot reach the original.
        var original = new StatBlock();
        original.Add(Stats.Health, 100f);
        original.AddSkillAffinity("mining", 0.25f);

        var clone = original.Clone();
        clone.Add(Stats.Health, 50f);
        clone.AddSkillAffinity("mining", 0.25f);

        CheckNear(original.Get(Stats.Health), 100f, 0.0001f, "Clone does not share scalar fields");
        CheckNear(original.GetSkillAffinity("mining"), 0.25f, 0.0001f,
                  "Clone does not share the skill affinity list");
    }

    private static void MoveSpeedStat()
    {
        Console.WriteLine("Movement speed");

        var baseline = new StatBlock { moveSpeed = 5f };
        var ranger   = new StatBlock { moveSpeed = 0.7f };
        var specter  = new StatBlock { moveSpeed = 0.9f };

        var pure = baseline.Clone();
        pure.Add(ranger);
        CheckNear(pure.moveSpeed, 5.7f, 0.0001f, "one class adds to the baseline");

        var crossSpec = baseline.Clone();
        crossSpec.Add(ranger);
        crossSpec.Add(specter);
        CheckNear(crossSpec.moveSpeed, 6.6f, 0.0001f, "two classes both contribute");

        // Named access has to reach the same field the classes wrote to, or gear
        // granting "moveSpeed" would land somewhere else entirely.
        var geared = baseline.Clone();
        geared.Add(Stats.MoveSpeed, 1.5f);
        CheckNear(geared.moveSpeed, 6.5f, 0.0001f, "gear reaches the same field by id");

        Check(Stats.Get(Stats.MoveSpeed) != null, "movement speed is in the display registry");
        Check(Stats.Get(Stats.MoveSpeed)?.Group == Stats.GroupExplorer,
              "movement speed is grouped so the character sheet shows it");
    }

    // ── The maps ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Both hand-authored maps, parsed out of their recipe files: the ASCII layout and
    /// the table of node ids. Parsed from the source rather than duplicated here, so
    /// editing a map cannot leave the check passing against an old copy of it.
    /// </summary>
    private static void MapLayouts()
    {
        Console.WriteLine("Map layouts and node ids");
        MapChecks.Run(Check, RepoRoot());
    }

    private static void ContentFiles()
    {
        Console.WriteLine("Content files");
        DataChecks.Run(Check, RepoRoot());
    }

    private static string _repoRoot;

    /// <summary>
    /// The Unity project directory, found by walking up from the running assembly
    /// until StreamingAssets appears.
    ///
    /// This was a hard-coded absolute path, which tied the suite to one machine and
    /// one checkout. Locating the root by a landmark is what lets the harness live in
    /// the repository and run anywhere, CI included.
    /// </summary>
    private static string RepoRoot()
    {
        if (_repoRoot != null) return _repoRoot;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "Assets", "StreamingAssets")))
                return _repoRoot = dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            "Cannot find the Unity project root above " + AppContext.BaseDirectory);
    }

    // ── Drops ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// The size normalisation from DropPickup.UpdateVisualSize, reproduced here.
    ///
    /// The formula is short enough that copying it is honest, and testing it needs a
    /// Sprite — which is one of the few Unity types that cannot be usefully stubbed,
    /// since its bounds come from the importer.
    /// </summary>
    private static float DropScale(float spriteWorldHeight, long quantity)
    {
        const float target = 0.7f;

        float magnitude  = Mathf.Log10(Mathf.Max(1f, quantity)) / 6f;
        float stackBonus = 1f + Mathf.Clamp01(magnitude) * 0.7f;

        float normalise = spriteWorldHeight > 0.0001f ? target / spriteWorldHeight : target;
        return normalise * stackBonus;
    }

    // ── Equipment slots ───────────────────────────────────────────────────────

    /// <summary>
    /// Items name a slot FAMILY — "ring", not "ring7" — and every reader of that field
    /// has to agree. They did not: EquipmentManager expanded families while the art
    /// check demanded an exact id, and reported all six rings, amulets and trinkets in
    /// the game as broken.
    ///
    /// Checked against the real item_data.json, so a new item with a slot nobody
    /// implements fails here rather than in a playtest.
    /// </summary>
    private static void SlotFamilies()
    {
        Console.WriteLine("Equipment slots");

        Check(EquipmentSlots.IsEquippableSlot("ring"),    "'ring' resolves as a family");
        Check(EquipmentSlots.IsEquippableSlot("amulet"),  "'amulet' resolves as a family");
        Check(EquipmentSlots.IsEquippableSlot("trinket"), "'trinket' resolves as a family");
        Check(EquipmentSlots.IsEquippableSlot("helmet"),  "an exact slot id still resolves");

        Check(!EquipmentSlots.IsEquippableSlot("sporran"), "an invented slot does not resolve");
        Check(!EquipmentSlots.IsEquippableSlot(""),        "an empty slot does not resolve");

        int rings = 0;
        foreach (var _ in EquipmentSlots.Family("ring")) rings++;
        Check(rings == 10, $"'ring' expands to all ten ring slots (got {rings})");

        int helmets = 0;
        foreach (var _ in EquipmentSlots.Family("helmet")) helmets++;
        Check(helmets == 1, $"an exact id is a family of one (got {helmets})");

        // A family must not swallow a longer unrelated id by prefix accident.
        Check(EquipmentSlots.Representative("ring")?.SlotId == "ring1",
              "'ring' resolves to ring1 first");

        // The hands, which the boss drops depend on existing.
        Check(EquipmentSlots.Exists("mainhand"), "there is a main hand to hold a weapon");
        Check(EquipmentSlots.Exists("offhand"),  "there is an off hand to hold a shield");

        // P_Weapon and P_Shield each appear twice on the rig, once per arm, so a
        // find-first lookup dresses whichever the hierarchy happens to list first.
        // The qualifier is what keeps a sword out of the shield hand.
        Check(EquipmentSlots.Get("mainhand")?.AncestorFor(0) == "P_RArm",
              "the main hand is qualified to the right arm");
        Check(EquipmentSlots.Get("offhand")?.AncestorFor(0) == "P_LArm",
              "the off hand is qualified to the left arm");

        foreach (var slot in EquipmentSlots.All)
            Check(slot.SpumAncestors == null || slot.SpumAncestors.Length <= slot.SpumParts.Length,
                  $"'{slot.SlotId}' has no ancestor without a part to qualify");

        // GameContent carries its own copy of these ids, because it is compiled into
        // the server and EquipmentSlots is not. That copy is only safe while this
        // holds.
        var declared = new HashSet<string>(IdleExplorers.Rules.GameContent.KnownSlotIds);
        foreach (var slot in EquipmentSlots.All)
            Check(declared.Remove(slot.SlotId),
                  $"the shared catalogue knows about slot '{slot.SlotId}'");

        Check(declared.Count == 0,
              $"the shared catalogue invents no slots (extra: {string.Join(", ", declared)})");

        // Every equippable item in the shipping data.
        string path = Path.Combine(RepoRoot(), "Assets", "StreamingAssets", "item_data.json");
        if (!File.Exists(path)) { Check(false, $"cannot find {path}"); return; }

        string json = File.ReadAllText(path);
        int checkedItems = 0, unresolved = 0;

        foreach (Match m in Regex.Matches(json,
                     "\"id\":\\s*\"(?<id>[^\"]+)\"(?<body>[^{}]*)\"equipSlot\":\\s*\"(?<slot>[^\"]*)\""))
        {
            checkedItems++;
            if (EquipmentSlots.IsEquippableSlot(m.Groups["slot"].Value)) continue;

            unresolved++;
            Check(false, $"item '{m.Groups["id"].Value}' declares slot " +
                         $"'{m.Groups["slot"].Value}', which resolves to nothing");
        }

        Check(checkedItems > 0, "found equippable items to check");
        Check(unresolved == 0, $"every equippable item names a real slot ({checkedItems} checked)");
    }

    // ── Drops ─────────────────────────────────────────────────────────────────

    private static void DropSizing()
    {
        Console.WriteLine("Drop sizing");

        // bone_white.png: 512px at 100 pixels-per-unit.
        float bone = DropScale(5.12f, 1);
        // A 64px Kenney voxel icon at the same PPU.
        float voxel = DropScale(0.64f, 1);

        CheckNear(bone * 5.12f, 0.7f, 0.0001f, "a 512px icon lands at the target height");
        CheckNear(voxel * 0.64f, 0.7f, 0.0001f, "a 64px icon lands at the same height");

        Check(Math.Abs((bone * 5.12f) - (voxel * 0.64f)) < 0.0001f,
              "icons from different packs end up the same size");

        // Before: the scale ignored the sprite entirely, so world size was whatever the
        // texture happened to be. That is the eight-fold difference the player saw.
        Check(5.12f / 0.64f > 7f, "the packs really do differ by ~8x, which is why this matters");

        // Stacks still read as bigger, and still bounded.
        float single  = DropScale(1f, 1);
        float hundred = DropScale(1f, 100);
        float million = DropScale(1f, 1_000_000);

        Check(hundred > single,  "a hundred is visibly more than one");
        Check(million > hundred, "a million is visibly more than a hundred");
        Check(million / single <= 1.71f, "the largest stack stays under 1.7x a single item");
    }
}
