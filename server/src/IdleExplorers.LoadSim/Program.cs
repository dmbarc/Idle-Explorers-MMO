using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using IdleExplorers.Api.Auth;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

namespace IdleExplorers.LoadSim;

/// <summary>
/// N simulated players, doing what players do, against a running server.
///
/// ══ WHAT THIS IS FOR ══════════════════════════════════════════════════════════
///
/// Not the arithmetic — the rules suite covers that in milliseconds. This finds the
/// things that only exist under contention, and every one of them looks like "the
/// database is down" rather than like the bug it is:
///
///   · settlement queuing on the character-row lock
///   · connection pool exhaustion — Supabase allows 60 direct and 200 pooled, and an
///     API that opens one per request dies somewhere around fifty players
///   · transaction conflicts between live requests and any background sweep
///
/// ══ HOW TO READ THE OUTPUT ════════════════════════════════════════════════════
///
/// p99 is the number that matters, not the mean. An idle game where one request in a
/// hundred takes four seconds is a game where every player sees a four-second stall
/// several times an hour, and the mean will look excellent throughout.
///
///   dotnet run --project server/src/IdleExplorers.LoadSim -- --players 100
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var options = Options.Parse(args);

        Console.WriteLine($"Idle Explorers — load simulation");
        Console.WriteLine($"  target   {options.BaseUrl}");
        Console.WriteLine($"  players  {options.Players}");
        Console.WriteLine($"  duration {options.Seconds}s");
        Console.WriteLine();

        if (!await Reachable(options.BaseUrl))
        {
            Console.Error.WriteLine($"Nothing answering at {options.BaseUrl}.");
            Console.Error.WriteLine("Start the server first:");
            Console.Error.WriteLine("  dotnet run --project server/src/IdleExplorers.Api");
            return 1;
        }

        var results = new Results();
        var players = new List<SimulatedPlayer>();

        Console.Write("creating players");

        for (int i = 0; i < options.Players; i++)
        {
            players.Add(await SimulatedPlayer.CreateAsync(options.BaseUrl, results));
            if (i % 10 == 0) Console.Write(".");
        }

        Console.WriteLine($" {players.Count} ready");
        Console.WriteLine();

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(options.Seconds));
        var clock = Stopwatch.StartNew();

        // Started together deliberately. A staggered ramp is gentler and hides exactly
        // the lock contention this is looking for.
        var running = players.Select(p => p.PlayAsync(stop.Token)).ToArray();

        var reporter = ReportEvery(TimeSpan.FromSeconds(5), results, clock, stop.Token);

        await Task.WhenAll(running);
        await reporter;

        Console.WriteLine();
        results.PrintSummary(clock.Elapsed);

        foreach (var player in players) await player.DisposeAsync();

        return results.Verdict();
    }

    private static async Task<bool> Reachable(string baseUrl)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var response = await client.GetAsync($"{baseUrl}/readyz");

            return response.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static async Task ReportEvery(TimeSpan interval, Results results,
                                          Stopwatch clock, CancellationToken cancellation)
    {
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                await Task.Delay(interval, cancellation);
                results.PrintTick(clock.Elapsed);
            }
        }
        catch (OperationCanceledException)
        {
            // The run ended. Nothing to report about that.
        }
    }
}

/// <summary>Command line, with defaults that are useful on their own.</summary>
public sealed record Options(string BaseUrl, int Players, int Seconds)
{
    public static Options Parse(string[] args)
    {
        string baseUrl = "http://127.0.0.1:5199";
        int    players = 10;
        int    seconds = 30;

        for (int i = 0; i < args.Length - 1; i++)
        {
            switch (args[i])
            {
                case "--url":      baseUrl = args[i + 1]; break;
                case "--players":  players = int.Parse(args[i + 1]); break;
                case "--seconds":  seconds = int.Parse(args[i + 1]); break;
            }
        }

        return new Options(baseUrl, Math.Max(1, players), Math.Max(1, seconds));
    }
}

/// <summary>
/// One player, looping the way a real client does.
///
/// The loop is deliberately the real one rather than a hot request flood: heartbeat
/// every twenty seconds, settle every few, occasionally switch what they are doing.
/// A flood measures how fast the server can refuse work; this measures whether it
/// stays pleasant while a hundred people play.
/// </summary>
public sealed class SimulatedPlayer : IAsyncDisposable
{
    private const string ConnectionString =
        "Host=127.0.0.1;Port=54322;Database=postgres;Username=postgres;Password=postgres";

