#nullable enable

using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace IdleExplorers.Api.Tests;

/// <summary>
/// The two things a character keeps that are not progression.
///
/// ══ WHY THEY ARE TESTED BY ROUND TRIP ═════════════════════════════════════════
///
/// Because "the endpoint exists" is not the property that matters, and this suite has
/// already been fooled once by exactly that distinction: every authentication test
/// passed against a server that accepted no token at all, because each one asked
/// whether something was REFUSED and none asked whether anything got through.
///
/// So these write a value and read it back somewhere else -- appearance through the
/// roster as well as the character, because the select screen draws from the roster
/// and that is the query that was missing the column.
/// </summary>
[Collection("api")]
public class AppearanceAndLocationTests(ApiFixture api)
{
    private static void RequireDatabase() => Skip.IfNot(ApiFixture.DatabaseReachable, ApiFixture.SkipReason);

    [SkippableFact]
    public async Task AppearanceSurvivesTheRoundTrip()
    {
        RequireDatabase();

        var player      = await api.NewPlayerAsync();
        Guid characterId = await Create(player, "Bricta");

        var look = new { hairAddress = "hair_07", eyeAddress = "eye_02", hairColor = "#8844AA" };

        HttpResponseMessage saved = await Put(player, $"/character/{characterId}/appearance", new { appearance = look });

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        JsonElement character = await Read(player, $"/character/{characterId}");
        JsonElement stored    = character.GetProperty("appearance");

        Assert.Equal("hair_07", stored.GetProperty("hairAddress").GetString());
        Assert.Equal("#8844AA", stored.GetProperty("hairColor").GetString());
    }

    /// <summary>
    /// The roster carries it too.
    ///
    /// This is the query that decides what the character select screen draws, and it
    /// is a different SELECT from the one above -- so a column added to one and not
    /// the other produces a character sheet with the right face and a card with the
    /// wrong one.
    /// </summary>
    [SkippableFact]
    public async Task TheRosterCarriesTheAppearance()
    {
        RequireDatabase();

        var player      = await api.NewPlayerAsync();
        Guid characterId = await Create(player, "Rosterface");

        await Put(player, $"/character/{characterId}/appearance",
                  new { appearance = new { hairAddress = "hair_11" } });

        JsonElement account = await Read(player, "/account/");

        foreach (JsonElement entry in account.GetProperty("characters").EnumerateArray())
        {
            if (entry.GetProperty("characterId").GetGuid() != characterId) continue;

            Assert.Equal("hair_11",
                         entry.GetProperty("appearance").GetProperty("hairAddress").GetString());
            return;
        }

        Assert.Fail("the character was not in the roster");
    }

    /// <summary>
    /// A look sent at creation is kept, rather than needing a second call.
    /// </summary>
    [SkippableFact]
    public async Task CreationCarriesTheAppearance()
    {
        RequireDatabase();

        var player = await api.NewPlayerAsync();

        var request = new HttpRequestMessage(HttpMethod.Post, "/character/")
        {
            Content = JsonContent.Create(new
            {
                name       = "Bornpretty",
                classId    = "",
                appearance = new { hairAddress = "hair_03", bodyAddress = "body_01" },
            }),
        };

        request.Headers.Add("Idempotency-Key", Player.NewKey());

        HttpResponseMessage created = await player.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, created.StatusCode);

