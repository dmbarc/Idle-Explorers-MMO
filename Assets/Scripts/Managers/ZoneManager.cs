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

        if (IsLoading)
        {
            Debug.LogWarning($"[ZoneManager] Already loading a map — ignoring request for '{mapId}'.");
            return;
        }

        StartCoroutine(EnterMapRoutine(map));
    }

    private IEnumerator EnterMapRoutine(MapData map)
    {
        IsLoading = true;

        // Unload the previous map before loading the next, so two terrains never
        // coexist and NavMesh queries stay unambiguous.
        if (!string.IsNullOrEmpty(_loadedSceneName))
        {
            var unload = SceneManager.UnloadSceneAsync(_loadedSceneName);
            if (unload != null) yield return unload;
            _loadedSceneName = null;
        }

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

        // Default to fighting this map's monster unless the player picks a node
        GameManager.Activity?.SetDefaultCombatActivity(map.id);

        Debug.Log($"[ZoneManager] Entered {map.DisplayName} ({map.id}) in {CurrentZone?.DisplayName ?? map.zoneId}");
    }

    // ── Teardown ──────────────────────────────────────────────────────────────

    public void UnloadCurrentZone()
    {
        if (string.IsNullOrEmpty(_loadedSceneName)) return;
        if (gameObject.activeInHierarchy) StartCoroutine(UnloadRoutine());
    }

    private IEnumerator UnloadRoutine()
    {
        string toUnload  = _loadedSceneName;
        _loadedSceneName = null;

        var unload = SceneManager.UnloadSceneAsync(toUnload);
        if (unload != null) yield return unload;

        CurrentMap    = null;
        CurrentZone   = null;
        CurrentMapId  = null;
        CurrentZoneId = null;
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
