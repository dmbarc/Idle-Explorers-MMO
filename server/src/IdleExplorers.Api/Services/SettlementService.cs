using IdleExplorers.Api.Infrastructure;
using IdleExplorers.Rules;
using Npgsql;

namespace IdleExplorers.Api.Services;

/// <summary>
/// Turning elapsed time into things a player owns.
///
/// ══ WHERE THE NUMBERS COME FROM ═══════════════════════════════════════════════
///
/// Not from here. IdleExplorers.Rules.Settlement decides what a window earned, and
/// the client runs the identical function to animate a progress bar. This service
/// does the parts a pure function cannot: read the row, work out how much of the
/// window anybody was watching, apply the result to storage, and write the ledger.
///
/// The split is the whole point of the architecture. Arithmetic that both hosts share
/// cannot drift; effects that only the server may cause live only here.
///
/// ══ SETTLE-ON-READ ════════════════════════════════════════════════════════════
///
/// There is no reward tick and no background job that pays people. Every read of a
/// character settles first, so state is never stale and the elapsed window is always
/// the honest one. Calling it twice in the same instant yields nothing the second
/// time, because elapsed is zero — the timestamp IS the bookkeeping, which is what
/// makes the whole thing idempotent without needing to be told.
/// </summary>
public sealed class SettlementService(Db db, ContentCache content)
{
    /// <summary>
    /// Longest window a single settlement will pay, absent purchased time.
    ///
    /// Not a punishment: it stops a fortnight's absence handing out quantities that
    /// trivialise the game, and it bounds what one request can do. Purchased AFK time
    /// is added on top through credited_seconds rather than by raising this — routing
    /// a 72-hour gem through a 24-hour cap would silently pay 24 and explain, to
    /// someone who had just spent a thousand relic coins on the other 48.
    /// </summary>
    public const long MaxWindowSeconds = 24 * 3600;

    /// <summary>
    /// How long one heartbeat vouches for.
    ///
    /// The client beats every 20 seconds while its tab is focused. Three times that
    /// tolerates a dropped beat and a slow network without either handing out active
    /// rate to somebody who has gone, or docking somebody who is plainly still there.
    ///
    /// Stated plainly, because it is the ceiling on what faking buys: a client that
    /// beats while nobody is at the keyboard earns the ACTIVE rate rather than the
    /// offline one — about 1.6x — and its kills count toward the boss gate. It cannot
    /// earn faster than real time, cannot choose drops, and leaves a perfectly regular
    /// signature a query finds. That is the accepted cost of not paying a round trip
    /// per action.
    /// </summary>
    public static readonly TimeSpan HeartbeatCovers = TimeSpan.FromSeconds(60);

    /// <summary>What one settlement did, for the response and for telemetry.</summary>
    public sealed record Outcome
    {
        public long   Actions             { get; init; }
        public long   SupervisedActions   { get; init; }
        public long   XpGained            { get; init; }
        public double ElapsedSeconds      { get; init; }
        public double SupervisedSeconds   { get; init; }
        public bool   StoppedForRoom      { get; init; }
        public long   LostToFullInventory { get; init; }
        public bool   RanOutOfInputs      { get; init; }

        public Dictionary<string, long> Items    { get; init; } = new();
        public Dictionary<string, long> Currency { get; init; } = new();

        public static readonly Outcome Nothing = new();
    }

    /// <summary>
    /// Settles a character up to <paramref name="now"/>, inside one transaction.
    ///
    /// The character row is locked for the duration, which is what stops two
    /// simultaneous settles both reading the same last_settled_at and both paying the
    /// same window. The idempotency key stops a RETRY; only the lock stops a race.
    /// </summary>
    public Task<Outcome> SettleAsync(Guid characterId, DateTimeOffset now,
                                     CancellationToken cancellation = default) =>
        db.InCharacterTransactionAsync(characterId,
            (connection, tx) => SettleLockedAsync(connection, tx, characterId, now, cancellation),
            cancellation);

