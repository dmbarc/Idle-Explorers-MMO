using IdleExplorers.Api.Infrastructure;
using IdleExplorers.Rules;
using Npgsql;

namespace IdleExplorers.Api.Services;

/// <summary>
/// The monsters standing in a map, kept topped up.
///
/// ══ WHAT THIS REPLACES ════════════════════════════════════════════════════════
///
/// Every client spawning its own. MonsterSpawner placed a ring of goblins around the
/// player, gave them health and let them die -- all locally. Two players in the same
/// clearing fought two different sets of goblins occupying the same ground, and
/// neither could see the other's.
///
/// ══ WHAT IT DELIBERATELY DOES NOT DO ══════════════════════════════════════════
///
/// Pay anybody. Loot and experience come from settlement, which integrates each
/// player's own time against server-owned rates -- so two people fighting the same
/// goblin are both paid for the time they spent, and neither is paid twice.
///
/// That is why a shared population is a smaller change than it looks: it decides what
/// is SEEN, not what is earned. A client that lies about damage makes a goblin fall
/// over slightly early on everybody's screen and gains nothing.
///
/// ══ WHY IT IS LAZY ════════════════════════════════════════════════════════════
///
/// Topped up on the presence poll that already happens every two seconds for every
/// player in the map. A map nobody is standing in does not need a population, and a
/// background timer that maintained one would be a process to run, watch and pay for
/// in exchange for nothing anybody can see.
/// </summary>
public sealed class PopulationService(ContentCache content)
{
    /// <summary>One monster, as the map holds it.</summary>
    public readonly record struct Live(
        Guid Id, string MonsterId, float X, float Z,
        double Health, double MaxHealth, double SecondsDead);

    /// <summary>
    /// Brings a map's population up to strength and returns it.
    ///
    /// Revives what has lain dead long enough, spawns up to the ceiling, and reports
    /// everything -- corpses included, so the client can fade one rather than blink
    /// it out of existence.
    ///
    /// ══ WHY ONE STATEMENT PER STEP AND NO LOCK ════════════════════════════════
    ///
    /// Several players poll the same map at once, and all of them run this. The steps
    /// are written so that racing them is harmless rather than prevented: the revive
    /// is conditional on still being dead, and the top-up inserts only the shortfall
    /// it measured. Two clients racing can overshoot by a monster or two, which the
    /// next poll's ceiling absorbs.
    ///
    /// A lock would serialise every player in a map behind one another, twice a
    /// second, to prevent a cosmetic overshoot.
    /// </summary>
    public async Task<List<Live>> RefreshAsync(NpgsqlConnection connection, string mapId,
                                               DateTimeOffset now, CancellationToken cancellation)
    {
        MapData? map = content.Catalogue.GetMap(mapId);

        // A map with no monster authored has no population, and that is not an error:
        // the Goblin Throne is one, and so is anywhere the player is only passing
        // through.
        if (map is null || string.IsNullOrEmpty(map.defaultMonsterId)) return [];

        MonsterData? monster = content.Catalogue.GetMonster(map.defaultMonsterId);

        if (monster is null) return [];

        // ══ A BOSS IS NEVER A POPULATION ══════════════════════════════════════
        //
        // The throne's defaultMonsterId is goblin_king because that is what a player
        // fights there, and a spawner reading it as an instruction is exactly how the
        // arena filled with Kings. The client-side guard is still there; this is the
        // same rule on the side that now owns the spawning.
        if (monster.isBoss) return [];

        await ReviveAsync(connection, mapId, now, cancellation);
        await TopUpAsync(connection, mapId, monster, now, cancellation);
        await PruneAsync(connection, mapId, cancellation);

        return await ReadAsync(connection, mapId, now, cancellation);
    }

