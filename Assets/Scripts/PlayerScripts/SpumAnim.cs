using UnityEngine;

/// <summary>
/// The one place that knows how SPUM's animator is actually wired.
///
/// ══ WHY THE CORPSES STOOD BACK UP ═════════════════════════════════════════════
///
/// SPUMController.controller declares five parameters, and only two of them are
/// booleans:
///
///     1_Move     Bool
///     2_Attack   TRIGGER
///     3_Damaged  TRIGGER
///     4_Death    TRIGGER
///     isDeath    Bool
///
/// Every call site in this project used SetBool on all of them. Unity does not
/// complain — a trigger is stored as a bool internally, so SetBool(true) fires it and
/// the transition consumes it — which is why attacks looked fine and nothing pointed
/// at the real problem.
///
/// Death was the one that broke. The DEATH state has exactly one outgoing transition:
/// to IDLE, with `hasExitTime` at 0.982 and the single condition **isDeath == false**.
/// Nothing in the project ever set isDeath, so every corpse played its death clip and
/// then stood up in its idle pose, exactly as reported.
///
/// MonsterController had a coroutine meant to prevent this by freezing the animator
/// once the clip finished. It polled `IsName("4_Death")` — but 4_Death is the
/// PARAMETER name; the STATE is called "DEATH". The match never fired, the animator
/// was never frozen, and the coroutine simply spun every frame for the corpse's whole
/// life. Setting isDeath is the fix the controller was authored to expect, so the
/// coroutine is gone.
///
/// Names live here as constants and the types are respected, so the next person to
/// animate something cannot get this wrong by copying a call site.
/// </summary>
public static class SpumAnim
{
    public const string Move    = "1_Move";     // Bool
    public const string Attack  = "2_Attack";   // Trigger
    public const string Damaged = "3_Damaged";  // Trigger
    public const string Death   = "4_Death";    // Trigger
    public const string IsDead  = "isDeath";    // Bool — gates DEATH → IDLE

    /// <summary>Walking or standing still.</summary>
    public static void SetMoving(Animator anim, bool moving)
    {
        if (anim == null) return;
        anim.SetBool(Move, moving);
    }

    /// <summary>
    /// One swing. A trigger, so this plays the clip once and returns to idle — call it
    /// again for the next swing rather than holding it on.
    /// </summary>
    public static void PlayAttack(Animator anim)
    {
        if (anim == null) return;
        anim.SetBool(Move, false);
        anim.SetTrigger(Attack);
    }

    /// <summary>Cancels a swing that has been triggered but not yet consumed.</summary>
    public static void CancelAttack(Animator anim)
    {
        if (anim == null) return;
        anim.ResetTrigger(Attack);
    }

    /// <summary>One flinch.</summary>
    public static void PlayHurt(Animator anim)
    {
        if (anim == null) return;
        anim.SetBool(Move, false);
        anim.SetTrigger(Damaged);
    }

    /// <summary>
    /// Dies, and STAYS dead.
    ///
    /// The bool is the load-bearing half: it is the only condition on the transition
    /// out of DEATH, so without it the corpse returns to idle when the clip ends.
    /// </summary>
    public static void PlayDeath(Animator anim)
    {
        if (anim == null) return;

        anim.SetBool(Move, false);
        anim.ResetTrigger(Attack);
        anim.ResetTrigger(Damaged);

        anim.SetBool(IsDead, true);
        anim.SetTrigger(Death);
    }

    /// <summary>
    /// Back on their feet. Clears everything PlayDeath set, including the triggers,
    /// so a queued death cannot fire the moment the character revives.
    /// </summary>
    public static void Revive(Animator anim)
    {
        if (anim == null) return;

        anim.SetBool(IsDead, false);
        anim.ResetTrigger(Death);
        anim.ResetTrigger(Damaged);
        anim.ResetTrigger(Attack);
        anim.SetBool(Move, false);

        // A corpse that was frozen by older code would otherwise revive as a statue.
        anim.speed = 1f;
    }
}
