using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace IdleExplorers.Api.Auth;

/// <summary>
/// Who is calling.
///
/// ══ WHAT THE SERVER TRUSTS FROM A REQUEST ═════════════════════════════════════
///
/// Exactly one thing: a signed token, and only the subject inside it. Not an account
/// id in the body, not a character id in a header, not a display name. Everything
/// else about the caller is looked up from that subject.
///
/// This is the whole boundary. Every "act on another character's id" attack is
/// stopped here or nowhere — and the way it is stopped is by never reading identity
/// from anywhere a client can write.
///
/// ══ WHY THE SYMMETRIC KEY ═════════════════════════════════════════════════════
///
/// Supabase signs its access tokens with a shared HS256 secret, so this validates
/// against the same secret. It therefore also has the power to MINT tokens, which is
/// exactly why it lives in server configuration and never in a Unity build.
///
/// TODO(Phase 7): Supabase is migrating to asymmetric JWTs with a published JWKS. At
/// that point this becomes a key-set URL and the server loses the ability to mint,
/// which is strictly better. The seam is here.
/// </summary>
public static class SupabaseAuth
{
    /// <summary>
    /// The local stack's JWT secret, as `supabase start` prints it.
    ///
    /// Identical on every Supabase CLI install, which is what makes it safe to write
    /// down and useless to an attacker: it authenticates nothing but a container on
    /// this machine's loopback. Production supplies its own through configuration and
    /// startup refuses without one.
    /// </summary>
    public const string LocalDevelopmentSecret =
        "super-secret-jwt-token-with-at-least-32-characters-long";

    public static void AddSupabaseAuth(this IServiceCollection services, IConfiguration configuration)
    {
        string secret = configuration["SUPABASE_JWT_SECRET"]
                     ?? Environment.GetEnvironmentVariable("SUPABASE_JWT_SECRET")
                     ?? LocalDevelopmentSecret;

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey         = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)),

                    // Supabase issues tokens for its own project URL, which differs
                    // between local, staging and production. The signature is what
                    // authenticates them; the issuer string would only add a value to
                    // keep in step across three environments.
                    ValidateIssuer   = false,
                    ValidateAudience = false,

                    ValidateLifetime = true,

                    // Default is five minutes, which on an expiring access token means
                    // five minutes of accepting one that is dead. There is no clock
                    // skew worth tolerating between Supabase and an API that reads its
                    // time from the same database.
                    ClockSkew = TimeSpan.Zero,
                };
            });

        services.AddAuthorization();
    }

    /// <summary>
    /// The Supabase user id from the token, or null when unauthenticated.
    ///
    /// `sub` is the only claim read. A token could carry a hundred others and none of
    /// them mean anything here.
    /// </summary>
    public static Guid? SupabaseUserId(this ClaimsPrincipal? principal)
    {
        string? subject = principal?.FindFirstValue(ClaimTypes.NameIdentifier)
                       ?? principal?.FindFirstValue("sub");

        return Guid.TryParse(subject, out Guid id) ? id : null;
    }
}
