#nullable enable

using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using IdleExplorers.Api.Endpoints;
using Xunit;

namespace IdleExplorers.Api.Tests;

/// <summary>
/// The unpaid shop grant, and the switch that has to be thrown for it.
///
/// ══ WHAT IS ACTUALLY BEING PROTECTED ══════════════════════════════════════════
///
/// A tap that mints premium currency for free. The danger is not that it exists --
/// it has to, or the shop cannot be tested before a payment processor does -- but
/// that it could be ON without anybody deciding it should be.
///
/// FeatureFlags treats an unknown flag as ON, which is right for gameplay and exactly
/// wrong here: a fresh database, a dropped row or a mistyped seed would open the tap.
/// So the endpoint asks the strict variant, and the first test below is the one that
/// matters -- a flag that was never mentioned grants nothing.
/// </summary>
[Collection("api")]
public class ShopTests(ApiFixture api)
{
    private static void RequireDatabase() => Skip.IfNot(ApiFixture.DatabaseReachable, ApiFixture.SkipReason);

    /// <summary>
    /// The default is no.
    ///
    /// The migration seeds the row false, so this is the shipped state: the shop is
    /// reachable, the pack is real, and nothing is granted.
    /// </summary>
    [SkippableFact]
    public async Task WithTheFlagOffNothingIsGranted()
    {
        RequireDatabase();

        var player = await api.NewPlayerAsync();

        await SetFlag(false);

        HttpResponseMessage refused = await Grant(player, FirstPackId);

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(Welcome, await RelicCoins(player));
    }

    /// <summary>
    /// And a flag NOBODY HAS EVER SET grants nothing either.
    ///
    /// This is the whole reason IsExplicitlyEnabledAsync exists. Asking the ordinary
    /// IsEnabledAsync here would return true -- unknown flags are on -- and a database
    /// that had never heard of this switch would hand out premium currency to anyone
    /// who asked.
    /// </summary>
    [SkippableFact]
    public async Task AFlagNobodySetGrantsNothing()
    {
        RequireDatabase();

        var player = await api.NewPlayerAsync();

        await using (var connection = await api.OpenDatabaseAsync())
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "delete from feature_flag where flag = $1;";
            command.Parameters.AddWithValue(ShopEndpoints.TestGrantsFlag);
            await command.ExecuteNonQueryAsync();
        }

        api.Flags.Forget();

        HttpResponseMessage refused = await Grant(player, FirstPackId);

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(Welcome, await RelicCoins(player));

