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
                return ApplyGrantAfkTime(effect, item);

            case "buff":
                return ApplyBuff(effect, item);

            case "changeClass":
                return ApplyChangeClass();

            case "changeAppearance":
                return ApplyChangeAppearance();

            case "resetSkills":
                return ApplyResetSkills();

            case "damageEquipment":
                return ApplyDamageEquipment(effect);

            case "fuseInto":
                return ApplyFuse(effect, item);

            default:
                Debug.LogWarning($"[ItemEffect] '{item.id}' has unhandled action '{effect.action}'.");
                return false;
        }
    }

    /// <summary>
    /// Consumes a second item to make a third.
    ///
    /// ══ WHY BOTH HALVES CARRY THE SAME EFFECT ═════════════════════════════════
    ///
    /// The Slasher and the Smasher each have a Use that eats the other. Either can be
    /// the one clicked, and the result is the same weapon -- which is what a player
    /// expects, and what saves them having to work out which half is the "real" one.
    ///
    /// ══ WHY IT REFUSES RATHER THAN PARTIALLY SUCCEEDS ═════════════════════════
    ///
    /// Everything is checked before anything is spent. A fuse that consumed the
    /// partner and then found no room for the result would destroy two of the rarest
    /// items in the game and hand back nothing, and there would be no way to tell
    /// afterwards whether it had ever worked.
    ///
    /// TODO(Phase 4): the server owns this once crafting moves. The check-then-spend
    /// shape here is deliberately the shape that transaction will take.
    /// </summary>
    private static bool ApplyFuse(ItemEffect effect, ItemData item)
    {
        var inventory = GameManager.Inventory;
        var content   = GameManager.Content;

        if (inventory == null || content == null) return false;

        string resultId  = effect.param;
        string partnerId = effect.requires;

        var result = content.GetItem(resultId);

        if (result == null)
        {
            // Caught by content validation long before this, but a null here would
            // spend both halves for nothing -- so it is checked at the point of use
            // as well as at import.
            Debug.LogWarning($"[ItemEffect] '{item.id}' fuses into unknown item '{resultId}'.");
            return false;
        }

        if (string.IsNullOrEmpty(partnerId))
        {
            Debug.LogWarning($"[ItemEffect] '{item.id}' has a fuse with no partner declared.");
            return false;
        }

        if (inventory.GetQuantity(partnerId) < 1)
        {
            string partnerName = content.GetItem(partnerId)?.DisplayName ?? partnerId;

            GameEvents.FireToast($"You need a {partnerName} as well.", ChatTone.Warning);
            return false;
        }

        // Room for the result BEFORE anything is spent. The item being used is still
        // in the bag at this point and its slot is not free yet, so this is the
        // strictly safe question.
        if (!inventory.CanAddItem(resultId))
        {
            GameEvents.FireToast("No room to forge that.", ChatTone.Bad);
            return false;
        }

        if (!inventory.RemoveItem(partnerId, 1))
        {
            // The stock check passed a moment ago, so this can only be a disagreement
            // between the two -- grant nothing rather than guess.
            Debug.LogWarning($"[ItemEffect] Could not consume '{partnerId}' despite having it.");
            return false;
        }

        inventory.AddItem(resultId, 1);

        GameManager.Audio?.Play(Sfx.SetProc);
        GameEvents.FireToast($"✦ The halves fuse into {result.DisplayName}.", ChatTone.Good);

        return true;
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
    private static bool ApplyGrantAfkTime(ItemEffect effect, ItemData item)
    {
        var character = CharacterManager.Current;
        var activity  = GameManager.Activity;

        if (character == null || activity == null) return false;

        if (character.currentActivity == null || string.IsNullOrEmpty(character.currentActivity.skillId))
        {
            GameEvents.FireToast("Pick an activity before using this.", ChatTone.Bad);
            return false;
        }

        // ══ THE SERVER OWNS THE TIME WHEN THERE IS ONE ═════════════════════════
        //
        // Everything below rewinds lastLogoutUnixTime and runs the local accrual --
        // which returns null outright under an authoritative server, so a gem toasted
        // "Nothing accrued" and, once the message changed, silently did nothing at
        // all. Worse, rewinding a timestamp is the one thing the server must never be
        // taught to accept.
        //
        // Server-side it is a credited-seconds balance the settle drains. Handled
        // there, this returns true so the item is consumed exactly once -- by the
        // endpoint, which is also what removed it from the bag.
        if (IdleExplorers.Backend.ServerState.IsAuthoritative)
        {
            _ = UseOnServerAsync(item);
            return true;
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
    /// Drinks a potion.
    ///
    /// ══ WHY THE SERVER IS ASKED EVEN THOUGH THE EFFECT IS "VISUAL" ════════════
    ///
    /// It is not visual. A buff multiplies damage, damage decides the farm rate, and
    /// the farm rate is the economy — so a buff the client granted itself would be a
    /// client setting its own income. It is the mystic gem's shape exactly, and the
    /// gem is the one that taught this lesson: crediting locally looked like it worked
    /// and was overwritten by the next pull.
    ///
    /// The magnitude and duration come BACK from the server rather than being read out
    /// of the item here, because the server clamps both. A client that applied the raw
    /// content numbers would draw an unclamped bar over a clamped effect.
    /// </summary>
    private static bool ApplyBuff(ItemEffect effect, ItemData item)
    {
        if (IdleExplorers.Backend.ServerState.IsAuthoritative)
        {
            _ = DrinkOnServerAsync(item);
            return true;
        }

        // Offline and in the editor. Through the same shared reader the server uses,
        // so the two cannot disagree about what the item says.
        var potion = IdleExplorers.Rules.Buffs.Read(item);

        if (potion == null)
        {
            Debug.LogWarning($"[ItemEffect] '{item.id}' has a buff effect the rules do not " +
                             $"recognise (stat '{effect.param}').");
            return false;
        }

        BuffManager.Adopt(potion.statId, potion.magnitude, potion.secondsRemaining, potion.label);

        GameEvents.FireToast($"{item.DisplayName} takes hold.", ChatTone.Good);
        return true;
    }

    /// <summary>
    /// Hands the potion to the server and mirrors whatever it granted.
    ///
    /// The item id rather than the effect, for the same reason the gem sends an id:
    /// the server reads what the potion does from its own catalogue, so a client
    /// cannot describe a 25% draught as 400%.
    /// </summary>
    private static async Awaitable DrinkOnServerAsync(ItemData item)
    {
        string itemId = item?.id;

        if (string.IsNullOrEmpty(itemId))
        {
            GameEvents.FireToast("Nothing to drink.", ChatTone.Bad);
            return;
        }

        var granted = await IdleExplorers.Backend.ServerState.DrinkAsync(itemId);

        if (granted == null) return;

        BuffManager.Adopt(granted.buffStatId, granted.buffMagnitude,
                          granted.buffSeconds, granted.buffLabel);

        GameEvents.FireToast($"{item.DisplayName} takes hold.", ChatTone.Good);
    }

    /// <summary>
    /// Hands the gem to the server and shows what it bought.
    ///
    /// The item id rather than the magnitude: the server reads the seconds from its
    /// own catalogue, so a client cannot describe a one-hour gem as seventy-two.
    /// </summary>
    private static async Awaitable UseOnServerAsync(ItemData item)
    {
        string itemId = item?.id;

        if (string.IsNullOrEmpty(itemId))
        {
            GameEvents.FireToast("Nothing to use.", ChatTone.Bad);
            return;
        }

        if (await IdleExplorers.Backend.ServerState.UseItemAsync(itemId))
            GameManager.UI?.Push<AFKSummaryScreen>();
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

            case "summonAlly":
                SummonAlly(effect, item);
                break;
        }
    }

    /// <summary>
    /// Brings something in to fight for a while.
    ///
    /// The Goblin Spear's six percent. Its damage is a fraction of the SUMMONER's,
    /// so the ally scales with the character rather than being a flat number that is
    /// overwhelming at level one and irrelevant at forty.
    ///
    /// One at a time: the existing one is dismissed rather than stacked. A six
    /// percent proc on a fast weapon would otherwise fill the arena, and a player
    /// who could not see their own character would reasonably call that a bug.
    /// </summary>
    private static void SummonAlly(ItemEffect effect, ItemData item)
    {
        var player = Object.FindFirstObjectByType<PlayerController>();
        if (player == null) return;

        foreach (var existing in Object.FindObjectsByType<AllyController>(FindObjectsInactive.Exclude))
            if (existing != null) Object.Destroy(existing.gameObject);

        // Half the summoner's swing. Enough to notice, not enough to replace them.
        double damage = player.AttackDamage * 0.5d;

        AllyController.Summon(effect.param, player.transform, damage,
                              Mathf.Max(1f, effect.magnitude));
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
