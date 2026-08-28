using System;
using System.Collections.Generic;
using UnityEngine;
using IdleExplorers.Rules;

/// <summary>
/// Owns the currently active character's runtime state.
/// Saves SkillActivityData on disconnect for the AFK system.
/// Phase 1: local stub. Phase 8: server sync.
/// </summary>
public class CharacterManager : MonoBehaviour
{
    public static CharacterData Current { get; private set; }

    /// <summary>
    /// Settles the time away and loads what the server says this character has.
    ///
    /// Async and not awaited by the caller, because SelectCharacter is called from UI
    /// click handlers that cannot wait -- the screen changes immediately and the
    /// numbers land a moment later, which is the same shape every panel already
    /// handles through ServerState.Changed.
    ///
    /// The settle comes FIRST. Pulling before settling would show the state from
    /// before the player was paid for their night, and then change under them.
    /// </summary>
    private async Awaitable PullFromServerAsync(CharacterData character)
    {
        // ══ THE ANSWER IS NOT THROWN AWAY ANY MORE ══════════════════════════
        //
        // This used to discard the settlement. The server had already integrated the
        // whole time away and granted every item, and the player was shown nothing --
        // no summary screen, no explanation for an inventory that had changed while
        // they were gone. The game's central promise, kept and then not mentioned.
        var settled = await IdleExplorers.Backend.ServerState.SettleAsync();

        GameManager.Activity?.AdoptServerSettlement(settled, character);

        // Settle already pulls when it pays. This covers the case where it paid
        // nothing -- a character logged out idle still needs its inventory.
        await IdleExplorers.Backend.ServerState.PullCharacterAsync(character.characterId);

        // The loop that keeps it honest from here. Attached to this manager's own
        // object so it lives exactly as long as the managers do.
        IdleExplorers.Backend.ServerSync.Attach(gameObject);
    }

    /// <summary>
    /// Selects a character and waits for the server to finish settling it.
    ///
    /// ══ WHY THE PULL IS AWAITED ═════════════════════════════════════════
    ///
    /// It used to be fire-and-forget, and the caller read PendingSummary on the very
    /// next line -- before the request had left, let alone come back. So the AFK
    /// summary was always null under an authoritative server and the game went
    /// straight in, every time, no matter how long the player had been away.
    ///
    /// A race that is lost one hundred per cent of the time looks exactly like a
    /// feature that was never built.
    /// </summary>
    public async Awaitable SelectCharacterAsync(CharacterData character)
    {
        SelectCharacter(character);

        if (_entry != null) await _entry;
    }

    /// <summary>The in-flight server entry, so a caller can wait for it.</summary>
    private Awaitable _entry;

    public void SelectCharacter(CharacterData character)
    {
        // Drop the previous character's activity and pending AFK summary before
        // anything reads them — otherwise selecting a second character showed the
        // first one's rewards and current activity.
        GameManager.Activity?.ClearActivity();

        Current = character;
        Current.isOnline = true;
        GameEvents.OnCharacterSelected?.Invoke(character);
        Debug.Log($"[CharacterManager] Selected: {character.characterName} ({character.classId})");

        // ══ WHO PAYS FOR THE TIME AWAY ════════════════════════════════════════
        //
        // Under an authoritative server, nobody here does. Pulling the character is
        // what settles it: the server integrates the whole gap from its own
        // last_settled_at and the answer arrives as state, already applied.
        //
        // ProcessAFKRewards returns null in that mode anyway, so this is belt and
        // braces -- but the pull has to happen, and it has to happen HERE, because
        // everything downstream reads Current expecting it to be populated.
        if (IdleExplorers.Backend.ServerState.IsAuthoritative)
        {
            _entry = PullFromServerAsync(character);
        }
        else if (character.lastLogoutUnixTime > 0)
        {
            // Grant everything earned while this character was logged out. Must run
            // after Current is set — the reward path writes into the active character.
            GameManager.Activity?.ProcessAFKRewards(character);
        }

        // After AFK accrual, so a top-up cannot occupy the last slot the rewards
        // needed. Inert outside the Editor and development builds.
        DevTools.EnsureTestItems();
    }

    // ── Naming ────────────────────────────────────────────────────────────────

    public const int NameMinLength = 2;
    public const int NameMaxLength = 20;

