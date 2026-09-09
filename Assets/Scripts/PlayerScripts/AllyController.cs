using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// A monster fighting on your side, briefly.
///
/// ══ WHY IT IS NOT A MonsterController WITH A FLAG ═════════════════════════════
///
/// A flag would have to be read by everything that finds monsters: the spawner's
/// population count, the player's targeting, every AoE, the kill ledger. Miss one
/// and you get a summoned goblin the player can accidentally attack, or one that
/// counts toward the boss gate when it dies.
///
/// A separate type on its own layer means the default answer everywhere is "not a
/// monster", and the places that DO want it have to say so.
///
/// ══ WHY IT DOES NOT DROP LOOT OR GRANT EXPERIENCE ═════════════════════════════
///
/// Its kills are not the player's kills. A spear that summons a goblin which then
/// credits goblin kills would be a spear that opens the boss portal by itself, and
/// the whole point of the active-kill gate is that the player was there for them.
/// </summary>
public class AllyController : MonoBehaviour
{
    /// <summary>How far it will wander from its owner before coming back.</summary>
    private const float LeashRange = 14f;

    /// <summary>How far it looks for something to hit.</summary>
    private const float SeekRange = 10f;

    private MonsterData _data;
    private Transform   _owner;
    private NavMeshAgent _agent;
    private Animator     _anim;
    private SpriteFacing _facing;

    private double _damage;
    private float  _expiresAt;
    private float  _nextSwingAt;

    private MonsterController _target;

    /// <summary>
    /// Summons an ally beside its owner.
    /// </summary>
    /// <param name="monsterId">What to look like and how hard to hit.</param>
    /// <param name="damage">Resolved by the caller from the summoner's own damage.</param>
    /// <param name="seconds">How long before it leaves.</param>
    public static AllyController Summon(string monsterId, Transform owner,
                                        double damage, float seconds)
    {
        if (owner == null) return null;

        var data = GameManager.Content?.GetMonster(monsterId);
        if (data == null) return null;

        // Beside rather than on top, so it does not spawn inside the player and get
        // shoved somewhere strange by the navmesh on its first frame.
        Vector3 beside = owner.position + owner.right * 1.5f;

        if (NavMesh.SamplePosition(beside, out NavMeshHit hit, 4f, NavMesh.AllAreas))
            beside = hit.position;

        var prefab = Resources.Load<GameObject>($"Monsters/{monsterId}");

        GameObject go = prefab != null
            ? Instantiate(prefab, beside, Quaternion.identity)
            : new GameObject($"Ally_{monsterId}");

        go.name = $"Ally_{data.DisplayName}";
        go.transform.position = beside;

        // Whatever made it a monster is not wanted. An ally that kept its
        // MonsterController would be attackable by its own summoner.
        foreach (var monster in go.GetComponentsInChildren<MonsterController>())
            Destroy(monster);

        var ally = go.AddComponent<AllyController>();

        ally._data      = data;
        ally._owner     = owner;
        ally._damage    = damage;
        ally._expiresAt = Time.time + Mathf.Max(1f, seconds);

        return ally;
    }

    private void Start()
    {
        _agent = GetComponent<NavMeshAgent>();

        if (_agent == null)
        {
            _agent = gameObject.AddComponent<NavMeshAgent>();
            _agent.speed = 4.5f;
            _agent.radius = 0.4f;
        }

        _anim   = GetComponentInChildren<Animator>();
        _facing = SpriteFacing.Attach(gameObject);

        GameEvents.FireToast($"✦ A {_data.DisplayName} joins you.", ChatTone.Good);
    }

    private void Update()
    {
        if (Time.time >= _expiresAt) { Leave(); return; }
        if (_owner == null)          { Leave(); return; }

        if (_target == null || !_target.IsAlive()) _target = FindTarget();

        if (_target == null)
        {
            Follow();
            return;
        }

        if (_facing != null) _facing.LookTarget = _target.transform;

        float distance = Vector3.Distance(transform.position, _target.transform.position);
        float reach    = 2f;

        if (distance > reach)
        {
            if (_agent != null && _agent.isActiveAndEnabled)
            {
                _agent.isStopped = false;
                _agent.SetDestination(_target.transform.position);
            }

            return;
        }

        if (_agent != null && _agent.isActiveAndEnabled) _agent.isStopped = true;

        if (Time.time < _nextSwingAt) return;

        _target.TakeDamage(_damage);
        SpumAnim.PlayAttack(_anim);

        _nextSwingAt = Time.time + Mathf.Max(0.5f, _data.attackSpeedSeconds);
    }

    /// <summary>
    /// The nearest living monster, within reach of the OWNER rather than of itself.
    ///
    /// Leashed to the player deliberately: an ally that chased something across the
    /// map would pull half a camp back with it, and the player would spend the
    /// summon's thirty seconds fighting a fight they did not pick.
    /// </summary>
    private MonsterController FindTarget()
    {
        MonsterController best = null;
        float bestDistance = SeekRange;

        foreach (var monster in Object.FindObjectsByType<MonsterController>(FindObjectsInactive.Exclude))
        {
            if (monster == null || !monster.IsAlive()) continue;

            if (Vector3.Distance(_owner.position, monster.transform.position) > LeashRange) continue;

            float distance = Vector3.Distance(transform.position, monster.transform.position);
            if (distance >= bestDistance) continue;

            best = monster;
            bestDistance = distance;
        }

        return best;
    }

    private void Follow()
    {
        if (_agent == null || !_agent.isActiveAndEnabled) return;

        if (Vector3.Distance(transform.position, _owner.position) < 3f)
        {
            _agent.isStopped = true;
            return;
        }

        _agent.isStopped = false;
        _agent.SetDestination(_owner.position);
    }

    private void Leave()
    {
        AbilityVFX.Play("smoke", transform.position);
        Destroy(gameObject);
    }
}
