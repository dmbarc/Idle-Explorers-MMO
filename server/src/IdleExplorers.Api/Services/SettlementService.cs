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

        /// <summary>Combat only. Same number as Actions, named for what it is.</summary>
        public long Kills => Actions;

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
        double elapsed  = (now - row.LastSettledAt).TotalSeconds;
        double credited = row.CreditedSeconds;

        // ══ BOUGHT TIME IS NOT ELAPSED TIME ══════════════════════════════════
        //
        // Never negative. The trigger forbids moving last_settled_at backwards, but a
        // clock read microseconds apart on two connections can still land behind, and
        // a negative window must be nothing rather than a debt.
        //
        // CREDITED IS CHECKED TOO, and that is not a refinement -- it is the whole
        // reason mystic gems did nothing. Using one settles first and then credits, so
        // the settle that follows runs with an elapsed of very nearly zero. Guarded on
        // elapsed alone this returned Nothing before ever reading the seventy-two
        // hours sitting in the column, and the gem was consumed for no reward.
        if (elapsed <= 0d && credited <= 0d) return Outcome.Nothing;

        // Clamped at zero rather than passed through: a fractionally negative window
        // must not eat into what was paid for.
        double payable = Math.Min(Math.Max(0d, elapsed), MaxWindowSeconds) + credited;

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
            ActivityKind.Combat => await CombatAsync(connection, tx, characterId, row, window, cancellation),
            _                   => Outcome.Nothing,
        };

        // ══ TELEMETRY THE CLIENT COULD NOT HONESTLY SEND ══════════════════════
        //
        // The server decided this payout, so the server is the only party that can
        // report it truthfully. A client-reported "I earned 4,000 xp" would be a
        // claim about a reward, which is the whole thing this architecture removed --
        // and a funnel built on claims measures how players' clients behave rather
        // than how players do.
        //
        // Only settlements that produced something. An idle character closing its
        // window every few seconds would otherwise be most of the table.
        if (outcome.Actions > 0L)
        {
            await connection.ExecuteAsync(
                """
                insert into telemetry_event (account_id, character_id, event, payload, occurred_at)
                values ($1, $2, 'settled', $3::jsonb, $4);
                """,
                tx, row.AccountId, characterId,
                $"{{\"kind\":\"{activity.Kind.ToString().ToLowerInvariant()}\"," +
                $"\"skill\":{System.Text.Json.JsonSerializer.Serialize(activity.SkillId ?? "")}," +
                $"\"actions\":{outcome.Actions}," +
                $"\"supervised\":{outcome.SupervisedActions}," +
                $"\"xp\":{outcome.XpGained}," +
                $"\"elapsed\":{(long)elapsed}}}",
                now);
        }

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

    // ── Combat ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Integrates a combat window: kills, loot, experience, and the boss gate.
    ///
    /// ══ NO SIMULATION, AND NO KILL REPORTS ════════════════════════════════════
    ///
    /// There is no per-monster fight here, and no "I killed a goblin" endpoint
    /// anywhere -- that call would make the client the author of kills, which is the
    /// whole thing being removed. Damage per second comes from a stat snapshot the
    /// server assembles, time-to-kill follows from the monster's health, and kills
    /// fall out of the same integral that pays a mining node.
    ///
    /// The client's spawner and monster controllers keep running as theatre. What is
    /// on screen and what is in the database agree because both derive from the same
    /// content, not because either reports to the other.
    /// </summary>
    private async Task<Outcome> CombatAsync(NpgsqlConnection connection, NpgsqlTransaction tx,
                                            Guid characterId, Row row, SettlementWindow window,
                                            CancellationToken cancellation)
    {
        ActivityState activity = row.Activity;

        MonsterData? monster = content.Catalogue.GetMonster(activity.MonsterId);
        if (monster is null) return Outcome.Nothing;

        // The snapshot is taken HERE, from what is worn now -- not passed in, and
        // never from the client. Swapping gear changes the next window, not this one.
        StatBlock stats     = await ResolveStatsAsync(connection, tx, characterId, cancellation);
        ItemData? mainHand  = await EquippedItemAsync(connection, tx, characterId, "mainhand", cancellation);
        double    dps       = StatAssembly.DamagePerSecond(stats, mainHand);

        List<InventoryEntry> bag = await ReadInventoryAsync(connection, tx, characterId, cancellation);
        var room = new BagRoom(bag);

        var rng = new CounterRandom(Seed(characterId, row.LastSettledAt));

        SettlementResult result = Settlement.Combat(
            activity, monster, window,
            dps:                     dps,
            travelAndRespawnSeconds: TravelAndRespawnSeconds,
            diligence:               row.Diligence,
            dropQuantityMultiplier:  1f,
            room:                    room,
            rng:                     rng);

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

        // ── Kills ─────────────────────────────────────────────────────────────
        //
        // Active and AFK land in one statement, from the same integral that paid the
        // loot. A second code path counting kills is a second thing to get wrong, and
        // the one an attacker would aim at -- the boss portal reads this column.
        await connection.ExecuteAsync(
            """
            insert into kill_counter (character_id, monster_id, active_kills, afk_kills)
            values ($1, $2, $3, $4)
            on conflict (character_id, monster_id) do update
               set active_kills = kill_counter.active_kills + excluded.active_kills,
                   afk_kills    = kill_counter.afk_kills    + excluded.afk_kills;
            """,
            tx, characterId, monster.id,
            result.SupervisedActions,
            result.Actions - result.SupervisedActions);

        // ── Loot ──────────────────────────────────────────────────────────────
        foreach (LootStack stack in result.Loot)
        {
            if (stack.Quantity <= 0L) continue;

            string? wallet = Currency.WalletFor(stack.ItemId);

            if (wallet != null)
            {
                await CreditWalletAsync(connection, tx, row.AccountId, characterId,
                                        wallet, stack.Quantity, "loot", cancellation);

                outcome.Currency[wallet] = outcome.Currency.GetValueOrDefault(wallet) + stack.Quantity;
                continue;
            }

            long stored = SlotContainer.AddUpTo(bag, stack.ItemId, stack.Quantity);
            if (stored <= 0L) continue;

            await WriteItemLedgerAsync(connection, tx, row.AccountId, characterId,
                                       stack.ItemId, stored, "loot", cancellation);

            outcome.Items[stack.ItemId] = outcome.Items.GetValueOrDefault(stack.ItemId) + stored;
        }

        if (outcome.Items.Count > 0)
            await WriteInventoryAsync(connection, tx, characterId, bag, cancellation);

        if (result.XpGained > 0L)
            await GrantXpAsync(connection, tx, characterId, "combat", result.XpGained, cancellation);

        return outcome;
    }

    /// <summary>
    /// Walking to the next spawn and waiting for it.
    ///
    /// TODO(Phase 4): per-map, from content -- a dense camp and an empty hollow are
    /// not the same walk. One number until there is a second map worth distinguishing.
    /// </summary>
    private const float TravelAndRespawnSeconds = 2.5f;

    /// <summary>
    /// Capacity, per item, answered from the bag being filled as it fills.
    ///
    /// A loot table is many items, so combat cannot be handed one number the way
    /// gathering is -- and a bag full of coins must not stop the bones.
    /// </summary>
    private sealed class BagRoom(List<InventoryEntry> bag) : IItemRoom
    {
        public long RoomFor(string itemId) => SlotContainer.FreeCapacityFor(bag, itemId);
    }

    /// <summary>
    /// The character's stats, assembled from base, classes and worn gear.
    ///
    /// Talents are read and applied. Armour set bonuses are not yet -- deciding which
    /// sets are active needs the in-flight ledger and a clock, and they only ADD, so a
    /// character is under-powered rather than over-powered until they are. That is the
    /// safe direction for a number deciding how fast somebody farms.
    ///
    /// TODO(Phase 1): set bonuses, through the same setBonuses argument StatAssembly
    /// already takes.
    /// </summary>
    private async Task<StatBlock> ResolveStatsAsync(NpgsqlConnection connection, NpgsqlTransaction tx,
                                                    Guid characterId, CancellationToken cancellation)
    {
        var classes = new List<ClassData>();

        string? classId = await connection.ScalarAsync<string>(
            "select class_id from character where id = $1;", tx, characterId);

        if (!string.IsNullOrEmpty(classId) && content.Catalogue.GetClass(classId) is { } resolved)
            classes.Add(resolved);

        var equipped = new List<ItemData>();

        await using (var command = connection.Sql(
            "select item_id, durability from equipment where character_id = $1;", tx, characterId))
        {
            await using var reader = await command.ExecuteReaderAsync(cancellation);

            while (await reader.ReadAsync(cancellation))
            {
                ItemData? item = content.Catalogue.GetItem(reader.GetString(0));
                if (item is null) continue;

                // A broken piece contributes nothing. Durability is the payoff for
                // maintaining gear, and armour that works at zero makes it decorative.
                if (item.HasDurability && reader.GetInt32(1) <= 0) continue;

                equipped.Add(item);
            }
        }

        StatBlock block = StatAssembly.Build(content.Catalogue.BaseStats, classes, equipped);

        // ══ TALENTS ═══════════════════════════════════════════════════════════
        //
        // Applied HERE rather than inside StatAssembly, because a talent bonus is a
        // percentage OF the assembled block -- it multiplies what the gear and the
        // class produced, so it has to come after both.
        //
        // Without this the talent endpoints would persist points that changed nothing
        // the server computed: a player could spend into a damage tree and farm at
        // exactly the same rate, which is worse than having no talents at all.
        List<TalentRank> ranks = await ReadTalentsAsync(connection, tx, characterId, cancellation);

        if (ranks.Count > 0)
        {
            List<TalentNode> nodes = Talents.NodesOf(classes);

            // Through the MULTIPLIER fields the block already carries, not by scaling
            // the raw values. StatBlock.Resolve and EffectiveAttackSpeed both read the
            // multipliers and apply their own floors -- scaling the base numbers would
            // bypass those and double-count against gear that also multiplies.
            float damage = Talents.Bonus(ranks, nodes, "attackDamagePercent");

            block.minHitMultiplier += damage;
            block.maxHitMultiplier += damage;

            block.healthMultiplier += Talents.Bonus(ranks, nodes, "maxHpPercent");

            // attackSpeedPercent is a REDUCTION -- it shortens the interval between
            // swings. EffectiveAttackSpeed is attackSpeed / (1 + attackSpeedMultiplier),
            // so a POSITIVE addition here is a faster swing. That reads backwards at a
            // glance, which is exactly why the sign is decided in one place.
            block.attackSpeedMultiplier += Talents.Bonus(ranks, nodes, "attackSpeedPercent");
        }

        return block;
    }

    /// <summary>
    /// The character's talent ranks.
    ///
    /// Read on every settlement, which sounds expensive and is not: a character has a
    /// handful of rows and this rides a transaction that is already open. Caching it
    /// would mean a respec that does not take effect until something evicts the entry,
    /// which is a support ticket nobody can reproduce.
    /// </summary>
    private static async Task<List<TalentRank>> ReadTalentsAsync(
        NpgsqlConnection connection, NpgsqlTransaction? tx, Guid characterId,
        CancellationToken cancellation)
    {
        var ranks = new List<TalentRank>();

        await using var command = connection.Sql(
            "select node_id, rank from talent where character_id = $1;", tx, characterId);

        await using var reader = await command.ExecuteReaderAsync(cancellation);

        while (await reader.ReadAsync(cancellation))
            ranks.Add(new TalentRank { nodeId = reader.GetString(0), rank = reader.GetInt32(1) });

        return ranks;
    }

    private async Task<ItemData?> EquippedItemAsync(NpgsqlConnection connection, NpgsqlTransaction tx,
                                                    Guid characterId, string slotId,
                                                    CancellationToken cancellation)
    {
        string? itemId = await connection.ScalarAsync<string>(
            "select item_id from equipment where character_id = $1 and slot_id = $2;",
            tx, characterId, slotId);

        return string.IsNullOrEmpty(itemId) ? null : content.Catalogue.GetItem(itemId);
    }

    // ── What the boss fight borrows ───────────────────────────────────────────

    /// <summary>
    /// The combat snapshot, frozen.
    ///
    /// ══ WHY THE BOSS ASKS THE SETTLEMENT SERVICE ══════════════════════════════
    ///
    /// So that a character's boss DPS is, by construction, the same number as their
    /// farming DPS. Two code paths answering "how hard does this character hit" is
    /// exactly the drift the shared rules assembly was built to prevent -- and here it
    /// would be worse than a wrong number on screen, because the boss enrage timer is
    /// a DPS check and the farm rate is a DPS integral. Gear that helped one and not
    /// the other would be a balance problem nobody could see the cause of.
    ///
    /// Read inside the caller's transaction, under the caller's character lock, so
    /// what is frozen is what was worn at that instant and not a moment either side.
    /// </summary>
    public async Task<FrozenCombat> FreezeCombatAsync(NpgsqlConnection connection, NpgsqlTransaction tx,
                                                      Guid characterId, CancellationToken cancellation)
    {
        StatBlock stats    = await ResolveStatsAsync(connection, tx, characterId, cancellation);
        ItemData? mainHand = await EquippedItemAsync(connection, tx, characterId, "mainhand", cancellation);

        double dps = StatAssembly.DamagePerSecond(stats, mainHand);

        float attackSeconds = WeaponProfile.AttackSeconds(mainHand, stats.EffectiveAttackSpeed);

        return new FrozenCombat(dps, attackSeconds);
    }

    /// <summary>What a character hits for, at one instant, for the length of one fight.</summary>
    public readonly record struct FrozenCombat(double Dps, double AttackSeconds);

    /// <summary>
    /// Moves earned loot into the bag and the wallet.
    ///
    /// Public because claiming boss loot is the same operation as receiving gathered
    /// loot, and a second implementation would be a second place to forget the bag cap.
    /// Returns what would not fit, so the caller can leave it pending rather than
    /// destroying it.
    /// </summary>
    public async Task<long> GrantItemAsync(NpgsqlConnection connection, NpgsqlTransaction tx,
                                           Guid accountId, Guid characterId,
                                           string itemId, long quantity, string reason,
                                           CancellationToken cancellation)
    {
        if (quantity <= 0L) return 0L;

        if (Currency.IsCurrency(itemId))
        {
            string wallet = Currency.WalletFor(itemId)!;

            await CreditWalletAsync(connection, tx, accountId, characterId,
                                    wallet, quantity, reason, cancellation);

            return quantity;
        }

        List<InventoryEntry> bag = await ReadInventoryAsync(connection, tx, characterId, cancellation);

        long fitted = SlotContainer.AddUpTo(bag, itemId, quantity);

        if (fitted > 0L)
        {
            await WriteInventoryAsync(connection, tx, characterId, bag, cancellation);
            await WriteItemLedgerAsync(connection, tx, accountId, characterId, itemId, fitted, reason, cancellation);
        }

        return fitted;
    }

    // ── Bonus actions ─────────────────────────────────────────────────────────

    /// <summary>
    /// Pays extra actions of whatever the character is already doing.
    ///
    /// ══ WHY IT GOES THROUGH THE SAME PATHS ════════════════════════════════════
    ///
    /// A minigame bonus is not a special reward — it is more of the thing the player
    /// was already earning. So it produces items through the same stacking rules,
    /// respects the same bag capacity, writes the same ledger rows and pays the same
    /// experience. A second reward path would be a second set of rules to get wrong,
    /// and the one place a bag cap gets forgotten.
    ///
    /// The caller has already decided how many, from the server's own action count.
    /// Nothing here reads anything a client sent.
    /// </summary>
    public async Task<Outcome> GrantBonusAsync(NpgsqlConnection connection, NpgsqlTransaction tx,
                                               Guid characterId, long bonusActions,
                                               CancellationToken cancellation = default)
    {
        if (bonusActions <= 0L) return Outcome.Nothing;

        Row? row = await ReadActivityAsync(connection, tx, characterId, cancellation);
        if (row is null) return Outcome.Nothing;

        ActivityState activity = row.Activity;

        // Only gathering and crafting have minigames. Combat has a boss for the same
        // purpose, and a timed input during a fight is a different design argument.
        if (activity.Kind is not (ActivityKind.Gather or ActivityKind.Craft))
            return Outcome.Nothing;

        if (activity.Kind == ActivityKind.Gather)
            return await GrantGatherBonusAsync(connection, tx, characterId, row, bonusActions, cancellation);

        return await GrantCraftBonusAsync(connection, tx, characterId, row, bonusActions, cancellation);
    }

    private async Task<Outcome> GrantGatherBonusAsync(NpgsqlConnection connection, NpgsqlTransaction tx,
                                                      Guid characterId, Row row, long actions,
                                                      CancellationToken cancellation)
    {
        ActivityState activity = row.Activity;
        if (string.IsNullOrEmpty(activity.TargetItemId)) return Outcome.Nothing;

        var outcome = new Outcome
        {
            Actions   = actions,
            XpGained  = MultiplyClamped(actions, (long)activity.XpPerAction),
        };

        if (Currency.IsCurrency(activity.TargetItemId))
        {
            string wallet = Currency.WalletFor(activity.TargetItemId)!;

            await CreditWalletAsync(connection, tx, row.AccountId, characterId,
                                    wallet, actions, "gather", cancellation);

            outcome.Currency[wallet] = actions;
        }
        else
        {
            var bag = await ReadInventoryAsync(connection, tx, characterId, cancellation);

            // The bag still decides. A minigame that overflowed capacity would be a
            // minigame that destroyed ore as a reward for playing well.
            long stored = SlotContainer.AddUpTo(bag, activity.TargetItemId, actions);

            if (stored > 0L)
            {
                await WriteInventoryAsync(connection, tx, characterId, bag, cancellation);
                await WriteItemLedgerAsync(connection, tx, row.AccountId, characterId,
                                           activity.TargetItemId, stored, "gather", cancellation);

                outcome.Items[activity.TargetItemId] = stored;
            }
        }

        if (outcome.XpGained > 0L && !string.IsNullOrEmpty(activity.SkillId))
            await GrantXpAsync(connection, tx, characterId, activity.SkillId, outcome.XpGained, cancellation);

        return outcome;
    }

    /// <summary>
    /// Extra crafts, which still cost their materials.
    ///
    /// The bonus is TIME, not free goods: a perfect run means the player got through
    /// more of the queue, and the queue still consumes what it consumes. Handing out
    /// free output instead would make the anvil a printing press for anyone who could
    /// keep rhythm.
    /// </summary>
    private async Task<Outcome> GrantCraftBonusAsync(NpgsqlConnection connection, NpgsqlTransaction tx,
                                                     Guid characterId, Row row, long actions,
                                                     CancellationToken cancellation)
    {
        CraftRecipe? recipe = content.Catalogue.GetRecipe(row.Activity.RecipeId);
        if (recipe is null) return Outcome.Nothing;

        var bag = await ReadInventoryAsync(connection, tx, characterId, cancellation);

        long affordable = MaxCraftsFrom(bag, recipe);
        long crafts     = Math.Min(actions, affordable);

        if (crafts <= 0L) return Outcome.Nothing with { RanOutOfInputs = true };

        if (!ConsumeFor(bag, recipe, crafts)) return Outcome.Nothing;

        long produced  = Math.Max(1L, recipe.outputQuantity) * crafts;
        long room      = SlotContainer.FreeCapacityFor(bag, recipe.outputItemId);
        long delivered = SlotContainer.AddUpTo(bag, recipe.outputItemId, Math.Min(produced, room));

        await WriteInventoryAsync(connection, tx, characterId, bag, cancellation);

        foreach (var input in recipe.inputs ?? [])
        {
            if (input == null || string.IsNullOrEmpty(input.itemId)) continue;

            await WriteItemLedgerAsync(connection, tx, row.AccountId, characterId,
                                       input.itemId, -(input.quantity * crafts),
                                       "craft_input", cancellation);
        }

        if (delivered > 0L)
        {
            await WriteItemLedgerAsync(connection, tx, row.AccountId, characterId,
                                       recipe.outputItemId, delivered, "craft_output", cancellation);
        }

        long xp = MultiplyClamped(crafts, (long)recipe.xpPerCraft);

        if (xp > 0L && !string.IsNullOrEmpty(recipe.skillId))
            await GrantXpAsync(connection, tx, characterId, recipe.skillId, xp, cancellation);

        var outcome = new Outcome
        {
            Actions        = crafts,
            XpGained       = xp,
            RanOutOfInputs = crafts < actions,
        };

        if (delivered > 0L) outcome.Items[recipe.outputItemId] = delivered;

        return outcome;
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

    /// <summary>
    /// Internal rather than private: the shop test-grant path credits the same way,
    /// through the same balance-and-ledger pair in one transaction. A second copy of
    /// this would be a second chance to move a balance without a ledger row.
    /// </summary>
    /// <summary>
    /// Takes currency, refusing rather than going negative.
    ///
    /// ══ THE AFFORDABILITY IS IN THE WHERE CLAUSE ═════════════════════════════
    ///
    /// Read-then-write is a race even inside a transaction unless the row is locked,
    /// and the wallet row is not what these transactions lock. Putting the condition
    /// in the WHERE makes the database do the comparison and the deduction as one
    /// thing: no row matched means it could not be afforded, and no balance moved.
    ///
    /// RETURNING is what carries that back. A separate read afterwards cannot tell a
    /// balance that landed on zero apart from an update that never happened -- a
    /// first version of this did exactly that and would have let a purchase through
    /// on an empty wallet.
    ///
    /// Returns false rather than throwing, because "you cannot afford that" is a
    /// sentence for a player rather than an exception for a log.
    /// </summary>
    internal static async Task<bool> SpendWalletAsync(NpgsqlConnection connection, NpgsqlTransaction tx,
                                                      Guid accountId, Guid? characterId, string currency,
                                                      long amount, string reason,
                                                      CancellationToken cancellation)
    {
        if (amount <= 0L) return true;

        long? after = await connection.ScalarAsync<long?>(
            """
            update wallet set balance = balance - $3
            where account_id = $1 and currency = $2 and balance >= $3
            returning balance;
            """,
            tx, accountId, currency, amount);

        if (after is null) return false;

        // Same transaction as the balance, always. A balance that moves without a
        // ledger row is a number nobody can explain, and sum(delta) = balance is the
        // invariant the scenario tests assert.
        await connection.ExecuteAsync(
            """
            insert into wallet_ledger (account_id, currency, delta, reason, character_id)
            values ($1, $2, $3, $4, $5);
            """,
            tx, accountId, currency, -amount, reason,
            (object?)characterId ?? DBNull.Value);

        return true;
    }

    internal static async Task CreditWalletAsync(NpgsqlConnection connection, NpgsqlTransaction tx,
                                                Guid accountId, Guid? characterId, string currency,
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
            tx, accountId, currency, amount, reason,
            // NULL rather than a placeholder guid: character_id carries a foreign key,
            // and an account-level movement -- a coin pack, say -- belongs to no
            // character. Guid.Empty is not a character and the insert rejects it.
            (object?)characterId ?? DBNull.Value);
    }

    internal static async Task WriteItemLedgerAsync(NpgsqlConnection connection, NpgsqlTransaction tx,
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

    /// <summary>Internal so the item-use endpoint reads a bag the same way a settle does.</summary>
    internal static async Task<List<InventoryEntry>> ReadInventoryAsync(
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
    internal static async Task WriteInventoryAsync(NpgsqlConnection connection, NpgsqlTransaction tx,
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

    /// <summary>
    /// Multiplication that saturates rather than wrapping.
    ///
    /// Its own copy rather than the rules' -- that one is private to Settlement, and
    /// widening it to share four lines would export an implementation detail. The
    /// property that matters is the same: experience that wrapped negative would be
    /// a character losing levels as a reward.
    /// </summary>
    private static long MultiplyClamped(long count, long each)
    {
        if (count <= 0L || each <= 0L) return 0L;

        return count > long.MaxValue / each ? long.MaxValue : count * each;
    }

    /// <summary>Thirty slots, matching InventoryManager.MaxSlots on the client.</summary>
    public const int InventoryCapacity = 30;

    // ── Reading the row ───────────────────────────────────────────────────────

    private sealed class Row
    {
        public Guid            AccountId;
        public ActivityState   Activity = new();
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
