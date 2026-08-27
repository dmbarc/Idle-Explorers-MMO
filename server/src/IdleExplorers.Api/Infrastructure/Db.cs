using Npgsql;

namespace IdleExplorers.Api.Infrastructure;

/// <summary>
/// Connections, and the transaction shape every mutation uses.
///
/// ══ POOLING IS NOT OPTIONAL ═══════════════════════════════════════════════════
///
/// Supabase allows 60 direct connections and 200 pooled. An API that opens a raw
/// connection per request exhausts that somewhere around fifty concurrent players,
/// and the failure looks like the database being down rather than the API being
/// wrong. NpgsqlDataSource pools by default; this type exists so nothing in the
/// codebase has the option of not using it.
/// </summary>
public sealed class Db : IAsyncDisposable
{
    private readonly NpgsqlDataSource _source;

    public Db(string connectionString)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        _source = builder.Build();
    }

    public async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellation = default) =>
        await _source.OpenConnectionAsync(cancellation);

    /// <summary>
    /// Runs <paramref name="work"/> inside one transaction, and locks the character
    /// row for its duration.
    ///
    /// ══ WHY THE LOCK IS THE POINT ═════════════════════════════════════════════
    ///
    /// Almost every exploit worth worrying about is a race. Two settle requests
    /// arriving together both read the same last_settled_at and both pay the window.
    /// Two craft requests both see enough bars. Two loot claims both find the drop
    /// unclaimed. The idempotency key stops a RETRY; it does nothing about two
    /// genuinely different requests arriving in the same millisecond.
    ///
    /// SELECT ... FOR UPDATE on the character row serialises everything that touches
    /// that character, which is the correct granularity: it costs nothing across
    /// different players, and there is no such thing as two operations on one
    /// character that may safely interleave.
    ///
    /// The row is locked BEFORE anything is read, so the read is already inside the
    /// serialised region. Reading first and locking second is the same race with more
    /// steps.
    /// </summary>
    public async Task<T> InCharacterTransactionAsync<T>(
        Guid characterId,
        Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> work,
        CancellationToken cancellation = default)
    {
        await using var connection  = await OpenAsync(cancellation);
        await using var transaction = await connection.BeginTransactionAsync(cancellation);

        await using (var lockCommand = connection.CreateCommand())
        {
            lockCommand.Transaction = transaction;
            lockCommand.CommandText = "select 1 from character where id = $1 for update;";
            lockCommand.Parameters.AddWithValue(characterId);

            await lockCommand.ExecuteNonQueryAsync(cancellation);
        }

        T result = await work(connection, transaction);

        await transaction.CommitAsync(cancellation);
        return result;
    }

    /// <summary>The same, for work scoped to an account rather than one character.</summary>
    public async Task<T> InAccountTransactionAsync<T>(
        Guid accountId,
        Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> work,
        CancellationToken cancellation = default)
    {
        await using var connection  = await OpenAsync(cancellation);
        await using var transaction = await connection.BeginTransactionAsync(cancellation);

        await using (var lockCommand = connection.CreateCommand())
        {
            lockCommand.Transaction = transaction;
            lockCommand.CommandText = "select 1 from account where id = $1 for update;";
            lockCommand.Parameters.AddWithValue(accountId);

            await lockCommand.ExecuteNonQueryAsync(cancellation);
        }

        T result = await work(connection, transaction);

        await transaction.CommitAsync(cancellation);
        return result;
    }

    public ValueTask DisposeAsync() => _source.DisposeAsync();
}

/// <summary>
/// Parameterised commands, briefly.
///
/// Every value reaches Postgres as a parameter, never as string interpolation. Not
/// only for injection -- although a player-chosen character name goes into a query
/// on the very first request -- but because a parameter carries its type, and a
/// bigint quantity formatted into SQL by a machine with a comma decimal separator is
/// a bug that only happens on someone else's laptop.
/// </summary>
public static class DbCommands
{
    public static NpgsqlCommand Sql(this NpgsqlConnection connection, string sql,
                                    NpgsqlTransaction? transaction = null,
                                    params object?[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;

        foreach (object? value in parameters)
            command.Parameters.AddWithValue(value ?? DBNull.Value);

        return command;
    }

    public static async Task<int> ExecuteAsync(this NpgsqlConnection connection, string sql,
                                               NpgsqlTransaction? transaction = null,
                                               params object?[] parameters)
    {
        await using var command = connection.Sql(sql, transaction, parameters);
        return await command.ExecuteNonQueryAsync();
    }

    public static async Task<T?> ScalarAsync<T>(this NpgsqlConnection connection, string sql,
                                                NpgsqlTransaction? transaction = null,
                                                params object?[] parameters)
    {
        await using var command = connection.Sql(sql, transaction, parameters);
        object? value = await command.ExecuteScalarAsync();

        if (value is null or DBNull) return default;

        // Already the right type is the common case, and it has to be checked first:
        // Convert.ChangeType throws on anything that is not IConvertible, and Guid is
        // not -- so every id read through here would fail on a type that came back
        // perfectly correct.
        if (value is T typed) return typed;

        // Otherwise convert, because Postgres widens more freely than C# does: a
        // sum(bigint) arrives as numeric where the caller reasonably expects a long.
        Type target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);

        return (T)Convert.ChangeType(value, target);
    }
}
