using IdleExplorers.Api.Auth;
using IdleExplorers.Api.Endpoints;
using IdleExplorers.Api.Infrastructure;
using IdleExplorers.Api.Services;

namespace IdleExplorers.Api;

public class Program
{
    public static int Main(string[] args)
    {
        // The container's HEALTHCHECK runs the binary against itself rather than
        // shelling out to curl, which the aspnet runtime image does not ship. Adding
        // curl to a production image to ask one question is a larger attack surface
        // than answering it in six lines.
        if (args.Contains("--healthcheck")) return HealthCheck();

        Build(args).Run();
        return 0;
    }

    /// <summary>
    /// Asks the running server whether it is alive. Zero means yes.
    ///
    /// Liveness, not readiness. A container that cannot reach Postgres is still
    /// alive, and killing it would turn a database blip into a restart loop across
    /// every instance at once -- which is how a brief outage becomes a long one.
    /// </summary>
    private static int HealthCheck()
    {
        string port = Environment.GetEnvironmentVariable("PORT") ?? "8080";

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };

            HttpResponseMessage response =
                client.GetAsync($"http://127.0.0.1:{port}/healthz").GetAwaiter().GetResult();

            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception)
        {
            return 1;
        }
    }

    /// <summary>
    /// Composed in a method rather than top-level statements so the scenario tests can
    /// build the same application with a different clock and a different database.
    ///
    /// A test that boots a DIFFERENT application than production runs is a test of
    /// something nobody ships.
    /// </summary>
    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // ── Configuration ─────────────────────────────────────────────────────
        //
        // The connection string carries the database password, so it comes from the
        // environment and never from a file in the repository. In production it is a
        // Fly secret; locally it defaults to the stack `supabase start` prints, whose
        // credentials are identical on every machine and listen on loopback only.
        string connectionString =
            builder.Configuration["IDLE_EXPLORERS_DB"]
            ?? Environment.GetEnvironmentVariable("IDLE_EXPLORERS_DB")
            ?? "Host=127.0.0.1;Port=54322;Database=postgres;Username=postgres;Password=postgres";

        // ══ FIELDS, NOT JUST PROPERTIES ═══════════════════════════════════════
        //
        // System.Text.Json serialises properties and ignores FIELDS unless told
        // otherwise. Every type in the shared rules tree is made of fields, because
        // JsonUtility on the client reads nothing else -- so without this, any shared
        // type returned from an endpoint arrives as `{}`.
        //
        // Not a theoretical risk: the boss timeline shipped as an array of empty
        // objects, which is a boss that attacks with no telegraph. And it does not
        // throw, on either side. The client would simply draw nothing.
        //
        // Set once, globally, rather than per endpoint. A serialiser option that has
        // to be remembered is one that gets forgotten on the endpoint nobody tests.
        builder.Services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.IncludeFields = true);

        builder.Services.AddSingleton(new Db(connectionString));
        builder.Services.AddScoped<IGameClock, DatabaseClock>();
        builder.Services.AddSingleton<ContentCache>();
        // A SINGLETON, so the ten-second cache is process-wide. Scoped would mean one
        // query per request, which is the load the cache exists to avoid.
        builder.Services.AddSingleton<FeatureFlags>();
        builder.Services.AddScoped<Caller>();
        builder.Services.AddScoped<SettlementService>();

        builder.Services.AddSupabaseAuth(builder.Configuration);

        var app = builder.Build();

        // Fail at startup rather than on the first request. A container that boots
        // happily with unreadable content and then serves an empty catalogue is a
        // deploy that looks successful and is not.
        app.Services.GetRequiredService<ContentCache>().Load();

        // First, so it wraps everything below -- including the idempotency
        // middleware, which opens a connection of its own and can therefore be
        // the thing that fails when the pool is empty.
        app.UseMiddleware<TransientFaultMiddleware>();

        app.UseAuthentication();
        app.UseAuthorization();

        // After authentication, because the key is scoped to an account, and before
        // any endpoint, because an endpoint that runs first has already had its effect.
        app.UseMiddleware<IdempotencyMiddleware>();

        MapHealth(app);
        AccountEndpoints.Map(app);
        CharacterEndpoints.Map(app);
        ActivityEndpoints.Map(app);
        BossEndpoints.Map(app);
        EncounterEndpoints.Map(app);
        EquipmentEndpoints.Map(app);
        TelemetryEndpoints.Map(app);

        return app;
    }

    private static void MapHealth(WebApplication app)
    {
        // Liveness only: the process is up. Deliberately touches nothing, so a
        // database blip does not cause an orchestrator to kill a healthy server.
        app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

        // Readiness: should this instance take traffic?
        //
        // This one DOES hit the database, because an API that cannot reach Postgres
        // can serve nothing useful and should be pulled from rotation rather than
        // returning five hundred errors a second to players.
        app.MapGet("/readyz", async (Db db, IGameClock clock, ContentCache content) =>
        {
            try
            {
                DateTimeOffset now = await clock.NowAsync();

                return Results.Ok(new
                {
                    status         = "ready",
                    databaseTime   = now,
                    contentVersion = content.Version,
                    items          = content.Catalogue.Items.Count,
                    monsters       = content.Catalogue.Monsters.Count,
                    recipes        = content.Catalogue.CraftRecipes.Count,
                });
            }
            catch (Exception e)
            {
                return Results.Problem(
                    title:      "not ready",
                    detail:     e.Message,
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });
    }
}
