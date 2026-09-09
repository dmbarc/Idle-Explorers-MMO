using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using IdleExplorers.Api.Infrastructure;
using Xunit;

namespace IdleExplorers.Api.Tests;

/// <summary>
/// Whether a browser will let the game talk to its own server.
///
/// ══ WHY THIS IS ITS OWN FILE ══════════════════════════════════════════════════
///
/// Because the failure is invisible everywhere it is convenient to test. UnityWebRequest
/// outside WebGL is not a browser and ignores CORS entirely, so the editor works
/// perfectly while a web build cannot make a single call. Every other test in this suite
/// uses HttpClient, which is also not a browser.
///
/// The API answered a preflight with 405 until this existed. Nothing caught it, because
/// nothing here is a browser -- so these tests send the headers a browser would and read
/// the ones it checks.
/// </summary>
[Collection("api")]
public class CorsTests(ApiFixture api)
{
    private static void RequireDatabase() => Skip.IfNot(ApiFixture.DatabaseReachable, ApiFixture.SkipReason);

    /// <summary>
    /// A preflight is unauthenticated by design, and must not be refused for it.
    ///
    /// The browser sends OPTIONS before it will attach any header the page asked for,
    /// Authorization included.
    ///
    /// Worth recording what this does NOT catch: moving UseCors after UseAuthorization
    /// was tried, and everything still passed. In minimal APIs UseAuthorization rejects
    /// nothing by itself -- the requirement lives on the endpoint and is enforced later
    /// -- so both orders work, and a comment claiming otherwise was wrong.
    ///
    /// What it does catch is CORS being absent, verified by removing it: this test and
    /// the one below fail. That is the regression worth having.
    /// </summary>
    [SkippableFact]
    public async Task APreflightIsAnsweredWithoutAToken()
    {
        RequireDatabase();

        using var request = new HttpRequestMessage(HttpMethod.Options, "/account/");

        request.Headers.Add("Origin", Allowed);
        request.Headers.Add("Access-Control-Request-Method", "GET");
        request.Headers.Add("Access-Control-Request-Headers", "authorization");

        var response = await api.CreateClient().SendAsync(request);

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.MethodNotAllowed, response.StatusCode);

        // The header the browser actually reads. Without it, everything above is
        // decoration and the request is still blocked.
        Assert.True(response.Headers.Contains("Access-Control-Allow-Origin"),
                    "the response carries no Access-Control-Allow-Origin");
    }

    [SkippableFact]
    public async Task AnAllowedOriginIsEchoedOnARealRequest()
    {
        RequireDatabase();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/healthz");
        request.Headers.Add("Origin", Allowed);

        var response = await api.CreateClient().SendAsync(request);

        Assert.True(response.Headers.TryGetValues("Access-Control-Allow-Origin", out var values));
        Assert.Contains(Allowed, values);
    }

    [SkippableFact]
    public async Task AnUnknownOriginGetsNothing()
    {
        RequireDatabase();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/healthz");
        request.Headers.Add("Origin", "https://not-the-game.example");

        var response = await api.CreateClient().SendAsync(request);

        // No header means the browser blocks it, which is the whole mechanism. An
        // exact allow-list rather than a wildcard, because "no cookies today" is a
        // fact about the current design and not a promise about the next one.
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    /// <summary>
    /// Credentials mean cookies, and this API has none.
    ///
    /// Allowing them alongside a broad origin list is the classic way to turn CORS from
    /// a formality into a hole. The bearer token is attached deliberately by the game
    /// and is unaffected by this header either way.
    /// </summary>
    [SkippableFact]
    public async Task CredentialsAreNotAllowed()
    {
        RequireDatabase();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/healthz");
        request.Headers.Add("Origin", Allowed);

        var response = await api.CreateClient().SendAsync(request);

        Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
    }

    // ── Parsing, which needs no server ────────────────────────────────────────

    /// <summary>
    /// The trailing slash that costs an afternoon.
    ///
    /// An Origin header is scheme + host + port and never carries one, so a configured
    /// value with a slash matches nothing at all -- and the symptom is a browser
    /// blocking every request against a config that looks obviously correct.
    /// </summary>
    [Fact]
    public void TrailingSlashesAreTrimmed()
    {
        string[] origins = BrowserOrigins.Parse("https://play.example.com/, https://other.example/");

        Assert.Equal(new[] { "https://play.example.com", "https://other.example" }, origins);
    }

    [Fact]
    public void NothingConfiguredIsNoOrigins()
    {
        // Not a wildcard. An unconfigured production server that silently accepted
        // every origin is a mistake nobody notices, because everything works.
        Assert.Empty(BrowserOrigins.Parse(null));
        Assert.Empty(BrowserOrigins.Parse(""));
        Assert.Empty(BrowserOrigins.Parse("  ,  , "));
    }

    /// <summary>
    /// The origin the fixture runs with. See ApiFixture, which configures it.
    /// </summary>
    private const string Allowed = "https://play.idle-explorers.test";
}
