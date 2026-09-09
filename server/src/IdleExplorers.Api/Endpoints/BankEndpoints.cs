using IdleExplorers.Api.Auth;
using IdleExplorers.Api.Infrastructure;
using IdleExplorers.Api.Services;
using IdleExplorers.Rules;
using Microsoft.AspNetCore.Mvc;

namespace IdleExplorers.Api.Endpoints;

/// <summary>
/// The account-wide bank. Items, so Critical tier, and it had no server home at all.
///
/// ══ WHY THE ACCOUNT LOCK AND NOT THE CHARACTER LOCK ═══════════════════════════
///
/// The bank is per ACCOUNT while the bag is per character, and that asymmetry is the
/// whole point of it -- it is how the character who mined the ore hands it to the one
/// who smiths. It is therefore the one place two of a player's OWN characters can race
/// each other, logged in on two tabs.
///
/// A character lock would not serialise that: two characters take two different locks
/// and both proceed. So every operation here takes the account lock, and the character
/// side of the move happens inside it.
///
/// ══ WHY A DEPOSIT IS NOT "REMOVE THEN ADD" ════════════════════════════════════
///
/// It is, but in one transaction, and the ORDER matters: the item leaves the bag first
/// and lands in the bank second. Reversed, a failure between the two duplicates it.
/// Both directions are written that way and the transaction makes the point moot --
/// which is exactly why it is worth being deliberate about, since the day somebody
/// splits this across two calls the ordering is the only thing left.
/// </summary>
public static class BankEndpoints
{
    /// <summary>
    /// Slots in the bank.
    ///
    /// Matches the CHECK constraint on the column. Two numbers that must agree, so the
    /// constraint is the one that cannot be bypassed and this is the one that produces
    /// a readable message.
    /// </summary>
    public const int Capacity = 200;

    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/bank").RequireAuthorization();

        // ── What is in it ─────────────────────────────────────────────────────
        group.MapGet("/", async Task<IResult> (HttpContext http, Caller caller, Db db) =>
        {
            Guid? accountId = await caller.AccountIdAsync(http.User, http.RequestAborted);
            if (accountId is null) return Results.Unauthorized();

            await using var connection = await db.OpenAsync(http.RequestAborted);

            List<InventoryEntry> bank = await ReadAsync(connection, null, accountId.Value,
                                                        http.RequestAborted);

            return Results.Ok(new
            {
                capacity = Capacity,
                used     = SlotContainer.UsedSlots(bank),
                slots    = Slots(bank),
            });
        });

        // ── Bag → bank ────────────────────────────────────────────────────────
        group.MapPost("/{characterId:guid}/deposit", async Task<IResult> (
            HttpContext http, Caller caller, Db db, SettlementService settlement, IGameClock clock,
            Guid characterId, [FromBody] MoveRequest? request) =>
        {
            Guid? accountId = await caller.AccountIdAsync(http.User, http.RequestAborted);
            if (accountId is null) return Results.Unauthorized();

            if (!await caller.OwnsCharacterAsync(accountId.Value, characterId, http.RequestAborted))
                return NotYours();

            string itemId   = request?.ItemId ?? "";
            long   quantity = request?.Quantity ?? 0L;

            if (string.IsNullOrWhiteSpace(itemId) || quantity <= 0L) return NothingToMove();

            DateTimeOffset now = await clock.NowAsync(http.RequestAborted);

            return await db.InAccountTransactionAsync<IResult>(accountId.Value, async (connection, tx) =>
            {
                // Settled first, so ore the character has earned but not been paid is
                // in the bag before anybody counts it. Without this a player who banks
                // immediately after a long AFK run appears to have nothing.
                await settlement.SettleLockedAsync(connection, tx, characterId, now, http.RequestAborted);

                List<InventoryEntry> bag = await BagAsync(connection, tx, characterId, http.RequestAborted);

                long held = SlotContainer.GetQuantity(bag, itemId);

                if (held < quantity)
                {
                    return Results.Problem(
                        title:      "not enough",
                        detail:     $"You have {held:N0}, not {quantity:N0}.",
                        statusCode: StatusCodes.Status409Conflict);
                }

                List<InventoryEntry> bank = await ReadAsync(connection, tx, accountId.Value,
                                                            http.RequestAborted);

                // How much the bank can actually take. Asked BEFORE anything leaves the
                // bag, so a full bank refuses the move rather than eating half of it.
                long room = SlotContainer.FreeCapacityFor(bank, itemId);

                if (room <= 0L)
                {
                    return Results.Problem(
                        title:      "bank is full",
                        detail:     "There is no room for that.",
                        statusCode: StatusCodes.Status409Conflict);
                }

                long moved = Math.Min(quantity, room);

                // Out of the bag first. The transaction makes the order irrelevant
                // today; it is written this way so that the day somebody splits this in
                // two, the failure loses an item rather than duplicating one.
                SlotContainer.RemoveItem(bag, itemId, moved);
                SlotContainer.AddUpTo(bank, itemId, moved);

                await WriteBagAsync(connection, tx, characterId, bag);
                await WriteAsync(connection, tx, accountId.Value, bank);

                return Results.Ok(new
                {
                    itemId,
                    moved,

                    // Said out loud when it is not what was asked for. A partial move
                    // that reports success is a player who thinks they banked 4,000 ore
                    // and finds 1,200.
                    requested = quantity,
                    partial   = moved < quantity,

                    bankUsed = SlotContainer.UsedSlots(bank),
                    capacity = Capacity,
                });
            }, http.RequestAborted);
        });

