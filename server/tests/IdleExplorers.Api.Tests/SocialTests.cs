#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace IdleExplorers.Api.Tests;

/// <summary>
/// Seeing other people, and standing with them.
///
/// ══ WHAT IS ACTUALLY BEING PROTECTED ══════════════════════════════════════════
///
/// Not the coordinates. Position is presentation here — nothing rewards where a
/// character stands — so a client that lies about it gains nothing and these tests do
/// not pretend otherwise.
///
/// What matters is everything the reporter does NOT get to decide: the name and level
/// attached to them in somebody else's world, whether they can see a map they are not
/// on, and whether a fifth player can take the fourth seat. The last one is a race,
/// and a race that is only usually lost is a bug that only usually happens.
/// </summary>
[Collection("api")]
public class SocialTests(ApiFixture api)
{
    private static void RequireDatabase() => Skip.IfNot(ApiFixture.DatabaseReachable, ApiFixture.SkipReason);

    /// <summary>
    /// A map nobody else in this suite is standing on.
    ///
    /// ══ WHY NOT goblin_camp ═══════════════════════════════════════════════
    ///
    /// presence is one shared table and these tests share a database. Using a real map
    /// id, "how many others can I see" counted everybody every OTHER test had left
    /// standing there -- so three of these passed alone and failed in the suite, which
    /// is the least useful way for a test to fail.
    ///
    /// A per-test map is isolation by construction rather than by cleanup, and it
    /// needs no teardown that could itself be skipped. Presence does not validate the
    /// id -- only saving a LOCATION does, because that decides what a character wakes
    /// up next to.
    /// </summary>
    private readonly string _map = $"test_map_{Guid.NewGuid():N}";

    /// <summary>Somewhere else, for the test that proves maps are separate worlds.</summary>
    private readonly string _elsewhere = $"test_map_{Guid.NewGuid():N}";

    // ── Presence ──────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task TwoPlayersOnAMapSeeEachOther()
    {
        RequireDatabase();

        var (aPlayer, a) = await Someone("Ayla");
        var (bPlayer, b) = await Someone("Bryn");

        await Report(aPlayer, a, _map, 1f, 2f);

        JsonElement seen = await Report(bPlayer, b, _map, 3f, 4f);

        JsonElement[] others = seen.GetProperty("others").EnumerateArray().ToArray();

        Assert.Single(others);
        Assert.Equal(a, others[0].GetProperty("characterId").GetGuid());
        Assert.Equal("Ayla", others[0].GetProperty("name").GetString());
    }

    /// <summary>
    /// And you are never in your own list.
    ///
    /// Cheap to get wrong and memorable to ship: a twin standing exactly where you
    /// are, mirroring every step.
    /// </summary>
    [SkippableFact]
    public async Task YouAreNotYourOwnNeighbour()
    {
        RequireDatabase();

        var (player, character) = await Someone("Solo");

        JsonElement seen = await Report(player, character, _map, 0f, 0f);

        Assert.Empty(seen.GetProperty("others").EnumerateArray());
    }

    [SkippableFact]
    public async Task ADifferentMapIsADifferentWorld()
    {
        RequireDatabase();

        var (aPlayer, a) = await Someone("Here");
        var (bPlayer, b) = await Someone("Elsewhere");

        await Report(aPlayer, a, _map, 0f, 0f);

        JsonElement seen = await Report(bPlayer, b, _elsewhere, 0f, 0f);

        Assert.Empty(seen.GetProperty("others").EnumerateArray());
    }

    /// <summary>
    /// The level in somebody else's world comes from the DATABASE.
    ///
    /// A presence report carries coordinates and nothing else. If a name or a level
    /// could ride along with it, the cheapest cheat in the game would be to introduce
    /// yourself as level 500.
    /// </summary>
    [SkippableFact]
    public async Task LevelIsReadFromTheCharacterNotTheReport()
    {
        RequireDatabase();

        var (aPlayer, a) = await Someone("Truthful");
        var (bPlayer, b) = await Someone("Observer");

        await SetXp(a, 100_000L);
        await Report(aPlayer, a, _map, 0f, 0f);

        JsonElement seen = await Report(bPlayer, b, _map, 0f, 0f);

        JsonElement them = seen.GetProperty("others").EnumerateArray().Single();

        Assert.True(them.GetProperty("level").GetInt32() > 1);
    }

