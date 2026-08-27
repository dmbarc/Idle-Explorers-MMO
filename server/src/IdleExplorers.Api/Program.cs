using IdleExplorers.Api.Auth;
using IdleExplorers.Api.Endpoints;
using IdleExplorers.Api.Infrastructure;
using IdleExplorers.Api.Services;

namespace IdleExplorers.Api;

public class Program
{
    public static void Main(string[] args) => Build(args).Run();

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

        builder.Services.AddSingleton(new Db(connectionString));
        builder.Services.AddSingleton<IGameClock, DatabaseClock>();
        builder.Services.AddSingleton<ContentCache>();
        builder.Services.AddScoped<Caller>();
        builder.Services.AddScoped<SettlementService>();

        builder.Services.AddSupabaseAuth(builder.Configuration);

        var app = builder.Build();

        // Fail at startup rather than on the first request. A container that boots
        // happily with unreadable content and then serves an empty catalogue is a
        // deploy that looks successful and is not.
        app.Services.GetRequiredService<ContentCache>().Load();

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
