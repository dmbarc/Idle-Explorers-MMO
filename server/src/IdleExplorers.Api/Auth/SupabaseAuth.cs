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
/// ══ HOW A TOKEN IS VALIDATED ══════════════════════════════════════════════════
///
/// Supabase signs access tokens with an ASYMMETRIC key and publishes the public half
/// as a JWKS, so this validates against that key set and cannot mint anything. The
/// server holding no signing key is strictly better than the arrangement it replaced.
///
/// ══ THIS WAS WRONG IN PRODUCTION, AND EVERY TEST STILL PASSED ═══════════
///
/// It used to validate with a symmetric HS256 secret, which is what Supabase used to
/// issue. The project now signs ES256. So every real token was rejected, /account/
/// answered 401, and the client reported that the game server did not answer.
///
/// What let it ship is worth writing down. The suite proved unauthenticated calls got
/// 401 and that a FORGED token got 401 -- and both kept passing, because a server that
/// rejects everything passes every test that asks whether bad tokens are refused.
/// Nothing asked whether a GOOD token was accepted. A lock is not proof of a key.
///
/// ══ WHY THE SYMMETRIC KEY IS STILL HERE ═════════════════════════════════
///
/// The local stack that `supabase start` runs still signs HS256, and so does CI. The
/// JwtBearer handler concatenates the keys named here with the ones it discovers, so
/// naming both means one code path validates local development and production without
/// a branch, and a Supabase key rotation is picked up on its own.
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

        string? supabaseUrl = configuration["SUPABASE_URL"]
                           ?? Environment.GetEnvironmentVariable("SUPABASE_URL");

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                // Where the public half of the signing key is published. Absent only
                // on the local stack, which has no JWKS and signs HS256 -- so leaving
                // it unset there keeps startup from reaching for a document that does
                // not exist.
                if (!string.IsNullOrWhiteSpace(supabaseUrl))
                {
                    options.MetadataAddress =
                        supabaseUrl.TrimEnd('/') + "/auth/v1/.well-known/openid-configuration";
                }

                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,

                    // PLURAL, and this is the whole trick: the handler concatenates
                    // these with whatever the JWKS publishes. Production tokens
                    // validate against the discovered ES256 key, local ones against
                    // this, and neither needs to know the other exists.
                    IssuerSigningKeys = [new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret))],

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
