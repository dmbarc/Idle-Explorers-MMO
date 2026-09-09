using IdleExplorers.Rules;
using Npgsql;

namespace IdleExplorers.Tools;

/// <summary>
/// One character, on one screen.
///
/// ══ WHAT THIS IS FOR ══════════════════════════════════════════════════════════
///
/// A player says "I mined all night and got nothing". Answering that means looking at
/// their activity row, their last settlement, their inventory and the ledger, and
/// doing it in Studio means four queries and remembering four table names. Doing it
/// slowly is how a support question becomes a support ticket.
///
/// ══ WHY THE INVARIANTS RUN HERE TOO ═══════════════════════════════════════════
///
/// The test suite already asserts that the ledger balances. That proves the code was
/// right about the data the tests created. This proves it about the data that actually
/// exists -- which is a different claim, and the only one that matters at three in the
/// morning after a bad deploy.
///
/// They are cheap enough to run unconditionally, so there is no flag to remember.
/// </summary>
public static class Inspect
{
    /// <summary>Ledger rows shown. Recent history, not an audit -- the table is the audit.</summary>
    public const int LedgerRows = 20;

    public static async Task<int> RunAsync(Options options)
    {
        if (options.Character.Length == 0)
        {
            Console.Error.WriteLine("inspect needs --character NAME-OR-ID.");
            return 2;
        }

        await using var connection = new NpgsqlConnection(options.Connection);
        await connection.OpenAsync();

        (Guid id, Guid accountId, string name, string classId, long xp, string map)? found =
            await FindAsync(connection, options.Character);

        if (found is null)
        {
            Console.Error.WriteLine($"No living character called '{options.Character}'.");
            return 1;
        }

        var character = found.Value;

        Console.WriteLine();
        Console.WriteLine($"  {character.name}   {(character.classId.Length == 0 ? "(no class)" : character.classId)}");
        Console.WriteLine($"  {character.id}");
        Console.WriteLine($"  level {Levelling.CharacterLevel(character.xp)}   {character.xp:N0} xp" +
                          $"   {(character.map.Length == 0 ? "nowhere" : character.map)}");
        Console.WriteLine();

        await SkillsAsync(connection, character.id);
        await ActivityAsync(connection, character.id);
        await EquipmentAsync(connection, character.id);
        await InventoryAsync(connection, character.id);
        await KillsAsync(connection, character.id);
        await WalletAsync(connection, character.accountId);
        await LedgerAsync(connection, character.accountId);

        int violations = await InvariantsAsync(connection, character.accountId);

        // A non-zero exit on a broken invariant, so this is usable from a script. A
        // nightly check that can only be read by a human is a check nobody reads.
        return violations == 0 ? 0 : 1;
    }

    // ── Lookup ────────────────────────────────────────────────────────────────

    /// <summary>
    /// By id if it parses as one, by name otherwise.
    ///
    /// Names are unique per ACCOUNT, not globally, so a name can match several
    /// characters across accounts. The first by creation wins and the rest are listed,
    /// which beats either guessing silently or refusing to answer.
    /// </summary>
    private static async Task<(Guid, Guid, string, string, long, string)?> FindAsync(
        NpgsqlConnection connection, string needle)
    {
        string sql = Guid.TryParse(needle, out Guid asId)
            ? "select id, account_id, name, class_id, xp, last_map_id from character where id = $1 and deleted_at is null;"
            : "select id, account_id, name, class_id, xp, last_map_id from character where lower(name) = lower($1) and deleted_at is null order by created_at;";

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(Guid.TryParse(needle, out _) ? asId : needle);

        await using var reader = await command.ExecuteReaderAsync();

        if (!await reader.ReadAsync()) return null;

        var first = (reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2),
                     reader.GetString(3), reader.GetInt64(4), reader.GetString(5));

        var others = new List<Guid>();
        while (await reader.ReadAsync()) others.Add(reader.GetGuid(0));

        if (others.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"  {others.Count + 1} characters share that name. Showing the oldest;");
            Console.WriteLine("  the others are:");