    /// <summary>
    /// The same work, for a caller that already holds the lock.
    ///
    /// Crafting and combat endpoints settle before they act — otherwise the materials
    /// a craft consumes might be materials a pending gather has not yet delivered.
    /// </summary>
    public async Task<Outcome> SettleLockedAsync(NpgsqlConnection connection, NpgsqlTransaction tx,
                                                 Guid characterId, DateTimeOffset now,
                                                 CancellationToken cancellation = default)
    {
        Row? row = await ReadActivityAsync(connection, tx, characterId, cancellation);
        if (row is null) return Outcome.Nothing;

        ActivityState activity = row.Activity;

        // ── The window ────────────────────────────────────────────────────────
        double elapsed = (now - row.LastSettledAt).TotalSeconds;

        // Never negative. The trigger forbids moving last_settled_at backwards, but a
        // clock read microseconds apart on two connections can still land behind, and
        // a negative window must be nothing rather than a debt.
        if (elapsed <= 0d) return Outcome.Nothing;

        double credited = row.CreditedSeconds;
        double payable  = Math.Min(elapsed, MaxWindowSeconds) + credited;

        double supervised = SupervisedSeconds(row, now);

        // Purchased time is offline time. A gem cannot buy the active rate, or the
        // rate becomes purchasable rather than earned.
        var window = new SettlementWindow
        {
            SupervisedSeconds   = Math.Min(supervised, payable),
            UnsupervisedSeconds = Math.Max(0d, payable - Math.Min(supervised, payable)),
        };

        Outcome outcome = activity.Kind switch
        {
            ActivityKind.Gather => await GatherAsync(connection, tx, characterId, row, window, cancellation),
            ActivityKind.Craft  => await CraftAsync(connection, tx, characterId, row, window, cancellation),
            _                   => Outcome.Nothing,
        };

        // The clock moves whether or not anything was earned. An idle character still
        // closes its window, or the next settlement re-examines the same span forever.
        await connection.ExecuteAsync(
            """
            update activity
               set last_settled_at  = $2,
                   progress         = $3,
                   credited_seconds = 0
             where character_id = $1;
            """,
            tx, characterId, now, row.PendingProgress);

        return outcome with
        {
            ElapsedSeconds    = elapsed,
            SupervisedSeconds = window.SupervisedSeconds,
        };
    }

    /// <summary>
    /// How much of the window somebody was watching.
    ///
    /// A heartbeat vouches for the time around it, so supervision runs from the start
    /// of the window until the last heartbeat's cover expires. Everything after that
    /// is offline, whatever the client says — and the client is never asked.
    /// </summary>
    private static double SupervisedSeconds(Row row, DateTimeOffset now)
    {
        if (row.LastHeartbeatAt is not { } beat) return 0d;

        DateTimeOffset coveredUntil = beat + HeartbeatCovers;
        if (coveredUntil <= row.LastSettledAt) return 0d;

        DateTimeOffset end = coveredUntil < now ? coveredUntil : now;

        return Math.Max(0d, (end - row.LastSettledAt).TotalSeconds);
    }

    // ── Gathering ─────────────────────────────────────────────────────────────

