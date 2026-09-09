#nullable enable

using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using IdleExplorers.Api.Endpoints;
using IdleExplorers.Api.Infrastructure;
using Xunit;

namespace IdleExplorers.Api.Tests;

/// <summary>
/// One account, one client.
///
/// ══ WHAT WAS POSSIBLE ═════════════════════════════════════════════════════════
///
/// Signing into the same account in two browsers and playing the same character in
/// both — two clients each settling, each setting activities, each writing positions
/// over one another.
///
/// Nothing duplicated, because settlement holds a row lock and the timestamp is the
/// bookkeeping. But it is a race nobody should have to reason about, and it is the
/// shape every duplication exploit begins with.
///
/// ══ THE THREE THINGS THAT MATTER ══════════════════════════════════════════════
///
/// The newest client wins, the older one is refused, and READS still work — a
/// displaced tab has to be able to draw a coherent screen while it explains itself.
///
/// And the soft edge, which is deliberate and tested rather than assumed: a request
/// carrying NO claim is allowed. Refusing those would mean this could not be deployed
/// without shipping the matching client in the same instant.
/// </summary>
[Collection("api")]
public class SessionGuardTests(ApiFixture api)
{
    private static void RequireDatabase() => Skip.IfNot(ApiFixture.DatabaseReachable, ApiFixture.SkipReason);

    [SkippableFact]
    public async Task TheNewestClientHoldsTheAccount()
    {
        RequireDatabase();

        var player = await api.NewPlayerAsync();

        Guid first  = await Claim(player);
        Guid second = await Claim(player);

        Assert.NotEqual(first, second);
    }

    /// <summary>
    /// The displaced client cannot change anything.
    ///
    /// Paired below with a read that still works, because a guard that refuses
    /// everything would pass this on its own for the wrong reason.
    /// </summary>
    [SkippableFact]
    public async Task TheDisplacedClientCannotWrite()
    {
        RequireDatabase();

        var player      = await api.NewPlayerAsync();
        Guid characterId = await OwnershipTests.CreateCharacter(player, "Doubled");

        Guid stale = await Claim(player);
        await Claim(player);                       // a second browser takes it

        HttpResponseMessage refused = await Post(player, stale,
                                                 $"/presence/{characterId}",
                                                 new { mapId = "goblin_camp", x = 0f, z = 0f });

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

        // The title is what the client matches on to tell this apart from "group is
        // full" and "already fighting", which are also 409.
        JsonElement body = JsonDocument.Parse(await refused.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(SessionGuard.DisplacedTitle, body.GetProperty("title").GetString());
    }

    /// <summary>
    /// And the current one still can.
    ///
    /// The other half of the pair. Without it, a guard that refused every write would
    /// look correct.
    /// </summary>
    [SkippableFact]
    public async Task TheCurrentClientStillCan()
    {
        RequireDatabase();

        var player      = await api.NewPlayerAsync();
        Guid characterId = await OwnershipTests.CreateCharacter(player, "Current");

        await Claim(player);
        Guid current = await Claim(player);

        HttpResponseMessage allowed = await Post(player, current,
                                                 $"/presence/{characterId}",
                                                 new { mapId = "goblin_camp", x = 0f, z = 0f });

        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    /// <summary>
    /// A displaced client can still READ.
    ///
    /// So the stale tab shows a coherent screen while it tells the player they have
    /// been signed in elsewhere, rather than collapsing into errors.
    /// </summary>
    [SkippableFact]
    public async Task ADisplacedClientCanStillRead()
    {
        RequireDatabase();

        var player = await api.NewPlayerAsync();

        Guid stale = await Claim(player);
        await Claim(player);

        var request = new HttpRequestMessage(HttpMethod.Get, "/account/");
        request.Headers.Add(SessionEndpoints.HeaderName, stale.ToString());

        HttpResponseMessage read = await player.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
    }

    /// <summary>
    /// NO claim at all is allowed, on purpose.
    ///
    /// The deliberate soft edge. A client that never claimed is an older build, a
    /// tool, or the first request of a sign-in about to claim one — not a second
    /// browser. Refusing them would mean this could not ship without the matching
    /// client landing in the same instant, and the failure mode would be every
    /// request in the game returning 409.
    ///
    /// Written down as a test because it is the one line somebody will later read as
    /// a hole and "fix".
    /// </summary>
    [SkippableFact]
    public async Task AClientWithNoClaimIsAllowed()
    {
        RequireDatabase();

        var player      = await api.NewPlayerAsync();
        Guid characterId = await OwnershipTests.CreateCharacter(player, "Unclaimed");

        await Claim(player);
        await Claim(player);

        // No session header at all.
        HttpResponseMessage allowed = await OwnershipTests.Post(
            player, $"/presence/{characterId}", new { mapId = "goblin_camp", x = 0f, z = 0f });

        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    /// <summary>
    /// Signing out releases the claim, and only if it is still yours.
    ///
    /// A client that was displaced and then signed out must not take the NEW client's
    /// claim with it on the way — which would displace somebody who did nothing wrong.
    /// </summary>
    [SkippableFact]
    public async Task ADisplacedClientSigningOutDoesNotTakeTheAccount()
    {
        RequireDatabase();

        var player      = await api.NewPlayerAsync();
        Guid characterId = await OwnershipTests.CreateCharacter(player, "Polite");

        Guid stale   = await Claim(player);
        Guid current = await Claim(player);

        var release = new HttpRequestMessage(HttpMethod.Delete, "/session/");
        release.Headers.Add(SessionEndpoints.HeaderName, stale.ToString());
        release.Headers.Add("Idempotency-Key", Player.NewKey());

        await player.Client.SendAsync(release);

        // The current client is untouched.
        HttpResponseMessage allowed = await Post(player, current,
                                                 $"/presence/{characterId}",
                                                 new { mapId = "goblin_camp", x = 0f, z = 0f });

        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    /// <summary>Somebody else's claim is no help against your own account.</summary>
    [SkippableFact]
    public async Task AnotherAccountsClaimIsNotYours()
    {
        RequireDatabase();

        var mine      = await api.NewPlayerAsync();
        var stranger  = await api.NewPlayerAsync();

        Guid characterId = await OwnershipTests.CreateCharacter(mine, "Separate");

        await Claim(mine);
        Guid theirs = await Claim(stranger);

        HttpResponseMessage refused = await Post(mine, theirs,
                                                 $"/presence/{characterId}",
                                                 new { mapId = "goblin_camp", x = 0f, z = 0f });

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
    }

    // ── Machinery ─────────────────────────────────────────────────────────────

    private static async Task<Guid> Claim(Player player)
    {
        HttpResponseMessage response = await OwnershipTests.Post(player, "/session/", new { });

        response.EnsureSuccessStatusCode();

        JsonElement body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        return body.GetProperty("sessionId").GetGuid();
    }

    private static async Task<HttpResponseMessage> Post(Player player, Guid session,
                                                        string path, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body),
        };

        request.Headers.Add("Idempotency-Key", Player.NewKey());
        request.Headers.Add(SessionEndpoints.HeaderName, session.ToString());

        return await player.Client.SendAsync(request);
    }
}