            foreach (Guid other in others) Console.WriteLine($"    {other}");
        }

        return first;
    }

    // ── The sheet ─────────────────────────────────────────────────────────────

    private static async Task SkillsAsync(NpgsqlConnection connection, Guid characterId)
    {
        var lines = new List<string>();

        await using var command = new NpgsqlCommand(
            "select skill_id, xp from character_skill where character_id = $1 and xp > 0 order by xp desc;",
            connection);

        command.Parameters.AddWithValue(characterId);

        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            long xp = reader.GetInt64(1);

            lines.Add($"    {reader.GetString(0),-14} {Levelling.SkillLevel(xp),4}   {xp,12:N0} xp");
        }

        Section("Skills", lines);
    }

    private static async Task ActivityAsync(NpgsqlConnection connection, Guid characterId)
    {
        await using var command = new NpgsqlCommand(
            """
            select kind, skill_id, node_id, recipe_id, monster_id, last_settled_at, progress,
                   credited_seconds, last_heartbeat_at, now()
              from activity where character_id = $1;
            """,
            connection);

        command.Parameters.AddWithValue(characterId);

        await using var reader = await command.ExecuteReaderAsync();

        if (!await reader.ReadAsync()) { Section("Activity", ["    no row -- this is a bug"]); return; }

        string kind   = reader.GetString(0);
        string target = kind switch
        {
            "gather" => reader.GetString(2),
            "craft"  => reader.GetString(3),
            "combat" => reader.GetString(4),
            _        => "",
        };

        var settled   = reader.GetFieldValue<DateTimeOffset>(5);
        var now       = reader.GetFieldValue<DateTimeOffset>(9);
        double unpaid = (now - settled).TotalSeconds;

        var lines = new List<string>
        {
            $"    {kind}{(target.Length > 0 ? $"  {target}" : "")}" +
            $"{(reader.GetString(1).Length > 0 ? $"  ({reader.GetString(1)})" : "")}",

            $"    settled     {settled:u}   {unpaid,10:N0}s unsettled",
            $"    progress    {reader.GetDouble(6):P1} of an action",
            $"    credited    {reader.GetInt64(7):N0}s of gem time waiting",
        };

        if (!reader.IsDBNull(8))
        {
            double sinceBeat = (now - reader.GetFieldValue<DateTimeOffset>(8)).TotalSeconds;

            lines.Add($"    heartbeat   {sinceBeat,10:N0}s ago");
        }

        Section("Activity", lines);
    }

    private static async Task EquipmentAsync(NpgsqlConnection connection, Guid characterId)
    {
        var lines = new List<string>();

        await using var command = new NpgsqlCommand(
            "select slot_id, item_id, durability from equipment where character_id = $1 order by slot_id;",
            connection);

        command.Parameters.AddWithValue(characterId);

        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
            lines.Add($"    {reader.GetString(0),-10} {reader.GetString(1),-24} {reader.GetInt32(2),4} dur");

        Section("Equipment", lines);
    }

    private static async Task InventoryAsync(NpgsqlConnection connection, Guid characterId)
    {
        var lines = new List<string>();

        await using var command = new NpgsqlCommand(
            "select slot_index, item_id, quantity from inventory_slot where character_id = $1 order by slot_index;",
            connection);

        command.Parameters.AddWithValue(characterId);

        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
            lines.Add($"    {reader.GetInt32(0),3}  {reader.GetString(1),-24} {reader.GetInt64(2),10:N0}");

        Section($"Inventory ({lines.Count} slots used)", lines);
    }

    private static async Task KillsAsync(NpgsqlConnection connection, Guid characterId)
    {
        var lines = new List<string>();

        await using var command = new NpgsqlCommand(
            """
            select monster_id, active_kills, afk_kills from kill_counter
             where character_id = $1 order by active_kills + afk_kills desc;
            """,
            connection);

        command.Parameters.AddWithValue(characterId);

        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            lines.Add($"    {reader.GetString(0),-16} {reader.GetInt64(1),8:N0} active" +
                      $"   {reader.GetInt64(2),8:N0} afk");
        }

        Section("Kills", lines);
    }

    private static async Task WalletAsync(NpgsqlConnection connection, Guid accountId)
    {
        var lines = new List<string>();

        await using var command = new NpgsqlCommand(
            "select currency, balance from wallet where account_id = $1 order by currency;",
            connection);

        command.Parameters.AddWithValue(accountId);

        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
            lines.Add($"    {reader.GetString(0),-12} {reader.GetInt64(1),14:N0}");

        Section("Wallet", lines);
    }

    private static async Task LedgerAsync(NpgsqlConnection connection, Guid accountId)
    {
        var lines = new List<string>();

        await using var command = new NpgsqlCommand(
            """
            select created_at, currency, delta, reason from wallet_ledger
             where account_id = $1 order by id desc limit $2;
            """,
            connection);

        command.Parameters.AddWithValue(accountId);
        command.Parameters.AddWithValue(LedgerRows);

        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            lines.Add($"    {reader.GetFieldValue<DateTimeOffset>(0):u}  {reader.GetString(1),-12} " +
                      $"{reader.GetInt64(2),+12:N0}   {reader.GetString(3)}");
        }

        Section($"Last {LedgerRows} wallet movements", lines);
    }

    // ── Invariants ────────────────────────────────────────────────────────────

    /// <summary>
    /// The claims that must hold about real data, checked against real data.
    ///
    /// Returns how many failed, so a caller can exit non-zero. Prints "all hold" when
    /// none do, because silence is indistinguishable from a check that did not run --
    /// and a check nobody can tell ran is a check nobody trusts.
    /// </summary>
    private static async Task<int> InvariantsAsync(NpgsqlConnection connection, Guid accountId)
    {
        var problems = new List<string>();

        // The one that matters. If the ledger and the balance disagree, one of them was
        // written without the other, and every coin in the account is now a claim
        // nobody can source.
        await using (var command = new NpgsqlCommand(
            """
            select w.currency, w.balance, coalesce(sum(l.delta), 0)
              from wallet w
              left join wallet_ledger l
                on l.account_id = w.account_id and l.currency = w.currency
             where w.account_id = $1
             group by w.currency, w.balance;
            """,
            connection))
        {
            command.Parameters.AddWithValue(accountId);

            await using var reader = await command.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                long balance = reader.GetInt64(1);

                // sum(bigint) comes back as numeric in Postgres, not bigint. Reading it
                // as a long throws, which is a genuinely confusing way for an invariant
                // check to fail.
                long ledger = (long)reader.GetDecimal(2);

                if (balance != ledger)
                {
                    problems.Add($"{reader.GetString(0)}: balance {balance:N0} " +
                                 $"but the ledger sums to {ledger:N0}");
                }
            }
        }

        // A wallet row with no ledger history at all is not automatically wrong -- a
        // zero balance never written to is fine -- so this only fires on value that
        // appeared from nowhere, which the query above already catches. What it cannot
        // catch is a ledger for a currency with no wallet row, so:
        await using (var command = new NpgsqlCommand(
            """
            select l.currency, sum(l.delta)
              from wallet_ledger l
             where l.account_id = $1
               and not exists (select 1 from wallet w
                                where w.account_id = l.account_id and w.currency = l.currency)
             group by l.currency;
            """,
            connection))
        {
            command.Parameters.AddWithValue(accountId);

            await using var reader = await command.ExecuteReaderAsync();

            while (await reader.ReadAsync())
                problems.Add($"{reader.GetString(0)}: {reader.GetDecimal(1):N0} in the ledger with no wallet row");
        }

        Console.WriteLine("  Invariants");

        if (problems.Count == 0)
        {
            Console.WriteLine("    all hold");
        }
        else
        {
            foreach (string problem in problems) Console.WriteLine($"    BROKEN  {problem}");
        }

        Console.WriteLine();

        return problems.Count;
    }

    // ── Layout ────────────────────────────────────────────────────────────────

    private static void Section(string heading, IReadOnlyList<string> lines)
    {
        Console.WriteLine($"  {heading}");

        if (lines.Count == 0) Console.WriteLine("    (none)");
        else foreach (string line in lines) Console.WriteLine(line);

        Console.WriteLine();
    }
}
