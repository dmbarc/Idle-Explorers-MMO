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
/// One request, one effect.
///
/// ══ THE BUG THESE WERE WRITTEN AGAINST ════════════════════════════════════════
///
/// The first version of the middleware looked for a stored response, ran the handler,
/// then stored the answer. Two copies of one request arriving together both found
/// nothing stored, and both executed. The character lock serialised them, which means
/// the second craft happened AFTER the first rather than instead of it.
///
/// The fix was to claim the key before doing the work, using the unique constraint as
/// the claim.
///
/// ══ WHAT MAKES THE RACE TEST HONEST ═══════════════════════════════════════════
///
/// Counting characters afterwards does NOT distinguish the two designs, which is
/// worth writing down because the first version of that test did exactly that and
/// passed with idempotency switched off. Twenty concurrent creations of one name are
/// collapsed to one by the unique index on (account_id, name), whatever the
/// middleware does. The domain happened to be self-protecting, so the test proved
/// only that.
///
/// What distinguishes them is WHY the extras failed. Under a working claim every
/// duplicate is stopped before it reaches a handler, so the only conflict anyone sees
/// is "request in progress". Under the broken design the duplicates execute and are
/// refused by the database, which surfaces as "name taken" — a request that got all
/// the way to doing its work.
/// </summary>
[Collection("api")]
public class IdempotencyTests(ApiFixture api)
{
    private static void RequireDatabase() => Skip.IfNot(ApiFixture.DatabaseReachable, ApiFixture.SkipReason);

    [SkippableFact]
    public async Task AMutationWithoutAKeyIsRefused()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();

        // No Idempotency-Key header at all.
        var response = await player.Client.PostAsJsonAsync("/character/", new { name = "Keyless" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // And nothing happened.
        Assert.Empty(await CharactersOf(player));
    }

    [SkippableFact]
    public async Task AReadNeedsNoKey()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();

        // GET carries no key and must still work — requiring one would be ceremony,
        // since replaying a read is free.
        (await player.Client.GetAsync("/account/")).EnsureSuccessStatusCode();
    }

    /// <summary>
    /// The ordinary retry: a tab suspended mid-request, the client sends it again.
    /// The player must get their answer back, not a second character.
    /// </summary>
    [SkippableFact]
    public async Task TheSameRequestTwiceMakesOneCharacter()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        string key = Player.NewKey();

        var first  = await OwnershipTests.Post(player, "/character/", new { name = "Bricta" }, key);
        var second = await OwnershipTests.Post(player, "/character/", new { name = "Bricta" }, key);

        first.EnsureSuccessStatusCode();
        second.EnsureSuccessStatusCode();

        // The stored answer, byte for byte — not merely "already done". A retry that
        // returns nothing looks like a failure to the player.
        Assert.Equal(await first.Content.ReadAsStringAsync(),
                     await second.Content.ReadAsStringAsync());

        Assert.True(second.Headers.Contains("Idempotent-Replay"));

