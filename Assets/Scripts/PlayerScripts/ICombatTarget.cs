using UnityEngine;

/// <summary>
/// Something the player can select and hit.
///
/// ══ WHY THIS EXISTS ═══════════════════════════════════════════════════════════
///
/// Because the Goblin King could not be attacked. Not "was hard to attack" -- there
/// was no code path from a click, from auto-mode, or from an ability to a boss at
/// all. PlayerController held its target as a MonsterController and BossController is
/// deliberately not one, so the King was invisible to every line that chooses what to
/// swing at. He stood in his arena throwing cones at somebody who could only watch.
///
/// The one thing that DID reach him was Bladestorm, because it had been written with
/// a second loop over BossControllers bolted on beside the first. That is the shape
/// this replaces: the alternative to an abstraction here is every future targeting
/// feature remembering to write itself twice, and the day one of them forgets is a
/// boss that quietly cannot be hit by the newest ability.
///
/// ══ WHY NOT MAKE BossController A MonsterController ═══════════════════════════
///
/// Inheritance would bring the wander, the aggro timer, the respawn and the loot roll
/// with it -- all of which a boss must not have, and all of which live in a private
/// Update that cannot be overridden. See the argument at the top of BossController.
/// It has not changed; what changed is that "they share nothing" was never quite
/// true. They share exactly this: you can point at them, and you can hurt them.
///
/// ══ WHAT IS DELIBERATELY NOT IN HERE ══════════════════════════════════════════
///
/// Slow, aggro-drop, set-bonus interaction and loot. Those are things one does to a
/// MONSTER, and a caller that needs them casts. A boss that could be slowed by a
/// frost ability or made to forget you by a stealth one would be a boss whose fight
/// is decided by which buttons you brought, and the encounter is deliberately not
/// that: it is a damage check against a clock.
/// </summary>
public interface ICombatTarget
{
    /// <summary>Where it is. Both implementers are MonoBehaviours, so this comes free.</summary>
    Transform transform { get; }

    /// <summary>For highlighting the selection. Also free.</summary>
    GameObject gameObject { get; }

    /// <summary>Its name as a player would say it, for logs and messages.</summary>
    string TargetName { get; }

    /// <summary>
    /// Worth pointing at.
    ///
    /// A method rather than a property purely because MonsterController already had
    /// it as one, and changing that would touch a dozen callers to no effect.
    /// </summary>
    bool IsAlive();

    /// <summary>How much of a swing it turns aside. Fed to StatBlock.DamageThrough.</summary>
    float Armor { get; }

    /// <summary>
    /// Takes a hit.
    ///
    /// What the number MEANS differs between the two, and honestly so. A monster's
    /// health is the client's own and this is the whole event. The King's is the
    /// server's, and the amount here is a PREDICTION -- BossController reports the
    /// swing and lets the server price it, then corrects the bar. Callers do not need
    /// to know which, and the day one starts caring is the day the boss becomes
    /// cheatable.
    /// </summary>
    void TakeDamage(double amount, bool wasCrit);
}