    /// <summary>
    /// Brings back anything that has been dead long enough.
    ///
    /// Conditional on still being dead, so two clients racing this produce one revive
    /// rather than two. Health is restored from the row's own max rather than from
    /// content, so a monster whose stats were rebalanced mid-life comes back as the
    /// thing that died.
    ///
    /// ══ AND spawned_at IS DELIBERATELY NOT TOUCHED ════════════════════════════
    ///
    /// It was, and that made a revived monster the YOUNGEST row in the map -- which is
    /// exactly what PruneAsync deletes first. So a monster that had lived in the world
    /// since the server started could be killed, revived, and then culled as though it
    /// were overshoot, while genuinely new rows survived.
    ///
    /// spawned_at means "when this monster first appeared", and coming back from the
    /// dead is not appearing. Leaving it alone also makes prune's ordering mean what it
    /// says: the newest ARRIVALS are the excess.
    /// </summary>
    private static async Task ReviveAsync(NpgsqlConnection connection, string mapId,
                                          DateTimeOffset now, CancellationToken cancellation)
    {
        await connection.ExecuteAsync(
            """
            update map_monster
               set health        = max_health,
                   died_at       = null,
                   tagged_by     = null,
                   tagged_damage = 0
             where map_id = $1
               and died_at is not null
               and $3 - died_at >= make_interval(secs => $2);
            """,
            null, mapId, Population.RespawnSeconds, now);
    }

    /// <summary>
    /// Spawns the shortfall.
    ///
    /// Positions are chosen HERE rather than by the client, because every client has
    /// to draw the same goblin in the same place. They are rough on purpose: the
    /// client drops the point onto its own NavMesh, which is the only thing that
    /// knows where the walls are.
    /// </summary>
    private static async Task TopUpAsync(NpgsqlConnection connection, string mapId,
                                         MonsterData monster, DateTimeOffset now,
                                         CancellationToken cancellation)
    {
        // ══ CORPSES COUNT ═════════════════════════════════════════════════════
        //
        // This counted only the LIVING, so every death immediately spawned a
        // replacement -- and then the corpse got up twenty-five seconds later and the
        // map was one over, which the prune then had to cull. A map being farmed sat
        // in a permanent cycle of spawning and deleting monsters nobody had seen.
        //
        // A corpse IS one of the map's monsters. It is lying down, and it will get up
        // where it fell. Counting it keeps the population at a steady fourteen rows
        // instead of fourteen-alive-plus-however-many-are-dead.
        long held = await connection.ScalarAsync<long>(
            "select count(*)::bigint from map_monster where map_id = $1;",
            null, mapId);

        int wanted = Population.Clamp(Population.PerMap);
        int short_ = wanted - (int)held;

        if (short_ <= 0) return;

        // Seeded from the map and the minute, so two servers -- or one server twice --
        // place a given batch identically, and a map's population does not jitter
        // around the field every time somebody polls.
        var rng = new CounterRandom(HashCode.Combine(mapId, now.UtcTicks / TimeSpan.TicksPerMinute));

        double maxHealth = Math.Max(1d, monster.maxHp);

        for (int i = 0; i < short_; i++)
        {
            float x = rng.Range(-Population.SpawnExtent, Population.SpawnExtent);
            float z = rng.Range(-Population.SpawnExtent, Population.SpawnExtent);

            await connection.ExecuteAsync(
                """
                insert into map_monster (map_id, monster_id, x, z, health, max_health, spawned_at)
                values ($1, $2, $3, $4, $5, $5, $6);
                """,
                null, mapId, monster.id, x, z, maxHealth, now);
        }
    }

    /// <summary>
    /// Removes any overshoot.
    ///
    /// ══ WHY AN OVERSHOOT IS POSSIBLE AT ALL ═══════════════════════════════════
    ///
    /// TopUp measures the shortfall and inserts it, without a lock, because locking
    /// would serialise every player in a map behind one another twice a second to
    /// prevent something cosmetic. Two clients racing can therefore each insert the
    /// same shortfall, and the map ends up with more monsters than it should.
    ///
    /// Left alone that is permanent: nothing else ever removes a live monster, so a
    /// map that overshot once stays overshot until somebody kills the extras. Worse,
    /// the read is capped at MaxPerMap, so past that point the newest monsters are
    /// simply invisible — present in the table, absent from every screen, and
    /// unkillable because no client knows their ids.
    ///
    /// So the race stays unlocked and the damage it does is undone here, on the next
    /// poll, by deleting the youngest of the excess. Deleting rather than killing:
    /// a monster nobody has ever seen has no corpse worth drawing.
    /// </summary>
    private static async Task PruneAsync(NpgsqlConnection connection, string mapId,
                                         CancellationToken cancellation)
    {
        await connection.ExecuteAsync(
            """
            delete from map_monster
             where id in (
                   select id from map_monster
                    where map_id = $1
                    -- id as a tiebreak, because a fixed clock -- the test harness has
                    -- one -- gives every row in a batch the same spawned_at, and an
                    -- unstable ordering makes the prune delete an arbitrary monster
                    -- rather than the newest one.
                    order by spawned_at desc, id desc
                   offset $2);
            """,
            null, mapId, Population.Clamp(Population.PerMap));
    }