    private async Task<Outcome> GatherAsync(NpgsqlConnection connection, NpgsqlTransaction tx,
                                            Guid characterId, Row row, SettlementWindow window,
                                            CancellationToken cancellation)
    {
        ActivityState activity = row.Activity;

        if (string.IsNullOrEmpty(activity.TargetItemId)) return Outcome.Nothing;

        // Currency never needs a bag, so the room question does not apply to it.
        bool isCurrency = Currency.IsCurrency(activity.TargetItemId);

        List<InventoryEntry> bag = isCurrency
            ? []
            : await ReadInventoryAsync(connection, tx, characterId, cancellation);

        long room = isCurrency
            ? long.MaxValue / 4
            : SlotContainer.FreeCapacityFor(bag, activity.TargetItemId);

        // Seeded from the character and the window it is settling, so the same
        // settlement re-run produces the same finds -- and a disputed drop months
        // later is re-derivable from two columns rather than gone.
        var rng = new CounterRandom(Seed(characterId, row.LastSettledAt));

        SettlementResult result = Settlement.Gather(
            activity, window,
            diligence: row.Diligence,
            insight:   row.Insight,
            roomForItems: room,
            rng: rng);

        row.PendingProgress = result.Progress;

        if (result.Actions <= 0L) return Outcome.Nothing;

        var outcome = new Outcome
        {
            Actions             = result.Actions,
            SupervisedActions   = result.SupervisedActions,
            XpGained            = result.XpGained,
            StoppedForRoom      = result.StoppedForRoom,
            LostToFullInventory = result.LostToFullInventory,
        };

        if (result.ItemsGranted > 0L)
        {
            if (isCurrency)
            {
                await CreditWalletAsync(connection, tx, row.AccountId, characterId,
                                        Currency.WalletFor(activity.TargetItemId)!,
                                        result.ItemsGranted, "gather", cancellation);

                outcome.Currency[Currency.WalletFor(activity.TargetItemId)!] = result.ItemsGranted;
            }
            else
            {
                long stored = SlotContainer.AddUpTo(bag, activity.TargetItemId, result.ItemsGranted);

                await WriteInventoryAsync(connection, tx, characterId, bag, cancellation);
                await WriteItemLedgerAsync(connection, tx, row.AccountId, characterId,
                                           activity.TargetItemId, stored, "gather", cancellation);

                outcome.Items[activity.TargetItemId] = stored;
            }
        }

        if (result.XpGained > 0L && !string.IsNullOrEmpty(activity.SkillId))
            await GrantXpAsync(connection, tx, characterId, activity.SkillId, result.XpGained, cancellation);

        return outcome;
    }