    /// <summary>
    /// Shared name rules for both character creation and renaming.
    ///
    /// Creation never checked for duplicates, which is how an account ends up with
    /// two characters of the same name and no way to tell their cards apart.
    /// </summary>
    /// <param name="excluding">
    /// The character being renamed, so it does not collide with its own name.
    /// Pass null when creating.
    /// </param>
    public static bool ValidateName(string name, CharacterData excluding, out string error)
    {
        string trimmed = (name ?? "").Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            error = "Your explorer needs a name.";
            return false;
        }
        if (trimmed.Length < NameMinLength)
        {
            error = "That name is too short.";
            return false;
        }
        if (trimmed.Length > NameMaxLength)
        {
            error = $"Names are at most {NameMaxLength} characters.";
            return false;
        }

        var characters = AccountManager.Current?.characters;
        if (characters != null)
        {
            foreach (var c in characters)
            {
                if (c == null || ReferenceEquals(c, excluding)) continue;
                if (string.Equals(c.characterName, trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    error = $"You already have a character named '{trimmed}'.";
                    return false;
                }
            }
        }

        error = null;
        return true;
    }

    /// <summary>Renames a character in place and persists. Returns false with a reason.</summary>
    public bool TryRename(CharacterData character, string newName, out string error)
    {
        if (character == null)
        {
            error = "No character selected.";
            return false;
        }

        if (!ValidateName(newName, character, out error)) return false;

        string trimmed = newName.Trim();
        if (string.Equals(character.characterName, trimmed, StringComparison.Ordinal))
        {
            error = null;
            return true;   // no-op rename, not an error
        }

        character.characterName = trimmed;
        character.renameCount++;
        GameManager.Save?.Save();

        GameEvents.OnCharacterRosterChanged?.Invoke();

        error = null;
        return true;
    }

    // ── Class change ──────────────────────────────────────────────────────────

    /// <summary>
    /// Moves the active character to a different class.
    ///
    /// Levels, XP, skills, inventory and equipment all survive — only the class and
    /// its talent tree change, because talent node ids are scoped to a class and mean
    /// nothing in another one. Points are not lost: they are derived from character
    /// level, so emptying the tree hands every one of them back.
    /// </summary>
    public static bool ChangeClass(string newClassId)
    {
        var character = Current;
        if (character == null)
        {
            GameEvents.FireToast("No character selected.", ChatTone.Bad);
            return false;
        }

        var newClass = GameManager.Content?.GetClass(newClassId);
        if (newClass == null)
        {
            Debug.LogWarning($"[CharacterManager] No class '{newClassId}' — class change refused.");
            GameEvents.FireToast("That class does not exist.", ChatTone.Bad);
            return false;
        }

        if (character.classId == newClassId)
        {
            GameEvents.FireToast($"You are already a {newClass.DisplayName}.", ChatTone.Bad);
            return false;
        }

        string previous = GameManager.Content?.GetClass(character.classId)?.DisplayName
                          ?? character.classId;

        // ══ THE SIGIL RESETS EVERY CLASS, NOT JUST THE FIRST ══════════════════
        //
        // It used to replace the primary and leave a second and third intact, on the
        // reasoning that those trees were earned separately. In play that is not what
        // it reads as: the item says you "become something else entirely", and a
        // Sorcerer/Archer who sigils into a Knight came out a Knight/Archer, still
        // carrying half of who they used to be with no way to shed the rest.
        //
        // Nothing is lost that cannot be taken straight back. Class slots are gated on
        // LEVEL, not spent permanently, so the extra slots are free the moment this
        // returns and "+ ADD CLASS" is waiting on the talent panel. What the player
        // gets is a clean re-pick of the whole build, which is what they reached for
        // the sigil to do.
        var cleared = new List<string>(character.ClassIds());

        var ids = character.ClassIds();
        ids.Clear();
        ids.Add(newClassId);

        ClassManager.SyncLegacyClassId(character);
        character.classChangeCount++;

        // Every tree the character had, so no points are stranded on a class they no
        // longer hold. ClearClassTalents per id rather than ClearForClassChange, so a
        // tree belonging to the class being ADOPTED is not wiped along with them.
        foreach (string classId in cleared)
            if (classId != newClassId) TalentManager.ClearClassTalents(character, classId);

        GameManager.Save?.Save();

        // The HUD rebuilds its action bar from this, and PlayerController recomputes
        // its stats — the new class has different health, damage and swing speed.
        GameEvents.OnClassChanged?.Invoke(newClassId);
        GameEvents.OnEquipmentChanged?.Invoke();
        GameEvents.OnCharacterRosterChanged?.Invoke();

        string extra = cleared.Count > 1
            ? $" Your other {cleared.Count - 1} class(es) are reset — pick again from the talent panel."
            : "";

        GameEvents.FireToast($"{previous} → {newClass.DisplayName}. Talents refunded.{extra}",
                             ChatTone.Good);
        Debug.Log($"[CharacterManager] {character.characterName}: {previous} → {newClass.DisplayName}" +
                  $" (reset {cleared.Count} class(es))");
        return true;
    }

