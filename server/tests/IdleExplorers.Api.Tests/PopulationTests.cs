#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace IdleExplorers.Api.Tests;

/// <summary>
/// One population per map, not one per player.
///
/// ══ WHAT WAS WRONG ════════════════════════════════════════════════════════════
///
/// Every client spawned its own goblins. Two players standing in the same clearing
/// fought two different sets occupying the same ground, and neither could see the
/// other's. The rewards were never affected — settlement pays from time against
/// server-owned rates — so what was broken was that the world was private, in a game
/// whose whole subject is a shared one.
///
/// ══ HOW THESE TESTS AVOID THE ISOLATION TRAP ══════════════════════════════════
///
/// The population belongs to a REAL map id, because the server reads the map's
/// monster out of content — so every test in this file shares goblin_camp with every
/// other test in the suite, and any assertion that counted rows would be counting
/// somebody else's monsters.
///
/// So nothing here counts. Every assertion is about a SPECIFIC monster id that the
/// test itself observed or struck. That is isolation by construction, and it is the
/// rule three presence tests and an encounter test had to learn the hard way.
/// </summary>
[Collection("api")]
public class PopulationTests(ApiFixture api)
{
    private static void RequireDatabase() => Skip.IfNot(ApiFixture.DatabaseReachable, ApiFixture.SkipReason);

    private const string Camp   = "goblin_camp";
    private const string Throne = "goblin_throne";

    /// <summary>
    /// A map has monsters standing in it, reported by the poll that is already
    /// happening.
    /// </summary>
    [SkippableFact]
    public async Task AMapHasAPopulation()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await OwnershipTests.CreateCharacter(player, "Watcher");

        var monsters = await Poll(player, character, Camp);

