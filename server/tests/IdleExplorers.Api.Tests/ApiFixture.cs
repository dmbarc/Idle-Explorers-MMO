using System;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using IdleExplorers.Api.Auth;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using Xunit;

namespace IdleExplorers.Api.Tests;

/// <summary>
/// A running copy of the real server, and a way to be somebody in front of it.
///
/// ══ WHY IT MINTS ITS OWN TOKENS ═══════════════════════════════════════════════
///
/// Signing a JWT with the local stack's HS256 secret produces exactly what Supabase
/// Auth produces, so the tests go through the real authentication middleware rather
/// than around it. A test that stubs out auth cannot catch an auth bug, and auth is
/// the entire ownership boundary in this API.
///
/// It also means the "act as another player" tests are honest: they present a
/// perfectly valid token for a different subject, which is what an attacker with
/// their own account actually has.
/// </summary>
public sealed class ApiFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    private const string ConnectionString =
        "Host=127.0.0.1;Port=54322;Database=postgres;Username=postgres;Password=postgres;Include Error Detail=true";

    public static bool DatabaseReachable { get; } = Probe();

    public const string SkipReason =
        "No database reachable. Start the local stack with: supabase start";

    private static bool Probe()
    {
        try
        {
            using var connection = new NpgsqlConnection(ConnectionString);
            connection.Open();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public Task InitializeAsync() => Task.CompletedTask;

    async Task IAsyncLifetime.DisposeAsync()
    {
        await DisposeAsync();
    }

    /// <summary>
    /// A brand new player: an auth user, and a client already holding their token.
    ///
    /// The account row is deliberately NOT created here. Its creation on first
    /// authenticated request is behaviour worth testing rather than setup worth
    /// skipping.
    /// </summary>
    public async Task<Player> NewPlayerAsync()
    {
        var userId = Guid.NewGuid();

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                insert into auth.users (id, instance_id, aud, role, email,
                                        encrypted_password, created_at, updated_at)
                values ($1, '00000000-0000-0000-0000-000000000000',
                        'authenticated', 'authenticated', $2, '', now(), now());
                """;
            command.Parameters.AddWithValue(userId);
            command.Parameters.AddWithValue($"{userId}@test.invalid");

            await command.ExecuteNonQueryAsync();
        }

        HttpClient client = CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TokenFor(userId));

        return new Player(userId, client, this);
    }

    /// <summary>
    /// A token indistinguishable from one Supabase would issue.
    ///
    /// Only `sub` matters -- it is the only claim the server reads -- but the rest are
    /// included so a future validation change (an audience check, say) fails here
    /// rather than in production.
    /// </summary>
    public static string TokenFor(Guid userId, TimeSpan? lifetime = null)
    {
        var key         = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SupabaseAuth.LocalDevelopmentSecret));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        DateTime expires = DateTime.UtcNow.Add(lifetime ?? TimeSpan.FromHours(1));

        // Anchored to the expiry rather than to now, because a NEGATIVE lifetime is
        // how the tests build an already-expired token — and a notBefore later than
        // the expiry makes the constructor throw, which looks like a broken test
        // rather than the rejection it is meant to be checking for.
        DateTime notBefore = expires.AddHours(-1);

        var token = new JwtSecurityToken(
            issuer:             "http://127.0.0.1:54321/auth/v1",
            audience:           "authenticated",
            claims:
            [
                new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
                new Claim("role", "authenticated"),
            ],
            notBefore:          notBefore,
            expires:            expires,
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>A token signed with the wrong key. Structurally perfect, worthless.</summary>
    public static string ForgedToken(Guid userId)
    {
        var key = new SymmetricSecurityKey(
            Encoding.UTF8.GetBytes("an-attacker-guessed-this-secret-and-it-is-long-enough"));

        var token = new JwtSecurityToken(
            claims:             [new Claim(JwtRegisteredClaimNames.Sub, userId.ToString())],
            expires:            DateTime.UtcNow.AddHours(1),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public async Task<NpgsqlConnection> OpenDatabaseAsync()
    {
        var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();

        return connection;
    }

    /// <summary>Removes a test player and everything that cascades from them.</summary>
    public async Task RemoveAsync(Guid userId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "delete from auth.users where id = $1;";
        command.Parameters.AddWithValue(userId);

        await command.ExecuteNonQueryAsync();
    }
}

/// <summary>One authenticated player, and the client that speaks as them.</summary>
public sealed class Player(Guid userId, HttpClient client, ApiFixture fixture) : IAsyncDisposable
{
    public Guid       UserId { get; } = userId;
    public HttpClient Client { get; } = client;

    /// <summary>
    /// A fresh idempotency key.
    ///
    /// Every mutating request needs one, and most tests do not care what it is — only
    /// the ones deliberately reusing a key do, and those pass their own.
    /// </summary>
    public static string NewKey() => Guid.NewGuid().ToString("N");

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await fixture.RemoveAsync(UserId);
    }
}

[CollectionDefinition("api")]
public class ApiCollection : ICollectionFixture<ApiFixture> { }