    public void CreateCharacter(CharacterData character)
    {
        if (AccountManager.Current == null) return;

        // ══ THE SERVER MINTS THE ID WHEN THERE IS ONE ═════════════════════════
        //
        // A locally-generated Guid would name a character the server has never heard
        // of, and every call about it -- settle, equip, engage -- would 404 on a
        // character that plainly exists on screen. The server also enforces the
        // per-account limit and the name uniqueness, neither of which this can.
        if (IdleExplorers.Backend.ServerState.IsAuthoritative)
        {
            _ = CreateOnServerAsync(character);
            return;
        }

        character.characterId = Guid.NewGuid().ToString();
        character.level = 1;
        character.xp = 0;
        AccountManager.Current.characters.Add(character);
        GameEvents.OnCharacterCreated?.Invoke(character);
        GameEvents.OnCharacterRosterChanged?.Invoke();
        Debug.Log($"[CharacterManager] Created: {character.characterName}");
    }

    /// <summary>
    /// Asks the server for a character, and takes the id it gives back.
    ///
    /// The roster is then re-pulled rather than having the new character appended
    /// locally: the server decides what the account holds, and a local append would be
    /// this client's opinion of a list it does not own.
    /// </summary>
    private async Awaitable CreateOnServerAsync(CharacterData character)
    {
        try
        {
            var created = await IdleExplorers.Backend.GameBackend.Current
                .CreateCharacterAsync(character.characterName, character.classId, character.spumConfig);

            if (created == null || string.IsNullOrEmpty(created.characterId))
            {
                GameEvents.FireToast("Could not create that character.", ChatTone.Bad);
                return;
            }

            await IdleExplorers.Backend.ServerState.PullAccountAsync();

            GameEvents.OnCharacterCreated?.Invoke(character);
            GameEvents.OnCharacterRosterChanged?.Invoke();

            Debug.Log($"[CharacterManager] Server created: {created.name} ({created.characterId})");
        }
        catch (IdleExplorers.Backend.BackendException e)
        {
            // The server refuses a duplicate name and a twelfth character, and both
            // messages are written for a player to read. Showing them beats a generic
            // failure that leaves somebody guessing which rule they hit.
            GameEvents.FireToast(e.Title, ChatTone.Bad);
        }
    }

    /// <summary>
    /// Saves current activity snapshot and marks the character offline.
    /// Called before returning to the main menu or app backgrounding.
    /// </summary>
    public void SaveAndDisconnect()
    {
        if (Current == null) return;
        Current.isOnline = false;
        Current.lastLogoutUnixTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Snapshot whatever the character was doing so AFK accrual has something
        // to work from on next login.
        if (GameManager.Activity?.CurrentActivity != null)
            Current.currentActivity = GameManager.Activity.CurrentActivity;

        Debug.Log($"[CharacterManager] Saved and disconnected: {Current.characterName}");

        // Write to disk — the logout timestamp is worthless if it dies with the process.
        GameManager.Save?.Save();

        // TODO Phase 8: push to server
        Current = null;
        GameManager.Activity?.ClearActivity();
    }

    public void AddXP(long amount)
    {
        if (Current == null) return;
        Current.xp += amount;

        GameEvents.OnCharacterXPGained?.Invoke(amount);
        // Level formula: XP required for level L = L^2 * 83 (OSRS-like but scaled to 1-999)
        int newLevel = XPToLevel(Current.xp);
        if (newLevel > Current.level)
        {
            Current.level = newLevel;
            GameEvents.OnCharacterLevelUp?.Invoke(newLevel);
        }
    }

    // The curve moved to the shared rules tree, because the server decides levels
    // and a second copy of a progression formula is the drift that tree exists to
    // prevent.
    //
    // Moving it also fixed a real bug. It was computed in float, and a float carries
    // about 16 million integers exactly while this curve reaches ~82 million — so
    // from level 451 upward the rounding granted the level ONE XP EARLY, at 429 of
    // the remaining boundaries. The skill curve was worse: level 258 up, 695
    // boundaries. Levelling uses double, and LevellingTests pins the exact cases.

    /// <summary>Convert total XP to character level (1–999).</summary>
    public static int XPToLevel(long totalXP) => Levelling.CharacterLevel(totalXP);

    /// <summary>XP required to reach a given level.</summary>
    public static long LevelToXP(int level) => Levelling.CharacterXpFor(level);

    /// <summary>XP needed to reach the next level from current XP.</summary>
    public static long XPToNextLevel(long currentXP) => Levelling.CharacterXpToNext(currentXP);
}