        Assert.NotEmpty(monsters);
        Assert.All(monsters, m => Assert.Equal("goblin", m.MonsterId));
        Assert.All(monsters, m => Assert.True(m.MaxHealth > 0d));
    }

    /// <summary>
    /// Two players see the SAME monsters. This is the entire point of the change.
    ///
    /// Compared by id, not by count: the population is shared with the rest of the
    /// suite and the number in it moves while this runs. What must be true is that
    /// whatever one player can see, the other can see too.
    /// </summary>
    [SkippableFact]
    public async Task TwoPlayersSeeTheSameMonsters()
    {
        RequireDatabase();

        await using var one = await api.NewPlayerAsync();
        await using var two = await api.NewPlayerAsync();

        Guid first  = await OwnershipTests.CreateCharacter(one, "Alik");
        Guid second = await OwnershipTests.CreateCharacter(two, "Berin");

        var mine   = await Poll(one, first,  Camp);
        var theirs = await Poll(two, second, Camp);

        Assert.NotEmpty(mine);

        var seen = theirs.Select(m => m.Id).ToHashSet();

        // Everything the first player saw is in the second player's answer. The
        // reverse need not hold: the suite is spawning and killing monsters
        // concurrently, so the second poll may legitimately see MORE.
        Assert.All(mine, m => Assert.Contains(m.Id, seen));

        // And in the same place, or they are not the same world.
        var byId = theirs.ToDictionary(m => m.Id);

        foreach (var m in mine)
        {
            Assert.Equal(m.X, byId[m.Id].X, 3);
            Assert.Equal(m.Z, byId[m.Id].Z, 3);
        }
    }

    /// <summary>
    /// Hitting one takes its health down, and the OTHER player sees that.
    ///
    /// The observable outcome, not the row: a shared population whose damage is only
    /// visible to the attacker is the private spawner again with extra steps.
    /// </summary>
    [SkippableFact]
    public async Task DamageIsVisibleToEverybody()
    {
        RequireDatabase();

        await using var one = await api.NewPlayerAsync();
        await using var two = await api.NewPlayerAsync();

        Guid attacker = await OwnershipTests.CreateCharacter(one, "Hitter");
        Guid onlooker = await OwnershipTests.CreateCharacter(two, "Looker");

        Monster target = (await Poll(one, attacker, Camp)).First(m => m.Health > 0d);

        double before = target.Health;

        await Strike(one, attacker, target.Id, damage: 1d, seconds: 5d);

        Monster after = (await Poll(two, onlooker, Camp)).Single(m => m.Id == target.Id);

        Assert.True(after.Health < before,
                    $"the onlooker should see the damage; {before} -> {after.Health}");
    }

    /// <summary>
    /// A client cannot delete the map in a frame.
    ///
    /// ══ WHY THIS CAP EXISTS AT ALL ════════════════════════════════════════════
    ///
    /// Not to protect the economy — a fast kill earns exactly what a slow one does,
    /// because settlement pays for time. It is to stop one client emptying everybody
    /// else's screen, which is griefing rather than cheating.
    /// </summary>
    [SkippableFact]
    public async Task AbsurdDamageIsCapped()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await OwnershipTests.CreateCharacter(player, "Liar");

        Monster target = (await Poll(player, character, Camp)).First(m => m.Health > 0d);

        JsonElement hit = await Strike(player, character, target.Id,
                                       damage: 1_000_000_000d, seconds: 0.1d);

        Assert.True(hit.GetProperty("alive").GetBoolean(),
                    "a billion damage in a tenth of a second should not kill anything");
        Assert.True(hit.GetProperty("health").GetDouble() > 0d);
    }

    /// <summary>
    /// And an honest hit still lands.
    ///
    /// The paired acceptance. A cap that refused everything would satisfy the test
    /// above perfectly and leave every monster in the game invulnerable — which is
    /// the same shape as an API that rejected every real token and passed every
    /// authentication test.
    /// </summary>
    [SkippableFact]
    public async Task AnHonestHitLands()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await OwnershipTests.CreateCharacter(player, "Honest");

        Monster target = (await Poll(player, character, Camp)).First(m => m.Health > 0d);

        JsonElement hit = await Strike(player, character, target.Id, damage: 1d, seconds: 5d);

        Assert.True(hit.GetProperty("hit").GetBoolean());
        Assert.True(hit.GetProperty("health").GetDouble() < target.Health);
    }

    /// <summary>
    /// Killing one leaves a corpse, and the corpse comes back.
    ///
    /// The revive is triggered by moving died_at into the past rather than by waiting
    /// twenty-five seconds. The server's condition is expressed in SQL against its own
    /// now(), so this exercises the real one.
    /// </summary>
    [SkippableFact]
    public async Task AKilledMonsterComesBack()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await OwnershipTests.CreateCharacter(player, "Reaper");

        Monster target = (await Poll(player, character, Camp)).First(m => m.Health > 0d);

        await KillWithStrikes(player, character, target.Id);

        // Still reported, as a corpse, so the client fades it rather than blinking it.
        Monster corpse = (await Poll(player, character, Camp)).Single(m => m.Id == target.Id);

        Assert.Equal(0d, corpse.Health);
        Assert.True(corpse.SecondsDead >= 0d);

        await AgeTheCorpse(target.Id);

        Monster back = (await Poll(player, character, Camp)).Single(m => m.Id == target.Id);

        Assert.True(back.Health > 0d, "a monster dead long enough should be back on its feet");
        Assert.Equal(0d, back.SecondsDead);
    }

    /// <summary>
    /// Striking something already dead is not an error.
    ///
    /// It is the NORMAL outcome of two people fighting the same goblin: one of them
    /// lands the last hit and the other's swing arrives a moment later. Answering with
    /// a failure would make a shared world look broken every time it worked.
    /// </summary>
    [SkippableFact]
    public async Task StrikingACorpseIsNotAnError()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await OwnershipTests.CreateCharacter(player, "Late");

        Monster target = (await Poll(player, character, Camp)).First(m => m.Health > 0d);

        await KillWithStrikes(player, character, target.Id);

        JsonElement hit = await Strike(player, character, target.Id, damage: 1d, seconds: 5d);

        Assert.False(hit.GetProperty("hit").GetBoolean());
        Assert.False(hit.GetProperty("alive").GetBoolean());
    }

    /// <summary>
    /// A boss map gets no population.
    ///
    /// The throne's defaultMonsterId is goblin_king because that is what a player
    /// fights there, and a spawner reading it as an instruction is exactly how the
    /// arena filled with Kings. The client guard was the first fix; this is the same
    /// rule on the side that now owns the spawning.
    /// </summary>
    [SkippableFact]
    public async Task ABossMapIsNotPopulated()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await OwnershipTests.CreateCharacter(player, "Throneward");

        Assert.Empty(await Poll(player, character, Throne));
    }

    // ── Machinery ─────────────────────────────────────────────────────────────

    private readonly record struct Monster(
        Guid Id, string MonsterId, float X, float Z,
        double Health, double MaxHealth, double SecondsDead);

    /// <summary>One presence poll, returning just the monsters in the answer.</summary>
    private static async Task<List<Monster>> Poll(Player player, Guid character, string mapId)
    {
        var response = await OwnershipTests.Post(player, $"/presence/{character}",
                                                 new { mapId, x = 0f, z = 0f });

        response.EnsureSuccessStatusCode();

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var monsters = new List<Monster>();

        foreach (JsonElement m in body.RootElement.GetProperty("monsters").EnumerateArray())
        {
            monsters.Add(new Monster(
                m.GetProperty("id").GetGuid(),
                m.GetProperty("monsterId").GetString() ?? "",
                m.GetProperty("x").GetSingle(),
                m.GetProperty("z").GetSingle(),
                m.GetProperty("health").GetDouble(),
                m.GetProperty("maxHealth").GetDouble(),
                m.GetProperty("secondsDead").GetDouble()));
        }

        return monsters;
    }

    private static async Task<JsonElement> Strike(Player player, Guid character,
                                                  Guid monsterId, double damage, double seconds)
    {
        var response = await OwnershipTests.Post(player, $"/world/{character}/strike",
                                                 new { monsterId, damage, seconds });

        response.EnsureSuccessStatusCode();

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    /// <summary>
    /// Kills one by hitting it until it falls.
    ///
    /// ══ WHY NOT AN UPDATE STATEMENT ═══════════════════════════════════════════
    ///
    /// The first version wrote `died_at = now()` straight into the row, and every
    /// corpse revived instantly. The reason is worth writing down: the API runs on the
    /// INJECTED clock, which the fixture starts a minute ahead of the database and
    /// other tests advance further. So a death stamped with the database's now() is
    /// already minutes old by the server's reckoning, and the revive fires on the very
    /// next poll.
    ///
    /// Killing it the way the game does keeps both halves on one clock — which is also
    /// the property production depends on, since `died_at` and the respawn comparison
    /// must agree about what time it is.
    /// </summary>
    private static async Task KillWithStrikes(Player player, Guid character, Guid monsterId)
    {
        for (int swing = 0; swing < 40; swing++)
        {
            JsonElement hit = await Strike(player, character, monsterId,
                                           damage: 1_000d, seconds: 5d);

            if (!hit.GetProperty("alive").GetBoolean()) return;
        }

        Assert.Fail($"forty capped strikes did not kill monster {monsterId}");
    }

    /// <summary>
    /// Moves a corpse's death far enough into the past that it is due back on ANY
    /// clock — the database's, the fixture's, or one some other test has advanced.
    /// </summary>
    private async Task AgeTheCorpse(Guid monsterId)
    {
        await using var db = await api.OpenDatabaseAsync();
        await using var command = db.CreateCommand();

        command.CommandText =
            "update map_monster set died_at = timestamptz '2000-01-01 00:00:00+00' where id = $1;";
        command.Parameters.AddWithValue(monsterId);

        await command.ExecuteNonQueryAsync();
    }
}
