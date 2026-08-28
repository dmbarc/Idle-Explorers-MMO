using System.Collections.Generic;
using IdleExplorers.Backend;
using IdleExplorers.Rules;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Draws the map's shared monster population, and reports what we hit.
///
/// ══ WHAT THIS REPLACES ════════════════════════════════════════════════════════
///
/// MonsterSpawner making its own. Every client spawned a private ring of goblins
/// around its own player: two people standing in the same clearing fought two
/// different sets occupying the same ground, and neither could see the other's.
///
/// ══ WHY IT RECONCILES RATHER THAN REBUILDS ════════════════════════════════════
///
/// The same reasoning as RemotePlayerView, and the same trap: each report is the
/// whole truth about a map, so the naive version destroys every monster and builds
/// them again. That would restart every animation twice a second and throw away the
/// health bar's own tweening — and, worse, invalidate the target the player is
/// currently swinging at on every single poll.
///
/// So: update the ones still there, spawn the new, remove the departed.
///
/// ══ WHAT THE SERVER DECIDES AND WHAT THIS DOES ════════════════════════════════
///
/// The server decides which monsters exist, where they stand, and how much health
/// they have. This decides how they look doing it — the animation, the health bar,
/// the damage numbers, the corpse fading out.
///
/// Damage is PREDICTED locally so a swing lands immediately on screen, and reported
/// so everybody else sees it. The server's number wins on the next poll. In the
/// ordinary case the two agree, because both are computed from the same shared rules.
///
/// ══ WHAT IT DELIBERATELY DOES NOT DO ══════════════════════════════════════════
///
/// Pay anybody. Loot and experience come from settlement, which integrates each
/// player's own time — so two people fighting the same goblin are both paid for the
/// time they spent, and neither is paid twice. A monster dying here is a thing that
/// was SEEN, not a thing that was earned.
/// </summary>
public class MonsterSync : MonoBehaviour
{
    /// <summary>The one instance. Attached to the managers object, like PresenceSync.</summary>
    private static MonsterSync _instance;

    /// <summary>Server id → the thing drawing it.</summary>
    private readonly Dictionary<string, MonsterController> _drawn = new();

    /// <summary>Damage reported by this client and not yet sent, per server id.</summary>
    private readonly Dictionary<string, double> _pending = new();

    /// <summary>When each monster's pending damage started accumulating.</summary>
    private readonly Dictionary<string, float> _since = new();

    private float _nextFlushAt;
    private bool  _flushing;

    /// <summary>
    /// How often reported damage is sent.
    ///
    /// Twice a second, batched per monster. A request per swing would be a request
    /// per swing per player, for a population whose only job is to be looked at.
    /// </summary>
    private const float FlushSeconds = 0.5f;

    private const string MonsterResourcePath = "Monsters/";
    private const string FallbackPrefabName  = "_default";

    /// <summary>
    /// True when the server owns the population.
    ///
    /// MonsterSpawner asks this before spawning anything. Offline it is false and the
    /// old local spawner runs unchanged, which is what keeps the editor playable with
    /// no network.
    /// </summary>
    public static bool ServerOwnsPopulation =>
        _instance != null && ServerState.IsAuthoritative;

    /// <summary>
    /// Starts drawing, once.
    ///
    /// On the managers object so it lives exactly as long as they do — the same
    /// arrangement as PresenceSync and ServerSync, and for the same reason: a
    /// component on a map object would stop the moment somebody travelled.
    /// </summary>
    public static void Attach(GameObject host)
    {
        if (_instance != null || host == null) return;

        _instance = host.AddComponent<MonsterSync>();
    }

    private void OnDestroy()
    {
        if (_instance == this) _instance = null;
    }

    // ── Drawing what the server reported ──────────────────────────────────────

    /// <summary>Takes the population out of a presence answer.</summary>
    public static void Adopt(WorldMonster[] reported)
    {
        if (_instance != null) _instance.Reconcile(reported);
    }

    private void Reconcile(WorldMonster[] reported)
    {
        // A null list is a server that did not answer, NOT an empty map. Clearing on
        // it would empty the world on every hiccup and refill it two seconds later.
        if (reported == null) return;

        var present = new HashSet<string>();

        foreach (WorldMonster row in reported)
        {
            if (row == null || string.IsNullOrEmpty(row.id)) continue;

            present.Add(row.id);

            if (_drawn.TryGetValue(row.id, out MonsterController drawn) && drawn != null)
            {
                drawn.AdoptServerState(row.health, row.maxHealth, row.secondsDead);
                continue;
            }

            // A corpse we never saw alive is not worth raising just to fade it.
            if (!row.Alive) continue;

            MonsterController spawned = Spawn(row);

            if (spawned != null) _drawn[row.id] = spawned;
        }

        Departed(present);
    }

    /// <summary>Removes anything the server no longer reports.</summary>
    private void Departed(HashSet<string> present)
    {
        List<string> gone = null;

        foreach (var pair in _drawn)
            if (!present.Contains(pair.Key)) (gone ??= new List<string>()).Add(pair.Key);

        if (gone == null) return;

        foreach (string id in gone)
        {
            if (_drawn.TryGetValue(id, out MonsterController drawn) && drawn != null)
                Destroy(drawn.gameObject);

            _drawn.Remove(id);
            _pending.Remove(id);
            _since.Remove(id);
        }
    }

