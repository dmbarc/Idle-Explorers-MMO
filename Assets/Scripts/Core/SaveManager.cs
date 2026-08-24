using System;
using System.IO;
using UnityEngine;

/// <summary>
/// Local save/load of AccountData to persistentDataPath as JSON.
/// Phase 1: single local file. Phase 8: server-authoritative, this becomes the
/// offline cache only.
///
/// Persistence is what makes the AFK loop real — lastLogoutUnixTime has to survive
/// the process exiting, or no time ever elapses between sessions.
/// </summary>
public class SaveManager : MonoBehaviour
{
    private const string SAVE_FILE = "account.json";

    public static string SavePath => Path.Combine(Application.persistentDataPath, SAVE_FILE);

    /// <summary>Previous good save, kept so one bad write cannot end a career.</summary>
    private static string BackupPath => SavePath + ".bak";

    /// <summary>Where a half-written save lands before it replaces the real one.</summary>
    private static string TempPath => SavePath + ".tmp";

    /// <summary>True when a save file exists on disk.</summary>
    public bool HasSave => File.Exists(SavePath);

    // ── Save ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes the account to disk atomically.
    ///
    /// File.WriteAllText truncates the target and then writes, so a crash, a battery
    /// death or an OS kill partway through leaves a truncated file that parses to
    /// null — and Load treats null as "no save" and hands back a brand new account.
    /// The player loses everything, and nothing anywhere reports why. This game saves
    /// on every craft, bank transfer and talent point, so the window is not small.
    ///
    /// Writing to a temporary file first and moving it into place means the real save
    /// is only ever replaced by a complete one, and the previous good copy survives
    /// as a backup.
    /// </summary>
    public void Save()
    {
        var account = AccountManager.Current;
        if (account == null)
        {
            Debug.LogWarning("[SaveManager] No account to save.");
            return;
        }

        // Stamp before writing. A brand-new account has never been through Rehydrate,
        // so without this it would be written at version 0 and then "migrated" — and
        // the migration refunds talents.
        account.saveVersion = CurrentSaveVersion;

        try
        {
            string json = JsonUtility.ToJson(account, prettyPrint: true);

            // Guard against serialising an empty document over a good save.
            if (string.IsNullOrWhiteSpace(json) || json.Length < 2)
            {
                Debug.LogError("[SaveManager] Refusing to write an empty save.");
                return;
            }

            File.WriteAllText(TempPath, json);

            if (File.Exists(SavePath))
            {
                // Replace keeps the old file as the backup in one operation.
                File.Replace(TempPath, SavePath, BackupPath, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(TempPath, SavePath);
            }

            Debug.Log($"[SaveManager] Saved {account.characters?.Count ?? 0} character(s) to {SavePath}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[SaveManager] Save failed: {e.Message}");
        }
    }

    // ── Load ──────────────────────────────────────────────────────────────────

    /// <summary>Returns the saved account, or null if there is no valid save.</summary>
    public AccountData Load()
    {
        var account = LoadFrom(SavePath);

        // A save that will not parse is not the same as no save. Falling straight
        // through to a fresh account means the next autosave — seconds later —
        // overwrites the damaged file, destroying whatever could have been recovered
        // from it by hand.
        if (account == null && File.Exists(SavePath))
        {
            QuarantineBadSave();

            account = LoadFrom(BackupPath);
            if (account != null)
                Debug.LogWarning("[SaveManager] Recovered the previous save from the backup.");
        }

        if (account == null) return null;

        Rehydrate(account);
        Debug.Log($"[SaveManager] Loaded {account.characters.Count} character(s).");
        return account;
    }

    private AccountData LoadFrom(string path)
    {
        if (!File.Exists(path)) return null;

        try
        {
            string json = File.ReadAllText(path);
            var parsed  = JsonUtility.FromJson<AccountData>(json);

            if (parsed == null)
                Debug.LogWarning($"[SaveManager] '{Path.GetFileName(path)}' parsed to null.");

            return parsed;
        }
        catch (Exception e)
        {
            Debug.LogError($"[SaveManager] Could not read '{Path.GetFileName(path)}': {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// Moves an unreadable save aside rather than letting it be overwritten.
    /// Timestamped, so a repeated failure cannot clobber the first evidence.
    /// </summary>
    private void QuarantineBadSave()
    {
        try
        {
            string corrupt = $"{SavePath}.corrupt-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
            File.Move(SavePath, corrupt);
            Debug.LogError($"[SaveManager] Save could not be read. Moved to {corrupt} — " +
                           "it has NOT been deleted and may be recoverable.");
        }
        catch (Exception e)
        {
            Debug.LogError($"[SaveManager] Could not set the damaged save aside: {e.Message}");
        }
    }

    /// <summary>Fills in collections JsonUtility writes as null, and runs migrations.</summary>
    private void Rehydrate(AccountData account)
    {
        // JsonUtility writes null collections as null, not empty — rehydrate so
        // callers never have to null-check the lists. The bank is also absent
        // entirely from saves written before it existed.
        account.characters ??= new System.Collections.Generic.List<CharacterData>();
        account.bank       ??= new System.Collections.Generic.List<InventoryEntry>();

        foreach (var ch in account.characters)
        {
            if (ch == null) continue;

            ch.skills     ??= new System.Collections.Generic.List<SkillProgress>();
            ch.inventory  ??= new System.Collections.Generic.List<InventoryEntry>();
            ch.mergeBoard ??= new System.Collections.Generic.List<InventoryEntry>();
            ch.equipment  ??= new System.Collections.Generic.List<EquipmentEntry>();
            // Absent from every save written before talents existed.
            ch.talents    ??= new System.Collections.Generic.List<TalentRank>();
            ch.storedDurability ??= new System.Collections.Generic.List<ItemDurability>();

            // Absent from every save written before cross-speccing. ClassIds() does
            // the actual migration from the single classId, so calling it is enough.
            ch.ClassIds();
            ch.Hotbar();

            // Nobody is online at load. If the game was killed mid-session the
            // flag stayed true, and the character card showed "⚡ ONLINE"
            // forever while never accruing anything.
            ch.isOnline = false;

            BackfillCharacterXP(ch);
        }

        MigrateTalentTrees(account);
    }

    /// <summary>
    /// The version of the save format this build writes.
    ///
    /// Bumped when a change makes existing saved data mean something different, rather
    /// than merely adding to it. Most changes need no bump: a new field that defaults
    /// sensibly is handled by the rehydration above.
    /// </summary>
    public const int CurrentSaveVersion = 2;

    /// <summary>
    /// Refunds every talent once, when a save predates abilities living in the trees.
    ///
    /// The trees were rebuilt: nodes were renamed, and the four abilities each class
    /// used to hand out at creation are now bought from the tree. A character whose
    /// points sit in nodes that no longer exist would keep those points locked away
    /// AND have no abilities — TalentManager ignores ranks whose node id it cannot
    /// find, so they are neither refunded nor useful.
    ///
    /// Stamped so it happens exactly once. Running it again on an already-migrated
    /// save would wipe a build the player had since chosen deliberately.
    /// </summary>
    private static void MigrateTalentTrees(AccountData account)
    {
        if (account == null || account.saveVersion >= CurrentSaveVersion) return;

        int wiped = 0;
        foreach (var ch in account.characters)
        {
            if (ch?.talents == null || ch.talents.Count == 0) continue;

            ch.talents.Clear();
            ch.Hotbar().Clear();
            wiped++;
        }

        account.saveVersion = CurrentSaveVersion;

        if (wiped == 0) return;

        Debug.Log($"[SaveManager] Talent trees were rebuilt — refunded {wiped} character(s).");
        GameEvents.FireToast($"Talents refunded on {wiped} character(s): abilities now come from the tree.");
    }

    /// <summary>
    /// Brings a character onto the current character-XP rule: a quarter of all skill
    /// XP, from every skill.
    ///
    /// Character XP used to come from combat kills alone. A character who mined,
    /// fished and cooked could hold two hundred thousand skill XP and still be level 1
    /// — and talent points come from character level, so that playstyle would have
    /// earned nothing. Applying the new rule only to future XP would leave existing
    /// characters permanently behind for having played before it changed.
    ///
    /// Only ever raises. A character already at or above what their skills imply is
    /// left exactly as they are, so this cannot take progress away and re-running it
    /// on an already-migrated save is a no-op.
    /// </summary>
    private static void BackfillCharacterXP(CharacterData character)
    {
        if (character?.skills == null || character.skills.Count == 0) return;

        long totalSkillXP = 0;
        foreach (var skill in character.skills)
            if (skill != null) totalSkillXP += skill.xp;

        long implied = totalSkillXP / 4;
        if (implied <= character.xp) return;

        int previousLevel = character.level;

        character.xp    = implied;
        character.level = CharacterManager.XPToLevel(implied);

        Debug.Log($"[SaveManager] {character.characterName}: character XP backfilled from skill XP " +
                  $"({totalSkillXP:N0} skill XP → level {previousLevel} → {character.level}).");
    }

    // ── Delete (used by a 'reset progress' option later) ──────────────────────

    public void DeleteSave()
    {
        if (!HasSave) return;
        try
        {
            File.Delete(SavePath);
            Debug.Log("[SaveManager] Save deleted.");
        }
        catch (Exception e)
        {
            Debug.LogError($"[SaveManager] Delete failed: {e.Message}");
        }
    }

    // ── Autosave hooks ────────────────────────────────────────────────────────

    void OnApplicationPause(bool paused)
    {
        // Mobile: the app can be killed while backgrounded without OnApplicationQuit
        // ever firing, so this is the only reliable save point on that platform.
        if (paused) SaveActiveState();
    }

    void OnApplicationQuit() => SaveActiveState();

    /// <summary>
    /// Stamps the active character's logout time, then writes to disk.
    /// Without the stamp, AFK accrual on next login would measure from zero.
    /// </summary>
    public void SaveActiveState()
    {
        var active = CharacterManager.Current;
        if (active != null)
        {
            active.lastLogoutUnixTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            active.isOnline           = false;
            if (GameManager.Activity?.CurrentActivity != null)
                active.currentActivity = GameManager.Activity.CurrentActivity;
        }
        Save();
    }
}