    // ── Crafting ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Integrates a crafting window and moves the materials.
    ///
    /// ══ WHY THE STOCK IS COUNTED BEFORE THE RULES RUN ═════════════════════════
    ///
    /// Settlement.Craft decides how many crafts the TIME allowed; the bag decides how
    /// many the materials allowed; the answer is the smaller. Handing the rules the
    /// stock figure up front is what lets them report ran-out honestly rather than
    /// the caller discovering it afterwards and having to reverse-engineer why.
    ///
    /// Consumption is all-or-nothing and inside the same transaction as the output.
    /// A crash between the two would otherwise charge a player for goods they never
    /// received, which is the single worst-feeling bug an economy can have.
    /// </summary>
    private async Task<Outcome> CraftAsync(NpgsqlConnection connection, NpgsqlTransaction tx,
                                           Guid characterId, Row row, SettlementWindow window,
                                           CancellationToken cancellation)
    {
        ActivityState activity = row.Activity;

        CraftRecipe? recipe = content.Catalogue.GetRecipe(activity.RecipeId);

        // A recipe that has been removed from content since the activity was set. Pay
        // nothing rather than guess -- see IsActivityValid on the client for the same
        // decision: a stale snapshot used to conjure its target item forever.
        if (recipe is null) return Outcome.Nothing;

        List<InventoryEntry> bag = await ReadInventoryAsync(connection, tx, characterId, cancellation);

        long affordable = MaxCraftsFrom(bag, recipe);
        long roomFor    = string.IsNullOrEmpty(recipe.outputItemId)
            ? 0L
            : SlotContainer.FreeCapacityFor(bag, recipe.outputItemId);

        SettlementResult result = Settlement.Craft(
            activity, recipe, window,
            diligence:         row.Diligence,
            outputMultiplier:  1f,
            maxCraftsByInputs: affordable,
            roomForOutput:     roomFor);

        row.PendingProgress = result.Progress;

        if (result.Actions <= 0L)
        {
            return Outcome.Nothing with { RanOutOfInputs = result.RanOutOfInputs };
        }

        // Spend first. If the bag disagrees with the count taken a moment ago -- which
        // it cannot, inside this lock, but the check costs nothing -- grant nothing.
        if (!ConsumeFor(bag, recipe, result.Actions))
            return Outcome.Nothing;

        long produced  = Math.Max(1L, recipe.outputQuantity) * result.Actions;
        long delivered = SlotContainer.AddUpTo(bag, recipe.outputItemId, Math.Min(produced, roomFor));

        await WriteInventoryAsync(connection, tx, characterId, bag, cancellation);

        // Both halves of the trade are ledgered, because "where did this chestplate
        // come from" and "where did two thousand bars go" are the same question asked
        // from opposite ends.
        foreach (var input in recipe.inputs ?? [])
        {
            if (input == null || string.IsNullOrEmpty(input.itemId)) continue;

            await WriteItemLedgerAsync(connection, tx, row.AccountId, characterId,
                                       input.itemId, -(input.quantity * result.Actions),
                                       "craft_input", cancellation);
        }

        if (delivered > 0L)
        {
            await WriteItemLedgerAsync(connection, tx, row.AccountId, characterId,
                                       recipe.outputItemId, delivered, "craft_output", cancellation);
        }

        if (result.XpGained > 0L && !string.IsNullOrEmpty(recipe.skillId))
            await GrantXpAsync(connection, tx, characterId, recipe.skillId, result.XpGained, cancellation);

        var outcome = new Outcome
        {
            Actions             = result.Actions,
            SupervisedActions   = result.SupervisedActions,
            XpGained            = result.XpGained,
            StoppedForRoom      = result.StoppedForRoom,
            LostToFullInventory = result.LostToFullInventory,
            RanOutOfInputs      = result.RanOutOfInputs,
        };

        if (delivered > 0L) outcome.Items[recipe.outputItemId] = delivered;

        return outcome;
    }

    /// <summary>
    /// How many times this recipe can be made from what is in the bag.
    ///
    /// The limiting ingredient decides, which is the whole of it -- but it is worth
    /// being explicit that a recipe with NO inputs would otherwise be unbounded, and
    /// content is hand-authored.
    /// </summary>
    private static long MaxCraftsFrom(List<InventoryEntry> bag, CraftRecipe recipe)
    {
        if (recipe.inputs == null || recipe.inputs.Length == 0) return 0L;

        long limit = long.MaxValue;

        foreach (var input in recipe.inputs)
        {
            if (input == null || string.IsNullOrEmpty(input.itemId)) continue;
            if (input.quantity <= 0L) return 0L;

            long held = SlotContainer.GetQuantity(bag, input.itemId);
            limit = Math.Min(limit, held / input.quantity);

            if (limit == 0L) return 0L;
        }

        return limit == long.MaxValue ? 0L : limit;
    }

    /// <summary>Spends the inputs for a batch, or changes nothing and says no.</summary>
    private static bool ConsumeFor(List<InventoryEntry> bag, CraftRecipe recipe, long crafts)
    {
        if (crafts <= 0L || recipe.inputs == null) return false;

        // Checked in full before anything is removed. A partial spend that then fails
        // is materials destroyed for nothing.
        foreach (var input in recipe.inputs)
        {
            if (input == null || string.IsNullOrEmpty(input.itemId)) continue;

            if (SlotContainer.GetQuantity(bag, input.itemId) < input.quantity * crafts)
                return false;
        }

        foreach (var input in recipe.inputs)
        {
            if (input == null || string.IsNullOrEmpty(input.itemId)) continue;

            SlotContainer.RemoveItem(bag, input.itemId, input.quantity * crafts);
        }

        return true;
    }

