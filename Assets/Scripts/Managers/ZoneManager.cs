using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Loads map scenes additively on top of Bootstrap, so the Managers GameObject
/// survives every map transition.
///
/// CurrentMap is the single source of truth that MonsterSpawner and
/// SkillNodeController read to configure themselves from JSON.
/// </summary>
public class ZoneManager : MonoBehaviour
{
    /// <summary>Scene used when a map's sceneAddress is blank (all of them, currently).</summary>
    public const string DefaultMapScene = "Map_GoblinCamp";

    public string   CurrentMapId  { get; private set; }
    public string   CurrentZoneId { get; private set; }
    public MapData  CurrentMap    { get; private set; }
    public ZoneData CurrentZone   { get; private set; }
    public bool     IsLoading     { get; private set; }

    private string _loadedSceneName;

    // ── Entry point ───────────────────────────────────────────────────────────

    public void EnterMap(string mapId)
    {
        var map = GameManager.Content?.GetMap(mapId);
        if (map == null)
        {
            Debug.LogError($"[ZoneManager] Unknown mapId '{mapId}'.");
            return;
        }

        // Cancel any in-flight transition rather than dropping this request. Leaving
        // to the menu starts an unload; entering a map immediately after would
        // otherwise race it and could leave two map scenes loaded at once.
        StopAllCoroutines();
        StartCoroutine(EnterMapRoutine(map));
    }

    private IEnumerator EnterMapRoutine(MapData map)
    {
        IsLoading = true;

        // Unload the previous map before loading the next, so two terrains never
        // coexist and NavMesh queries stay unambiguous. Search by name rather than
        // trusting _loadedSceneName, which a cancelled unload may have cleared.
        yield return UnloadAllMapScenes();

        string sceneName = string.IsNullOrEmpty(map.sceneAddress) ? DefaultMapScene : map.sceneAddress;

        // Set the map before the scene loads: objects in it resolve themselves
        // from CurrentMap in their own Start(), which runs on activation.
        CurrentMap    = map;
        CurrentMapId  = map.id;
        CurrentZoneId = map.zoneId;
        CurrentZone   = GameManager.Content?.GetZone(map.zoneId);

        var load = SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Additive);
        if (load == null)
        {
            Debug.LogError($"[ZoneManager] Scene '{sceneName}' is not in Build Settings. " +
                           "Run: Idle Explorers → Prepare Map Scene");
            IsLoading = false;
            yield break;
        }

        yield return load;
        _loadedSceneName = sceneName;

        var loadedScene = SceneManager.GetSceneByName(sceneName);
        if (loadedScene.IsValid()) SceneManager.SetActiveScene(loadedScene);

        IsLoading = false;

        GameEvents.FireMapEntered(map.id);
        GameEvents.OnZoneEntered?.Invoke(map.zoneId);

        RestoreOrDefaultActivity(map);

        Debug.Log($"[ZoneManager] Entered {map.DisplayName} ({map.id}) in {CurrentZone?.DisplayName ?? map.zoneId}");
    }

    /// <summary>
    /// Resumes whatever the character was last doing on this map, falling back to
    /// combat only when there is nothing to resume.
    ///
    /// Entering a map used to overwrite the saved activity with combat
    /// unconditionally, so a character parked at a fishing spot came back reported
    /// as having been fighting — and accrued the wrong rewards.
    /// </summary>
    private void RestoreOrDefaultActivity(MapData map)
    {
        var saved = CharacterManager.Current?.currentActivity;

        bool resumable = saved != null
                         && !string.IsNullOrEmpty(saved.skillId)
                         && saved.skillId != "combat"
                         && saved.mapId == map.id;

        if (resumable)
        {
            GameManager.Activity?.ResumeActivity(saved);
            Debug.Log($"[ZoneManager] Resumed {saved.skillId} on {saved.activityTargetName}.");
            return;
        }

        GameManager.Activity?.SetDefaultCombatActivity(map.id);
    }

    // ── Teardown ──────────────────────────────────────────────────────────────

    public void UnloadCurrentZone()
    {
        if (!gameObject.activeInHierarchy) return;

        StopAllCoroutines();
        StartCoroutine(UnloadRoutine());
    }

    private IEnumerator UnloadRoutine()
    {
        IsLoading = true;
        yield return UnloadAllMapScenes();

        CurrentMap    = null;
        CurrentZone   = null;
        CurrentMapId  = null;
        CurrentZoneId = null;
        IsLoading     = false;
    }

    /// <summary>
    /// Unloads every loaded map scene, not just the one this manager thinks is
    /// current. Guards against a cancelled transition leaving a stray scene behind,
    /// which would give the next map two terrains and two players.
    /// </summary>
    private IEnumerator UnloadAllMapScenes()
    {
        for (int i = SceneManager.sceneCount - 1; i >= 0; i--)
        {
            var scene = SceneManager.GetSceneAt(i);
            if (!scene.isLoaded) continue;
            if (!IsMapScene(scene.name)) continue;

            var op = SceneManager.UnloadSceneAsync(scene);
            if (op != null) yield return op;
        }
        _loadedSceneName = null;
    }

    /// <summary>True for any scene that is a loadable map, so Bootstrap is never unloaded.</summary>
    private static bool IsMapScene(string sceneName)
    {
        if (string.IsNullOrEmpty(sceneName)) return false;
        if (sceneName == DefaultMapScene) return true;

        var maps = GameManager.Content?.Maps;
        if (maps == null) return false;

        foreach (var kv in maps)
            if (!string.IsNullOrEmpty(kv.Value.sceneAddress) && kv.Value.sceneAddress == sceneName)
                return true;

        return false;
    }

    // ── Requirements ──────────────────────────────────────────────────────────

    /// <summary>
    /// Whether the active character meets a map's gates. Used by the travel screen
    /// to grey out locked maps and explain why.
    /// </summary>
    public bool CanEnterMap(string mapId, out string reason)
    {
        reason = null;

        var map = GameManager.Content?.GetMap(mapId);
        if (map == null) { reason = "Unknown map."; return false; }

        var character = CharacterManager.Current;
        var account   = AccountManager.Current;

        if (character != null && character.level < map.reqCharLevel)
        {
            reason = $"Requires character level {map.reqCharLevel}.";
            return false;
        }
        if (account != null && account.accountLevel < map.reqAccountLevel)
        {
            reason = $"Requires account level {map.reqAccountLevel}.";
            return false;
        }
        if (map.reqAnyCharLevel > 0 && account?.characters != null)
        {
            int highest = 0;
            foreach (var c in account.characters)
                if (c.level > highest) highest = c.level;

            if (highest < map.reqAnyCharLevel)
            {
                reason = $"Requires any character at level {map.reqAnyCharLevel}.";
                return false;
            }
        }
        return true;
    }
}
