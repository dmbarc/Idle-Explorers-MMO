namespace IdleExplorers.Api.Infrastructure;

/// <summary>
/// Which web pages the browser will let talk to this API.
///
/// ══ WHAT CORS IS AND IS NOT DOING HERE ════════════════════════════════════════
///
/// It is NOT the security boundary. Every request carries a bearer token in a header
/// the page has to attach deliberately, and there are no cookies -- so there is no
/// ambient credential a hostile page could ride on. An attacker who steals a token
/// does not need a browser at all, and CORS never sees them.
///
/// What CORS actually is here is a CORRECTNESS requirement: without it the browser
/// refuses the game's own requests, and a WebGL build cannot talk to its server at
/// all. The API answered a preflight with 405 before this existed, which would have
/// presented as every single call failing the moment the game left the editor.
///
/// It is also defence in depth worth having. The allow-list is exact rather than a
/// wildcard, because "no cookies today" is a fact about the current design and not a
/// promise about the next one.
///
/// ══ WHY THE EDITOR DOES NOT NEED THIS ═════════════════════════════════════════
///
/// UnityWebRequest outside WebGL is not a browser and does not enforce CORS at all.
/// That asymmetry is worth stating, because it means the editor will keep working
/// perfectly while a web build is comprehensively broken -- and somebody will conclude
/// the server is fine.
/// </summary>
public static class BrowserOrigins
{
    public const string Policy = "game";

    /// <summary>
    /// Comma-separated origins, from configuration.
    ///
    /// An origin is scheme + host + port and nothing else: "https://play.example.com",
    /// never a trailing slash and never a path. A trailing slash silently matches
    /// nothing, which is the single most common way to spend an afternoon on this.
    /// </summary>
    public const string Setting = "IDLE_EXPLORERS_ORIGINS";

    /// <summary>
    /// Where a local WebGL build is usually served from.
    ///
    /// Unity's own "Build and Run" serves on 60606 or a nearby port, and people run
    /// python -m http.server on 8000. Allowed only OUTSIDE production, so a deployed
    /// server never trusts a page on somebody's laptop.
    /// </summary>
    public static readonly string[] LocalDevelopment =
    {
        "http://localhost:8000",  "http://127.0.0.1:8000",
        "http://localhost:60606", "http://127.0.0.1:60606",
    };

    public static void AddGameCors(this IServiceCollection services,
                                   IConfiguration configuration, bool isProduction)
    {
        string[] origins = Parse(configuration[Setting]
                                 ?? Environment.GetEnvironmentVariable(Setting));

        if (!isProduction) origins = [.. origins, .. LocalDevelopment];

        services.AddCors(options => options.AddPolicy(Policy, policy =>
        {
            if (origins.Length == 0)
            {
                // ══ NO ORIGINS MEANS NO BROWSER ACCESS ════════════════════════
                //
                // Deliberately not a wildcard. An unconfigured production server that
                // silently accepted every origin would be a mistake nobody notices,
                // because everything works -- and the failure mode of getting this
                // wrong in the permissive direction is invisible.
                //
                // With none configured, non-browser clients (the editor, the load sim,
                // curl) keep working exactly as before. Only web pages are refused,
                // and the startup log says so.
                return;
            }

            policy.WithOrigins(origins)
                  .AllowAnyHeader()
                  .AllowAnyMethod()

                  // The Idempotency-Key round trip: a browser cannot read a response
                  // header it was not told about, and a client that cannot see its own
                  // replay marker cannot tell a replay from a fresh result.
                  .WithExposedHeaders("Idempotency-Key", "Retry-After");

            // NOT AllowCredentials. Credentials mean cookies, this API has none, and
            // combining them with a broad origin list is the classic way to turn CORS
            // from a formality into a hole. The bearer token is attached explicitly by
            // the game and is unaffected.
        }));
    }

    /// <summary>Splits and tidies, dropping the trailing slashes people always add.</summary>
    public static string[] Parse(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured)) return [];

        var origins = new List<string>();

        foreach (string part in configured.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            string origin = part.Trim().TrimEnd('/');

            if (origin.Length > 0) origins.Add(origin);
        }

        return origins.ToArray();
    }
}