    // ── Storage ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Experience for a skill, and the quarter of it that reaches the character.
    ///
    /// One rule applied wherever skill xp lands -- see Levelling. Levels are never
    /// written: they are derived from xp on read, so the two cannot drift.
    /// </summary>
    private static async Task GrantXpAsync(NpgsqlConnection connection, NpgsqlTransaction tx,
                                           Guid characterId, string skillId, long xp,
                                           CancellationToken cancellation)
    {
        await connection.ExecuteAsync(
            """
            insert into character_skill (character_id, skill_id, xp)
            values ($1, $2, $3)
            on conflict (character_id, skill_id) do update
               set xp = character_skill.xp + excluded.xp;
            """,
            tx, characterId, skillId, xp);

        long characterXp = Levelling.CharacterXpFromSkillXp(xp);
        if (characterXp <= 0L) return;

        await connection.ExecuteAsync(
            "update character set xp = xp + $2 where id = $1;",
            tx, characterId, characterXp);
    }

    private static async Task CreditWalletAsync(NpgsqlConnection connection, NpgsqlTransaction tx,
                                                Guid accountId, Guid characterId, string currency,
                                                long amount, string reason,
                                                CancellationToken cancellation)
    {
        if (amount <= 0L) return;

        await connection.ExecuteAsync(
            """
            insert into wallet (account_id, currency, balance)
            values ($1, $2, $3)
            on conflict (account_id, currency) do update
               set balance = wallet.balance + excluded.balance;
            """,
            tx, accountId, currency, amount);

        // Written in the SAME transaction as the balance, always. A balance that moves
        // without a ledger row is a number nobody can explain, and the invariant the
        // scenario tests assert -- sum(delta) equals balance -- is what makes the
        // audit trail worth having.
        await connection.ExecuteAsync(
            """
            insert into wallet_ledger (account_id, currency, delta, reason, character_id)
            values ($1, $2, $3, $4, $5);
            """,
            tx, accountId, currency, amount, reason, characterId);
    }

    private static async Task WriteItemLedgerAsync(NpgsqlConnection connection, NpgsqlTransaction tx,
                                                   Guid accountId, Guid characterId, string itemId,
                                                   long delta, string reason,
                                                   CancellationToken cancellation)
    {
        if (delta == 0L) return;

        await connection.ExecuteAsync(
            """
            insert into item_ledger (account_id, character_id, item_id, delta, reason)
            values ($1, $2, $3, $4, $5);
            """,
            tx, accountId, characterId, itemId, delta, reason);
    }