        // ── Bank → bag ────────────────────────────────────────────────────────
        group.MapPost("/{characterId:guid}/withdraw", async Task<IResult> (
            HttpContext http, Caller caller, Db db, SettlementService settlement, IGameClock clock,
            Guid characterId, [FromBody] MoveRequest? request) =>
        {
            Guid? accountId = await caller.AccountIdAsync(http.User, http.RequestAborted);
            if (accountId is null) return Results.Unauthorized();

            if (!await caller.OwnsCharacterAsync(accountId.Value, characterId, http.RequestAborted))
                return NotYours();

            string itemId   = request?.ItemId ?? "";
            long   quantity = request?.Quantity ?? 0L;

            if (string.IsNullOrWhiteSpace(itemId) || quantity <= 0L) return NothingToMove();

            DateTimeOffset now = await clock.NowAsync(http.RequestAborted);

            return await db.InAccountTransactionAsync<IResult>(accountId.Value, async (connection, tx) =>
            {
                await settlement.SettleLockedAsync(connection, tx, characterId, now, http.RequestAborted);

                List<InventoryEntry> bank = await ReadAsync(connection, tx, accountId.Value,
                                                            http.RequestAborted);

                long stored = SlotContainer.GetQuantity(bank, itemId);

                if (stored < quantity)
                {
                    return Results.Problem(
                        title:      "not in the bank",
                        detail:     $"The bank holds {stored:N0}, not {quantity:N0}.",
                        statusCode: StatusCodes.Status409Conflict);
                }

                List<InventoryEntry> bag = await BagAsync(connection, tx, characterId, http.RequestAborted);

                long room = SlotContainer.FreeCapacityFor(bag, itemId);

                if (room <= 0L)
                {
                    return Results.Problem(
                        title:      "bag is full",
                        detail:     "Make room before withdrawing.",
                        statusCode: StatusCodes.Status409Conflict);
                }

                long moved = Math.Min(quantity, room);

                SlotContainer.RemoveItem(bank, itemId, moved);
                SlotContainer.AddUpTo(bag, itemId, moved);

                await WriteAsync(connection, tx, accountId.Value, bank);
                await WriteBagAsync(connection, tx, characterId, bag);

                return Results.Ok(new
                {
                    itemId,
                    moved,
                    requested = quantity,
                    partial   = moved < quantity,
                });
            }, http.RequestAborted);
        });
    }

    // ── Storage ───────────────────────────────────────────────────────────────

    private static async Task<List<InventoryEntry>> ReadAsync(Npgsql.NpgsqlConnection connection,
                                                              Npgsql.NpgsqlTransaction? tx,
                                                              Guid accountId,
                                                              CancellationToken cancellation)
    {
        var bank = new List<InventoryEntry>();

        for (int i = 0; i < Capacity; i++) bank.Add(new InventoryEntry());

        await using var command = connection.Sql(
            "select slot_index, item_id, quantity from bank_slot where account_id = $1;",
            tx, accountId);

        await using var reader = await command.ExecuteReaderAsync(cancellation);

        while (await reader.ReadAsync(cancellation))
        {
            int slot = reader.GetInt32(0);

            // Defensive despite the CHECK constraint: a row outside the range would
            // otherwise throw an IndexOutOfRange on a read, which is a 500 on a page
            // load rather than one bad slot.
            if (slot < 0 || slot >= Capacity) continue;

            bank[slot].itemId   = reader.GetString(1);
            bank[slot].quantity = reader.GetInt64(2);
        }

        return bank;
    }

    /// <summary>
    /// Rewrites the whole bank.
    ///
    /// Delete-then-insert rather than a diff. Two hundred slots is nothing, and a diff
    /// is where an off-by-one silently drops a stack -- the shape of bug that costs a
    /// player a night's mining and cannot be proved afterwards.
    /// </summary>
    private static async Task WriteAsync(Npgsql.NpgsqlConnection connection,
                                         Npgsql.NpgsqlTransaction tx,
                                         Guid accountId, List<InventoryEntry> bank)
    {
        await connection.ExecuteAsync("delete from bank_slot where account_id = $1;", tx, accountId);

        for (int i = 0; i < bank.Count; i++)
        {
            if (SlotContainer.IsEmpty(bank[i])) continue;

            await connection.ExecuteAsync(
                """
                insert into bank_slot (account_id, slot_index, item_id, quantity)
                values ($1, $2, $3, $4);
                """,
                tx, accountId, i, bank[i].itemId, bank[i].quantity);
        }
    }

    private static async Task<List<InventoryEntry>> BagAsync(Npgsql.NpgsqlConnection connection,
                                                             Npgsql.NpgsqlTransaction tx,
                                                             Guid characterId,
                                                             CancellationToken cancellation)
    {
        var bag = new List<InventoryEntry>();

        for (int i = 0; i < SettlementService.InventoryCapacity; i++) bag.Add(new InventoryEntry());

        await using var command = connection.Sql(
            "select slot_index, item_id, quantity from inventory_slot where character_id = $1;",
            tx, characterId);

        await using var reader = await command.ExecuteReaderAsync(cancellation);

        while (await reader.ReadAsync(cancellation))
        {
            int slot = reader.GetInt32(0);
            if (slot < 0 || slot >= bag.Count) continue;

            bag[slot].itemId   = reader.GetString(1);
            bag[slot].quantity = reader.GetInt64(2);
        }

        return bag;
    }

    private static async Task WriteBagAsync(Npgsql.NpgsqlConnection connection,
                                            Npgsql.NpgsqlTransaction tx,
                                            Guid characterId, List<InventoryEntry> bag)
    {
        await connection.ExecuteAsync(
            "delete from inventory_slot where character_id = $1;", tx, characterId);

        for (int i = 0; i < bag.Count; i++)
        {
            if (SlotContainer.IsEmpty(bag[i])) continue;

            await connection.ExecuteAsync(
                """
                insert into inventory_slot (character_id, slot_index, item_id, quantity)
                values ($1, $2, $3, $4);
                """,
                tx, characterId, i, bag[i].itemId, bag[i].quantity);
        }
    }

    private static object[] Slots(List<InventoryEntry> bank)
    {
        var slots = new List<object>();

        for (int i = 0; i < bank.Count; i++)
        {
            if (SlotContainer.IsEmpty(bank[i])) continue;

            slots.Add(new { slot = i, itemId = bank[i].itemId, quantity = bank[i].quantity });
        }

        return slots.ToArray();
    }

    private static IResult NothingToMove() =>
        Results.Problem(
            title:      "nothing to move",
            detail:     "Name an item and a quantity above zero.",
            statusCode: StatusCodes.Status400BadRequest);

    private static IResult NotYours() =>
        Results.Problem(
            title:      "no such character",
            detail:     "That character does not exist, or does not belong to you.",
            statusCode: StatusCodes.Status404NotFound);

    public sealed record MoveRequest(string? ItemId, long Quantity);
}