    /// <summary>
    /// The map's population as the client should draw it.
    ///
    /// Corpses are included, with how long they have been one, so the client fades
    /// rather than blinks. Anything dead longer than the respawn window is left out --
    /// it is about to be revived by the next poll and drawing it would be drawing a
    /// monster that is neither alive nor going.
    /// </summary>
    private static async Task<List<Live>> ReadAsync(NpgsqlConnection connection, string mapId,
                                                    DateTimeOffset now, CancellationToken cancellation)
    {
        var live = new List<Live>();

        await using var command = connection.Sql(
            """
            select id, monster_id, x, z, health, max_health,
                   coalesce(extract(epoch from ($2 - died_at)), 0)
              from map_monster
             where map_id = $1
               and (died_at is null or $2 - died_at < make_interval(secs => $3))
             order by spawned_at
             limit $4;
            """,
            null, mapId, now, Population.RespawnSeconds, Population.MaxPerMap);

        await using var reader = await command.ExecuteReaderAsync(cancellation);

        while (await reader.ReadAsync(cancellation))
        {
            live.Add(new Live(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetFloat(2),
                reader.GetFloat(3),
                reader.GetDouble(4),
                reader.GetDouble(5),
                (double)reader.GetDecimal(6)));
        }

        return live;
    }

    /// <summary>
    /// Applies reported damage to one monster, capped.
    ///
    /// ══ WHY THE REPORT IS ACCEPTED AT ALL ═════════════════════════════════════
    ///
    /// Because the alternative is a server that simulates every swing of every player
    /// in every map, for a population whose only job is to be looked at. What a kill
    /// PAYS comes from settlement and is not affected by anything here.
    ///
    /// So the cap is about keeping the world coherent rather than about protecting the
    /// economy: it stops one client deleting a map's population in a frame, and it is
    /// deliberately generous — dps × elapsed with three seconds of slack, the boss's
    /// own ceiling.
    ///
    /// Returns the health that remains, or null when there was no such live monster.
    /// </summary>
    public static async Task<double?> StrikeAsync(NpgsqlConnection connection, NpgsqlTransaction? tx,
                                                  Guid monsterId, Guid characterId,
                                                  double damage, double ceiling,
                                                  DateTimeOffset now, CancellationToken cancellation)
    {
        double applied = Math.Max(0d, Math.Min(damage, ceiling));

        if (applied <= 0d) return null;

        // ══ ONE STATEMENT ═════════════════════════════════════════════════════
        //
        // The read, the subtraction, the death and the tag in a single update, so two
        // players hitting the same goblin in the same instant cannot both read "10
        // health left" and each decide they killed it. Postgres serialises the row.
        //
        // The tag goes to whoever has done the most damage, which is decided by the
        // same statement that adds it -- greatest() rather than a compare-then-write.
        await using var command = connection.Sql(
            """
            update map_monster
               set health = greatest(0, health - $2),
                   died_at = case when health - $2 <= 0 then $4 else died_at end,
                   tagged_by = case when $2 > tagged_damage then $3 else tagged_by end,
                   tagged_damage = greatest(tagged_damage, $2)
             where id = $1 and died_at is null
            returning health;
            """,
            tx, monsterId, applied, characterId, now);

        await using var reader = await command.ExecuteReaderAsync(cancellation);

        return await reader.ReadAsync(cancellation) ? reader.GetDouble(0) : null;
    }
}
