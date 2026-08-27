using System.Text.Json;
using IdleExplorers.Api.Auth;
using IdleExplorers.Api.Infrastructure;
using IdleExplorers.Api.Services;
using IdleExplorers.Rules;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace IdleExplorers.Api.Endpoints;

/// <summary>
/// The boss fight. Engage, act, resolve, claim.
///
/// ══ THE ONLY THING THAT KEEPS THIS HONEST ═════════════════════════════════════
///
///     cumulative damage  ≤  frozen DPS × (now − started_at + tolerance)
///
/// Both terms are server-owned: the DPS is frozen at engage from what the character
/// was wearing, and the elapsed time is the database clock. No client can move either.
/// A request claiming a million damage is credited with what the character could have
/// done in the time that has actually passed, which is the honest number.
///
/// Everything else -- sequence numbers, ability cooldowns, batch limits -- shapes the
/// fight rather than securing it. That separation is deliberate. A defence assembled
/// from five checks fails the day somebody adds a sixth code path and forgets one; a
/// defence that is one inequality applied at one place does not.
///
/// ══ WHAT THE CLIENT IS TRUSTED WITH ═══════════════════════════════════════════
///
/// Its own position, its own dodging, and every pixel of the presentation. None of it
/// is verifiable and none of it pays anything. The fail condition is the ENRAGE TIMER,
/// not player death, precisely because death is the unverifiable half -- see
/// BossEncounter for the whole argument.
///
/// ══ WHY LOOT LANDS IN pending_loot ════════════════════════════════════════════
///
/// Because a bag can be full, and a boss kill that silently evaporates the drop
/// because slot thirty held four logs is the worst thing this game could do to
/// somebody's evening. Earning and collecting are separated: the first is decided once
/// and cannot be undone, the second is retryable forever.
/// </summary>
public static class EncounterEndpoints
{
    /// <summary>
    /// Actions accepted from one request beyond the batch limit.
    ///
    /// Over the limit the request is REFUSED rather than truncated, because a client
    /// sending more than this is not lagging -- the client posts twice a second by
    /// design -- and silently dropping the tail would hide the bug.
    /// </summary>
    public const int MaxBatch = BossEncounter.MaxActionsPerRequest;

    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/encounter").RequireAuthorization();

