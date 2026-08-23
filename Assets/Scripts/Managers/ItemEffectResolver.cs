using UnityEngine;

/// <summary>
/// The one place item effects are interpreted — consumables, equipment procs, and
/// the developer Mystic Gem all run through here.
///
/// Bulk paths matter as much as live ones. An idle game's proc that only fires
/// while you are watching is decorative, so AggregateMultiplier exists to let
/// offline accrual apply the same effects as expected value rather than rolling
/// per action, exactly as loot drops already do.
/// </summary>
public static class ItemEffectResolver
{
    // ── Consumables ───────────────────────────────────────────────────────────

    /// <summary>
    /// Uses one of an item from a specific inventory slot. Returns false without
    /// consuming when nothing could be applied, so a wasted click never eats an item.
    /// </summary>
    public static bool Consume(string itemId, int slotIndex)
    {
        var item = GameManager.Content?.GetItem(itemId);
        if (item?.effects == null) return false;

        bool appliedAny = false;

        foreach (var effect in item.effects)
        {
            if (effect == null || effect.trigger != "onConsume") continue;
            if (!Roll(effect)) continue;
            if (Apply(effect, item)) appliedAny = true;
        }

        if (!appliedAny) return false;

        GameManager.Inventory?.RemoveFromSlot(slotIndex, 1);
        return true;
    }

    /// <summary>Runs a single effect. Returns false when it could not take hold.</summary>
    private static bool Apply(ItemEffect effect, ItemData item)
    {
        switch (effect.action)
        {
            case "heal":
                return ApplyHeal(effect, item);

            case "damageSelf":
                return ApplyDamageSelf(effect, item);

            case "grantAfkTime":
                return ApplyGrantAfkTime(effect);

            case "changeClass":
                return ApplyChangeClass();

            default:
                Debug.LogWarning($"[ItemEffect] '{item.id}' has unhandled action '{effect.action}'.");
                return false;
        }
    }

    private static bool ApplyHeal(ItemEffect effect, ItemData item)
    {
        var player = Object.FindAnyObjectByType<PlayerController>();
        if (player == null)
        {
            GameEvents.FireToast("You can only eat that out in the world.");
            return false;
        }

        if (!player.Heal(effect.magnitude))
        {
            GameEvents.FireToast("Already at full health.");
            return false;
        }

        GameEvents.FireToast($"+{effect.magnitude:0} HP from {item.DisplayName}.");
        return true;
    }

    private static bool ApplyDamageSelf(ItemEffect effect, ItemData item)
    {
        var player = Object.FindAnyObjectByType<PlayerController>();
        if (player == null)
        {
            GameEvents.FireToast("You can only eat that out in the world.");
            return false;
        }

        player.TakeDamage(effect.magnitude);
        GameEvents.FireToast($"{item.DisplayName} bites back — {effect.magnitude:0} damage.");
        return true;
    }

    /// <summary>
    /// Fast-forwards the current activity by a fixed span.
    ///
    /// Deliberately routed through the real ProcessAFKRewards rather than granting
    /// rewards directly: that way it exercises the same accrual, the same crafting
    /// truncation and the same summary the game uses after an actual logout, instead
    /// of testing a debug branch that could quietly drift from the real one.
    /// </summary>
    private static bool ApplyGrantAfkTime(ItemEffect effect)
    {
        var character = CharacterManager.Current;
        var activity  = GameManager.Activity;

        if (character == null || activity == null) return false;

        if (character.currentActivity == null || string.IsNullOrEmpty(character.currentActivity.skillId))
        {
            GameEvents.FireToast("Pick an activity before using this.");
            return false;
        }

        long seconds = (long)Mathf.Max(1f, effect.magnitude);

        // Rewind the logout stamp so the standard accrual sees exactly this window.
        long now = System.DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        character.lastLogoutUnixTime = now - seconds;

        var summary = activity.ProcessAFKRewards(character);
        if (summary == null)
        {
            GameEvents.FireToast("Nothing accrued.");
            return false;
        }

        GameManager.UI?.Push<AFKSummaryScreen>();
        return true;
    }

