using System;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Npgsql;
using Xunit;

namespace IdleExplorers.Api.Tests;

/// <summary>
/// Putting things on, and the rules about what may be on at once.
///
/// Equipment looks presentational and is not: worn gear decides damage, damage
/// decides farm rate, and farm rate decides the economy. A client that could claim
/// to be wearing the Goblin Destroyer would be a client that sets its own kill rate.
/// </summary>
[Collection("api")]
public class EquipmentTests(ApiFixture api)
{
    private static void RequireDatabase() => Skip.IfNot(ApiFixture.DatabaseReachable, ApiFixture.SkipReason);

    // ── The basics ────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task SomethingInYourBagCanBeWorn()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await OwnershipTests.CreateCharacter(player, "Smith");

        await Give(character, "magic_staff", slot: 0);

        var response = await Equip(player, character, "magic_staff");
        response.EnsureSuccessStatusCode();

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("mainhand", body.RootElement.GetProperty("slotId").GetString());

        await using var db = await api.OpenDatabaseAsync();

        Assert.Equal("magic_staff", await Worn(db, character, "mainhand"));
        Assert.Equal(0L, await Held(db, character, "magic_staff"));
    }

    /// <summary>
    /// The whole point. A client that could equip what it does not have would be a
    /// client that equips anything.
    /// </summary>
    [SkippableFact]
    public async Task SomethingYouDoNotHaveCannotBeWorn()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await OwnershipTests.CreateCharacter(player, "Liar");

        var response = await Equip(player, character, "goblin_destroyer");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        await using var db = await api.OpenDatabaseAsync();
        Assert.Null(await Worn(db, character, "mainhand"));
    }

    [SkippableFact]
    public async Task AnInventedItemIsRefused()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await OwnershipTests.CreateCharacter(player, "Inventor");

        Assert.Equal(HttpStatusCode.BadRequest,
                     (await Equip(player, character, "sword_of_infinite_gold")).StatusCode);
    }

    [SkippableFact]
    public async Task SomethingThatIsNotGearIsRefused()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await OwnershipTests.CreateCharacter(player, "Miner");

        await Give(character, "tin_ore", slot: 0, quantity: 50);

        Assert.Equal(HttpStatusCode.BadRequest,
                     (await Equip(player, character, "tin_ore")).StatusCode);
    }

    [SkippableFact]
    public async Task TakingSomethingOffPutsItBackInTheBag()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await OwnershipTests.CreateCharacter(player, "Tidy");

        await Give(character, "magic_staff", slot: 0);
        (await Equip(player, character, "magic_staff")).EnsureSuccessStatusCode();

        (await Unequip(player, character, "mainhand")).EnsureSuccessStatusCode();

        await using var db = await api.OpenDatabaseAsync();

        Assert.Null(await Worn(db, character, "mainhand"));
        Assert.Equal(1L, await Held(db, character, "magic_staff"));
    }

    /// <summary>
    /// Off and on again must not be a free repair, or durability is theatre.
    /// </summary>
    [SkippableFact]
    public async Task ConditionSurvivesBeingTakenOff()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await OwnershipTests.CreateCharacter(player, "Careless");

        await Give(character, "magic_staff", slot: 0);
        (await Equip(player, character, "magic_staff")).EnsureSuccessStatusCode();

        await using (var db = await api.OpenDatabaseAsync())
        await using (var command = db.CreateCommand())
        {
            // Battered, but not broken.
            command.CommandText = "update equipment set durability = 17 where character_id = $1;";
            command.Parameters.AddWithValue(character);

            await command.ExecuteNonQueryAsync();
        }

        (await Unequip(player, character, "mainhand")).EnsureSuccessStatusCode();
        (await Equip(player, character, "magic_staff")).EnsureSuccessStatusCode();

        await using var check = await api.OpenDatabaseAsync();
        await using var read = check.CreateCommand();

        read.CommandText = "select durability from equipment where character_id = $1 and slot_id = 'mainhand';";
        read.Parameters.AddWithValue(character);

        Assert.Equal(17, Convert.ToInt32(await read.ExecuteScalarAsync()));
    }

    // ── Hands ─────────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task AShieldGoesInTheOffHand()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await OwnershipTests.CreateCharacter(player, "Guarded");

        await Give(character, "magic_staff",  slot: 0);
        await Give(character, "tin_buckler", slot: 1);

        (await Equip(player, character, "magic_staff")).EnsureSuccessStatusCode();
        (await Equip(player, character, "tin_buckler")).EnsureSuccessStatusCode();

        await using var db = await api.OpenDatabaseAsync();

        Assert.Equal("magic_staff",  await Worn(db, character, "mainhand"));
        Assert.Equal("tin_buckler", await Worn(db, character, "offhand"));
    }

    /// <summary>
    /// The Destroyer takes both hands, so the shield comes off — and lands in the
    /// bag rather than nowhere.
    /// </summary>
    [SkippableFact]
    public async Task ATwoHanderDisplacesTheOffHand()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await OwnershipTests.CreateCharacter(player, "Destroyer");

        await Give(character, "tin_buckler",      slot: 0);
        await Give(character, "goblin_destroyer", slot: 1);

        (await Equip(player, character, "tin_buckler")).EnsureSuccessStatusCode();
        (await Equip(player, character, "goblin_destroyer")).EnsureSuccessStatusCode();

        await using var db = await api.OpenDatabaseAsync();

        Assert.Equal("goblin_destroyer", await Worn(db, character, "mainhand"));
        Assert.Null(await Worn(db, character, "offhand"));

        // Displaced, not destroyed.
        Assert.Equal(1L, await Held(db, character, "tin_buckler"));
    }

    [SkippableFact]
    public async Task NothingGoesInTheOffHandWhileHoldingATwoHander()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await OwnershipTests.CreateCharacter(player, "Committed");

        await Give(character, "goblin_destroyer", slot: 0);
        await Give(character, "tin_buckler",      slot: 1);

        (await Equip(player, character, "goblin_destroyer")).EnsureSuccessStatusCode();

        var refused = await Equip(player, character, "tin_buckler");
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

        await using var db = await api.OpenDatabaseAsync();
        Assert.Null(await Worn(db, character, "offhand"));
    }

    /// <summary>
    /// The other half of the gate: once the levels are there, the gear goes on.
    ///
    /// Without this the refusal test alone would pass just as well against an endpoint
    /// that refused everything.
    /// </summary>
    [SkippableFact]
    public async Task GearAtYourLevelIsAllowed()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await OwnershipTests.CreateCharacter(player, "Journeyman");

        await Give(character, "iron_sword", slot: 0);

        // Smithing 10 on the shared curve is (10-1)^2 * 100 = 8,100.
        await GrantSkill(character, "smithing", 8_100);

        (await Equip(player, character, "iron_sword")).EnsureSuccessStatusCode();

        await using var db = await api.OpenDatabaseAsync();
        Assert.Equal("iron_sword", await Worn(db, character, "mainhand"));
    }

    // ── Requirements ──────────────────────────────────────────────────────────

    /// <summary>
    /// The iron sword needs smithing 10. A new character has none, and the tooltip
    /// saying so has to be true.
    /// </summary>
    [SkippableFact]
    public async Task GearAboveYourLevelIsRefused()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await OwnershipTests.CreateCharacter(player, "Novice");

        await Give(character, "iron_sword", slot: 0);

        // Level 10 smithing needs 8,100 xp on the skill curve; this character has 0.
        var response = await Equip(player, character, "iron_sword");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Contains("smithing", body.RootElement.GetProperty("detail").GetString());
    }

    // ── Class locks ───────────────────────────────────────────────────────────

    /// <summary>
    /// The Bulwark Cleaver is the warrior's. A sorcerer may not pick it up.
    ///
    /// ══ WHY THIS IS WORTH A SERVER CHECK ══════════════════════════════════════
    ///
    /// A class weapon grants an ability, and an ability is damage, and damage is the
    /// rate at which the whole economy is earned. "Warrior only" written in a tooltip
    /// is a suggestion; this is the rule.
    /// </summary>
    [SkippableFact]
    public async Task AnotherClassesWeaponIsRefused()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await OwnershipTests.CreateCharacter(player, "Bookish", "sorcerer");

        await Give(character, "bulwark_cleaver", slot: 0);
        await GrantSkill(character, "smithing", 100_000);

        var response = await Equip(player, character, "bulwark_cleaver");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("wrong class", body.RootElement.GetProperty("title").GetString());

        await using var db = await api.OpenDatabaseAsync();
        Assert.Null(await Worn(db, character, "mainhand"));
    }

    /// <summary>
    /// And the warrior's own weapon goes on.
    ///
    /// The paired acceptance. A gate that refused every class weapon would satisfy the
    /// test above perfectly, which is the mistake that let an API rejecting EVERY real
    /// token pass a full suite of authentication tests.
    /// </summary>
    [SkippableFact]
    public async Task YourOwnClassesWeaponIsAllowed()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await OwnershipTests.CreateCharacter(player, "Bruiser", "warrior");

        await Give(character, "bulwark_cleaver", slot: 0);
        await GrantSkill(character, "smithing", 100_000);

        (await Equip(player, character, "bulwark_cleaver")).EnsureSuccessStatusCode();

        await using var db = await api.OpenDatabaseAsync();
        Assert.Equal("bulwark_cleaver", await Worn(db, character, "mainhand"));
    }

    /// <summary>
    /// A second class earns its weapon too.
    ///
    /// The bug this is written against has already happened once, in the talent tree:
    /// the server built trees from character.class_id alone, so unlocking a second
    /// class and spending a point in it came back "no such talent". Equipment asks the
    /// same question, and a warrior who became a ranger has earned the ranger's bow.
    /// </summary>
    [SkippableFact]
    public async Task AWeaponFromYourSECONDClassIsAllowed()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await OwnershipTests.CreateCharacter(player, "Versatile", "warrior");

        await AddClassRow(character, "ranger");

        await Give(character, "windcut_bow", slot: 0);
        await GrantSkill(character, "smithing", 100_000);

        (await Equip(player, character, "windcut_bow")).EnsureSuccessStatusCode();

        await using var db = await api.OpenDatabaseAsync();
        Assert.Equal("windcut_bow", await Worn(db, character, "mainhand"));
    }

    /// <summary>
    /// An unlocked item is unlocked. Almost everything in the game has no classReq at
    /// all, and a gate that quietly refused those would be far worse than no gate.
    /// </summary>
    [SkippableFact]
    public async Task SomethingWithNoClassLockIsUnaffected()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await OwnershipTests.CreateCharacter(player, "Ordinary", "sorcerer");

        await Give(character, "tin_helmet", slot: 0);
        await GrantSkill(character, "smithing", 100_000);

        (await Equip(player, character, "tin_helmet")).EnsureSuccessStatusCode();

        await using var db = await api.OpenDatabaseAsync();
        Assert.Equal("tin_helmet", await Worn(db, character, "helmet"));
    }

    /// <summary>
    /// A slot that is not in the item's family. Without this, a helmet in the main
    /// hand would inherit a weapon's reach and damage.
    /// </summary>
    [SkippableFact]
    public async Task AnItemCannotBeForcedIntoTheWrongSlot()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await OwnershipTests.CreateCharacter(player, "Sneaky");

        await Give(character, "tin_helmet", slot: 0);

        var response = await Equip(player, character, "tin_helmet", slotId: "mainhand");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        await using var db = await api.OpenDatabaseAsync();
        Assert.Null(await Worn(db, character, "mainhand"));
    }

    // ── Ownership and races ───────────────────────────────────────────────────

    [SkippableFact]
    public async Task OneAccountCannotDressAnothersCharacter()
    {
        RequireDatabase();

        await using var owner = await api.NewPlayerAsync();
        await using var thief = await api.NewPlayerAsync();

        Guid character = await OwnershipTests.CreateCharacter(owner, "Victim");
        await Give(character, "iron_sword", slot: 0);

        Assert.Equal(HttpStatusCode.NotFound,
                     (await Equip(thief, character, "iron_sword")).StatusCode);
    }

    /// <summary>
    /// One sword, ten simultaneous equips. It must end up worn once, and there must
    /// not be a copy left in the bag -- the classic way an item is duplicated is by
    /// being in two places after a race.
    /// </summary>
    [SkippableFact]
    public async Task RacingEquipsCannotDuplicateTheItem()
    {
        RequireDatabase();

        await using var player = await api.NewPlayerAsync();
        Guid character = await OwnershipTests.CreateCharacter(player, "Racer");

        await Give(character, "tin_buckler", slot: 0);

        var attempts = Enumerable.Range(0, 10)
            .Select(_ => Equip(player, character, "tin_buckler"))
            .ToArray();

        foreach (var response in await Task.WhenAll(attempts)) response.Dispose();

        await using var db = await api.OpenDatabaseAsync();

        Assert.Equal("tin_buckler", await Worn(db, character, "offhand"));
        Assert.Equal(0L, await Held(db, character, "tin_buckler"));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Task<System.Net.Http.HttpResponseMessage> Equip(
        Player player, Guid character, string itemId, string slotId = null) =>
        OwnershipTests.Post(player, $"/equipment/{character}/equip", new { itemId, slotId });

    private static Task<System.Net.Http.HttpResponseMessage> Unequip(
        Player player, Guid character, string slotId) =>
        OwnershipTests.Post(player, $"/equipment/{character}/unequip", new { slotId });

    /// <summary>
    /// Puts an item in the bag directly.
    ///
    /// Through SQL because no endpoint grants items -- which is the point of two of
    /// the abuse tests.
    /// </summary>
    private async Task Give(Guid character, string itemId, int slot, long quantity = 1)
    {
        await using var db = await api.OpenDatabaseAsync();
        await using var command = db.CreateCommand();

        command.CommandText = """
            insert into inventory_slot (character_id, slot_index, item_id, quantity)
            values ($1, $2, $3, $4)
            on conflict (character_id, slot_index) do update
               set item_id = excluded.item_id, quantity = excluded.quantity;
            """;
        command.Parameters.AddWithValue(character);
        command.Parameters.AddWithValue(slot);
        command.Parameters.AddWithValue(itemId);
        command.Parameters.AddWithValue(quantity);

        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Gives a character a class WITHOUT making it their primary.
    ///
    /// Written against the table rather than through /character/{id}/class so the test
    /// is about the equipment gate rather than about whatever the unlock endpoint
    /// happens to require of a character's level.
    /// </summary>
    private async Task AddClassRow(Guid character, string classId)
    {
        await using var db = await api.OpenDatabaseAsync();
        await using var command = db.CreateCommand();

        command.CommandText = """
            insert into character_class (character_id, class_id)
            values ($1, $2)
            on conflict do nothing;
            """;
        command.Parameters.AddWithValue(character);
        command.Parameters.AddWithValue(classId);

        await command.ExecuteNonQueryAsync();
    }

    private async Task GrantSkill(Guid character, string skillId, long xp)
    {
        await using var db = await api.OpenDatabaseAsync();
        await using var command = db.CreateCommand();

        command.CommandText = """
            insert into character_skill (character_id, skill_id, xp)
            values ($1, $2, $3)
            on conflict (character_id, skill_id) do update set xp = excluded.xp;
            """;
        command.Parameters.AddWithValue(character);
        command.Parameters.AddWithValue(skillId);
        command.Parameters.AddWithValue(xp);

        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> Worn(NpgsqlConnection db, Guid character, string slotId)
    {
        await using var command = db.CreateCommand();

        command.CommandText = "select item_id from equipment where character_id = $1 and slot_id = $2;";
        command.Parameters.AddWithValue(character);
        command.Parameters.AddWithValue(slotId);

        object result = await command.ExecuteScalarAsync();
        return result is null or DBNull ? null : (string)result;
    }

    private static async Task<long> Held(NpgsqlConnection db, Guid character, string itemId)
    {
        await using var command = db.CreateCommand();

        command.CommandText = """
            select coalesce(sum(quantity), 0) from inventory_slot
             where character_id = $1 and item_id = $2;
            """;
        command.Parameters.AddWithValue(character);
        command.Parameters.AddWithValue(itemId);

        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}