    /// <summary>
    /// Builds one where the server said it stands.
    ///
    /// The reported position is dropped onto the NavMesh first. The server chooses a
    /// rough point from the map's extent because it has no idea where the walls are —
    /// that is the client's NavMesh, and it is the only thing that does.
    /// </summary>
    private MonsterController Spawn(WorldMonster row)
    {
        MonsterData data = GameManager.Content?.GetMonster(row.monsterId);

        if (data == null) return null;

        GameObject prefab = Resources.Load<GameObject>(MonsterResourcePath + row.monsterId)
                         ?? Resources.Load<GameObject>(MonsterResourcePath + FallbackPrefabName);

        if (prefab == null)
        {
            Debug.LogWarning($"[MonsterSync] No prefab for '{row.monsterId}'. " +
                             "Run 'Idle Explorers → Setup Everything'.");
            return null;
        }

        var monster = Instantiate(prefab, Ground(new Vector3(row.x, 0f, row.z)),
                                  Quaternion.identity);

        var controller = monster.GetComponent<MonsterController>();

        if (controller == null)
        {
            Destroy(monster);
            return null;
        }

        // Initialize before Start runs, so the content stats replace the prefab's.
        controller.Initialize(data);
        controller.BindToServer(row.id);
        controller.AdoptServerState(row.health, row.maxHealth, row.secondsDead);

        return controller;
    }

    /// <summary>
    /// Puts a rough point on walkable ground.
    ///
    /// Falls back to the raw point rather than refusing: a monster standing slightly
    /// off the mesh is visible and fightable, and one that never appeared because the
    /// sample missed is a hole in a population everybody else can see.
    /// </summary>
    private static Vector3 Ground(Vector3 where)
    {
        if (Physics.Raycast(where + Vector3.up * 60f, Vector3.down,
                            out RaycastHit hit, 200f,
                            Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            where = hit.point;

        return NavMesh.SamplePosition(where, out NavMeshHit nav, 8f, NavMesh.AllAreas)
            ? nav.position
            : where;
    }

    // ── Reporting what we hit ─────────────────────────────────────────────────

    /// <summary>
    /// Records damage this client dealt, to be sent with the next flush.
    ///
    /// Accumulated rather than sent, because a swing is a frame event and a request
    /// is not. The window each report claims is measured from the FIRST unsent hit,
    /// which is what the server's ceiling is checked against.
    /// </summary>
    public static void ReportDamage(string serverId, double amount)
    {
        if (_instance == null || string.IsNullOrEmpty(serverId) || amount <= 0d) return;

        if (!_instance._pending.ContainsKey(serverId))
            _instance._since[serverId] = Time.time;

        _instance._pending.TryGetValue(serverId, out double already);
        _instance._pending[serverId] = already + amount;
    }

    private void Update()
    {
        if (_flushing || Time.time < _nextFlushAt) return;

        _nextFlushAt = Time.time + FlushSeconds;

        if (_pending.Count == 0) return;
        if (!ServerState.IsAuthoritative || string.IsNullOrEmpty(ServerState.CharacterId)) return;

        _ = FlushAsync();
    }

    private async Awaitable FlushAsync()
    {
        _flushing = true;

        try
        {
            // Copied before awaiting: the dictionary is written by every swing that
            // lands while this request is in flight, and enumerating it across an
            // await is a collection modified during iteration.
            var batch = new List<KeyValuePair<string, double>>(_pending);

            _pending.Clear();

            foreach (var (id, damage) in batch)
            {
                _since.TryGetValue(id, out float started);
                _since.Remove(id);

                double seconds = Mathf.Max(0.1f, Time.time - started);

                StrikeResult result =
                    await GameBackend.Current.StrikeAsync(ServerState.CharacterId, id,
                                                          damage, seconds);

                if (result == null) continue;

                // The server's health is the one that counts. Applied straight away
                // rather than waiting for the next poll, so a kill lands on screen at
                // the moment the server agrees it did.
                if (_drawn.TryGetValue(id, out MonsterController drawn) && drawn != null)
                    drawn.AdoptServerState(result.health, drawn.MaxHealth,
                                           result.alive ? 0d : 0.01d);
            }
        }
        catch (BackendException e)
        {
            ServerState.NoteIfDisplaced(e);

            // Quiet. This runs twice a second, and a toast per failure during a blip
            // is worse than the blip — the same reasoning as the presence loop.
            Debug.LogWarning($"[MonsterSync] {e.Message}");
        }
        finally
        {
            _flushing = false;
        }
    }

    /// <summary>
    /// Forgets everything. Called when the map changes, because the population
    /// belongs to the map and the objects went with the scene.
    /// </summary>
    public static void Clear()
    {
        if (_instance == null) return;

        _instance._drawn.Clear();
        _instance._pending.Clear();
        _instance._since.Clear();
    }

    /// <summary>How long a corpse takes to fade, shared with the server's respawn.</summary>
    public static double CorpseSeconds => Population.RespawnSeconds;
}
