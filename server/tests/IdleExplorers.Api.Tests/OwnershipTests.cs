using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace IdleExplorers.Api.Tests;

/// <summary>
/// Who may touch what.
///
/// This is the boundary the whole architecture rests on. Everything else — settlement
/// arithmetic, ledgers, idempotency — is worth nothing if a player can pass somebody
/// else's character id and be believed.
/// </summary>
[Collection("api")]
public class OwnershipTests(ApiFixture api)
{
    private static void RequireDatabase() => Skip.IfNot(ApiFixture.DatabaseReachable, ApiFixture.SkipReason);

    [SkippableFact]
    public async Task AnUnauthenticatedCallerGetsNothing()
    {
        RequireDatabase();

        using var anonymous = api.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/account/")).StatusCode);
    }

    /// <summary>
    /// A structurally perfect token signed with the wrong key. This is what an
    /// attacker can actually build: they know the shape, they do not have the secret.
    /// </summary>
    [SkippableFact]
    public async Task AForgedTokenIsRejected()
    {
        RequireDatabase();

        using var forger = api.CreateClient();
        forger.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", ApiFixture.ForgedToken(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.Unauthorized, (await forger.GetAsync("/account/")).StatusCode);
    }

    [SkippableFact]
    public async Task AnExpiredTokenIsRejected()
    {
        RequireDatabase();

        using var stale = api.CreateClient();
        stale.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", ApiFixture.TokenFor(Guid.NewGuid(), lifetime: TimeSpan.FromSeconds(-60)));

        Assert.Equal(HttpStatusCode.Unauthorized, (await stale.GetAsync("/account/")).StatusCode);
    }

    /// <summary>
    /// The account row is created by the first authenticated request, not by signup.
    /// Supabase owns the credential; the game owns the account, and they are created
    /// at different moments.
    /// </summary>
    [SkippableFact]
    public async Task TheFirstRequestCreatesTheAccount()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();

        var response = await player.Client.GetAsync("/account/");
        response.EnsureSuccessStatusCode();

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(player.UserId, body.RootElement.GetProperty("accountId").GetGuid());
        Assert.Empty(body.RootElement.GetProperty("characters").EnumerateArray());

