using System.Linq;
using IdleExplorers.Api.Auth;
using IdleExplorers.Api.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace IdleExplorers.Api.Endpoints;

/// <summary>
/// The bootstrap call, and the account behind it.
/// </summary>
public static class AccountEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/account").RequireAuthorization();

        // ── Bootstrap ─────────────────────────────────────────────────────────
        //
        // The first call any client makes. Everything it needs to render a character
        // select screen, and nothing it needs to be believed about.
        group.MapGet("/", async (HttpContext http, Caller caller, Db db, ContentCache content,
                                 FeatureFlags flags) =>
        {
            Guid? accountId = await caller.AccountIdAsync(http.User, http.RequestAborted);
            if (accountId is null) return Results.Unauthorized();

            await using var connection = await db.OpenAsync(http.RequestAborted);

            string displayName = await connection.ScalarAsync<string>(
                "select display_name from account where id = $1;",
                null, accountId.Value) ?? "";

            long accountXp = await connection.ScalarAsync<long>(
                "select account_xp from account where id = $1;",
                null, accountId.Value);

            var characters = new List<object>();

            await using (var command = connection.Sql(
                """
                select id, name, class_id, xp, last_map_id, created_at
                  from character
                 where account_id = $1 and deleted_at is null
                 order by created_at;
                """,
                null, accountId.Value))
            {
                await using var reader = await command.ExecuteReaderAsync(http.RequestAborted);

                while (await reader.ReadAsync(http.RequestAborted))
                {
                    long xp = reader.GetInt64(3);

                    characters.Add(new
                    {
                        id        = reader.GetGuid(0),
                        name      = reader.GetString(1),
                        classId   = reader.GetString(2),
                        xp,
                        // Derived, never stored. See Levelling for why.
                        level     = IdleExplorers.Rules.Levelling.CharacterLevel(xp),
                        lastMapId = reader.GetString(4),
                        createdAt = reader.GetFieldValue<DateTimeOffset>(5),
                    });
                }
            }

            var wallets = new Dictionary<string, long>();

            await using (var command = connection.Sql(
                "select currency, balance from wallet where account_id = $1;",
                null, accountId.Value))
            {
                await using var reader = await command.ExecuteReaderAsync(http.RequestAborted);

                while (await reader.ReadAsync(http.RequestAborted))
                    wallets[reader.GetString(0)] = reader.GetInt64(1);
            }

            // Both currencies are always present, at zero if untouched. A client that
            // has to distinguish "no wallet row" from "no money" will get it wrong
            // once and show a blank where a zero belongs.
            wallets.TryAdd(IdleExplorers.Rules.Currency.Coins, 0L);
            wallets.TryAdd(IdleExplorers.Rules.Currency.RelicCoins, 0L);

            return Results.Ok(new
            {
                accountId = accountId.Value,
                displayName,
                accountXp,
                characters,

                // An array, not an object keyed by currency: JsonUtility cannot
                // deserialise a Dictionary and produces an empty one without saying so.
                // See ActivityEndpoints.Stacks for the whole argument.
                wallets = wallets
                    .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => new { currency = pair.Key, balance = pair.Value })
                    .ToArray(),

                // The client ships its own copy of the twelve content files so it can
                // draw tooltips and sweep cooldowns without a round trip. This is how
                // it learns that copy is stale.
                contentVersion = content.Version,

                // ══ WHAT IS SWITCHED ON ═══════════════════════════════════════
                //
                // An array of pairs, for the same JsonUtility reason as the wallets.
                //
                // This copy is COURTESY, not enforcement: it lets the client hide a
                // disabled feature rather than showing it broken. Every flag with an
                // effect is checked again in the handler that would do the thing, so a
                // client ignoring this gets a refusal rather than a reward.
                flags = (await flags.AllAsync(http.RequestAborted))
                    .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => new { flag = pair.Key, enabled = pair.Value })
                    .ToArray(),
            });
        });

        // ── Renaming ──────────────────────────────────────────────────────────
        group.MapPost("/name", async (HttpContext http, Caller caller, Db db,
                                      [FromBody] RenameRequest request) =>
        {
            Guid? accountId = await caller.AccountIdAsync(http.User, http.RequestAborted);
            if (accountId is null) return Results.Unauthorized();

            string name = (request.DisplayName ?? "").Trim();

            // Validated here rather than trusted, and the same bounds the column
            // carries. A check constraint that only ever fires as a 500 is a check
            // nobody benefits from.
            if (name.Length is < 1 or > 24)
            {
                return Results.Problem(
                    title:      "invalid display name",
                    detail:     "Between 1 and 24 characters.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            await using var connection = await db.OpenAsync(http.RequestAborted);

            await connection.ExecuteAsync(
                "update account set display_name = $2 where id = $1;",
                null, accountId.Value, name);

            return Results.Ok(new { displayName = name });
        });
    }

    public sealed record RenameRequest(string? DisplayName);
}
