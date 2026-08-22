using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Spawns the current map's default monster, defined by zone_data.json.
///
/// The prefab is resolved by monster id from Resources/Monsters/ rather than
/// assigned in the Inspector, so a map's monster changes with the data alone.
/// Adding a new monster is dropping a prefab in that folder — no code, no
/// scene edit.
/// </summary>
public class MonsterSpawner : MonoBehaviour
{
    [Header("Spawn area (world space)")]
    public float minXSpawn = 400f;
    public float maxXSpawn = 500f;
    public float minZSpawn = 400f;
    public float maxZSpawn = 500f;

    [Header("Rate")]
    public float spawnInterval  = 5f;
    public int   maxMonsterCount = 100;

    private const string MonsterResourcePath = "Monsters/";
    private const string FallbackPrefabName  = "_default";

    private int         currentMonsterCount = 0;
    private float       timer = 0f;
    private GameObject  _cachedPrefab;
    private MonsterData _cachedData;
    private string      _cachedMonsterId;

    void Update()
    {
        timer += Time.deltaTime;
        if (timer >= spawnInterval && currentMonsterCount < maxMonsterCount)
        {
            SpawnMonster();
            timer = 0f;
        }
    }

    void SpawnMonster()
    {
        if (!ResolveMonster(out GameObject prefab, out MonsterData data)) return;

        Vector3 randomPos = new Vector3(
            Random.Range(minXSpawn, maxXSpawn), 10f, Random.Range(minZSpawn, maxZSpawn));

        if (!NavMesh.SamplePosition(randomPos, out NavMeshHit hit, 20f, NavMesh.AllAreas))
            return;

        GameObject monster = Instantiate(prefab, hit.position, Quaternion.identity);
        currentMonsterCount++;

        MonsterController mc = monster.GetComponent<MonsterController>();
        if (mc != null)
        {
            // Initialize before Start runs so JSON stats replace prefab defaults
            mc.Initialize(data);
            mc.SetSpawner(this);
        }
    }

    /// <summary>
    /// Looks up the current map's monster id, its JSON definition, and its prefab.
    /// Results are cached until the map changes.
    /// </summary>
    private bool ResolveMonster(out GameObject prefab, out MonsterData data)
    {
        prefab = null;
        data   = null;

        var map = GameManager.Zone?.CurrentMap;
        if (map == null || string.IsNullOrEmpty(map.defaultMonsterId)) return false;

        if (map.defaultMonsterId != _cachedMonsterId)
        {
            _cachedMonsterId = map.defaultMonsterId;
            _cachedData      = GameManager.Content?.GetMonster(_cachedMonsterId);
            _cachedPrefab    = Resources.Load<GameObject>(MonsterResourcePath + _cachedMonsterId);

            if (_cachedPrefab == null)
            {
                // Expected while art is still being made — the data is correct,
                // only the model is standing in.
                _cachedPrefab = Resources.Load<GameObject>(MonsterResourcePath + FallbackPrefabName);
                if (_cachedPrefab != null)
                    Debug.Log($"[MonsterSpawner] No prefab for '{_cachedMonsterId}' — using placeholder visual.");
                else
                    Debug.LogError($"[MonsterSpawner] No prefab for '{_cachedMonsterId}' and no " +
                                   $"'{MonsterResourcePath}{FallbackPrefabName}' fallback. Nothing will spawn.");
            }
        }

        prefab = _cachedPrefab;
        data   = _cachedData;
        return prefab != null;
    }

    // Called by dying monsters
    public void MonsterDied()
    {
        currentMonsterCount = Mathf.Max(0, currentMonsterCount - 1);
    }
}
