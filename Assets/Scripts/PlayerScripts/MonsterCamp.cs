using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// A place on the map where its monster lives.
///
/// MonsterSpawner's default is a ring around the player, and that is the right default:
/// it works on any map without knowing its coordinates, and it was written to replace
/// a hardcoded world-space box that broke the moment a second map existed. But a ring
/// means monsters materialise around you wherever you stand, which reads as a spawner
/// rather than as a place. A map that wants its enemies somewhere in particular drops
/// these instead, and the spawner prefers them.
///
/// Maps place these from their own layout — the Hollow marks them in its ASCII grid —
/// so a camp is authored as part of the picture of the map rather than as a coordinate
/// typed into a component.
///
/// The registry is static because the spawner asks on a timer and FindObjectsByType on
/// a timer is a scene walk nobody needs. It is kept correct by OnEnable/OnDisable
/// rather than by a scene scan, so unloading a map takes its camps with it.
/// </summary>
public class MonsterCamp : MonoBehaviour
{
    [Tooltip("How far from this point monsters may appear.")]
    public float radius = 7f;

    [Tooltip("Attempts to find walkable ground inside this camp before giving up on it.")]
    public int placementAttempts = 6;

    /// <summary>Every camp in every loaded scene. Empty on maps that use the ring.</summary>
    public static readonly List<MonsterCamp> Active = new();

    void OnEnable()  => Active.Add(this);
    void OnDisable() => Active.Remove(this);

    /// <summary>
    /// A walkable point inside this camp, or false when there is none.
    ///
    /// Drops to the ground before sampling for the same reason the ring does: a
    /// NavMesh sample that starts fifty units in the air needs a search radius larger
    /// than the drop, and a camp on a hillside would otherwise never place anything.
    /// </summary>
    public bool TryPickSpawnPoint(out Vector3 position)
    {
        position = Vector3.zero;

        for (int attempt = 0; attempt < Mathf.Max(1, placementAttempts); attempt++)
        {
            Vector2 offset    = Random.insideUnitCircle * Mathf.Max(0.5f, radius);
            Vector3 candidate = transform.position + new Vector3(offset.x, 0f, offset.y);

            if (Physics.Raycast(candidate + Vector3.up * 50f, Vector3.down,
                                out RaycastHit hit, 200f, ~0, QueryTriggerInteraction.Ignore))
                candidate = hit.point;

            if (!NavMesh.SamplePosition(candidate, out NavMeshHit navHit, 6f, NavMesh.AllAreas))
                continue;

            position = navHit.position;
            return true;
        }

        return false;
    }

    /// <summary>
    /// A random camp, or null when none are loaded.
    ///
    /// Uniform across camps rather than weighted toward the player: a camp you have
    /// cleared should refill while you are elsewhere, which is what makes it a place
    /// you can come back to rather than a spawn radius following you around.
    /// </summary>
    public static MonsterCamp PickRandom()
    {
        // Drop anything destroyed without OnDisable running — a map unload normally
        // cleans up after itself, but a destroyed Unity object still compares equal to
        // null while dereferencing it throws, and this is called on a timer.
        for (int i = Active.Count - 1; i >= 0; i--)
            if (Active[i] == null) Active.RemoveAt(i);

        if (Active.Count == 0) return null;
        return Active[UnityEngine.Random.Range(0, Active.Count)];
    }

    void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(0.9f, 0.35f, 0.25f, 0.35f);
        Gizmos.DrawWireSphere(transform.position, radius);
    }
}