    private static readonly string[] Nodes = ["tin_rock_1", "copper_rock_1", "oak_tree_1"];

    private readonly HttpClient _client;
    private readonly Results    _results;
    private readonly Guid       _userId;
    private readonly Random     _random;

    private Guid _characterId;

    private SimulatedPlayer(HttpClient client, Results results, Guid userId, int seed)
    {
        _client  = client;
        _results = results;
        _userId  = userId;
        _random  = new Random(seed);
    }

    public static async Task<SimulatedPlayer> CreateAsync(string baseUrl, Results results)
    {
        var userId = Guid.NewGuid();

        await using (var connection = new NpgsqlConnection(ConnectionString))
        {
            await connection.OpenAsync();

            await using var command = connection.CreateCommand();
            command.CommandText = """
                insert into auth.users (id, instance_id, aud, role, email,
                                        encrypted_password, created_at, updated_at)
                values ($1, '00000000-0000-0000-0000-000000000000',
                        'authenticated', 'authenticated', $2, '', now(), now());
                """;
            command.Parameters.AddWithValue(userId);
            command.Parameters.AddWithValue($"{userId}@loadsim.invalid");

            await command.ExecuteNonQueryAsync();
        }

        var client = new HttpClient
        {
            BaseAddress = new Uri(baseUrl),
            Timeout     = TimeSpan.FromSeconds(30),
        };
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", TokenFor(userId));

        var player = new SimulatedPlayer(client, results, userId, userId.GetHashCode());

        await player.SetUpAsync();
        return player;
    }

    private async Task SetUpAsync()
    {
        var created = await Post("/character/", new { name = $"Sim{_random.Next(100000, 999999)}" });

        if (created is null) return;

        using var body = System.Text.Json.JsonDocument.Parse(created);
        _characterId = body.RootElement.GetProperty("characterId").GetGuid();

        await Post($"/activity/{_characterId}", new { nodeId = Nodes[_random.Next(Nodes.Length)] });
    }

    public async Task PlayAsync(CancellationToken cancellation)
    {
        // Offset so a hundred players do not all beat on the same tick. Real clients
        // are spread out, and a synchronised herd measures a thundering herd.
        await Task.Delay(_random.Next(0, 2000), CancellationToken.None);

        var lastBeat = DateTimeOffset.UtcNow;

        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                if (DateTimeOffset.UtcNow - lastBeat > TimeSpan.FromSeconds(20))
                {
                    await Post($"/activity/{_characterId}/beat", new { });
                    lastBeat = DateTimeOffset.UtcNow;
                }

                await Post($"/activity/{_characterId}/settle", new { });
                await Get($"/character/{_characterId}");

                // One in twenty switches node, which is what makes the settle-then-
                // replace path show up under load rather than only in tests.
                if (_random.Next(20) == 0)
                    await Post($"/activity/{_characterId}", new { nodeId = Nodes[_random.Next(Nodes.Length)] });

                if (_random.Next(10) == 0)
                    await Get("/account/");

                await Task.Delay(_random.Next(400, 1200), cancellation);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                _results.Record("exception", TimeSpan.Zero, false, e.GetType().Name);
            }
        }
    }

    private async Task<string?> Post(string path, object body)
    {
        var clock = Stopwatch.StartNew();

        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));

        using var response = await _client.SendAsync(request);
        clock.Stop();

        _results.Record(path, clock.Elapsed, response.IsSuccessStatusCode,
                        ((int)response.StatusCode).ToString());

        return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync() : null;
    }

    private async Task Get(string path)
    {
        var clock = Stopwatch.StartNew();

        using var response = await _client.GetAsync(path);
        clock.Stop();

        _results.Record(path, clock.Elapsed, response.IsSuccessStatusCode,
                        ((int)response.StatusCode).ToString());
    }

    private static string TokenFor(Guid userId)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SupabaseAuth.LocalDevelopmentSecret));

        var token = new JwtSecurityToken(
            claims:             [new Claim(JwtRegisteredClaimNames.Sub, userId.ToString())],
            expires:            DateTime.UtcNow.AddHours(4),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "delete from auth.users where id = $1;";
        command.Parameters.AddWithValue(_userId);

        await command.ExecuteNonQueryAsync();
    }
}
