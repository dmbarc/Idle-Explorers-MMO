using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Spawns the current map's default monster, defined by zone_data.json.
///
/// Spawns are placed RELATIVE TO THE PLAYER, in a ring around them, rather than
/// inside a fixed world-space box. The box approach failed twice for the same
/// underlying reason: it encodes the shape of one particular map in a component that
/// every map uses. A scene saved with a 2000x2000 box put almost every roll outside
/// the baked NavMesh, and the fix — hardcoding the Goblin Camp coordinates instead —
/// would have broken the moment a second map existed.
///
/// A ring also gives two properties a box cannot: monsters never appear on top of
/// the player, and they are always close enough to find.
///
/// The prefab is resolved by monster id from Resources/Monsters/ rather than
/// assigned in the Inspector, so a map's monster changes with the data alone.
/// </summary>
public class MonsterSpawner : MonoBehaviour
{
    [Header("Spawn ring (around the player)")]
    [Tooltip("Closest a monster may appear to the player.")]
    public float minSpawnRadius = 12f;

    [Tooltip("Furthest a monster may appear from the player.")]
    public float maxSpawnRadius = 38f;

    [Header("Rate")]
    public float spawnInterval   = 4f;
    public int   maxMonsterCount = 12;

    [Header("Legacy world box (unused)")]
    [Tooltip("Kept only so old scene data deserializes without warnings. Spawning is " +
             "relative to the player now — see the class summary.")]
    public float minXSpawn, maxXSpawn, minZSpawn, maxZSpawn;

    /// <summary>
    /// Ceilings the Inspector cannot exceed. Serialized scene values silently
    /// override script defaults, so the defaults alone are no protection: this scene
    /// was saved with a 0.01-second interval and a cap of 500.
    /// </summary>
    private const float MinSpawnInterval  = 0.5f;
    private const int   MaxMonsterCeiling = 60;

    /// <summary>Attempts per spawn tick before giving up until the next one.</summary>
    private const int PlacementAttempts = 12;

    private const string MonsterResourcePath = "Monsters/";
    private const string FallbackPrefabName  = "_default";

    private int         currentMonsterCount = 0;
    private float       timer = 0f;
    private GameObject  _cachedPrefab;
    private MonsterData _cachedData;
    private string      _cachedMonsterId;
    private bool        _warnedAboutLimits;
    private bool        _warnedAboutPlacement;
    private Transform   _player;

    void Update()
    {
        ClampLimits();

        timer += Time.deltaTime;
        if (timer < spawnInterval || currentMonsterCount >= maxMonsterCount) return;

        timer = 0f;
        SpawnMonster();
    }

    /// <summary>Pulls absurd serialized values back into range, once, loudly.</summary>
    private void ClampLimits()
    {
        if (spawnInterval >= MinSpawnInterval && maxMonsterCount <= MaxMonsterCeiling) return;

        if (!_warnedAboutLimits)
        {
            Debug.LogWarning($"[MonsterSpawner] Scene values out of range " +
                             $"(interval {spawnInterval}s, cap {maxMonsterCount}) — clamped to " +
                             $"{MinSpawnInterval}s / {MaxMonsterCeiling}. " +
                             "Run 'Idle Explorers → Setup Everything' to correct the scene.");
            _warnedAboutLimits = true;
        }

        spawnInterval   = Mathf.Max(MinSpawnInterval, spawnInterval);
        maxMonsterCount = Mathf.Min(MaxMonsterCeiling, maxMonsterCount);
    }

