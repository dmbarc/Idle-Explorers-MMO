using IdleExplorers.Api.Auth;
using IdleExplorers.Api.Infrastructure;
using IdleExplorers.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace IdleExplorers.Api.Endpoints;

/// <summary>
/// The shop, and the one way a relic-coin balance can currently go up.
///
/// ══ THIS IS NOT A PURCHASE ════════════════════════════════════════════════════
///
/// No store is contacted, no receipt exists, and no money moves. It grants a pack's
/// coins so the shop and everything downstream of a premium balance can be tested
/// before a payment processor exists.
///
/// It replaces a client-side stub that granted coins into a local file the player
/// owns — which was both uncheatable-in-principle nonsense and unavailable in a
/// release build, so the shop simply refused. Granting on the SERVER means the test
/// coins behave exactly like real ones will: one balance, one ledger, one authority.
///
/// ══ WHY IT CANNOT BE LEFT ON BY ACCIDENT ══════════════════════════════════════
///
/// It asks IsExplicitlyEnabledAsync, not IsEnabledAsync. Unknown flags are ON in this
/// system — correct for gameplay, catastrophic for a currency tap, where a missing
/// row would mean a fresh database or a mistyped seed quietly minting the thing other
/// people are meant to pay for. The row must exist and say true.
///
/// ══ WHY EVERY GRANT SAYS WHAT IT WAS ══════════════════════════════════════════
///
/// The ledger reason is TestGrantReason, so the day real purchasing arrives every
/// coin that was never paid for can be found with one query and removed. A test grant
/// that is indistinguishable from a purchase is a balance nobody can ever reconcile.
/// </summary>
public static class ShopEndpoints
{
    /// <summary>The flag that must exist and be true. See the migration that seeds it false.</summary>
    public const string TestGrantsFlag = "shop_test_grants";

    /// <summary>
    /// What the ledger records for an unpaid grant.
    ///
    /// Deliberately unmistakable. `select * from wallet_ledger where reason = 'shop_test_grant'`
    /// is the whole audit.
    /// </summary>
    public const string TestGrantReason = "shop_test_grant";

    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/shop").RequireAuthorization();

        group.MapPost("/test-grant", async (HttpContext http, Caller caller, Db db,
                                            ContentCache content, FeatureFlags flags,
                                            [FromBody] TestGrantRequest request) =>
        {
            Guid? accountId = await caller.AccountIdAsync(http.User, http.RequestAborted);
            if (accountId is null) return Results.Unauthorized();

            // Checked FIRST, before the pack is even looked up, so a disabled server
            // gives away nothing about what it would have granted.
            if (!await flags.IsExplicitlyEnabledAsync(TestGrantsFlag, http.RequestAborted))
            {
                return Results.Problem(
                    title:      "not available",
                    detail:     "Purchasing is not available yet.",
                    statusCode: StatusCodes.Status403Forbidden);
            }

            string packId = (request.PackId ?? "").Trim();

            RelicCoinPack? pack = content.Catalogue.GetCoinPack(packId);

            if (pack is null || pack.coins <= 0L)
            {
                return Results.Problem(
                    title:      "unknown pack",
                    detail:     $"There is no coin pack '{packId}'.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            long balance = await db.InAccountTransactionAsync(accountId.Value,
                async (connection, tx) =>
                {
                    await SettlementService.CreditWalletAsync(
                        // No character: a pack belongs to the ACCOUNT, and relic coins
                        // are account-wide. Attributing it to whoever happened to be
                        // logged in would put a character in the audit trail that had
                        // nothing to do with it.
                        connection, tx, accountId.Value, null,
                        IdleExplorers.Rules.Currency.RelicCoins, pack.coins,
                        TestGrantReason, http.RequestAborted);

                    return await connection.ScalarAsync<long>(
                        "select balance from wallet where account_id = $1 and currency = $2;",
                        tx, accountId.Value, IdleExplorers.Rules.Currency.RelicCoins);
                },
                http.RequestAborted);

            return Results.Ok(new
            {
                packId,
                granted  = pack.coins,
                currency = IdleExplorers.Rules.Currency.RelicCoins,
                balance,

                // Said out loud in the response as well as the ledger, so a client
                // showing a receipt cannot accidentally present this as a purchase.
                paid     = false,
            });
        });
    }

    public sealed record TestGrantRequest(string? PackId);
}
