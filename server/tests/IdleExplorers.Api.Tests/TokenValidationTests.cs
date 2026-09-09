#nullable enable

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using IdleExplorers.Api.Auth;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace IdleExplorers.Api.Tests;

/// <summary>
/// Whether the server accepts the tokens it is actually sent.
///
/// THE BUG THIS EXISTS FOR
///
/// The API validated with a symmetric HS256 secret. The Supabase project signs ES256
/// and publishes the public half as a JWKS. So every real token was refused, /account/
/// answered 401, and the game reported that the server did not answer -- while the
/// whole suite stayed green.
///
/// It stayed green because every authentication test asked the same question. Does an
/// unauthenticated call get 401? Does a FORGED token get 401? Does an EXPIRED token get
/// 401? A server that refuses everything answers yes to all three. Nothing anywhere
/// asked whether a valid token was let in, so nothing noticed when none were.
///
/// A lock is not proof of a key. These tests are the key half.
/// </summary>
[Collection("api")]
public class TokenValidationTests(ApiFixture api)
{
    private static void RequireDatabase() => Skip.IfNot(ApiFixture.DatabaseReachable, ApiFixture.SkipReason);

    /// <summary>
    /// The missing half: a good token is ACCEPTED.
    ///
    /// Trivial to write and trivial to pass, which is exactly why its absence went
    /// unnoticed for so long -- every neighbouring test looked like thorough auth
    /// coverage.
    /// </summary>
    [SkippableFact]
    public async Task A_valid_token_is_accepted()
    {
        RequireDatabase();

        var player = await api.NewPlayerAsync();

        HttpResponseMessage response = await player.Client.GetAsync("/account/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// And the same call with no token at all is refused, in the same test run.
    ///
    /// Paired deliberately. Either assertion alone can pass on a broken server: the
    /// refusal passes when everything is refused, the acceptance passes when nothing
    /// is checked. Only together do they say authentication works.
    /// </summary>
    [SkippableFact]
    public async Task The_same_call_without_a_token_is_refused()
    {
        RequireDatabase();

        HttpClient anonymous = api.CreateClient();

        HttpResponseMessage response = await anonymous.GetAsync("/account/");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// A project URL makes the server discover the published key set.
    ///
    /// This is the configuration half, and it is checked rather than assumed because
    /// nothing else can catch it: the test suite signs HS256, so a server configured
    /// for HS256 only passes every functional test here and fails against the real
    /// Supabase project. The algorithms differ only in production.
    /// </summary>
    [Fact]
    public void A_project_url_configures_key_discovery()
    {
        JwtBearerOptions options = OptionsFor(new Dictionary<string, string?>
        {
            ["SUPABASE_URL"]        = "https://example-project.supabase.co",
            ["SUPABASE_JWT_SECRET"] = SupabaseAuth.LocalDevelopmentSecret,
        });

        Assert.Equal(
            "https://example-project.supabase.co/auth/v1/.well-known/openid-configuration",
            options.MetadataAddress);
    }

    /// <summary>
    /// A trailing slash on the project URL does not produce a double slash.
    ///
    /// A pasted Supabase URL has one about half the time, and the resulting metadata
    /// address 404s -- which surfaces as every token being rejected, the same
    /// indistinguishable symptom as the original bug.
    /// </summary>
    [Fact]
    public void A_trailing_slash_is_tolerated()
    {
        JwtBearerOptions options = OptionsFor(new Dictionary<string, string?>
        {
            ["SUPABASE_URL"] = "https://example-project.supabase.co/",
        });

        Assert.Equal(
            "https://example-project.supabase.co/auth/v1/.well-known/openid-configuration",
            options.MetadataAddress);
    }

    /// <summary>
    /// With no project URL there is no discovery, and the symmetric key stands alone.
    ///
    /// That is the local stack, which has no JWKS. Reaching for one would fail startup
    /// on a machine that is working perfectly.
    /// </summary>
    [Fact]
    public void Without_a_project_url_nothing_is_discovered()
    {
        JwtBearerOptions options = OptionsFor(new Dictionary<string, string?>());

        Assert.True(string.IsNullOrEmpty(options.MetadataAddress));
        Assert.NotEmpty(options.TokenValidationParameters.IssuerSigningKeys);
    }

    /// <summary>
    /// The symmetric key survives alongside discovery.
    ///
    /// Both are named at once on purpose: the handler concatenates the keys configured
    /// here with the ones it fetches, so local development and production validate
    /// through one code path. Dropping the symmetric key would break every test in this
    /// suite, which is a loud enough failure -- but dropping it in a way that only bites
    /// when a JWKS is also configured would not be, so it is asserted together.
    /// </summary>
    [Fact]
    public void Discovery_does_not_displace_the_symmetric_key()
    {
        JwtBearerOptions options = OptionsFor(new Dictionary<string, string?>
        {
            ["SUPABASE_URL"]        = "https://example-project.supabase.co",
            ["SUPABASE_JWT_SECRET"] = SupabaseAuth.LocalDevelopmentSecret,
        });

        Assert.NotEmpty(options.TokenValidationParameters.IssuerSigningKeys);
    }

    private static JwtBearerOptions OptionsFor(Dictionary<string, string?> settings)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        var services = new ServiceCollection();

        services.AddLogging();
        services.AddSupabaseAuth(configuration);

        return services.BuildServiceProvider()
                       .GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
                       .Get(JwtBearerDefaults.AuthenticationScheme);
    }
}
