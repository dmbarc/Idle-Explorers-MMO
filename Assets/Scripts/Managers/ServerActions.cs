using IdleExplorers.Backend;
using UnityEngine;

/// <summary>
/// The persistent actions that used to happen locally, sent to the server instead.
///
/// ══ WHY THIS MIRRORS LocalRewards ═════════════════════════════════════════════
///
/// Same problem, same shape. Equipping, banking and spending a talent point all change
/// state the player owns, all of them fed damage per second, and all three were still
/// being decided on the client after the core loop had moved.
///
/// Scattering an `if (IsAuthoritative)` through EquipmentManager and BankManager would
/// leave an invariant only checkable by proximity -- and that approach already failed
/// three times on the gathering path before being replaced by a single function that
/// makes the rule structural. Doing it the same way here means the same check works:
/// gameplay code does not decide, it calls.
///
/// ══ WHY EVERY CALL RETURNS false WHEN THE SERVER OWNS IT ══════════════════════
///
/// So the caller stops. These return "handled" rather than "succeeded": under an
/// authoritative server the local manager must do NOTHING, because the server has
/// already done it and the next pull will bring the result. A manager that also
/// applied its own version would show the player an answer that flickers back a
/// moment later.
/// </summary>
public static class ServerActions
{
    /// <summary>Whether the local managers should act at all.</summary>
    public static bool ClientDecides => !ServerState.IsAuthoritative;

    private static string CharacterId => CharacterManager.Current?.characterId ?? "";

    /// <summary>
    /// Wears an item.
    /// </summary>
    /// <returns>
    /// True when the SERVER is handling it and the caller should do nothing further.
    /// False when the client is on its own and should proceed as it always did.
    /// </returns>
    public static bool Equip(string itemId, string slotId)
    {
        if (ClientDecides || string.IsNullOrEmpty(CharacterId)) return false;

        Send(backend => backend.EquipAsync(CharacterId, itemId, slotId), "Equip");
        return true;
    }

    public static bool Unequip(string slotId)
    {
        if (ClientDecides || string.IsNullOrEmpty(CharacterId)) return false;

        Send(backend => backend.UnequipAsync(CharacterId, slotId), "Unequip");
        return true;
    }

    public static bool Deposit(string itemId, long quantity)
    {
        if (ClientDecides || string.IsNullOrEmpty(CharacterId)) return false;

        MoveAsync(itemId, quantity, deposit: true);
        return true;
    }

    public static bool Withdraw(string itemId, long quantity)
    {
        if (ClientDecides || string.IsNullOrEmpty(CharacterId)) return false;

        MoveAsync(itemId, quantity, deposit: false);
        return true;
    }

    public static bool SpendTalent(string nodeId)
    {
        if (ClientDecides || string.IsNullOrEmpty(CharacterId)) return false;

        SpendAsync(nodeId);
        return true;
    }

    // ── Machinery ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Sends a call whose answer is the whole character, and applies it.
    ///
    /// Applying the RETURNED snapshot rather than re-fetching: the server has just
    /// changed the thing being drawn, and a second round trip is both slower and a
    /// window in which the two disagree.
    /// </summary>
    private static async void Send(System.Func<IGameBackend, Awaitable<CharacterSnapshot>> call,
                                   string what)
    {
        try
        {
            await call(GameBackend.Current);

            // ══ THE ANSWER IS NOT A CHARACTER, SO IT IS NOT USED ═════════════════
            //
            // This used to apply the response as a CharacterSnapshot. The equipment
            // endpoints do not return one -- they answer { slotId, itemId, displaced }
            // -- so JsonUtility produced a snapshot with a null id, Apply refused it on
            // the id check, and the client learned nothing at all.
            //
            // Silently. The equip HAD happened on the server; the screen simply did not
            // know until the next full pull twenty or thirty seconds later, which is
            // exactly "nothing happens, and then sometimes it equips".
            //
            // Pulling is one extra round trip at the rate a human clicks, and it cannot
            // be wrong about a shape: the character read is the character read.
            await ServerState.PullCharacterAsync(CharacterId);
        }
        catch (BackendException e)
        {
            // The server's refusals are written for a player to read -- "Both your
            // hands are on that weapon", "You need Smithing 10". Showing the title
            // beats a generic failure that leaves somebody guessing which rule they hit.
            GameEvents.FireToast(e.Title, ChatTone.Bad);
            Debug.LogWarning($"[ServerActions] {what} refused: {e.Message}");
        }
    }

    private static async void MoveAsync(string itemId, long quantity, bool deposit)
    {
        try
        {
            var backend = GameBackend.Current;

            BankMoveResult result = deposit
                ? await backend.DepositAsync(CharacterId, itemId, quantity)
                : await backend.WithdrawAsync(CharacterId, itemId, quantity);

            // A partial move is not a failure, and it is not a success either. Saying
            // so is the difference between a player who knows their bank is full and
            // one who quietly loses track of 2,800 ore.
            if (result is { partial: true })
            {
                GameEvents.FireToast(
                    $"Only {result.moved:N0} of {result.requested:N0} moved — no room for the rest.",
                    ChatTone.Warning);
            }

            await ServerState.PullCharacterAsync(CharacterId);

            GameEvents.FireInventoryChanged();
        }
        catch (BackendException e)
        {
            GameEvents.FireToast(e.Title, ChatTone.Bad);
        }
    }

    private static async void SpendAsync(string nodeId)
    {
        try
        {
            TalentSnapshot talents = await GameBackend.Current.SpendTalentAsync(CharacterId, nodeId);

            if (talents == null) return;

            ApplyTalents(talents);

            // Talents change the stat block, and the stat block decides damage. Pulling
            // afterwards is what makes the character sheet agree with what the server
            // will use on the next settle.
            await ServerState.PullCharacterAsync(CharacterId);
        }
        catch (BackendException e)
        {
            // "Spend 2 more point(s) first", "Already at maximum rank" -- the server
            // computes these from the same shared rules the tooltip used, so the
            // message is one the player can act on.
            GameEvents.FireToast(e.Title, ChatTone.Bad);
        }
    }

    /// <summary>
    /// Writes the server's talent ranks over the local ones.
    ///
    /// Wholesale, like every other pull. A merge would need a rule for which side wins
    /// per node, and the answer is always the same side.
    /// </summary>
    public static void ApplyTalents(TalentSnapshot talents)
    {
        var character = CharacterManager.Current;

        if (character == null || talents?.ranks == null) return;

        character.talents ??= new System.Collections.Generic.List<TalentRank>();
        character.talents.Clear();

        foreach (var rank in talents.ranks)
        {
            if (rank == null || rank.rank <= 0) continue;

            character.talents.Add(new TalentRank { nodeId = rank.nodeId, rank = rank.rank });
        }

        GameEvents.OnTalentsChanged?.Invoke();
    }
}