    private static async Task<List<InventoryEntry>> ReadInventoryAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, Guid characterId,
        CancellationToken cancellation)
    {
        var bag = new List<InventoryEntry>();

        for (int i = 0; i < InventoryCapacity; i++)
            bag.Add(new InventoryEntry());

        await using var command = connection.Sql(
            "select slot_index, item_id, quantity from inventory_slot where character_id = $1;",
            tx, characterId);

        await using var reader = await command.ExecuteReaderAsync(cancellation);

        while (await reader.ReadAsync(cancellation))
        {
            int slot = reader.GetInt32(0);
            if (slot < 0 || slot >= bag.Count) continue;

            bag[slot].itemId   = reader.GetString(1);
            bag[slot].quantity = reader.GetInt64(2);
        }

        return bag;
    }

    /// <summary>
    /// Writes the bag back, slot for slot.
    ///
    /// Deleted and reinserted rather than diffed: a bag is thirty rows, the whole
    /// thing is inside a transaction that already holds the character lock, and a diff
    /// is a second model of what changed that can disagree with the first.
    /// </summary>
    private static async Task WriteInventoryAsync(NpgsqlConnection connection, NpgsqlTransaction tx,
                                                  Guid characterId, List<InventoryEntry> bag,
                                                  CancellationToken cancellation)
    {
        await connection.ExecuteAsync(
            "delete from inventory_slot where character_id = $1;", tx, characterId);

        for (int slot = 0; slot < bag.Count; slot++)
        {
            if (SlotContainer.IsEmpty(bag[slot])) continue;

            await connection.ExecuteAsync(
                """
                insert into inventory_slot (character_id, slot_index, item_id, quantity)
                values ($1, $2, $3, $4);
                """,
                tx, characterId, slot, bag[slot].itemId, bag[slot].quantity);
        }
    }

    /// <summary>Thirty slots, matching InventoryManager.MaxSlots on the client.</summary>
    public const int InventoryCapacity = 30;

    // ── Reading the row ───────────────────────────────────────────────────────

    private sealed class Row
    {
        public Guid            AccountId;
        public ActivityState   Activity;
        public DateTimeOffset  LastSettledAt;
        public DateTimeOffset? LastHeartbeatAt;
        public long            CreditedSeconds;
        public float           Diligence;
        public float           Insight;
        public double          PendingProgress;
    }

    private static async Task<Row?> ReadActivityAsync(NpgsqlConnection connection, NpgsqlTransaction tx,
                                                      Guid characterId, CancellationToken cancellation)
    {
        await using var command = connection.Sql(
            """
            select c.account_id,
                   a.kind, a.skill_id, a.target_item_id, a.recipe_id, a.monster_id,
                   a.seconds_per_action, a.active_rate_multi, a.afk_rate_multi,
                   a.xp_per_action, a.special_chance, a.progress,
                   a.last_settled_at, a.last_heartbeat_at, a.credited_seconds
              from activity a
              join character c on c.id = a.character_id
             where a.character_id = $1 and c.deleted_at is null;
            """,
            tx, characterId);

        await using var reader = await command.ExecuteReaderAsync(cancellation);

        if (!await reader.ReadAsync(cancellation)) return null;

        var activity = new ActivityState
        {
            Kind             = ParseKind(reader.GetString(1)),
            SkillId          = reader.GetString(2),
            TargetItemId     = reader.GetString(3),
            RecipeId         = reader.GetString(4),
            MonsterId        = reader.GetString(5),
            SecondsPerAction = reader.GetFloat(6),
            ActiveRateMulti  = reader.GetFloat(7),
            AfkRateMulti     = reader.GetFloat(8),
            XpPerAction      = reader.GetFloat(9),
            SpecialChance    = reader.GetFloat(10),
            Progress         = reader.GetDouble(11),
        };

        return new Row
        {
            AccountId       = reader.GetGuid(0),
            Activity        = activity,
            LastSettledAt   = reader.GetFieldValue<DateTimeOffset>(12),
            LastHeartbeatAt = reader.IsDBNull(13) ? null : reader.GetFieldValue<DateTimeOffset>(13),
            CreditedSeconds = reader.GetInt64(14),

            // TODO(Phase 1): both come from the character's resolved stat block, which
            // needs equipment, class and talents folded together server-side. Zero is
            // the correct placeholder -- it is the value for a character with no
            // bonuses, so settlement is already right for a new player and only
            // under-pays a geared one.
            Diligence       = 0f,
            Insight         = 0f,

            PendingProgress = activity.Progress,
        };
    }

    private static ActivityKind ParseKind(string kind) => kind switch
    {
        "gather" => ActivityKind.Gather,
        "craft"  => ActivityKind.Craft,
        "combat" => ActivityKind.Combat,
        _        => ActivityKind.Idle,
    };

    /// <summary>
    /// A seed that is a function of the character and the window being settled.
    ///
    /// Reproducible on purpose: re-running the same settlement produces the same
    /// finds, so a disputed drop months later can be re-derived from two stored
    /// columns rather than argued about. Also means a client cannot influence it,
    /// because neither input comes from the client.
    /// </summary>
    private static long Seed(Guid characterId, DateTimeOffset from) =>
        characterId.GetHashCode() * 31L + from.ToUnixTimeMilliseconds();
}
