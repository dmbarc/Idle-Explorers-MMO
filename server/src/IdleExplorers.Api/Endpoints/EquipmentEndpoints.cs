using IdleExplorers.Api.Auth;
using IdleExplorers.Api.Infrastructure;
using IdleExplorers.Api.Services;
using IdleExplorers.Rules;
using Microsoft.AspNetCore.Mvc;

namespace IdleExplorers.Api.Endpoints;

/// <summary>
/// Putting things on and taking them off.
///
/// ══ WHY EQUIPMENT IS SERVER-OWNED AT ALL ══════════════════════════════════════
///
/// It looks presentational and is not. Worn gear decides damage, which decides how
/// fast a character farms, which decides everything downstream — so a client that
/// could claim to be wearing the Goblin Destroyer would be a client that decides its
/// own kill rate. Equipment is the quiet path into the economy.
///
/// ══ NOTHING HERE IS A SLOT SHUFFLE ════════════════════════════════════════════
///
/// Moving an item between inventory slots is not an endpoint and never will be. It
/// is presentation: the player rearranges a grid, the server stores where things
/// ended up on the next write, and nobody gains anything by lying about it. Only
/// crossing the boundary between bag and body is worth a round trip.
/// </summary>
public static class EquipmentEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/equipment").RequireAuthorization();

        // ── Put it on ─────────────────────────────────────────────────────────
        group.MapPost("/{characterId:guid}/equip", async (HttpContext http, Caller caller, Db db,
                                                          ContentCache content, IGameClock clock,
                                                          SettlementService settlement,
                                                          Guid characterId,
                                                          [FromBody] EquipRequest request) =>
        {
            Guid? accountId = await caller.AccountIdAsync(http.User, http.RequestAborted);
            if (accountId is null) return Results.Unauthorized();

            if (!await caller.OwnsCharacterAsync(accountId.Value, characterId, http.RequestAborted))
                return NotYours();

            ItemData? item = content.Catalogue.GetItem(request.ItemId);

            if (item is null)
            {
                return Results.Problem(
                    title: "unknown item", detail: $"There is no item '{request.ItemId}'.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            if (!item.IsEquippable)
            {
                return Results.Problem(
                    title: "not equippable", detail: $"'{item.DisplayName}' cannot be worn.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            DateTimeOffset now = await clock.NowAsync(http.RequestAborted);

            return await db.InCharacterTransactionAsync(characterId, async (connection, tx) =>
            {
                // Settled first, because a gather still in flight might be delivering
                // the very item being equipped -- and refusing it as "not in your bag"
                // when the server is about to put it there is maddening.
                await settlement.SettleLockedAsync(connection, tx, characterId, now, http.RequestAborted);

                var bag = await ReadInventoryAsync(connection, tx, characterId, http.RequestAborted);

                if (SlotContainer.GetQuantity(bag, item.id) < 1)
                {
                    return Results.Problem(
                        title: "not in your bag", detail: $"You do not have a {item.DisplayName}.",
                        statusCode: StatusCodes.Status409Conflict);
                }

                // The level gate, checked here rather than trusted. levelReq is
                // measured against the item's own sourceSkill, which is what the
                // number was authored to mean.
                if (item.levelReq > 0 && !string.IsNullOrEmpty(item.sourceSkill))
                {
                    long skillXp = await connection.ScalarAsync<long>(
                        "select xp from character_skill where character_id = $1 and skill_id = $2;",
                        tx, characterId, item.sourceSkill);

                    int level = Levelling.SkillLevel(skillXp);

                    if (level < item.levelReq)
                    {
                        return Results.Problem(
                            title:      "level too low",
                            detail:     $"'{item.DisplayName}' needs {item.sourceSkill} {item.levelReq}; you are {level}.",
                            statusCode: StatusCodes.Status409Conflict);
                    }
                }

                var worn = await ReadEquipmentAsync(connection, tx, characterId, http.RequestAborted);

                string? slotId = ChooseSlot(item, request.SlotId, worn);

                if (slotId is null)
                {
                    return Results.Problem(
                        title: "no free slot", detail: $"Nowhere to put a {item.DisplayName}.",
                        statusCode: StatusCodes.Status409Conflict);
                }

                // ── The two-handed rule, both directions ──────────────────────
                ItemData? mainHand = worn.TryGetValue("mainhand", out string? held)
                    ? content.Catalogue.GetItem(held)
                    : null;

                if (slotId == "offhand" && !WeaponProfile.CanHoldOffHand(mainHand))
                {
                    return Results.Problem(
                        title: "both hands full", detail: "Both your hands are on that weapon.",
                        statusCode: StatusCodes.Status409Conflict);
                }

                var displaced = new List<string>();

                if (worn.TryGetValue(slotId, out string? already) && !string.IsNullOrEmpty(already))
                    displaced.Add(already);

                // A two-hander displaces the off hand as well, which is two items
                // needing room rather than one.
                if (slotId == "mainhand" && WeaponProfile.NeedsBothHands(item) &&
                    worn.TryGetValue("offhand", out string? bumped) && !string.IsNullOrEmpty(bumped))
                {
                    displaced.Add(bumped);
                }

                // Everything checked before anything moves. A partial equip that
                // stranded a shield is an item destroyed.
                SlotContainer.RemoveItem(bag, item.id, 1);

                foreach (string returning in displaced)
                {
                    if (SlotContainer.AddItem(bag, returning, 1)) continue;

                    return Results.Problem(
                        title:      "no room",
                        detail:     "No room for what you would be taking off.",
                        statusCode: StatusCodes.Status409Conflict);
                }

                foreach (string slot in displaced.Count > 0 ? SlotsOf(worn, displaced) : [])
                    await connection.ExecuteAsync(
                        "delete from equipment where character_id = $1 and slot_id = $2;",
                        tx, characterId, slot);

                await connection.ExecuteAsync(
                    """
                    insert into equipment (character_id, slot_id, item_id, durability)
                    values ($1, $2, $3, $4)
                    on conflict (character_id, slot_id) do update
                       set item_id = excluded.item_id, durability = excluded.durability;
                    """,
                    tx, characterId, slotId, item.id, await RestoreDurabilityAsync(
                        connection, tx, characterId, item, http.RequestAborted));

                await WriteInventoryAsync(connection, tx, characterId, bag, http.RequestAborted);

                return Results.Ok(new
                {
                    slotId,
                    itemId    = item.id,
                    displaced = displaced.ToArray(),
                });
            }, http.RequestAborted);
        });

        // ── Take it off ───────────────────────────────────────────────────────
        group.MapPost("/{characterId:guid}/unequip", async (HttpContext http, Caller caller, Db db,
                                                            IGameClock clock, SettlementService settlement,
                                                            Guid characterId,
                                                            [FromBody] UnequipRequest request) =>
        {
            Guid? accountId = await caller.AccountIdAsync(http.User, http.RequestAborted);
            if (accountId is null) return Results.Unauthorized();

            if (!await caller.OwnsCharacterAsync(accountId.Value, characterId, http.RequestAborted))
                return NotYours();

            string slotId = request.SlotId ?? "";

            if (!IsKnownSlot(slotId))
            {
                return Results.Problem(
                    title: "unknown slot", detail: $"There is no slot '{slotId}'.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            DateTimeOffset now = await clock.NowAsync(http.RequestAborted);

            return await db.InCharacterTransactionAsync(characterId, async (connection, tx) =>
            {
                await settlement.SettleLockedAsync(connection, tx, characterId, now, http.RequestAborted);

                string? itemId = await connection.ScalarAsync<string>(
                    "select item_id from equipment where character_id = $1 and slot_id = $2;",
                    tx, characterId, slotId);

                if (string.IsNullOrEmpty(itemId))
                    return Results.Ok(new { slotId, itemId = "", removed = false });

                var bag = await ReadInventoryAsync(connection, tx, characterId, http.RequestAborted);

                if (!SlotContainer.AddItem(bag, itemId, 1))
                {
                    return Results.Problem(
                        title: "bag full", detail: "No room to take that off.",
                        statusCode: StatusCodes.Status409Conflict);
                }

                // Condition is remembered, or taking a helmet off and putting it back
                // on is a free repair and durability becomes theatre.
                int durability = await connection.ScalarAsync<int>(
                    "select durability from equipment where character_id = $1 and slot_id = $2;",
                    tx, characterId, slotId);

                await connection.ExecuteAsync(
                    """
                    insert into stored_durability (character_id, item_id, durability)
                    values ($1, $2, $3)
                    on conflict (character_id, item_id) do update set durability = excluded.durability;
                    """,
                    tx, characterId, itemId, durability);

                await connection.ExecuteAsync(
                    "delete from equipment where character_id = $1 and slot_id = $2;",
                    tx, characterId, slotId);

                await WriteInventoryAsync(connection, tx, characterId, bag, http.RequestAborted);

                return Results.Ok(new { slotId, itemId, removed = true });
            }, http.RequestAborted);
        });
    }

    // ── Slots ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Where this item goes: the one asked for, or the first free one in its family.
    ///
    /// An item declares a FAMILY -- "ring" -- and the paperdoll has ring1 through
    /// ring10. Treating the declared value as a literal slot id is the exact mistake
    /// that made thrown rings unrecoverable on the client, so it is not repeated here.
    /// </summary>
    private static string? ChooseSlot(ItemData item, string? requested,
                                      Dictionary<string, string> worn)
    {
        if (!string.IsNullOrEmpty(requested))
        {
            // A requested slot still has to be one this item could go in, or a client
            // could put a helmet in the main hand and inherit a weapon's damage.
            return SlotsInFamily(item.equipSlot).Contains(requested) ? requested : null;
        }

        string? firstFree = null;

        foreach (string slot in SlotsInFamily(item.equipSlot))
        {
            if (worn.TryGetValue(slot, out string? occupant) && !string.IsNullOrEmpty(occupant)) continue;

            firstFree = slot;
            break;
        }

        // Nothing free: displace whatever is in the first slot of the family, which
        // is what a player clicking "equip" with ten rings on expects.
        return firstFree ?? SlotsInFamily(item.equipSlot).FirstOrDefault();
    }

    private static IEnumerable<string> SlotsInFamily(string declared)
    {
        if (string.IsNullOrEmpty(declared)) yield break;

        foreach (string slot in GameContent.KnownSlotIds)
            if (slot == declared || slot.StartsWith(declared, StringComparison.Ordinal))
                yield return slot;
    }

    private static bool IsKnownSlot(string slotId) =>
        !string.IsNullOrEmpty(slotId) && GameContent.KnownSlotIds.Contains(slotId);

    private static IEnumerable<string> SlotsOf(Dictionary<string, string> worn, List<string> itemIds)
    {
        foreach (var pair in worn)
            if (itemIds.Contains(pair.Value)) yield return pair.Key;
    }

    // ── Storage ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The condition this piece was last in, or full for one never worn.
    ///
    /// Zero would mean a brand new item arrives broken, which for a boss drop is a
    /// memorable first impression of the wrong kind.
    /// </summary>
    private static async Task<int> RestoreDurabilityAsync(
        Npgsql.NpgsqlConnection connection, Npgsql.NpgsqlTransaction tx,
        Guid characterId, ItemData item, CancellationToken cancellation)
    {
        if (!item.HasDurability) return 0;

        int? stored = await connection.ScalarAsync<int?>(
            "select durability from stored_durability where character_id = $1 and item_id = $2;",
            tx, characterId, item.id);

        return stored ?? item.maxDurability;
    }

    private static async Task<Dictionary<string, string>> ReadEquipmentAsync(
        Npgsql.NpgsqlConnection connection, Npgsql.NpgsqlTransaction tx,
        Guid characterId, CancellationToken cancellation)
    {
        var worn = new Dictionary<string, string>();

        await using var command = connection.Sql(
            "select slot_id, item_id from equipment where character_id = $1;", tx, characterId);

        await using var reader = await command.ExecuteReaderAsync(cancellation);

        while (await reader.ReadAsync(cancellation))
            worn[reader.GetString(0)] = reader.GetString(1);

        return worn;
    }

    private static async Task<List<InventoryEntry>> ReadInventoryAsync(
        Npgsql.NpgsqlConnection connection, Npgsql.NpgsqlTransaction tx,
        Guid characterId, CancellationToken cancellation)
    {
        var bag = new List<InventoryEntry>();

        for (int i = 0; i < SettlementService.InventoryCapacity; i++)
            bag.Add(new InventoryEntry());

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

    private static async Task WriteInventoryAsync(
        Npgsql.NpgsqlConnection connection, Npgsql.NpgsqlTransaction tx,
        Guid characterId, List<InventoryEntry> bag, CancellationToken cancellation)
    {
        await connection.ExecuteAsync(
            "delete from inventory_slot where character_id = $1;", tx, characterId);

        for (int slot = 0; slot < bag.Count; slot++)
        {
            if (SlotContainer.IsEmpty(bag[slot])) continue;

            await connection.ExecuteAsync(
                """
                insert into inventory_slot (character_id, slot_index, item_id, quantity)
                values ($1, $2, $3, $4);
                """,
                tx, characterId, slot, bag[slot].itemId, bag[slot].quantity);
        }
    }

    private static IResult NotYours() =>
        Results.Problem(
            title:      "no such character",
            detail:     "That character does not exist, or does not belong to you.",
            statusCode: StatusCodes.Status404NotFound);

    public sealed record EquipRequest(string? ItemId, string? SlotId);
    public sealed record UnequipRequest(string? SlotId);
}