    /// <summary>
    /// Opens the class picker. Returns true — and therefore consumes the item — as
    /// soon as the modal is up, not when a class is chosen.
    ///
    /// That is a real trade-off and the modal states it: holding the item back until
    /// a choice is made would mean an inventory slot in limbo and a modal that must
    /// not be dismissed by any route, including a screen change or a death. Spending
    /// it on open keeps the item flow identical to every other consumable.
    /// </summary>
    private static bool ApplyChangeClass()
    {
        if (CharacterManager.Current == null)
        {
            GameEvents.FireToast("No character selected.");
            return false;
        }

        var ui = GameManager.UI;
        if (ui == null)
        {
            GameEvents.FireToast("Cannot open the class picker right now.");
            return false;
        }

        ui.Push<ClassChangeModal>();
        return true;
    }

    // ── Equipment triggers ────────────────────────────────────────────────────

    /// <summary>
    /// Fires every equipped item's effects for a trigger. Live gameplay path —
    /// offline accrual uses AggregateMultiplier instead.
    /// </summary>
    public static void Fire(string trigger, string param = null)
    {
        var equipment = GameManager.Equipment;
        if (equipment == null) return;

        foreach (var item in equipment.EquippedItems())
        {
            if (item?.effects == null) continue;

            foreach (var effect in item.effects)
            {
                if (effect == null || effect.trigger != trigger) continue;
                if (!MatchesParam(effect, param)) continue;
                if (!Roll(effect)) continue;

                FireProc(effect, item);
            }
        }
    }

    private static void FireProc(ItemEffect effect, ItemData item)
    {
        switch (effect.action)
        {
            case "castEffect":
                // The visual is resolved by id, so new procs are a data change.
                AbilityVFX.Play(effect.param, PlayerPosition());
                GameEvents.FireToast($"✦ {item.DisplayName}");
                break;

            case "bonusXp":
                GameManager.Skills?.AddSkillXP(effect.param, (long)effect.magnitude);
                break;

            case "extraLoot":
                GameManager.Inventory?.AddItem(effect.param, (long)Mathf.Max(1f, effect.magnitude));
                break;

            case "doubleOutput":
                // Handled at the point of production, not here — the craft loop asks
                // AggregateMultiplier so the bonus lands on the actual output count.
                break;
        }
    }

    /// <summary>
    /// Combined multiplier from every equipped item matching a trigger and action.
    ///
    /// Expected value, not a roll: bulk accrual can cover tens of thousands of
    /// actions, and rolling each one would both hang the login and produce a
    /// different answer than the live path for the same equipment.
    /// </summary>
    public static float AggregateMultiplier(string trigger, string action, string param = null)
    {
        var equipment = GameManager.Equipment;
        if (equipment == null) return 1f;

        float multiplier = 1f;

        foreach (var item in equipment.EquippedItems())
        {
            if (item?.effects == null) continue;

            foreach (var effect in item.effects)
            {
                if (effect == null || effect.trigger != trigger || effect.action != action) continue;
                if (!MatchesParam(effect, param)) continue;

                // A 15% chance to double is, over many actions, a 1.15x multiplier.
                multiplier *= 1f + effect.chance * (effect.magnitude - 1f);
            }
        }

        return Mathf.Max(0.01f, multiplier);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>An effect with no param applies everywhere; one with a param is scoped to it.</summary>
    private static bool MatchesParam(ItemEffect effect, string param)
    {
        if (string.IsNullOrEmpty(param)) return true;
        if (string.IsNullOrEmpty(effect.param)) return true;
        return effect.param == param;
    }

    private static bool Roll(ItemEffect effect) =>
        effect.AlwaysFires || Random.value <= effect.chance;

    private static Vector3 PlayerPosition()
    {
        var player = Object.FindAnyObjectByType<PlayerController>();
        return player != null ? player.transform.position : Vector3.zero;
    }
}
