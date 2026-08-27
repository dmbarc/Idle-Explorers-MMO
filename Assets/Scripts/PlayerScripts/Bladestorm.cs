using IdleExplorers.Rules;
using UnityEngine;

/// <summary>
/// The Goblin Destroyer, planted in the ground and spinning.
///
/// ══ WHY IT IS ITS OWN OBJECT ══════════════════════════════════════════════════
///
/// The sword genuinely leaves your hands. It lands somewhere, hurts everything near
/// it for a few seconds, and comes back — which means for those few seconds there is
/// a thing in the world that is not the player and not a monster, and it needs a
/// position, a lifetime and a pulse of its own.
///
/// Running it as a coroutine on the player would tie its lifetime to the player's,
/// and a character who dies mid-whirl would leave a sword spinning forever with
/// nothing to stop it.
///
/// ══ THE HAND IS EMPTY WHILE IT IS AWAY ════════════════════════════════════════
///
/// The main-hand art is hidden for the duration and restored when it returns. Not
/// decoration: a player watching their sword spin in the dirt while an identical one
/// stays in their fist has been told the ability is a lie.
/// </summary>
public class Bladestorm : MonoBehaviour
{
    /// <summary>How often it hits, in seconds.</summary>
    public const float PulseSeconds = 0.5f;

    /// <summary>How fast it visibly spins, in degrees a second.</summary>
    private const float SpinDegreesPerSecond = 900f;

    private double _damagePerPulse;
    private float  _radius;
    private float  _endsAt;
    private float  _nextPulseAt;

    private Transform  _owner;
    private GameObject _art;

    /// <summary>
    /// Throws the sword to a point and starts it spinning.
    /// </summary>
    /// <param name="owner">Who threw it. The sword returns here.</param>
    /// <param name="at">Where it lands.</param>
    /// <param name="damagePerPulse">Already resolved. This only delivers it.</param>
    public static Bladestorm Throw(Transform owner, Vector3 at, double damagePerPulse,
                                   float radius, float seconds)
    {
        var go = new GameObject("Bladestorm");
        go.transform.position = at;

        var storm = go.AddComponent<Bladestorm>();

        storm._owner          = owner;
        storm._damagePerPulse = damagePerPulse;
        storm._radius         = Mathf.Max(0.5f, radius);
        storm._endsAt         = Time.time + Mathf.Max(0.5f, seconds);
        storm._nextPulseAt    = Time.time;

        storm.BuildArt();
        storm.HideTheWeapon(true);

        // A telegraph the player can see, so bystanders know where not to stand --
        // and so the radius on screen is the radius that hits, from one struct.
        TelegraphDecal.Show(AreaShape.Circle(new Ground(at.x, at.z), storm._radius),
                            Mathf.Max(0.5f, seconds));

        AbilityVFX.Play("impact", at);

        return storm;
    }

    private void Update()
    {
        transform.Rotate(Vector3.up, SpinDegreesPerSecond * Time.deltaTime, Space.World);

        if (Time.time >= _nextPulseAt)
        {
            Pulse();
            _nextPulseAt = Time.time + PulseSeconds;
        }

        if (Time.time < _endsAt) return;

        Retrieve();
    }

    /// <summary>
    /// One tick of damage to everything inside.
    ///
    /// The same AreaShape the telegraph was drawn from, so what is painted and what
    /// is hurt are one shape.
    /// </summary>
    private void Pulse()
    {
        var area = AreaShape.Circle(new Ground(transform.position.x, transform.position.z), _radius);

        foreach (var monster in Object.FindObjectsByType<MonsterController>(FindObjectsInactive.Exclude))
        {
            if (monster == null || !monster.IsAlive()) continue;

            var at = new Ground(monster.transform.position.x, monster.transform.position.z);
            if (!area.Contains(at)) continue;

            monster.TakeDamage(_damagePerPulse);
        }

        // Bosses are not MonsterControllers, and a whirlwind that ignored the one
        // enemy it was dropped by the King to fight would be a strange weapon.
        foreach (var boss in Object.FindObjectsByType<BossController>(FindObjectsInactive.Exclude))
        {
            if (boss == null || !boss.IsAlive) continue;

            var at = new Ground(boss.transform.position.x, boss.transform.position.z);
            if (!area.Contains(at)) continue;

            boss.TakeDamage(_damagePerPulse);
        }
    }

    private void Retrieve()
    {
        HideTheWeapon(false);
        Destroy(gameObject);
    }

    /// <summary>Restores the hand even if something destroys this early.</summary>
    private void OnDestroy() => HideTheWeapon(false);

    // ── The sword itself ──────────────────────────────────────────────────────

    /// <summary>
    /// A simple spinning cross, generated rather than modelled.
    ///
    /// Following GroundArrow and the projectile: no prefab, no importer settings, no
    /// meta file. It reads as a blade at the distance the camera sits at, which is
    /// the only distance it is ever seen from.
    /// </summary>
    private void BuildArt()
    {
        _art = GameObject.CreatePrimitive(PrimitiveType.Cube);
        _art.name = "Blade";
        _art.transform.SetParent(transform, false);
        _art.transform.localScale = new Vector3(0.15f, 0.1f, 1.6f);
        _art.transform.localPosition = Vector3.up * 0.6f;

        // No collider: it damages through the pulse above, and a physics collider
        // would shove monsters around the arena as it spun.
        var collider = _art.GetComponent<Collider>();
        if (collider != null) Destroy(collider);

        var renderer = _art.GetComponent<MeshRenderer>();
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
    }

    /// <summary>
    /// Hides or restores the main-hand art on the thrower.
    ///
    /// Found through the rig rather than remembered, because the character may have
    /// been re-dressed while the sword was away -- an equipment change mid-whirl is
    /// rare and entirely possible.
    /// </summary>
    private void HideTheWeapon(bool hidden)
    {
        if (_owner == null) return;

        var slot = EquipmentSlots.Get("mainhand");
        if (slot == null || !slot.RendersOnCharacter) return;

        for (int i = 0; i < slot.SpumParts.Length; i++)
        {
            foreach (var layer in SpumRig.Collect(_owner, slot.SpumParts[i], slot.AncestorFor(i)))
            {
                if (layer?.Renderer == null) continue;

                // Restored to what the rig shipped rather than to "on": a slot with
                // no weapon in it was already hidden, and turning it on would draw
                // the donor prefab's sword.
                layer.Renderer.enabled = hidden ? false : layer.OriginalEnabled;
            }
        }
    }
}