    void SpawnMonster()
    {
        if (!ResolveMonster(out GameObject prefab, out MonsterData data)) return;
        if (!TryFindSpawnPoint(out Vector3 position)) return;

        GameObject monster = Instantiate(prefab, position, Quaternion.identity);
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
    /// Where the next monster appears.
    ///
    /// A map that places MonsterCamps gets its monsters in those camps; every other
    /// map keeps the ring around the player. The ring stays the default deliberately —
    /// it is the only placement that works on a map nobody has authored camps for, and
    /// replacing it outright is how the previous world-space box broke the moment a
    /// second map existed.
    /// </summary>
    private bool TryFindSpawnPoint(out Vector3 position)
    {
        var camp = MonsterCamp.PickRandom();
        if (camp != null && camp.TryPickSpawnPoint(out position)) return true;

        return TryFindSpawnPointNearPlayer(out position);
    }

    /// <summary>
    /// Finds walkable ground in the ring around the player.
    ///
    /// The height matters as much as the horizontal position. Sampling from a fixed
    /// altitude — the old code used y = 10 — needs a search radius larger than that
    /// altitude, or every sample fails no matter how good the horizontal position is.
    /// Starting from the player's own height removes the guesswork.
    /// </summary>
    private bool TryFindSpawnPointNearPlayer(out Vector3 position)
    {
        position = Vector3.zero;

        Vector3 origin = PlayerPosition();

        float minRadius = Mathf.Max(1f, minSpawnRadius);
        float maxRadius = Mathf.Max(minRadius + 1f, maxSpawnRadius);

        for (int attempt = 0; attempt < PlacementAttempts; attempt++)
        {
            float angle  = Random.value * Mathf.PI * 2f;
            float radius = Mathf.Lerp(minRadius, maxRadius, Random.value);

            Vector3 candidate = origin + new Vector3(Mathf.Sin(angle), 0f, Mathf.Cos(angle)) * radius;

            // Drop to the ground first so the NavMesh sample starts near the surface
            // rather than in the air above it.
            if (Physics.Raycast(candidate + Vector3.up * 50f, Vector3.down,
                                out RaycastHit hit, 200f, ~0, QueryTriggerInteraction.Ignore))
                candidate = hit.point;

            if (!NavMesh.SamplePosition(candidate, out NavMeshHit navHit, 6f, NavMesh.AllAreas))
                continue;

            position = navHit.position;
            return true;
        }

        if (!_warnedAboutPlacement)
        {
            Debug.LogWarning($"[MonsterSpawner] No walkable ground found within " +
                             $"{minRadius}-{maxRadius} units of the player after {PlacementAttempts} " +
                             "attempts. Is the NavMesh baked? " +
                             "Run 'Idle Explorers → Setup Everything'.");
            _warnedAboutPlacement = true;
        }
        return false;
    }

    /// <summary>
    /// Where to centre the spawn ring. The player, or this spawner if there is none
    /// yet — so a map with no player still populates rather than silently staying empty.
    /// </summary>
    private Vector3 PlayerPosition()
    {
        if (_player == null)
        {
            var tagged = GameObject.FindWithTag("Player");
            if (tagged != null) _player = tagged.transform;
        }

        return _player != null ? _player.position : transform.position;
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

        // ══ A SPAWNER NEVER SPAWNS A BOSS ══════════════════════════════════
        //
        // The throne map's defaultMonsterId is goblin_king, because that is what a
        // player fights there -- and this pooled, respawning, population-capped
        // spawner read it as an instruction and produced Goblin Kings without end.
        //
        // GoblinThroneSetup.PlaceTheKing already puts exactly one in the scene, and
        // its own comment explains why a spawner is wrong for a boss: it must be one,
        // it must not come back mid-fight, and it must not share a population budget
        // with goblins.
        //
        // Enforced here rather than by authoring the map without a default, because
        // the default is also what the ACTIVITY system reads to know what you are
        // fighting. Both need it; only one should act on it.
        if (GameManager.Content?.GetMonster(map.defaultMonsterId) is { isBoss: true }) return false;

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

    void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(0.9f, 0.3f, 0.3f, 0.5f);
        Vector3 centre = Application.isPlaying ? PlayerPosition() : transform.position;
        Gizmos.DrawWireSphere(centre, minSpawnRadius);
        Gizmos.DrawWireSphere(centre, maxSpawnRadius);
    }
}
