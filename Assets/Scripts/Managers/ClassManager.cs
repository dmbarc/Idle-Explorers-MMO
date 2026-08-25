using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Cross-speccing: which classes a character has, what that combination is called,
/// and what it takes to add another.
///
/// A character may hold up to three classes. Their stats sum, their talent trees are
/// all spendable from one shared pool, and every ability from every tree is available
/// to put on the hotbar. Nothing is lost by specialising — a second class only adds —
/// which is what makes staying pure a real choice rather than a mistake.
///
/// Combined names come from spec_data.json. A combination the file has not named
/// falls back to "Warrior / Sorcerer" rather than showing nothing, so incomplete
/// content can never make a character's title disappear.
/// </summary>
public static class ClassManager
{
    // ── Titles ────────────────────────────────────────────────────────────────

    /// <summary>What to call this character: their class, or their combination.</summary>
    public static string TitleFor(CharacterData character)
    {
        if (character == null) return "";

        var ids = character.ClassIds();
        if (ids.Count == 0) return "Classless";

        var spec = FindSpec(ids);
        if (spec != null) return spec.DisplayName;

        // No name for this combination. Listing the parts is honest and readable, and
        // it is what a player would say out loud anyway.
        var names = new List<string>();
        foreach (var id in ids)
            names.Add(GameManager.Content?.GetClass(id)?.DisplayName ?? id);

        return string.Join(" / ", names);
    }

    /// <summary>The flavour line for a combination, or empty when it has no name.</summary>
    public static string DescriptionFor(CharacterData character)
    {
        if (character == null) return "";
        return FindSpec(character.ClassIds())?.description ?? "";
    }

    /// <summary>
    /// The named combination for a set of class ids, or null.
    ///
    /// Order-insensitive: Warrior+Sorcerer and Sorcerer+Warrior are the same Paladin,
    /// and requiring the file to list both orderings would double it and guarantee the
    /// two drift apart.
    /// </summary>
    public static SpecCombo FindSpec(List<string> classIds)
    {
        if (classIds == null || classIds.Count < 2) return null;

        var combos = GameManager.Content?.SpecCombos;
        if (combos == null) return null;

        foreach (var combo in combos)
        {
            if (combo?.classIds == null || combo.classIds.Length != classIds.Count) continue;
            if (Matches(combo.classIds, classIds)) return combo;
        }
        return null;
    }

    private static bool Matches(string[] a, List<string> b)
    {
        foreach (var id in a)
            if (!b.Contains(id)) return false;
        return true;
    }

    // ── Adding a class ────────────────────────────────────────────────────────

    /// <summary>Classes this character could still take, given their unlocked slots.</summary>
    public static List<ClassData> AvailableToAdd(CharacterData character)
    {
        var results = new List<ClassData>();
        if (character == null || !character.HasUnusedClassSlot()) return results;

        var classes = GameManager.Content?.Classes;
        if (classes == null) return results;

        var held = character.ClassIds();
        foreach (var kv in classes)
            if (!held.Contains(kv.Key)) results.Add(kv.Value);

        return results;
    }

    /// <summary>
    /// Takes a second or third class.
    ///
    /// Deliberately additive and irreversible-ish: nothing is refunded and nothing is
    /// lost, so the only cost is the talent points the new tree will want out of the
    /// shared pool. That is the real decision, and it is one the player keeps making
    /// every level rather than once at this prompt.
    /// </summary>
    public static bool AddClass(string classId)
    {
        var character = CharacterManager.Current;
        if (character == null)
        {
            GameEvents.FireToast("No character selected.", ChatTone.Bad);
            return false;
        }

        var cls = GameManager.Content?.GetClass(classId);
        if (cls == null)
        {
            Debug.LogWarning($"[ClassManager] No class '{classId}' — cannot add it.");
            return false;
        }

        var held = character.ClassIds();
        if (held.Contains(classId))
        {
            GameEvents.FireToast($"You are already a {cls.DisplayName}.", ChatTone.Bad);
            return false;
        }

        if (!character.HasUnusedClassSlot())
        {
            int next = held.Count == 1 ? CharacterData.ClassSlotTwoLevel
                                       : CharacterData.ClassSlotThreeLevel;
            GameEvents.FireToast($"Another class unlocks at level {next}.", ChatTone.Bad);
            return false;
        }

        held.Add(classId);
        SyncLegacyClassId(character);

        GameManager.Save?.Save();

        GameEvents.OnClassChanged?.Invoke(character.classId);
        GameEvents.OnCharacterRosterChanged?.Invoke();

        string title = TitleFor(character);
        GameEvents.FireToast($"You are now a {title}.", ChatTone.Good);
        Debug.Log($"[ClassManager] {character.characterName} added {cls.DisplayName} → {title}");
        return true;
    }

    /// <summary>
    /// Keeps the legacy single-class field pointing at the primary.
    ///
    /// GhostSnapshot reads classId, and so does every save written before
    /// cross-speccing. This is the ONLY place both are written, which is what stops
    /// the two representations drifting apart.
    /// </summary>
    public static void SyncLegacyClassId(CharacterData character)
    {
        var ids = character?.ClassIds();
        if (ids == null || ids.Count == 0) return;

        character.classId = ids[0];
    }
}
