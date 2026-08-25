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

            case "changeAppearance":
                return ApplyChangeAppearance();

            case "resetSkills":
                return ApplyResetSkills();

            case "damageEquipment":
                return ApplyDamageEquipment(effect);

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
            GameEvents.FireToast("You can only eat that out in the world.", ChatTone.Bad);
            return false;
        }

        if (!player.Heal(effect.magnitude))
        {
            GameEvents.FireToast("Already at full health.", ChatTone.Bad);
            return false;
        }

        GameEvents.FireToast($"+{effect.magnitude:0} HP from {item.DisplayName}.", ChatTone.Good);
        return true;
    }

    private static bool ApplyDamageSelf(ItemEffect effect, ItemData item)
    {
        var player = Object.FindAnyObjectByType<PlayerController>();
        if (player == null)
        {
            GameEvents.FireToast("You can only eat that out in the world.", ChatTone.Bad);
            return false;
        }

        player.TakeDamage(effect.magnitude);
        GameEvents.FireToast($"{item.DisplayName} bites back — {effect.magnitude:0} damage.", ChatTone.Bad);
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
            GameEvents.FireToast("Pick an activity before using this.", ChatTone.Bad);
            return false;
        }

        long seconds = (long)Mathf.Max(1f, effect.magnitude);

        // Rewind the logout stamp so the standard accrual sees exactly this window.
        long now = System.DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        character.lastLogoutUnixTime = now - seconds;

        // Raise the cap to cover the gem. The 24-hour ceiling limits PASSIVE accrual;
        // applying it to time the player bought would quietly deliver a third of a
        // 72-hour gem, and the summary would call it "capped".
        long cap = System.Math.Max(ActivityManager.MaxAFKSeconds, seconds);

        var summary = activity.ProcessAFKRewards(character, cap);
        if (summary == null)
        {
            GameEvents.FireToast("Nothing accrued.", ChatTone.Bad);
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
            GameEvents.FireToast("No character selected.", ChatTone.Bad);
            return false;
        }

        var ui = GameManager.UI;
        if (ui == null)
        {
            GameEvents.FireToast("Cannot open the class picker right now.", ChatTone.Bad);
            return false;
        }

        ui.Push<ClassChangeModal>();
        return true;
    }

    /// <summary>
    /// Opens the appearance editor on the current character.
    ///
    /// Consumes on open, exactly like the Shifting Sigil and for the same reason: an
    /// item held back until a choice is made needs a modal that cannot be escaped by
    /// any route, including a screen change, a death or a logout. The modal states the
    /// trade on its cancel button rather than hiding it.
    /// </summary>
    private static bool ApplyChangeAppearance()
    {
        if (CharacterManager.Current == null)
        {
            GameEvents.FireToast("No character selected.", ChatTone.Bad);
            return false;
        }

        var ui = GameManager.UI;
        if (ui == null)
        {
            GameEvents.FireToast("Cannot open the mirror right now.", ChatTone.Bad);
            return false;
        }

        ui.Push<AppearanceModal>();
        return true;
    }

    /// <summary>
    /// Puts every skill back to level 1 with no XP. A testing tool, so it is inert
    /// outside the Editor and development builds — an item that silently deletes a
    /// player's progress has no business existing in a shipped game.
    ///
    /// Deliberately leaves character level, talents and equipment alone. Skill XP and
    /// character XP are separate curves, and SaveManager.BackfillCharacterXP only ever
    /// raises, so wiping skills cannot claw back levels or talent points either way.
    /// That keeps this useful for re-testing the mining and smithing gates without
    /// dismantling everything else about the character.
    /// </summary>
    private static bool ApplyResetSkills()
    {
        if (!DevTools.Enabled)
        {
            GameEvents.FireToast("That does nothing here.", ChatTone.Bad);
            return false;
        }

        var character = CharacterManager.Current;
        if (character?.skills == null || character.skills.Count == 0)
        {
            GameEvents.FireToast("No skills to reset.", ChatTone.Bad);
            return false;
        }

        int reset = 0;
        foreach (var skill in character.skills)
        {
            if (skill == null) continue;
            if (skill.level <= 1 && skill.xp <= 0) continue;

            skill.level = 1;
            skill.xp    = 0;
            reset++;
        }

        if (reset == 0)
        {
            GameEvents.FireToast("Every skill is already at level 1.", ChatTone.Bad);
            return false;
        }

        GameManager.Save?.Save();

        // The activity snapshot may now describe work this character can no longer do
        // — cooking at level 1 cannot make what level 40 could. Re-validated on the
        // next station interaction, but the readout should not keep claiming it.
        GameEvents.FireActivityChanged(GameManager.Activity?.CurrentActivity);

        GameEvents.FireToast($"Reset {reset} skill(s) to level 1.");
        Debug.Log($"[DevTools] Reset {reset} skill(s) on {character.characterName}.");
        return true;
    }

    /// <summary>
    /// Wears down a random worn piece. A testing tool, not a curse.
    ///
    /// Durability is slow by design — an hour of being hit to see a helmet break — so
    /// everything downstream of it (the break message, stats dropping off, a set
    /// falling below its threshold, the repair cost) was expensive to look at even
    /// once. This makes all of that reachable in a click.
    ///
    /// The amount is jittered around the effect's magnitude rather than fixed, because
    /// the interesting states are the ones either side of zero and a fixed number
    /// walks a 70-point helmet down in exactly the same steps every time.
    /// </summary>
    private static bool ApplyDamageEquipment(ItemEffect effect)
    {
        var equipment = GameManager.Equipment;
        if (equipment == null)
        {
            GameEvents.FireToast("You are not wearing anything.", ChatTone.Bad);
            return false;
        }

        int nominal = Mathf.Max(1, Mathf.RoundToInt(effect.magnitude));
        int points  = UnityEngine.Random.Range(Mathf.Max(1, nominal / 2), nominal * 2 + 1);

        int lost = equipment.DamageRandom(points);
        if (lost <= 0)
        {
            GameEvents.FireToast("Nothing you are wearing can wear out any further.", ChatTone.Bad);
            return false;
        }

        // DamageSlot names the piece if this broke it; otherwise say what happened, or
        // the hammer reads as having done nothing at all.
        GameEvents.FireToast($"The hammer rings — {lost} durability gone.", ChatTone.Warning);
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