        JsonElement body = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal("hair_03",
                     body.GetProperty("appearance").GetProperty("hairAddress").GetString());
    }

    [SkippableFact]
    public async Task LocationSurvivesTheRoundTrip()
    {
        RequireDatabase();

        var player      = await api.NewPlayerAsync();
        Guid characterId = await Create(player, "Wanderer");

        HttpResponseMessage saved = await Put(player, $"/character/{characterId}/location", new { mapId = "goblin_camp" });

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        JsonElement character = await Read(player, $"/character/{characterId}");

        Assert.Equal("goblin_camp", character.GetProperty("lastMapId").GetString());
    }

    /// <summary>
    /// A map the content does not contain is refused.
    ///
    /// The one part of this pair that is validated, and the reason is the difference
    /// between the two: where a character wakes up decides what they can gather and
    /// fight. An unchecked map id lets a client choose to log in inside a zone it has
    /// not earned. A hairstyle costs nobody anything.
    /// </summary>
    [SkippableFact]
    public async Task AnInventedMapIsRefused()
    {
        RequireDatabase();

        var player      = await api.NewPlayerAsync();
        Guid characterId = await Create(player, "Trespasser");

        HttpResponseMessage refused = await Put(player, $"/character/{characterId}/location",
                  new { mapId = "the_vault_of_infinite_ore" });

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        JsonElement character = await Read(player, $"/character/{characterId}");

        Assert.NotEqual("the_vault_of_infinite_ore", character.GetProperty("lastMapId").GetString());
    }

    /// <summary>Neither is somebody else's to write.</summary>
    [SkippableFact]
    public async Task AnotherPlayersCharacterIsNotYours()
    {
        RequireDatabase();

        var owner     = await api.NewPlayerAsync();
        var stranger  = await api.NewPlayerAsync();
        Guid characterId = await Create(owner, "Mine");

        HttpResponseMessage look = await Put(stranger, $"/character/{characterId}/appearance",
                                            new { appearance = new { hairAddress = "hair_99" } });

        HttpResponseMessage where = await Put(stranger, $"/character/{characterId}/location",
                                              new { mapId = "goblin_camp" });

        Assert.Equal(HttpStatusCode.NotFound, look.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, where.StatusCode);
    }

    /// <summary>
    /// A new character is not naked.
    ///
    /// ══ WHY THE SERVER DOES THIS ════════════════════════════════════════
    ///
    /// A client that granted its own starting gear is a client that can grant itself
    /// anything, which is the whole thing this architecture removed. It also has to
    /// happen inside the creation transaction, or a rollback leaves clothes behind for
    /// a character that does not exist.
    /// </summary>
    [SkippableFact]
    public async Task ANewCharacterStartsDressed()
    {
        RequireDatabase();

        var player = await api.NewPlayerAsync();

        var request = new HttpRequestMessage(HttpMethod.Post, "/character/")
        {
            Content = JsonContent.Create(new { name = "Clothed", classId = "warrior" }),
        };

        request.Headers.Add("Idempotency-Key", Player.NewKey());

        HttpResponseMessage created = await player.Client.SendAsync(request);

        created.EnsureSuccessStatusCode();

        Guid characterId = JsonDocument.Parse(await created.Content.ReadAsStringAsync())
                                       .RootElement.GetProperty("characterId").GetGuid();

        JsonElement sheet = await Read(player, $"/character/{characterId}");

        int worn = sheet.GetProperty("equipment").GetArrayLength();

        Assert.True(worn >= 3, $"a new warrior is wearing {worn} pieces");
    }

    /// <summary>A class with no authored set is still creatable, just barer.</summary>
    [SkippableFact]
    public async Task AClassWithNoSetIsStillCreatable()
    {
        RequireDatabase();

        var player = await api.NewPlayerAsync();

        var request = new HttpRequestMessage(HttpMethod.Post, "/character/")
        {
            Content = JsonContent.Create(new { name = "Bare", classId = "" }),
        };

        request.Headers.Add("Idempotency-Key", Player.NewKey());

        HttpResponseMessage created = await player.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
    }

    private static Task<Guid> Create(Player player, string name) =>
        OwnershipTests.CreateCharacter(player, name);

    /// <summary>
    /// A mutating request, with the key the middleware insists on.
    ///
    /// Blanket by design: idempotency is enforced for every method that changes
    /// something, so a new endpoint is covered the day it is written rather than the
    /// day somebody remembers. Which means the tests carry a key too.
    /// </summary>
    private static async Task<HttpResponseMessage> Put(Player player, string path, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, path)
        {
            Content = JsonContent.Create(body),
        };

        request.Headers.Add("Idempotency-Key", Player.NewKey());

        return await player.Client.SendAsync(request);
    }

    private static async Task<JsonElement> Read(Player player, string path)
    {
        HttpResponseMessage response = await player.Client.GetAsync(path);

        response.EnsureSuccessStatusCode();

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }
}
