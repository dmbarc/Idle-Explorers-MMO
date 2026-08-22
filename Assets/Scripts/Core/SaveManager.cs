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

    /// <summary>True when a save file exists on disk.</summary>
    public bool HasSave => File.Exists(SavePath);

    // ── Save ──────────────────────────────────────────────────────────────────

    public void Save()
    {
        var account = AccountManager.Current;
        if (account == null)
        {
            Debug.LogWarning("[SaveManager] No account to save.");
            return;
        }

        try
        {
            string json = JsonUtility.ToJson(account, prettyPrint: true);
            File.WriteAllText(SavePath, json);
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
        if (!HasSave) return null;

        try
        {
            string json    = File.ReadAllText(SavePath);
            var    account = JsonUtility.FromJson<AccountData>(json);

            if (account == null)
            {
                Debug.LogWarning("[SaveManager] Save file parsed to null — starting fresh.");
                return null;
            }

            // JsonUtility writes null collections as null, not empty — rehydrate so
            // callers never have to null-check the lists.
            account.characters ??= new System.Collections.Generic.List<CharacterData>();
            foreach (var ch in account.characters)
            {
                ch.skills    ??= new System.Collections.Generic.List<SkillProgress>();
                ch.inventory ??= new System.Collections.Generic.List<InventoryEntry>();
                ch.mergeBoard ??= new System.Collections.Generic.List<InventoryEntry>();
            }

            Debug.Log($"[SaveManager] Loaded {account.characters.Count} character(s) from {SavePath}");
            return account;
        }
        catch (Exception e)
        {
            Debug.LogError($"[SaveManager] Load failed: {e.Message} — starting fresh.");
            return null;
        }
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