        // Both wallets present at zero, so a client never has to distinguish "no row"
        // from "no money".
        Assert.Equal(0L, StackQuantity(body.RootElement, "wallets", "coins"));
        Assert.Equal(0L, StackQuantity(body.RootElement, "wallets", "relic_coins"));
    }

    /// <summary>
    /// ══ THE ATTACK ════════════════════════════════════════════════════════════
    ///
    /// A real account, a real token, and somebody else's character id. One GUID of
    /// difference from a legitimate request, and the easiest thing in the game to
    /// try.
    /// </summary>
    [SkippableFact]
    public async Task OneAccountCannotReadAnotherAccountsCharacter()
    {
        RequireDatabase();

        await using var owner  = await api.NewPlayerAsync();
        await using var thief  = await api.NewPlayerAsync();

        Guid characterId = await CreateCharacter(owner, "Bricta");

        var response = await thief.Client.GetAsync($"/character/{characterId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [SkippableFact]
    public async Task OneAccountCannotDeleteAnotherAccountsCharacter()
    {
        RequireDatabase();

        await using var owner = await api.NewPlayerAsync();
        await using var thief = await api.NewPlayerAsync();

        Guid characterId = await CreateCharacter(owner, "Bricta");

        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/character/{characterId}");
        request.Headers.Add("Idempotency-Key", Player.NewKey());

        var response = await thief.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        // And it really is still there.
        Assert.Equal(HttpStatusCode.OK, (await owner.Client.GetAsync($"/character/{characterId}")).StatusCode);
    }

    /// <summary>
    /// A character that does not exist and one that is not yours must be
    /// indistinguishable, or an attacker enumerates other players a GUID at a time.
    /// </summary>
    [SkippableFact]
    public async Task MissingAndForbiddenLookTheSame()
    {
        RequireDatabase();

        await using var owner = await api.NewPlayerAsync();
        await using var thief = await api.NewPlayerAsync();

        Guid real      = await CreateCharacter(owner, "Bricta");
        Guid imaginary = Guid.NewGuid();

        var somebodyElses = await thief.Client.GetAsync($"/character/{real}");
        var nothingAtAll  = await thief.Client.GetAsync($"/character/{imaginary}");

        Assert.Equal(somebodyElses.StatusCode, nothingAtAll.StatusCode);
        Assert.Equal(await somebodyElses.Content.ReadAsStringAsync(),
                     await nothingAtAll.Content.ReadAsStringAsync());
    }

    [SkippableFact]
    public async Task ADeletedCharacterIsGoneButItsNameIsFree()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();

        Guid first = await CreateCharacter(player, "Bricta");

        using var delete = new HttpRequestMessage(HttpMethod.Delete, $"/character/{first}");
        delete.Headers.Add("Idempotency-Key", Player.NewKey());
        (await player.Client.SendAsync(delete)).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.NotFound, (await player.Client.GetAsync($"/character/{first}")).StatusCode);

        // The name comes back.
        Guid second = await CreateCharacter(player, "Bricta");
        Assert.NotEqual(first, second);
    }

    [SkippableFact]
    public async Task TwoLivingCharactersCannotShareAName()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();

        await CreateCharacter(player, "Bricta");

        var response = await Post(player, "/character/", new { name = "Bricta" });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [SkippableFact]
    public async Task DifferentAccountsMayShareCharacterNames()
    {
        RequireDatabase();

        await using var one = await api.NewPlayerAsync();
        await using var two = await api.NewPlayerAsync();

        await CreateCharacter(one, "Bricta");
        await CreateCharacter(two, "Bricta");
    }

    [SkippableFact]
    public async Task AnInventedClassIsRefused()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();

        var response = await Post(player, "/character/",
                                  new { name = "Cheater", classId = "god_emperor" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [SkippableTheory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("this name is very much too long to be allowed")]
    public async Task ANonsenseNameIsRefused(string name)
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();

        var response = await Post(player, "/character/", new { name });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    /// <summary>
    /// Reads a quantity out of the array-of-stacks shape the server returns.
    ///
    /// Arrays rather than an object keyed by item id, because JsonUtility on the
    /// client cannot deserialise a Dictionary -- see ActivityEndpoints.Stacks.
    /// </summary>
    internal static long StackQuantity(JsonElement response, string field, string id)
    {
        if (!response.TryGetProperty(field, out var stacks)) return 0L;

        // Read the shape rather than guess from the field name. Three arrays exist
        // and they are named for what they hold -- items are {itemId, quantity},
        // earnings are {currency, amount}, wallets are {currency, balance} -- and a
        // helper that inferred that from the property name got it backwards once
        // already.
        foreach (var stack in stacks.EnumerateArray())
        {
            if (Text(stack, "itemId") != id && Text(stack, "currency") != id) continue;

            if (stack.TryGetProperty("quantity", out var quantity)) return quantity.GetInt64();
            if (stack.TryGetProperty("amount",   out var amount))   return amount.GetInt64();
            if (stack.TryGetProperty("balance",  out var balance))  return balance.GetInt64();
        }

        return 0L;
    }

    private static string Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) ? value.GetString() : null;

    internal static bool HasStack(JsonElement response, string field, string id) =>
        StackQuantity(response, field, id) > 0L;


    internal static async Task<HttpResponseMessage> Post(Player player, string path, object body,
                                                         string key = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add("Idempotency-Key", key ?? Player.NewKey());

        return await player.Client.SendAsync(request);
    }

    internal static async Task<Guid> CreateCharacter(Player player, string name, string classId = "")
    {
        var response = await Post(player, "/character/", new { name, classId });
        response.EnsureSuccessStatusCode();

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("id").GetGuid();
    }
}