    /// <summary>A wild coordinate is brought back into the believable range.</summary>
    [SkippableFact]
    public async Task AnAbsurdPositionIsClamped()
    {
        RequireDatabase();

        var (aPlayer, a) = await Someone("Faraway");
        var (bPlayer, b) = await Someone("Watcher");

        await Report(aPlayer, a, _map, 9_999_999f, -9_999_999f);

        JsonElement seen = await Report(bPlayer, b, _map, 0f, 0f);
        JsonElement them = seen.GetProperty("others").EnumerateArray().Single();

        float extent = IdleExplorers.Rules.Presence.MapExtent;

        Assert.InRange(them.GetProperty("x").GetSingle(), -extent, extent);
        Assert.InRange(them.GetProperty("z").GetSingle(), -extent, extent);
    }

    [SkippableFact]
    public async Task SomebodyElsesCharacterCannotBePlaced()
    {
        RequireDatabase();

        var (_, mine)        = await Someone("Owned");
        var (stranger, _)    = await Someone("Stranger");

        HttpResponseMessage refused = await Post(stranger, $"/presence/{mine}",
                                                 new { mapId = _map, x = 0f, z = 0f });

        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
    }

    // ── Parties ───────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task AGroupFormsAndAccepts()
    {
        RequireDatabase();

        var (leaderPlayer, leader) = await Someone("Leader");
        var (joinerPlayer, joiner) = await Someone("Joiner");

        await Post(leaderPlayer, $"/party/{leader}", null);

        HttpResponseMessage joined = await Post(joinerPlayer, $"/party/{joiner}/join/{leader}", null);

        Assert.Equal(HttpStatusCode.OK, joined.StatusCode);

        JsonElement body = JsonDocument.Parse(await joined.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(2, body.GetProperty("members").GetArrayLength());
        Assert.Equal(leader, body.GetProperty("leaderCharacterId").GetGuid());
    }

    /// <summary>Forming twice is one group, not two.</summary>
    [SkippableFact]
    public async Task FormingTwiceIsOneGroup()
    {
        RequireDatabase();

        var (player, character) = await Someone("Eager");

        HttpResponseMessage first  = await Post(player, $"/party/{character}", null);
        HttpResponseMessage second = await Post(player, $"/party/{character}", null);

        Guid a = JsonDocument.Parse(await first.Content.ReadAsStringAsync())
                             .RootElement.GetProperty("partyId").GetGuid();
        Guid b = JsonDocument.Parse(await second.Content.ReadAsStringAsync())
                             .RootElement.GetProperty("partyId").GetGuid();

        Assert.Equal(a, b);
    }

    /// <summary>
    /// A FIFTH player cannot take the fourth seat, even arriving at the same instant.
    ///
    /// ══ WHY THIS IS THE TEST THAT MATTERS ═════════════════════════════════════
    ///
    /// The count-then-insert is only safe under a lock, and the lock has to be on the
    /// thing being contended. Locking each JOINER's own row would let four of them
    /// read "three members" simultaneously and all four insert — so the lock is taken
    /// against the leader's row, which every join to this party shares.
    ///
    /// Sequential joins would pass either way. Only the simultaneous version can tell
    /// the two designs apart.
    /// </summary>
    [SkippableFact]
    public async Task FiveCannotFitInFour()
    {
        RequireDatabase();

        var (leaderPlayer, leader) = await Someone("Captain");

        await Post(leaderPlayer, $"/party/{leader}", null);

        var joiners = new List<(Player Player, Guid Character)>();

        for (int i = 0; i < 6; i++) joiners.Add(await Someone($"Hopeful{i}"));

        // All at once, into one seat count.
        HttpResponseMessage[] answers = await Task.WhenAll(
            joiners.Select(j => Post(j.Player, $"/party/{j.Character}/join/{leader}", null)));

        int accepted = answers.Count(r => r.StatusCode == HttpStatusCode.OK);

        // Three seats were free; the leader holds the fourth.
        Assert.Equal(3, accepted);
        Assert.Equal(IdleExplorers.Rules.Party.MaxMembers, await MemberCount(leader));
    }

    [SkippableFact]
    public async Task YouCannotBeInTwoGroups()
    {
        RequireDatabase();

        var (onePlayer, one)     = await Someone("First");
        var (twoPlayer, two)     = await Someone("Second");
        var (joinerPlayer, join) = await Someone("Torn");

        await Post(onePlayer, $"/party/{one}", null);
        await Post(twoPlayer, $"/party/{two}", null);

        await Post(joinerPlayer, $"/party/{join}/join/{one}", null);

        HttpResponseMessage second = await Post(joinerPlayer, $"/party/{join}/join/{two}", null);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    /// <summary>
    /// The leader leaving hands the group to whoever has been in longest.
    ///
    /// Rather than dissolving a group around the people still standing in it.
    /// </summary>
    [SkippableFact]
    public async Task TheGroupSurvivesItsLeader()
    {
        RequireDatabase();

        var (leaderPlayer, leader) = await Someone("Departing");
        var (stayPlayer, stay)     = await Someone("Remaining");

        await Post(leaderPlayer, $"/party/{leader}", null);
        await Post(stayPlayer, $"/party/{stay}/join/{leader}", null);

        await Delete(leaderPlayer, $"/party/{leader}");

        HttpResponseMessage mine = await stayPlayer.Client.GetAsync($"/party/{stay}");

        JsonElement body = JsonDocument.Parse(await mine.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(1, body.GetProperty("members").GetArrayLength());
        Assert.Equal(stay, body.GetProperty("leaderCharacterId").GetGuid());
    }

    /// <summary>The last one out takes the group with them.</summary>
    [SkippableFact]
    public async Task AnEmptyGroupStopsExisting()
    {
        RequireDatabase();

        var (player, character) = await Someone("Lonely");

        await Post(player, $"/party/{character}", null);
        await Delete(player, $"/party/{character}");

        HttpResponseMessage mine = await player.Client.GetAsync($"/party/{character}");

        JsonElement body = JsonDocument.Parse(await mine.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(JsonValueKind.Null, body.GetProperty("partyId").ValueKind);
    }

    // ── Machinery ─────────────────────────────────────────────────────────────

    private async Task<(Player Player, Guid Character)> Someone(string name)
    {
        var player = await api.NewPlayerAsync();

        return (player, await OwnershipTests.CreateCharacter(player, name));
    }

    private static async Task<JsonElement> Report(Player player, Guid characterId,
                                                  string mapId, float x, float z)
    {
        HttpResponseMessage response = await Post(player, $"/presence/{characterId}",
                                                  new { mapId, x, z });

        response.EnsureSuccessStatusCode();

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }

    private static async Task<HttpResponseMessage> Post(Player player, string path, object? body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);

        if (body != null) request.Content = JsonContent.Create(body);

        request.Headers.Add("Idempotency-Key", Player.NewKey());

        return await player.Client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> Delete(Player player, string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, path);

        request.Headers.Add("Idempotency-Key", Player.NewKey());

        return await player.Client.SendAsync(request);
    }

    private async Task SetXp(Guid characterId, long xp)
    {
        await using var connection = await api.OpenDatabaseAsync();
        await using var command = connection.CreateCommand();

        command.CommandText = "update character set xp = $2 where id = $1;";
        command.Parameters.AddWithValue(characterId);
        command.Parameters.AddWithValue(xp);

        await command.ExecuteNonQueryAsync();
    }

    private async Task<int> MemberCount(Guid anyMember)
    {
        await using var connection = await api.OpenDatabaseAsync();
        await using var command = connection.CreateCommand();

        command.CommandText =
            """
            select count(*)::int from party_member
             where party_id = (select party_id from party_member where character_id = $1);
            """;

        command.Parameters.AddWithValue(anyMember);

        return (int)(await command.ExecuteScalarAsync() ?? 0);
    }
}