        await SetFlag(false);
    }

    /// <summary>
    /// Switched on, it grants -- and the balance is the SERVER'S.
    ///
    /// Paired with the refusals above deliberately. A refusal test alone passes on an
    /// endpoint that refuses everything, which is the mistake this suite has already
    /// shipped once in the authentication tests.
    /// </summary>
    [SkippableFact]
    public async Task WithTheFlagOnThePackIsGranted()
    {
        RequireDatabase();

        var player = await api.NewPlayerAsync();

        await SetFlag(true);

        try
        {
            HttpResponseMessage granted = await Grant(player, FirstPackId);

            Assert.Equal(HttpStatusCode.OK, granted.StatusCode);

            JsonElement body = JsonDocument
                .Parse(await granted.Content.ReadAsStringAsync()).RootElement;

            long amount = body.GetProperty("granted").GetInt64();

            Assert.True(amount > 0L);
            Assert.False(body.GetProperty("paid").GetBoolean());
            // The pack's coins ON TOP of the welcome grant. Comparing the reported
            // balance against the pack size alone would have quietly asserted that
            // accounts start empty, which they no longer do.
            Assert.Equal(Welcome + amount, body.GetProperty("balance").GetInt64());
            Assert.Equal(Welcome + amount, await RelicCoins(player));
        }
        finally
        {
            await SetFlag(false);
        }
    }

    /// <summary>
    /// Every unpaid coin is labelled as one.
    ///
    /// The point is reconciliation. When real purchasing arrives, one query has to be
    /// able to find every coin that was never paid for -- otherwise the test grants
    /// are indistinguishable from revenue for ever.
    /// </summary>
    [SkippableFact]
    public async Task TheLedgerSaysItWasNotPaidFor()
    {
        RequireDatabase();

        var player = await api.NewPlayerAsync();

        await SetFlag(true);

        try
        {
            await Grant(player, FirstPackId);

            await using var connection = await api.OpenDatabaseAsync();
            await using var command = connection.CreateCommand();

            command.CommandText =
                "select count(*) from wallet_ledger where account_id = $1 and reason = $2;";
            command.Parameters.AddWithValue(player.UserId);
            command.Parameters.AddWithValue(ShopEndpoints.TestGrantReason);

            Assert.Equal(1L, (long)(await command.ExecuteScalarAsync() ?? 0L));
        }
        finally
        {
            await SetFlag(false);
        }
    }

    [SkippableFact]
    public async Task AnInventedPackIsRefused()
    {
        RequireDatabase();

        var player = await api.NewPlayerAsync();

        await SetFlag(true);

        try
        {
            HttpResponseMessage refused = await Grant(player, "pack_of_infinite_wealth");

            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Equal(Welcome, await RelicCoins(player));
        }
        finally
        {
            await SetFlag(false);
        }
    }

    [SkippableFact]
    public async Task AnonymousCallersGetNothing()
    {
        RequireDatabase();

        await SetFlag(true);

        try
        {
            HttpClient anonymous = api.CreateClient();

            var request = new HttpRequestMessage(HttpMethod.Post, "/shop/test-grant")
            {
                Content = JsonContent.Create(new { packId = FirstPackId }),
            };

            request.Headers.Add("Idempotency-Key", Player.NewKey());

            HttpResponseMessage response = await anonymous.SendAsync(request);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        finally
        {
            await SetFlag(false);
        }
    }

    // ── Machinery ─────────────────────────────────────────────────────────────

    /// <summary>
    /// A real pack id from shop_data.json.
    ///
    /// The "with the flag on" test asserts a grant SUCCEEDS, so a pack that stopped
    /// existing would fail loudly there rather than quietly turning the refusal cases
    /// into tests of a missing pack.
    /// </summary>
    private static string FirstPackId => "relic_1300";

    /// <summary>
    /// What every account starts with.
    ///
    /// Named rather than written as 1000, so these tests move with the grant instead
    /// of failing the day somebody changes it -- and so the assertions read as "the
    /// welcome grant and nothing more" rather than as a magic number.
    /// </summary>
    private static long Welcome => IdleExplorers.Rules.Currency.WelcomeRelicCoins;

    private static async Task<HttpResponseMessage> Grant(Player player, string packId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/shop/test-grant")
        {
            Content = JsonContent.Create(new { packId }),
        };

        request.Headers.Add("Idempotency-Key", Player.NewKey());

        return await player.Client.SendAsync(request);
    }

    private async Task SetFlag(bool enabled)
    {
        await using var connection = await api.OpenDatabaseAsync();
        await using var command = connection.CreateCommand();

        command.CommandText =
            """
            insert into feature_flag (flag, enabled) values ($1, $2)
            on conflict (flag) do update set enabled = excluded.enabled;
            """;

        command.Parameters.AddWithValue(ShopEndpoints.TestGrantsFlag);
        command.Parameters.AddWithValue(enabled);

        await command.ExecuteNonQueryAsync();

        // The flag cache holds for ten seconds, which is fine in production and fatal
        // in a test that flips a switch and immediately asks. Forgetting it rather
        // than shortening it: the duration is a production decision.
        api.Flags.Forget();
    }

    private async Task<long> RelicCoins(Player player)
    {
        await using var connection = await api.OpenDatabaseAsync();
        await using var command = connection.CreateCommand();

        command.CommandText =
            "select coalesce(sum(balance), 0)::bigint from wallet " +
            "where account_id = $1 and currency = $2;";

        command.Parameters.AddWithValue(player.UserId);
        command.Parameters.AddWithValue(IdleExplorers.Rules.Currency.RelicCoins);

        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }
}
