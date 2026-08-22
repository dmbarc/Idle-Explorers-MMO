using UnityEngine;

/// <summary>
/// The Tinkerer's Deploy Turret. It fires at whatever is closest, and it is not fussy.
///
/// Built in code rather than from a prefab so the ability works before any turret art
/// exists — the visual is an AbilityVFX lookup that no-ops when the prefab is absent,
/// leaving a functional but invisible turret rather than a broken ability.
/// </summary>
public class TurretController : MonoBehaviour
{
    private double _damage;
    private float  _range;
    private float  _expiresAt;
    private float  _fireInterval = 1.2f;
    private float  _nextFireAt;

    /// <summary>Places a turret that lives for a fixed duration and then removes itself.</summary>
    public static TurretController Deploy(Vector3 position, double damage, float range, float duration)
    {
        var go = new GameObject("DeployedTurret");
        go.transform.position = position;

        var turret = go.AddComponent<TurretController>();
        turret._damage    = damage;
        turret._range     = Mathf.Max(1f, range);
        turret._expiresAt = Time.time + Mathf.Max(1f, duration);

        // Optional art. A missing prefab logs once and is otherwise silent.
        AbilityVFX.PlayAttached("turret", go.transform, lifetime: 0f);

        Object.Destroy(go, Mathf.Max(1f, duration));
        return turret;
    }

    void Update()
    {
        if (Time.time >= _expiresAt) return;
        if (Time.time < _nextFireAt) return;

        var target = NearestTarget();
        if (target == null) return;

        target.TakeDamage(_damage);
        AbilityVFX.Play("impact", target.transform.position, 1.5f);

        _nextFireAt = Time.time + _fireInterval;
    }

    private MonsterController NearestTarget()
    {
        MonsterController best = null;
        float bestDistance = _range;

        foreach (var m in Object.FindObjectsByType<MonsterController>(FindObjectsInactive.Exclude))
        {
            if (m == null || !m.IsAlive()) continue;

            float distance = Vector3.Distance(transform.position, m.transform.position);
            if (distance > bestDistance) continue;

            best         = m;
            bestDistance = distance;
        }

        return best;
    }

    void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(1f, 0.6f, 0.1f, 0.35f);
        Gizmos.DrawWireSphere(transform.position, _range);
    }
}