        Assert.Single(await CharactersOf(player));
    }

    /// <summary>
    /// ══ THE ONE THAT MATTERS ══════════════════════════════════════════════════
    ///
    /// Twenty copies of one request, in flight simultaneously. Under the original
    /// design several would slip past the read and each create a character.
    ///
    /// Twenty rather than two: a race with a narrow window passes a two-request test
    /// most of the time, which is worse than no test at all.
    /// </summary>
    [SkippableFact]
    public async Task TheSameRequestSentTwentyTimesAtOnceMakesOneCharacter()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        string key = Player.NewKey();

        var attempts = Enumerable.Range(0, 20)
            .Select(_ => OwnershipTests.Post(player, "/character/", new { name = "Bricta" }, key))
            .ToArray();

        HttpResponseMessage[] responses = await Task.WhenAll(attempts);

        var characters = await CharactersOf(player);

        Assert.Single(characters);

        var succeeded = responses.Where(r => r.IsSuccessStatusCode).ToList();
        var conflicts = responses.Where(r => r.StatusCode == HttpStatusCode.Conflict).ToList();

        Assert.Equal(responses.Length, succeeded.Count + conflicts.Count);
        Assert.NotEmpty(succeeded);

        // Every success reports the same character. Under either design this holds,
        // because the unique index protects the domain -- see the class comment.
        var ids = new HashSet<Guid>();

        foreach (var response in succeeded)
        {
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            ids.Add(body.RootElement.GetProperty("id").GetGuid());
        }

        Assert.Single(ids);
        Assert.Equal(characters[0], ids.Single());

        // THIS is the assertion that distinguishes the designs. A duplicate stopped by
        // the claim never reaches a handler, so it can only ever report "request in
        // progress". A duplicate that reports "name taken" got all the way to the
        // database and was refused there -- which means it executed.
        foreach (var conflict in conflicts)
        {
            using var body = JsonDocument.Parse(await conflict.Content.ReadAsStringAsync());
            string title = body.RootElement.GetProperty("title").GetString();

            Assert.True(title == "request in progress",
                        $"a duplicate reached the handler and was refused there: '{title}'");
        }

        foreach (var response in responses) response.Dispose();
    }

    /// <summary>
    /// Two DIFFERENT requests must both happen. An idempotency scheme that collapses
    /// them is not safe, it is broken — the player made two characters and got one.
    /// </summary>
    [SkippableFact]
    public async Task DifferentKeysDoDifferentWork()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();

        await OwnershipTests.Post(player, "/character/", new { name = "One" }, Player.NewKey());
        await OwnershipTests.Post(player, "/character/", new { name = "Two" }, Player.NewKey());

        Assert.Equal(2, (await CharactersOf(player)).Count);
    }

    /// <summary>
    /// Keys are scoped per account. Sharing a namespace would let one player's key
    /// choice deny another player theirs — and clients pick keys client-side.
    /// </summary>
    [SkippableFact]
    public async Task TheSameKeyOnTwoAccountsIsTwoRequests()
    {
        RequireDatabase();

        await using var one = await api.NewPlayerAsync();
        await using var two = await api.NewPlayerAsync();

        const string shared = "not-actually-unique";

        (await OwnershipTests.Post(one, "/character/", new { name = "Bricta" }, shared)).EnsureSuccessStatusCode();
        (await OwnershipTests.Post(two, "/character/", new { name = "Bricta" }, shared)).EnsureSuccessStatusCode();

        Assert.Single(await CharactersOf(one));
        Assert.Single(await CharactersOf(two));
    }

    /// <summary>
    /// Replaying a craft's answer to a purchase would be a worse bug than refusing.
    /// </summary>
    [SkippableFact]
    public async Task AKeyReusedOnAnotherEndpointIsRefused()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        string key = Player.NewKey();

        (await OwnershipTests.Post(player, "/character/", new { name = "Bricta" }, key))
            .EnsureSuccessStatusCode();

        var elsewhere = await OwnershipTests.Post(player, "/account/name",
                                                  new { displayName = "Renamed" }, key);

        Assert.Equal(HttpStatusCode.Conflict, elsewhere.StatusCode);

        // And the rename genuinely did not happen.
        using var account = JsonDocument.Parse(
            await (await player.Client.GetAsync("/account/")).Content.ReadAsStringAsync());

        Assert.NotEqual("Renamed", account.RootElement.GetProperty("displayName").GetString());
    }

    /// <summary>
    /// A FAILED request must stay retryable. Storing its answer would poison that key
    /// permanently, and the key is baked into the client's retry — so the player
    /// could never get past it by trying again, which is the only thing they can do.
    /// </summary>
    [SkippableFact]
    public async Task AFailedRequestCanBeRetriedWithTheSameKey()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        string key = Player.NewKey();

        // Refused for a bad name.
        var refused = await OwnershipTests.Post(player, "/character/", new { name = "" }, key);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        // The same key, now with something valid, must work.
        var retried = await OwnershipTests.Post(player, "/character/", new { name = "Bricta" }, key);
        retried.EnsureSuccessStatusCode();

        Assert.Single(await CharactersOf(player));
    }

    [SkippableFact]
    public async Task AnAbsurdlyLongKeyIsRefused()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();

        var response = await OwnershipTests.Post(player, "/character/",
                                                 new { name = "Bricta" }, new string('k', 500));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task<List<Guid>> CharactersOf(Player player)
    {
        var response = await player.Client.GetAsync("/account/");
        response.EnsureSuccessStatusCode();

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return body.RootElement.GetProperty("characters")
            .EnumerateArray()
            .Select(c => c.GetProperty("id").GetGuid())
            .ToList();
    }
}