        // ── Engage ────────────────────────────────────────────────────────────
        group.MapPost("/{characterId:guid}", async Task<IResult> (HttpContext http, Caller caller, Db db,
                                                    IGameClock clock, ContentCache content,
                                                    SettlementService settlement,
                                                    Guid characterId,
                                                    [FromBody] EngageRequest? request) =>
        {
            Guid? accountId = await caller.AccountIdAsync(http.User, http.RequestAborted);
            if (accountId is null) return Results.Unauthorized();

            if (!await caller.OwnsCharacterAsync(accountId.Value, characterId, http.RequestAborted))
                return NotYours();

            string monsterId = request?.MonsterId ?? "goblin_king";

            MonsterData? boss = content.Catalogue.GetMonster(monsterId);

            if (boss is null || !boss.isBoss)
            {
                return Results.Problem(
                    title:      "not a boss",
                    detail:     $"'{monsterId}' is not something you engage.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            DateTimeOffset now = await clock.NowAsync(http.RequestAborted);

            return await db.InCharacterTransactionAsync<IResult>(characterId, async (connection, tx) =>
            {
                // Settled first, inside the lock. The gate counts active kills and
                // settlement is what writes them, so checking before settling would
                // refuse a player whose thousandth goblin is sitting unpaid.
                await settlement.SettleLockedAsync(connection, tx, characterId, now, http.RequestAborted);

                bool unlocked = await connection.ScalarAsync<long>(
                    "select count(*) from unlock where character_id = $1 and unlock_id = $2;",
                    tx, characterId, BossEndpoints.GateUnlock) > 0L;

                if (!unlocked)
                {
                    // Re-checked here even though /boss/unlock checks it. The read is
                    // for the UI and may be a second stale; THIS is the one that
                    // matters, and it holds the character lock.
                    long active = await connection.ScalarAsync<long>(
                        """
                        select coalesce(active_kills, 0) from kill_counter
                         where character_id = $1 and monster_id = $2;
                        """,
                        tx, characterId, BossEndpoints.GateMonster);

                    return Results.Problem(
                        title:      "portal sealed",
                        detail:     $"{Math.Max(0L, BossEndpoints.GoblinKingGate - active)} more active goblin kills.",
                        statusCode: StatusCodes.Status409Conflict);
                }

                // An abandoned fight is closed before a new one starts, so a player who
                // alt-tabbed out of a wipe is not locked out of the boss forever by
                // their own dead encounter row.
                await ExpireStaleAsync(connection, tx, characterId, now);

                var frozen = await settlement.FreezeCombatAsync(connection, tx, characterId,
                                                                http.RequestAborted);

                double enrageSeconds = BossEncounter.ClampEnrage(boss.enrageSeconds);

                // Seeded from the character and the instant, so every roll in this
                // fight is re-derivable from two columns months later.
                long seed = HashCode.Combine(characterId, now.UtcTicks);

                Guid encounterId;

                try
                {
                    encounterId = await connection.ScalarAsync<Guid>(
                        """
                        insert into encounter (character_id, monster_id, frozen_dps,
                                               frozen_attack_seconds, seed, boss_max_hp,
                                               started_at, enrage_at)
                        values ($1, $2, $3, $4, $5, $6, $7, $8)
                        returning id;
                        """,
                        tx, characterId, monsterId, frozen.Dps, frozen.AttackSeconds,
                        seed, MaxHpOf(boss), now, now.AddSeconds(enrageSeconds));
                }
                catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
                {
                    // The partial unique index. Two engages racing would otherwise both
                    // start a fight, be fed by one set of actions, and loot twice.
                    return Results.Problem(
                        title:      "already fighting",
                        detail:     "You are already in an encounter. Resolve it first.",
                        statusCode: StatusCodes.Status409Conflict);
                }

                await TelemetryEndpoints.RecordAsync(
                    connection, tx, accountId.Value, characterId,
                    TelemetryEvents.BossEngaged, now,
                    ("monster", monsterId),
                    ("dps",     frozen.Dps.ToString("F1")));

                return Results.Ok(Describe(encounterId, boss, frozen, seed, enrageSeconds,
                                           damageDealt: 0L, elapsed: 0d));
            }, http.RequestAborted);
        });

        // ── Act ───────────────────────────────────────────────────────────────
        //
        // The client batches and posts at most twice a second. It reports WHAT IT DID,
        // never what that was worth -- the damage is computed here, from the frozen
        // snapshot, and clamped to what the clock allows.
        group.MapPost("/{characterId:guid}/actions", async Task<IResult> (HttpContext http, Caller caller, Db db,
                                                            IGameClock clock, ContentCache content,
                                                            Guid characterId,
                                                            [FromBody] ActionBatch? batch) =>
        {
            Guid? accountId = await caller.AccountIdAsync(http.User, http.RequestAborted);
            if (accountId is null) return Results.Unauthorized();

            if (!await caller.OwnsCharacterAsync(accountId.Value, characterId, http.RequestAborted))
                return NotYours();

            var actions = batch?.Actions ?? [];

            if (actions.Length > MaxBatch)
            {
                return Results.Problem(
                    title:      "batch too large",
                    detail:     $"At most {MaxBatch} actions per request.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            DateTimeOffset now = await clock.NowAsync(http.RequestAborted);

            return await db.InCharacterTransactionAsync<IResult>(characterId, async (connection, tx) =>
            {
                Live? live = await ReadLiveAsync(connection, tx, characterId, now);

                if (live is null) return NoFight();

                MonsterData? boss = content.Catalogue.GetMonster(live.MonsterId);
                if (boss is null) return NoFight();

                double elapsed = (now - live.StartedAt).TotalSeconds;

                long   damage    = live.DamageDealt;
                long   sequence  = live.LastSequence;
                int    accepted  = 0, rejected = 0;

                var cooldowns = Cooldowns(live.AbilityUsed);

                double ceiling = BossEncounter.DamageCeiling(live.FrozenDps, elapsed);

                // Seeded once and indexed by the action's own sequence, so the roll for
                // action 47 is the same number whichever request carried it -- a retry
                // cannot re-roll a bad hit into a good one.
                var rng = new CounterRandom(live.Seed, (ulong)Math.Max(0L, sequence));

                foreach (var action in actions)
                {
                    if (action is null) { rejected++; continue; }

                    // Strictly increasing. Cheap, and it makes a replayed request a
                    // comparison rather than a lookup of every request ever seen.
                    if (action.Sequence <= sequence) { rejected++; continue; }

                    BossAbility? ability = AbilityFor(boss, action.AbilityId);

                    if (!string.IsNullOrEmpty(action.AbilityId) && ability is null)
                    {
                        rejected++;
                        continue;
                    }

                    if (ability is not null)
                    {
                        double lastUsed = cooldowns.TryGetValue(ability.id, out double at) ? at : -1d;

                        if (!BossEncounter.AbilityReady(lastUsed, elapsed, ability.cooldownSeconds))
                        {
                            rejected++;
                            continue;
                        }

                        cooldowns[ability.id] = elapsed;
                    }

                    double swing = BossEncounter.SwingDamage(
                        live.FrozenDps, live.FrozenAttackSeconds,
                        ability?.damageMultiplier ?? 1f, boss.armor, rng);

                    damage   = Add(damage, (long)Math.Round(swing));
                    sequence = action.Sequence;
                    accepted++;
                }

                // ══ THE CEILING ═══════════════════════════════════════════════
                //
                // Applied to the CUMULATIVE total, never to this batch. A per-batch
                // bound would let a client idle for ten minutes and then send a
                // legitimate-looking burst every second for the rest of the fight.
                long allowed = (long)Math.Floor(ceiling);

                bool clamped = damage > allowed;

                if (clamped)
                {
                    damage = allowed;

                    // Recorded, not refused. Honest clients hit this at the very start
                    // of a fight when the tolerance is the whole budget, so refusing
                    // would break the opening swing for everybody to catch nobody.
                    await TelemetryEndpoints.RecordSecurityAsync(
                        connection, accountId.Value, "boss_damage_clamped",
                        $"{{\"encounter\":\"{live.Id}\",\"elapsed\":{elapsed:F1}}}",
                        now, http.RequestAborted);
                }

                if (damage < live.DamageDealt) damage = live.DamageDealt;

                await connection.ExecuteAsync(
                    """
                    update encounter
                       set damage_dealt = $2, last_sequence = $3, ability_used = $4::jsonb
                     where id = $1;
                    """,
                    tx, live.Id, damage, sequence, JsonSerializer.Serialize(cooldowns));

                long remaining = Math.Max(0L, live.BossMaxHp - damage);

                return Results.Ok(new
                {
                    encounterId = live.Id,

                    // The authoritative health. The client predicts locally from the
                    // shared rules and reconciles to this -- which is why it is
                    // returned on every action rather than only on request.
                    bossHp      = remaining,
                    bossMaxHp   = live.BossMaxHp,
                    phase       = BossEncounter.PhaseFor(remaining / (double)live.BossMaxHp, boss.phases),

                    elapsedSeconds   = elapsed,
                    remainingSeconds = Math.Max(0d, (live.EnrageAt - now).TotalSeconds),

                    accepted,
                    rejected,

                    // Stated plainly rather than hidden. A client that is being clamped
                    // has a bug or a cheat, and either way somebody should be able to
                    // see it without reading the security table.
                    clamped,

                    dead    = remaining <= 0L,
                    enraged = now >= live.EnrageAt,
                });
            }, http.RequestAborted);
        });

        // ── Resolve ───────────────────────────────────────────────────────────
        //
        // The SERVER decides the boss died, from its own health value. A client saying
        // "I killed it" is a claim about a reward, which is the whole thing this
        // architecture removed.
        group.MapPost("/{characterId:guid}/resolve", async Task<IResult> (HttpContext http, Caller caller, Db db,
                                                            IGameClock clock, ContentCache content,
                                                            Guid characterId) =>
        {
            Guid? accountId = await caller.AccountIdAsync(http.User, http.RequestAborted);
            if (accountId is null) return Results.Unauthorized();

            if (!await caller.OwnsCharacterAsync(accountId.Value, characterId, http.RequestAborted))
                return NotYours();

            DateTimeOffset now = await clock.NowAsync(http.RequestAborted);

            return await db.InCharacterTransactionAsync<IResult>(characterId, async (connection, tx) =>
            {
                Live? live = await ReadLiveAsync(connection, tx, characterId, now);

                if (live is null) return NoFight();

                MonsterData? boss = content.Catalogue.GetMonster(live.MonsterId);
                if (boss is null) return NoFight();

                double elapsed = (now - live.StartedAt).TotalSeconds;

                // The one line that decides the fight, and it reads only server state.
                bool won = live.DamageDealt >= live.BossMaxHp;

                await connection.ExecuteAsync(
                    "update encounter set ended_at = $2, won = $3 where id = $1;",
                    tx, live.Id, now, won);

                var drops = new List<(string ItemId, long Quantity)>();
                long xp = 0L;

                if (won)
                {
                    xp = Math.Max(0L, boss.xpReward);

                    // Seeded from the encounter, offset past the action indices so a
                    // loot roll can never collide with a damage roll from the same
                    // fight -- which would make the drop a function of how many times
                    // the player swung.
                    var rng = new CounterRandom(live.Seed, LootSeedOffset);

                    foreach (var entry in boss.lootTable ?? [])
                    {
                        if (entry is null) continue;

                        long quantity = RollDrop(entry, rng);
                        if (quantity <= 0L) continue;

                        drops.Add((entry.itemId, quantity));

                        await connection.ExecuteAsync(
                            """
                            insert into pending_loot (character_id, encounter_id, item_id, quantity)
                            values ($1, $2, $3, $4);
                            """,
                            tx, characterId, live.Id, entry.itemId, quantity);
                    }

                    if (xp > 0L)
                    {
                        await connection.ExecuteAsync(
                            "update character set xp = xp + $2 where id = $1;",
                            tx, characterId, xp);
                    }
                }

                await TelemetryEndpoints.RecordAsync(
                    connection, tx, accountId.Value, characterId,
                    TelemetryEvents.BossEnded, now,
                    ("monster", live.MonsterId),
                    ("won",     won ? "true" : "false"),
                    ("seconds", ((long)elapsed).ToString()),
                    ("damage",  live.DamageDealt.ToString()));

                return Results.Ok(new
                {
                    encounterId = live.Id,
                    won,
                    xpGained    = xp,
                    seconds     = elapsed,
                    damageDealt = live.DamageDealt,
                    bossMaxHp   = live.BossMaxHp,

                    // Named "pending" rather than "loot" on purpose: it is earned and
                    // recorded, and it is not in the bag until it is claimed.
                    pending     = drops.Select(d => new { itemId = d.ItemId, quantity = d.Quantity }).ToArray(),
                });
            }, http.RequestAborted);
        });

        // ── Claim ─────────────────────────────────────────────────────────────
        group.MapPost("/{characterId:guid}/loot", async Task<IResult> (HttpContext http, Caller caller, Db db,
                                                         SettlementService settlement,
                                                         Guid characterId) =>
        {
            Guid? accountId = await caller.AccountIdAsync(http.User, http.RequestAborted);
            if (accountId is null) return Results.Unauthorized();

            if (!await caller.OwnsCharacterAsync(accountId.Value, characterId, http.RequestAborted))
                return NotYours();

            return await db.InCharacterTransactionAsync<IResult>(characterId, async (connection, tx) =>
            {
                var waiting = new List<(long Id, string ItemId, long Quantity)>();

                await using (var command = connection.Sql(
                    """
                    select id, item_id, quantity from pending_loot
                     where character_id = $1 and claimed_at is null
                     order by id;
                    """,
                    tx, characterId))
                {
                    await using var reader = await command.ExecuteReaderAsync(http.RequestAborted);

                    while (await reader.ReadAsync(http.RequestAborted))
                        waiting.Add((reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2)));
                }

                var claimed = new List<object>();
                long stillWaiting = 0L;

                foreach (var (id, itemId, quantity) in waiting)
                {
                    long fitted = await settlement.GrantItemAsync(
                        connection, tx, accountId.Value, characterId,
                        itemId, quantity, "boss_loot", http.RequestAborted);

                    if (fitted <= 0L) { stillWaiting++; continue; }

                    if (fitted >= quantity)
                    {
                        // Marked rather than deleted. A claim is an economic event, and
                        // "where did this crown come from" should be answerable.
                        await connection.ExecuteAsync(
                            "update pending_loot set claimed_at = now() where id = $1;", tx, id);
                    }
                    else
                    {
                        // A partial fit leaves the remainder pending rather than
                        // destroying it. The alternative is a player losing four
                        // hundred coins of a stack because one slot was short.
                        await connection.ExecuteAsync(
                            "update pending_loot set quantity = quantity - $2 where id = $1;",
                            tx, id, fitted);

                        stillWaiting++;
                    }

                    claimed.Add(new { itemId, quantity = fitted });
                }

                return Results.Ok(new
                {
                    claimed      = claimed.ToArray(),
                    stillWaiting,

                    // Said out loud, because the reason nothing arrived is almost
                    // always a full bag and a silent no-op is indistinguishable from
                    // a broken button.
                    bagWasFull   = stillWaiting > 0L,
                });
            }, http.RequestAborted);
        });

        // ── What is waiting ───────────────────────────────────────────────────
        group.MapGet("/{characterId:guid}/loot", async Task<IResult> (HttpContext http, Caller caller, Db db,
                                                        Guid characterId) =>
        {
            Guid? accountId = await caller.AccountIdAsync(http.User, http.RequestAborted);
            if (accountId is null) return Results.Unauthorized();

            if (!await caller.OwnsCharacterAsync(accountId.Value, characterId, http.RequestAborted))
                return NotYours();

            await using var connection = await db.OpenAsync(http.RequestAborted);

            var waiting = new List<object>();

            await using var command = connection.Sql(
                """
                select item_id, quantity from pending_loot
                 where character_id = $1 and claimed_at is null
                 order by id;
                """,
                null, characterId);

            await using var reader = await command.ExecuteReaderAsync(http.RequestAborted);

            while (await reader.ReadAsync(http.RequestAborted))
                waiting.Add(new { itemId = reader.GetString(0), quantity = reader.GetInt64(1) });

            return Results.Ok(new { pending = waiting.ToArray() });
        });
    }

    // ── Reading the fight ─────────────────────────────────────────────────────

    /// <summary>
    /// Index at which loot rolls start.
    ///
    /// Far past any plausible action count, so a loot roll can never share an index
    /// with a damage roll from the same fight. Sharing would make the drop a function
    /// of how many times the player swung -- which is not random, it is a strategy.
    /// </summary>
    private const ulong LootSeedOffset = 1_000_000UL;

    private sealed record Live(Guid Id, string MonsterId, double FrozenDps, double FrozenAttackSeconds,
                               long Seed, long BossMaxHp, long DamageDealt, long LastSequence,
                               string AbilityUsed, DateTimeOffset StartedAt, DateTimeOffset EnrageAt);

    private static async Task<Live?> ReadLiveAsync(NpgsqlConnection connection, NpgsqlTransaction tx,
                                                   Guid characterId, DateTimeOffset now)
    {
        await using var command = connection.Sql(
            """
            select id, monster_id, frozen_dps, frozen_attack_seconds, seed, boss_max_hp,
                   damage_dealt, last_sequence, ability_used::text, started_at, enrage_at
              from encounter
             where character_id = $1 and ended_at is null;
            """,
            tx, characterId);

        await using var reader = await command.ExecuteReaderAsync();

        if (!await reader.ReadAsync()) return null;

        return new Live(
            reader.GetGuid(0), reader.GetString(1), reader.GetDouble(2), reader.GetDouble(3),
            reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7),
            reader.GetString(8),
            reader.GetFieldValue<DateTimeOffset>(9), reader.GetFieldValue<DateTimeOffset>(10));
    }

    /// <summary>
    /// Closes a fight that ran past its enrage timer and was never resolved.
    ///
    /// A player who alt-tabs out of a wipe would otherwise be locked out of the boss
    /// forever by their own dead encounter row -- the partial unique index does not
    /// care that nobody is playing it. Marked as a loss, because it was one.
    /// </summary>
    private static async Task ExpireStaleAsync(NpgsqlConnection connection, NpgsqlTransaction tx,
                                               Guid characterId, DateTimeOffset now)
    {
        await connection.ExecuteAsync(
            """
            update encounter
               set ended_at = $2, won = false
             where character_id = $1 and ended_at is null and enrage_at <= $2;
            """,
            tx, characterId, now);
    }

    private static BossAbility? AbilityFor(MonsterData boss, string? abilityId)
    {
        if (string.IsNullOrEmpty(abilityId) || boss.phases == null) return null;

        foreach (var phase in boss.phases)
        {
            if (phase?.abilities == null) continue;

            foreach (var ability in phase.abilities)
                if (ability != null && ability.id == abilityId) return ability;
        }

        return null;
    }

    private static Dictionary<string, double> Cooldowns(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new Dictionary<string, double>(StringComparer.Ordinal);

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, double>>(json)
                   ?? new Dictionary<string, double>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            // Cannot happen from the column, which is jsonb. Returning empty rather
            // than throwing means a corrupt row costs the player a free ability use
            // instead of ending their fight.
            return new Dictionary<string, double>(StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// One loot line, rolled once.
    ///
    /// A single kill, so this is a straight roll rather than the expected-value path
    /// the farm settlement takes for tens of thousands of kills.
    /// </summary>
    private static long RollDrop(LootEntry entry, IRandomSource rng)
    {
        double chance = entry.DropChance;

        if (chance <= 0d || rng.Next01() > chance) return 0L;

        long min = Math.Max(0L, entry.minQty);
        long max = Math.Max(min, entry.maxQty);

        return min == max ? min : rng.RangeInclusive(min, max);
    }

    private static object Describe(Guid id, MonsterData boss, SettlementService.FrozenCombat frozen,
                                   long seed, double enrageSeconds, long damageDealt, double elapsed)
    {
        long maxHp = MaxHpOf(boss);

        // ══ THE WHOLE TIMELINE, UP FRONT ══════════════════════════════════════
        //
        // Every phase's schedule, so the client draws every telegraph at exactly the
        // right moment with no network involved. Asking the server "what is the boss
        // doing now" would put the latency inside the reaction window, which is the
        // difference between a fight and a slideshow.
        var phases = new List<object>();

        for (int i = 0; boss.phases != null && i < boss.phases.Length; i++)
        {
            var phase = boss.phases[i];
            if (phase is null) continue;

            phases.Add(new
            {
                index              = i,
                name               = phase.name ?? "",
                fromHealthFraction = phase.fromHealthFraction,
                hasteMultiplier    = phase.hasteMultiplier,
                addsPerWave        = phase.addsPerWave,
                secondsBetweenWaves = phase.secondsBetweenWaves,

                casts = BossEncounter.Timeline(seed, i, phase, enrageSeconds).ToArray(),
            });
        }

        return new
        {
            encounterId = id,
            monsterId   = boss.id,
            name        = boss.name,

            bossHp      = Math.Max(0L, maxHp - damageDealt),
            bossMaxHp   = maxHp,
            armor       = boss.armor,

            // Echoed so the client can predict its own numbers from the shared rules
            // and reconcile. It is not authority -- it is a copy of the authority.
            frozenDps           = frozen.Dps,
            frozenAttackSeconds = frozen.AttackSeconds,

            enrageSeconds,
            elapsedSeconds = elapsed,

            phases = phases.ToArray(),
        };
    }

    /// <summary>
    /// The boss's health as a whole number.
    ///
    /// Content declares it as a double because every other rate in the game is one.
    /// Rounded once, HERE, so the column and every comparison against it agree -- a
    /// boss rounded differently at engage and at resolve would be beatable at 2,399.5
    /// and not at 2,400.
    /// </summary>
    private static long MaxHpOf(MonsterData boss) =>
        (long)Math.Max(1d, Math.Round(boss.maxHp));

    private static long Add(long a, long b)
    {
        long sum = a + b;

        return sum < a ? long.MaxValue : sum;   // saturate rather than wrap
    }

    private static IResult NoFight() =>
        Results.Problem(
            title:      "no encounter",
            detail:     "You are not fighting anything.",
            statusCode: StatusCodes.Status409Conflict);

    private static IResult NotYours() =>
        Results.Problem(
            title:      "no such character",
            detail:     "That character does not exist, or does not belong to you.",
            statusCode: StatusCodes.Status404NotFound);

    public sealed record EngageRequest(string? MonsterId);

    public sealed record ActionBatch(BossAction[]? Actions);

    /// <summary>
    /// What the client says it did.
    ///
    /// A sequence number and optionally an ability. Note what is NOT here: no damage,
    /// no timestamp, no target health, no position. Every one of those would be a
    /// claim the server would have to either trust or ignore, and a field that is
    /// always ignored is a field somebody eventually starts trusting.
    /// </summary>
    public sealed record BossAction(long Sequence, string? AbilityId);
}
