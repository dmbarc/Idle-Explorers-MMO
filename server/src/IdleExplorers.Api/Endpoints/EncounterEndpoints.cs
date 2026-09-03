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
                                                    SettlementService settlement, FeatureFlags flags,
                                                    Guid characterId,
                                                    [FromBody] EngageRequest? request) =>
        {
            Guid? accountId = await caller.AccountIdAsync(http.User, http.RequestAborted);
            if (accountId is null) return Results.Unauthorized();

            // Checked before anything else touches the database, and checked HERE
            // rather than trusting the copy handed to the client at login. The client
            // copy hides the portal; this is what stops the fight.
            if (!await flags.IsEnabledAsync(FeatureFlags.GoblinKing, http.RequestAborted))
                return SwitchedOff("The Goblin King");

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

                double enrageSeconds = BossEncounter.ClampEnrage(boss.enrageSeconds);

                Guid? partyId = await connection.ScalarAsync<Guid?>(
                    "select party_id from party_member where character_id = $1;", tx, characterId);

                Guid encounterId;
                bool joinedExisting;

                // ══ ENGAGING A FIGHT YOU ARE ALREADY IN IS A REJOIN ════════════════
                //
                // It used to be a 409, and that one refusal is what made the King look
                // broken in a playtest. Dying does not resolve an encounter, nor does
                // closing the tab, nor does walking back out of the arena -- so a
                // player who did any of those had a live row with their name on it, and
                // every attempt to fight the King again for the next five minutes was
                // answered "you are already in an encounter" by a server talking about
                // a fight nobody was playing.
                //
                // From inside the client that reads as an arena with no boss in it:
                // engage fails, so BossController never starts, so there are no
                // telegraphs, no health bar, and nothing to hit.
                //
                // So walking back in is a rejoin, into exactly the fight that was
                // already running -- right down to the seed.
                //
                // ══ AND THE SNAPSHOT IS NOT REFROZEN ═══════════════════════════════
                //
                // Deliberately, and it is what makes the rejoin safe. A rejoin that
                // refroze would be a gear swap: engage, see the King is a phase ahead,
                // walk out, put the good sword on, walk back in. The participant row
                // keeps whatever was frozen when this character FIRST joined, and that
                // is both what is echoed back and what the damage ceiling is drawn
                // from.
                Live? mine = await ReadLiveAsync(connection, tx, characterId, now);

                if (mine is not null && !string.Equals(mine.MonsterId, monsterId, StringComparison.Ordinal))
                {
                    // A live fight against something else. THIS one really is a
                    // refusal -- two bosses at once is what the index exists to stop.
                    return Results.Problem(
                        title:      "already fighting",
                        detail:     "You are already in an encounter. Resolve it first.",
                        statusCode: StatusCodes.Status409Conflict);
                }

                if (mine is not null)
                {
                    encounterId    = mine.Id;
                    joinedExisting = true;
                }
                else
                {
                    // Frozen only for somebody who is not already in the fight, for the
                    // reason above.
                    var frozen = await settlement.FreezeCombatAsync(connection, tx, characterId,
                                                                    http.RequestAborted);

                    // ══ WALKING INTO A FIGHT ALREADY IN PROGRESS ═══════════════════
                    //
                    // A group has ONE fight. Whoever engages first starts it; everybody
                    // else joins the same boss and the same health pool.
                    //
                    // Their snapshot is frozen NOW, not when the fight began, so
                    // arriving late brings the gear you are wearing. The CLOCK is the
                    // encounter's own, though: a latecomer does not get a fresh five
                    // minutes, because that would make walking out and back in the way
                    // to beat the enrage timer.
                    Guid? running = partyId is null ? null : await connection.ScalarAsync<Guid?>(
                        """
                        select id from encounter
                        where party_id = $1 and ended_at is null and monster_id = $2;
                        """,
                        tx, partyId.Value, monsterId);

                    if (running is not null)
                    {
                        encounterId    = running.Value;
                        joinedExisting = true;
                    }
                    else
                    {
                        // Seeded from the character and the instant, so every roll in
                        // this fight is re-derivable from two columns months later.
                        long seed = HashCode.Combine(characterId, now.UtcTicks);

                        try
                        {
                            encounterId = await connection.ScalarAsync<Guid>(
                                """
                                insert into encounter (character_id, monster_id, frozen_dps,
                                                       frozen_attack_seconds, seed, boss_max_hp,
                                                       started_at, enrage_at, party_id)
                                values ($1, $2, $3, $4, $5, $6, $7, $8, $9)
                                returning id;
                                """,
                                tx, characterId, monsterId, frozen.Dps, frozen.AttackSeconds,
                                seed, MaxHpOf(boss), now, now.AddSeconds(enrageSeconds), partyId);
                        }
                        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
                        {
                            // Two engages racing. Both would otherwise start a fight, be
                            // fed by one set of actions, and loot twice.
                            return Results.Problem(
                                title:      "already fighting",
                                detail:     "You are already in an encounter. Resolve it first.",
                                statusCode: StatusCodes.Status409Conflict);
                        }

                        joinedExisting = false;
                    }

                    try
                    {
                        await connection.ExecuteAsync(
                            """
                            insert into encounter_participant
                                (encounter_id, character_id, frozen_dps, frozen_attack_seconds, joined_at)
                            values ($1, $2, $3, $4, $5);
                            """,
                            tx, encounterId, characterId, frozen.Dps, frozen.AttackSeconds, now);
                    }
                    catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
                    {
                        // Raced with themselves. Harmless: the row that won holds the
                        // same snapshot, and the describe below is what they asked for.
                        joinedExisting = true;
                    }
                }

                // ══ DESCRIBED FROM THE ENCOUNTER, NEVER FROM THIS REQUEST ══════════
                //
                // The seed, the clock, the pool and this fighter's own frozen snapshot,
                // all read back out of the rows that are actually running.
                //
                // The bug this closes: a joiner used to be handed the seed generated
                // for THEIR request, so the entire attack timeline they telegraphed
                // came from a different fight than the one the leader was seeing --
                // two people in one arena dodging different cones. Their elapsed was
                // zero as well, so their enrage clock restarted and their King fought
                // on for five minutes after everybody else's had given up.
                Joined live = await ReadJoinedAsync(connection, tx, encounterId, characterId);

                await TelemetryEndpoints.RecordAsync(
                    connection, tx, accountId.Value, characterId,
                    TelemetryEvents.BossEngaged, now,
                    ("monster", monsterId),
                    ("dps",     live.FrozenDps.ToString("F1")),
                    ("joined",  joinedExisting ? "existing" : "new"),
                    ("party",   partyId?.ToString() ?? ""));

                return Results.Ok(Describe(encounterId, boss, live.FrozenDps, live.FrozenAttackSeconds,
                                           live.Seed, (live.EnrageAt - live.StartedAt).TotalSeconds,
                                           damageDealt: live.DamageDealt,
                                           elapsed: Math.Max(0d, (now - live.StartedAt).TotalSeconds)));
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

                // ══ TWO CLOCKS ══════════════════════════════════════════════════════
                //
                // elapsed is the FIGHT's age, and it is what ability cooldowns and the
                // boss timeline are measured against -- those are properties of the
                // encounter and are the same for everybody in it.
                //
                // mine is how long THIS fighter has been in the room, and it is what
                // the damage ceiling is measured against. Somebody who joined a minute
                // late must not inherit a minute of allowance they were not present
                // for; using elapsed for both would hand it to them.
                double elapsed = (now - live.StartedAt).TotalSeconds;
                double mine    = (now - live.JoinedAt).TotalSeconds;

                long   damage    = live.MyDamage;
                long   sequence  = live.LastSequence;
                int    accepted  = 0, rejected = 0;

                var cooldowns = Cooldowns(live.AbilityUsed);

                // ══ PER FIGHTER, NEVER ON THE POOL ═══════════════════════════════════
                //
                // The ceiling bounds what THIS character can have contributed. Applied
                // to the shared total instead, four people would pool their allowances
                // and any one of them could spend the lot -- which is a group fight as
                // a damage multiplier for a single cheating client.
                double ceiling = BossEncounter.DamageCeiling(live.FrozenDps, mine);

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

                    AbilityData? ability = AbilityFor(content.Catalogue, action.AbilityId);

                    if (!string.IsNullOrEmpty(action.AbilityId) && ability is null)
                    {
                        rejected++;
                        continue;
                    }

                    // A passive is not something you press. Reported as an action it is
                    // either a client bug or somebody looking for a free swing with a
                    // zero cooldown.
                    if (ability is not null && !AbilityPricing.IsPressable(ability))
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
                        ability is null ? 1d : AbilityPricing.DamageMultiplier(ability),
                        boss.armor, rng);

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

                if (damage < live.MyDamage) damage = live.MyDamage;

                // What this batch actually added, after the clamp. The pool moves by
                // the DELTA rather than being assigned, because three other people may
                // have hit the King between this fighter's last request and this one.
                long contributed = damage - live.MyDamage;

                await connection.ExecuteAsync(
                    """
                    update encounter_participant
                    set damage_dealt = $3, last_sequence = $4, ability_used = $5::jsonb
                    where encounter_id = $1 and character_id = $2;
                    """,
                    tx, live.Id, characterId, damage, sequence, JsonSerializer.Serialize(cooldowns));

                await connection.ExecuteAsync(
                    """
                    update encounter set damage_dealt = damage_dealt + $2 where id = $1;
                    """,
                    tx, live.Id, contributed);

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
                var offered = new List<object>();
                long xp = 0L;

                if (won)
                {
                    xp = Math.Max(0L, boss.xpReward);

                    // ══ EVERYBODY WHO FOUGHT IT ══════════════════════════════════════
                    //
                    // Not just whoever pressed resolve. A group that killed the King
                    // together and handed the drops to one of them is a group nobody
                    // joins twice.
                    //
                    // Read from encounter_participant rather than from the party,
                    // because the party is what it is NOW and the fight is what it
                    // was: somebody who left the group mid-fight still swung, and
                    // somebody who joined the group afterwards did not.
                    var fighters = new List<Guid>();

                    await using (var command = connection.Sql(
                        "select character_id from encounter_participant where encounter_id = $1 order by joined_at;",
                        tx, live.Id))
                    {
                        await using var reader = await command.ExecuteReaderAsync(http.RequestAborted);

                        while (await reader.ReadAsync(http.RequestAborted))
                            fighters.Add(reader.GetGuid(0));
                    }

                    if (fighters.Count == 0) fighters.Add(characterId);

                    // ══ EXPERIENCE IS NOT CONTESTED ════════════════════════════════
                    //
                    // Everybody who fought it gets the full amount, and nobody rolls
                    // for it. Splitting experience would make a group strictly worse
                    // than soloing for anybody who could manage it, which is the wrong
                    // thing for the only group encounter in the game to teach.
                    if (xp > 0L)
                    {
                        foreach (Guid fighter in fighters)
                        {
                            await connection.ExecuteAsync(
                                "update character set xp = xp + $2 where id = $1;",
                                tx, fighter, xp);
                        }
                    }

                    // ══ ITEMS ARE ════════════════════════════════════════════════════
                    //
                    // One table, rolled once, and the group rolls for what fell.
                    //
                    // It used to roll the whole table separately for every fighter,
                    // and the reason that had to change is arithmetic: four people
                    // killing the King produced four Goblin Spears, so by the second
                    // clear the drop nobody could get was the drop everybody had. A
                    // boss whose rewards multiply by party size has no reason to be a
                    // group boss.
                    //
                    // Solo is the exception, and not out of kindness -- offering one
                    // person a need/greed window against themselves is a dialog with
                    // one button in it. One fighter means the drop is theirs.
                    var rng = new CounterRandom(live.Seed, LootSeedOffset);

                    int rollIndex = 0;

                    foreach (var entry in boss.lootTable ?? [])
                    {
                        if (entry is null) continue;

                        long quantity = RollDrop(entry, rng);
                        if (quantity <= 0L) continue;

                        if (fighters.Count == 1)
                        {
                            if (fighters[0] == characterId) drops.Add((entry.itemId, quantity));

                            await connection.ExecuteAsync(
                                """
                                insert into pending_loot (character_id, encounter_id, item_id, quantity)
                                values ($1, $2, $3, $4);
                                """,
                                tx, fighters[0], live.Id, entry.itemId, quantity);

                            continue;
                        }

                        Guid rollId = await connection.ScalarAsync<Guid>(
                            """
                            insert into loot_roll (encounter_id, item_id, quantity, roll_index, offered_at)
                            values ($1, $2, $3, $4, $5)
                            returning id;
                            """,
                            tx, live.Id, entry.itemId, quantity, rollIndex, now);

                        offered.Add(new { rollId, itemId = entry.itemId, quantity });

                        rollIndex++;
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

                    // What the GROUP has to settle between them. Empty when one person
                    // fought it, because a roll against nobody is a dialog with one
                    // button in it.
                    rolls       = offered.ToArray(),
                });
            }, http.RequestAborted);
        });


        // ── Flee ──────────────────────────────────────────────────────────────
        //
        // ══ WHY LEAVING NEEDS AN ENDPOINT AT ALL ══════════════════════════════
        //
        // Because the throne was a one-way door: the only exits were killing the King
        // or dying to him, and neither is something a player should have to do to get
        // out of a room they walked into by accident.
        //
        // Adding a door to the ROOM is not enough on its own. Walking out leaves a
        // live encounter row with your name on it, and until that row is closed the
        // fight follows you around -- your damage still counts toward a boss you are
        // not looking at, and re-entering finds a fight already half spent.
        //
        // ══ WHY IT IS NOT resolve ═════════════════════════════════════════════
        //
        // resolve ends the ENCOUNTER, for everybody in it. One person walking out of a
        // four-person fight must not end the other three's, so this removes exactly
        // one participant and ends the fight only when it removed the last one.
        //
        // No loot, no experience, no verdict. Leaving is not losing, it is leaving --
        // and the enrage clock keeps running for whoever stayed.
        group.MapPost("/{characterId:guid}/flee", async Task<IResult> (HttpContext http, Caller caller,
                                                          Db db, IGameClock clock, Guid characterId) =>
        {
            Guid? accountId = await caller.AccountIdAsync(http.User, http.RequestAborted);
            if (accountId is null) return Results.Unauthorized();

            if (!await caller.OwnsCharacterAsync(accountId.Value, characterId, http.RequestAborted))
                return NotYours();

            DateTimeOffset now = await clock.NowAsync(http.RequestAborted);

            return await db.InCharacterTransactionAsync<IResult>(characterId, async (connection, tx) =>
            {
                Live? live = await ReadLiveAsync(connection, tx, characterId, now);

                // Not an error. A client that leaves the arena twice, or leaves an
                // arena it was never fighting in, has asked for a state that is
                // already true -- and a door that can fail is a door players get
                // trapped behind.
                if (live is null) return Results.Ok(new { left = false, ended = false });

                await connection.ExecuteAsync(
                    "delete from encounter_participant where encounter_id = $1 and character_id = $2;",
                    tx, live.Id, characterId);

                long remaining = await connection.ScalarAsync<long>(
                    "select count(*) from encounter_participant where encounter_id = $1;",
                    tx, live.Id);

                bool ended = remaining == 0L;

                if (ended)
                {
                    // The last one out closes the fight. Marked as a loss, because it
                    // was not a win -- and leaving it open would hold the party's
                    // partial unique index against the next attempt.
                    await connection.ExecuteAsync(
                        "update encounter set ended_at = $2, won = false where id = $1;",
                        tx, live.Id, now);
                }

                await TelemetryEndpoints.RecordAsync(
                    connection, tx, accountId.Value, characterId,
                    TelemetryEvents.BossEnded, now,
                    ("monster", live.MonsterId),
                    ("won",     "false"),
                    ("seconds", ((long)(now - live.StartedAt).TotalSeconds).ToString()),
                    ("damage",  live.MyDamage.ToString()),
                    ("left",    "true"));

                return Results.Ok(new { left = true, ended });
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


        // ── What the group is still rolling for ───────────────────────────────
        //
        // ══ WHY SETTLING HAPPENS ON A READ ════════════════════════════════════
        //
        // Somebody will close their browser with a roll open, and the crown must not
        // sit unclaimed for ever waiting on an answer that is never coming. The
        // obvious home for that is a background sweep, and there is no background
        // service in this API -- adding one to expire a loot roll would be a whole
        // new thing to run, watch and reason about for a job that has to happen
        // roughly once a minute.
        //
        // So an expired roll settles the next time anybody looks at it. In practice
        // that is within two seconds of expiring, because the group is polling this
        // endpoint while the window is open and stops only once it is empty. The
        // property that matters is that the OUTCOME does not depend on who looked
        // first: LootRoll.Decide reads only the answers, and an unanswered roll is a
        // pass.
        group.MapGet("/{characterId:guid}/rolls", async Task<IResult> (HttpContext http, Caller caller,
                                                          Db db, IGameClock clock, Guid characterId) =>
        {
            Guid? accountId = await caller.AccountIdAsync(http.User, http.RequestAborted);
            if (accountId is null) return Results.Unauthorized();

            if (!await caller.OwnsCharacterAsync(accountId.Value, characterId, http.RequestAborted))
                return NotYours();

            DateTimeOffset now = await clock.NowAsync(http.RequestAborted);

            return await db.InCharacterTransactionAsync<IResult>(characterId, async (connection, tx) =>
            {
                await SettleExpiredRollsAsync(connection, tx, characterId, now, http.RequestAborted);

                return Results.Ok(new { rolls = await OpenRollsAsync(connection, tx, characterId, now) });
            }, http.RequestAborted);
        });

        // ── Need, greed, or pass ──────────────────────────────────────────────
        //
        // The client sends a WORD. It does not send a number, because a die a client
        // rolls is a die a client chooses -- the roll is derived here from the fight's
        // own seed, which also means a contested drop can be re-derived from two
        // columns months later rather than taken on the server's word.
        group.MapPost("/{characterId:guid}/rolls/{rollId:guid}", async Task<IResult> (
                          HttpContext http, Caller caller, Db db, IGameClock clock,
                          Guid characterId, Guid rollId, [FromBody] RollChoice? request) =>
        {
            Guid? accountId = await caller.AccountIdAsync(http.User, http.RequestAborted);
            if (accountId is null) return Results.Unauthorized();

            if (!await caller.OwnsCharacterAsync(accountId.Value, characterId, http.RequestAborted))
                return NotYours();

            string choice = request?.Choice ?? LootRoll.Pass;

            if (!LootRoll.IsChoice(choice))
            {
                return Results.Problem(
                    title:      "no such choice",
                    detail:     $"'{choice}' is not need, greed or pass.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            DateTimeOffset now = await clock.NowAsync(http.RequestAborted);

            return await db.InCharacterTransactionAsync<IResult>(characterId, async (connection, tx) =>
            {
                Roll? roll = await ReadRollAsync(connection, tx, rollId);

                if (roll is null || roll.SettledAt is not null)
                {
                    // Already decided, or never existed. Not a refusal worth an error
                    // page: a player pressing "need" a moment after the timer ran out
                    // has done nothing wrong, and telling them so helps nobody.
                    return Results.Ok(new { answered = false, settled = true });
                }

                // ══ ONLY THE PEOPLE WHO FOUGHT IT ═════════════════════════════
                //
                // Checked here rather than implied by the client only showing the
                // dialog to fighters. Anybody with a roll id and an account could
                // otherwise put a hand up for a crown they were nowhere near.
                var fighters = await FightersAsync(connection, tx, roll.EncounterId);

                int index = fighters.IndexOf(characterId);

                if (index < 0)
                {
                    return Results.Problem(
                        title:      "not your fight",
                        detail:     "You did not fight this encounter.",
                        statusCode: StatusCodes.Status403Forbidden);
                }

                long seed = await connection.ScalarAsync<long>(
                    "select seed from encounter where id = $1;", tx, roll.EncounterId);

                int die = LootRoll.RollFor(seed, roll.RollIndex, index);

                // Insert, never update. Changing your mind after seeing what somebody
                // else rolled is the one thing a need/greed window must not allow, and
                // a primary key says so more reliably than a check would.
                long written = await connection.ScalarAsync<long>(
                    """
                    insert into loot_roll_choice (roll_id, character_id, choice, roll, answered_at)
                    values ($1, $2, $3, $4, $5)
                    on conflict (roll_id, character_id) do nothing
                    returning 1;
                    """,
                    tx, rollId, characterId, choice, die, now);

                bool settled = await TrySettleAsync(connection, tx, roll, fighters, now,
                                                    http.RequestAborted);

                return Results.Ok(new { answered = written > 0L, roll = die, settled });
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


    // ── Rolling for what the King dropped ─────────────────────────────────────

    private sealed record Roll(Guid Id, Guid EncounterId, string ItemId, long Quantity,
                               int RollIndex, DateTimeOffset OfferedAt, DateTimeOffset? SettledAt);

    private static async Task<Roll?> ReadRollAsync(NpgsqlConnection connection, NpgsqlTransaction tx,
                                                   Guid rollId)
    {
        await using var command = connection.Sql(
            """
            select id, encounter_id, item_id, quantity, roll_index, offered_at, settled_at
            from loot_roll where id = $1;
            """,
            tx, rollId);

        await using var reader = await command.ExecuteReaderAsync();

        if (!await reader.ReadAsync()) return null;

        return new Roll(
            reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetInt64(3),
            reader.GetInt32(4), reader.GetFieldValue<DateTimeOffset>(5),
            reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6));
    }

    /// <summary>
    /// Everybody who fought an encounter, in the order they joined it.
    ///
    /// The ORDER is load-bearing twice over: it is the index the dice are derived
    /// from, and it is the tie-break of last resort when two people need the same
    /// item and roll the same number. Both need it to be stable rather than fair,
    /// which "when did you get here" is.
    /// </summary>
    private static async Task<List<Guid>> FightersAsync(NpgsqlConnection connection,
                                                        NpgsqlTransaction tx, Guid encounterId)
    {
        var fighters = new List<Guid>();

        await using var command = connection.Sql(
            "select character_id from encounter_participant where encounter_id = $1 order by joined_at, character_id;",
            tx, encounterId);

        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync()) fighters.Add(reader.GetGuid(0));

        return fighters;
    }

    /// <summary>
    /// Every roll this character can still answer, with what they have already said.
    ///
    /// Their own answer is included and everybody else's is not, deliberately: seeing
    /// that two people have already pressed need would change what the third presses,
    /// and a need/greed window whose result depends on how long you waited before
    /// answering is a race rather than a roll.
    /// </summary>
    private static async Task<object[]> OpenRollsAsync(NpgsqlConnection connection, NpgsqlTransaction tx,
                                                       Guid characterId, DateTimeOffset now)
    {
        var rolls = new List<object>();

        await using var command = connection.Sql(
            """
            select r.id, r.item_id, r.quantity, r.offered_at, c.choice, c.roll
            from loot_roll r
            join encounter_participant p
              on p.encounter_id = r.encounter_id and p.character_id = $1
            left join loot_roll_choice c
              on c.roll_id = r.id and c.character_id = $1
            where r.settled_at is null
            order by r.offered_at, r.roll_index;
            """,
            tx, characterId);

        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            var offeredAt = reader.GetFieldValue<DateTimeOffset>(3);

            rolls.Add(new
            {
                rollId    = reader.GetGuid(0),
                itemId    = reader.GetString(1),
                quantity  = reader.GetInt64(2),
                secondsLeft = LootRoll.Remaining((now - offeredAt).TotalSeconds),

                myChoice  = reader.IsDBNull(4) ? "" : reader.GetString(4),
                myRoll    = reader.IsDBNull(5) ? 0  : reader.GetInt32(5),
            });
        }

        return rolls.ToArray();
    }

    /// <summary>
    /// Decides a roll if it can be decided, and pays out if it can.
    ///
    /// ══ WHY THE UPDATE IS THE LOCK ════════════════════════════════════════════
    ///
    /// Four people can answer the same roll in the same instant, and each of them is
    /// holding a lock on their OWN character row -- which is exactly the wrong row to
    /// be holding for a decision about a shared crown. Without something else, all
    /// four would find the roll answerable and all four would grant the winner a copy.
    ///
    /// So the settle is a conditional update on the roll itself: `where settled_at is
    /// null`. The first transaction to reach it flips the row and grants; every other
    /// one blocks on that row until the first commits, then updates nothing and grants
    /// nothing. The row IS the mutual exclusion, and it does not depend on anybody
    /// remembering to take a lock.
    /// </summary>
    private static async Task<bool> TrySettleAsync(NpgsqlConnection connection, NpgsqlTransaction tx,
                                                   Roll roll, List<Guid> fighters, DateTimeOffset now,
                                                   CancellationToken cancellation)
    {
        var entries = new List<LootRoll.Entry>();

        await using (var command = connection.Sql(
            "select character_id, choice, roll from loot_roll_choice where roll_id = $1;",
            tx, roll.Id))
        {
            await using var reader = await command.ExecuteReaderAsync(cancellation);

            while (await reader.ReadAsync(cancellation))
            {
                Guid who = reader.GetGuid(0);

                entries.Add(new LootRoll.Entry(who.ToString(), reader.GetString(1), reader.GetInt32(2),
                                               Math.Max(0, fighters.IndexOf(who))));
            }
        }

        if (!LootRoll.CanSettle(entries.Count, fighters.Count, (now - roll.OfferedAt).TotalSeconds))
            return false;

        string winner = LootRoll.Decide(entries);

        Guid? winnerId = Guid.TryParse(winner, out Guid parsed) ? parsed : null;

        long flipped = await connection.ScalarAsync<long>(
            """
            update loot_roll set settled_at = $2, winner_id = $3
             where id = $1 and settled_at is null
            returning 1;
            """,
            tx, roll.Id, now, winnerId);

        // Somebody else settled it first. Theirs is the payout; this one adds nothing.
        if (flipped <= 0L) return true;

        if (winnerId is not null)
        {
            // Into pending_loot rather than into the bag, for the same reason every
            // other boss drop goes there: a full bag must delay a crown, never eat it.
            await connection.ExecuteAsync(
                """
                insert into pending_loot (character_id, encounter_id, item_id, quantity)
                values ($1, $2, $3, $4);
                """,
                tx, winnerId.Value, roll.EncounterId, roll.ItemId, roll.Quantity);
        }

        return true;
    }

    /// <summary>
    /// Settles anything this character can see that has run out of time.
    ///
    /// Everybody passing is a real outcome and is recorded as one -- settled, with no
    /// winner -- rather than left open for ever. An item nobody wanted is destroyed,
    /// which is the honest reading of four people pressing pass.
    /// </summary>
    private static async Task SettleExpiredRollsAsync(NpgsqlConnection connection, NpgsqlTransaction tx,
                                                      Guid characterId, DateTimeOffset now,
                                                      CancellationToken cancellation)
    {
        var expired = new List<Roll>();

        await using (var command = connection.Sql(
            """
            select r.id, r.encounter_id, r.item_id, r.quantity, r.roll_index, r.offered_at
            from loot_roll r
            join encounter_participant p
              on p.encounter_id = r.encounter_id and p.character_id = $1
            where r.settled_at is null and r.offered_at <= $2;
            """,
            tx, characterId, now.AddSeconds(-LootRoll.DecideSeconds)))
        {
            await using var reader = await command.ExecuteReaderAsync(cancellation);

            while (await reader.ReadAsync(cancellation))
            {
                expired.Add(new Roll(
                    reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetInt64(3),
                    reader.GetInt32(4), reader.GetFieldValue<DateTimeOffset>(5), null));
            }
        }

        foreach (Roll roll in expired)
        {
            await TrySettleAsync(connection, tx, roll,
                                 await FightersAsync(connection, tx, roll.EncounterId),
                                 now, cancellation);
        }
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

    /// <summary>
    /// One fight, as one of its fighters sees it.
    ///
    /// ══ TWO HALVES, DELIBERATELY NAMED APART ══════════════════════════════
    ///
    /// BossMaxHp, DamageDealt, Seed and the deadlines belong to the ENCOUNTER and are
    /// the same for everybody in it. FrozenDps, FrozenAttackSeconds, LastSequence,
    /// AbilityUsed, MyDamage and JoinedAt belong to THIS FIGHTER and nobody else.
    ///
    /// Keeping the distinction visible is what stops the damage ceiling being applied
    /// to the shared pool -- which would let four people pool their allowances and one
    /// of them spend the lot.
    /// </summary>
    private sealed record Live(Guid Id, string MonsterId, double FrozenDps, double FrozenAttackSeconds,
                               long Seed, long BossMaxHp, long DamageDealt, long LastSequence,
                               string AbilityUsed, DateTimeOffset StartedAt, DateTimeOffset EnrageAt,
                               long MyDamage, DateTimeOffset JoinedAt, Guid? PartyId);

    private static async Task<Live?> ReadLiveAsync(NpgsqlConnection connection, NpgsqlTransaction tx,
                                                   Guid characterId, DateTimeOffset now)
    {
        // Joined on the PARTICIPANT, not on encounter.character_id. A fight is found
        // by whether this character is fighting it, which is the same question for the
        // person who engaged and for everybody who walked in afterwards.
        await using var command = connection.Sql(
            """
            select e.id, e.monster_id, p.frozen_dps, p.frozen_attack_seconds, e.seed,
                   e.boss_max_hp, e.damage_dealt, p.last_sequence, p.ability_used::text,
                   e.started_at, e.enrage_at, p.damage_dealt, p.joined_at, e.party_id
            from encounter_participant p
            join encounter e on e.id = p.encounter_id
            where p.character_id = $1 and e.ended_at is null;
            """,
            tx, characterId);

        await using var reader = await command.ExecuteReaderAsync();

        if (!await reader.ReadAsync()) return null;

        return new Live(
            reader.GetGuid(0), reader.GetString(1), reader.GetDouble(2), reader.GetDouble(3),
            reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7),
            reader.GetString(8),
            reader.GetFieldValue<DateTimeOffset>(9), reader.GetFieldValue<DateTimeOffset>(10),
            reader.GetInt64(11), reader.GetFieldValue<DateTimeOffset>(12),
            reader.IsDBNull(13) ? (Guid?)null : reader.GetGuid(13));
    }


    /// <summary>
    /// The fight as it stands, plus this fighter's own frozen snapshot.
    ///
    /// ══ WHY THIS EXISTS RATHER THAN REUSING WHAT ENGAGE COMPUTED ══════════════
    ///
    /// Because what engage computed is not necessarily what is stored. Three callers
    /// reach the same describe: somebody starting a fight, somebody joining a party's
    /// fight, and somebody walking back into their own. Only the first of those has a
    /// seed, a clock and a snapshot that are all its own; the other two must be told
    /// about the fight that is ACTUALLY running, or they telegraph a different one.
    ///
    /// So the describe reads from the rows, always, and the difference between the
    /// three callers stops existing at this line.
    /// </summary>
    private sealed record Joined(long Seed, long DamageDealt, DateTimeOffset StartedAt,
                                 DateTimeOffset EnrageAt, double FrozenDps, double FrozenAttackSeconds);

    private static async Task<Joined> ReadJoinedAsync(NpgsqlConnection connection, NpgsqlTransaction tx,
                                                      Guid encounterId, Guid characterId)
    {
        await using var command = connection.Sql(
            """
            select e.seed, e.damage_dealt, e.started_at, e.enrage_at,
                   p.frozen_dps, p.frozen_attack_seconds
            from encounter e
            join encounter_participant p
              on p.encounter_id = e.id and p.character_id = $2
            where e.id = $1;
            """,
            tx, encounterId, characterId);

        await using var reader = await command.ExecuteReaderAsync();

        if (!await reader.ReadAsync())
        {
            // Cannot happen: the caller has just inserted or found both rows inside
            // this transaction. Thrown rather than defaulted, because a describe built
            // from invented numbers is a client predicting a fight that is not there --
            // and that failure would be silent, which is worse than a 500.
            throw new InvalidOperationException(
                $"Encounter {encounterId} has no participant row for {characterId}.");
        }

        return new Joined(
            reader.GetInt64(0), reader.GetInt64(1),
            reader.GetFieldValue<DateTimeOffset>(2), reader.GetFieldValue<DateTimeOffset>(3),
            reader.GetDouble(4), reader.GetDouble(5));
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
            where ended_at is null and enrage_at <= $2
              and id in (select encounter_id from encounter_participant where character_id = $1);
            """,
            tx, characterId, now);
    }

    /// <summary>
    /// The PLAYER's ability, from the ability catalogue.
    ///
    /// ══ THE BUG THIS SHAPE FIXES ══════════════════════════════════════════════
    ///
    /// The first version looked the id up in the BOSS's phase abilities -- cleave_arc,
    /// king_charge, throne_quake. Those are what the King does to the player. An
    /// action report carries what the PLAYER did, so every real ability came back null
    /// and was rejected as unknown, and the only thing that worked was an ordinary
    /// swing.
    ///
    /// It passed its test, because the test used a made-up id and got the refusal it
    /// asked for. A refusal for the wrong reason is the hardest kind of green.
    ///
    /// Null means "no such ability", which is still a refusal -- a client naming an
    /// ability that does not exist is either stale or probing.
    /// </summary>
    private static AbilityData? AbilityFor(GameContent catalogue, string? abilityId) =>
        string.IsNullOrEmpty(abilityId) ? null : catalogue.GetAbility(abilityId);

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

    private static object Describe(Guid id, MonsterData boss, double frozenDps,
                                   double frozenAttackSeconds, long seed, double enrageSeconds,
                                   long damageDealt, double elapsed)
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
            frozenDps,
            frozenAttackSeconds,

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

    /// <summary>
    /// A feature that is deliberately off.
    ///
    /// 503 rather than 403, because "not right now" is the truth: nothing is wrong
    /// with this player or this request, and a client that treats it as permanent
    /// would need a restart to notice the flag coming back.
    /// </summary>
    private static IResult SwitchedOff(string what) =>
        Results.Problem(
            title:      "temporarily unavailable",
            detail:     $"{what} is switched off at the moment. Try again shortly.",
            statusCode: StatusCodes.Status503ServiceUnavailable);

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

    /// <summary>Need, greed or pass. A word, never a number -- see the endpoint.</summary>
    public sealed record RollChoice(string? Choice);

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
