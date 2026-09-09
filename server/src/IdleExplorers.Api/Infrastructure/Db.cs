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

    /// <summary>
    /// Most connections one instance of this API will hold.
    ///
    /// ══ WHY IT IS THIS SMALL ══════════════════════════════════════════════════
    ///
    /// Measured, not guessed. At three hundred simulated players with the pool at 60,
    /// Postgres started answering
    ///
    ///     53300: remaining connection slots are reserved for roles with the
    ///            SUPERUSER attribute
    ///
    /// The local stack allows a hundred connections in total, and Supabase's own
    /// services -- auth, storage, Studio, the pooler -- were already holding about
    /// twenty-five. Sixty for the API on top of that is over the line, and the
    /// failures arrived as 500s.
    ///
    /// The hosted free tier is tighter still: sixty DIRECT connections for everything,
    /// shared across however many instances of this API run. Twenty-five leaves room
    /// for a second instance, a migration, and somebody looking at Studio while the
    /// game is up.
    ///
    /// A bigger pool does not make the API faster once the database is the bottleneck.
    /// It only moves where the queue forms, from a place with a clear error to a place
    /// without one.
    /// </summary>
    public const int MaxPoolSize = 25;

    /// <summary>
    /// How long a request waits for a connection before giving up.
    ///
    /// Short, and deliberately shorter than the client's own timeout. A request that
    /// cannot get a connection within a few seconds is not going to produce a useful
    /// answer, and failing fast keeps the queue from growing into the thing that
    /// caused it.
    /// </summary>
    public const int PoolTimeoutSeconds = 10;

    public Db(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(Normalise(connectionString));

        // Only set when the caller has not chosen: a deployment tuning its own pool
        // should not be silently overridden by a default.
        if (!builder.ContainsKey("Maximum Pool Size")) builder.MaxPoolSize = MaxPoolSize;
        if (!builder.ContainsKey("Timeout"))           builder.Timeout     = PoolTimeoutSeconds;

        _source = new NpgsqlDataSourceBuilder(builder.ConnectionString).Build();
    }

    /// <summary>
    /// Accepts a postgres:// URI as well as Npgsql's keyword form.
    ///
    /// ══ WHY THIS IS NOT A CONVENIENCE ═════════════════════════════════════════
    ///
    /// Npgsql only understands "Host=...;Port=...;Username=...". Every dashboard that
    /// hands out a Postgres connection string -- Supabase, Neon, Render, Railway's own
    /// Postgres -- hands out a URI. So the string an operator copies is the string
    /// Npgsql refuses, and it refuses it with
    ///
    ///     Format of the initialization string does not conform to specification
    ///     starting at index 0
    ///
    /// which says nothing about URIs, nothing about Postgres, and appears at STARTUP
    /// rather than on a request. The first deploy of this server died exactly that
    /// way. Accepting the format people actually have is worth more than being right
    /// about the format Npgsql prefers.
    ///
    /// ══ WHY THE PASSWORD IS UNESCAPED ═════════════════════════════════════════
    ///
    /// A URI percent-encodes anything special, so a password containing @ or / or #
    /// arrives as %40, %2F, %23. Passing that through verbatim authenticates with the
    /// literal characters and fails with "password authentication failed" -- which
    /// reads as a wrong password rather than as an encoding bug, and sends whoever is
    /// debugging it to rotate a perfectly good credential.
    ///
    /// ══ AND WHY A URI IS STILL THE WORSE WAY TO CONFIGURE THIS ════════════════
    ///
    /// Unescaping cuts both ways. A password pasted RAW into a URI -- which is what
    /// everybody does -- is not encoded, so any % in it is read as the start of an
    /// escape: "ab%cd" decodes to "ab\xCD" and authentication fails. The parser cannot
    /// tell an encoded password from a raw one, and guessing would be worse.
    ///
    /// So the URI form is supported because people have it, and the KEYWORD form is
    /// what the documentation should tell them to use:
    ///
    ///     Host=...;Port=5432;Database=postgres;Username=postgres;Password=...;SSL Mode=Require
    ///
    /// Nothing in it needs escaping except a literal semicolon, which can be quoted.
    /// </summary>
    public static string Normalise(string connectionString)
    {
        string trimmed = (connectionString ?? "").Trim();

        bool isUri = trimmed.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase)
                  || trimmed.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase);

        if (!isUri) return trimmed;

        Uri uri;

        try
        {
            uri = new Uri(trimmed);
        }
        catch (UriFormatException e)
        {
            // ══ THE MESSAGE .NET GIVES IS ABOUT THE WRONG THING ═══════════════
            //
            // A slash, hash or question mark in the password ends the authority
            // section, and what .NET reports is "Invalid port specified" -- so an
            // operator with a / in their password is told to look at the port, at
            // startup, on a container that then exits.
            //
            // Naming the real cause here is the difference between a five-minute fix
            // and an afternoon. The password itself is never included, in this message
            // or any other.
            throw new ArgumentException(
                "IDLE_EXPLORERS_DB looks like a postgres:// URI but could not be parsed. " +
                "The usual cause is a password containing / # ? or @, which a URI reads " +
                "as structure. Use the keyword form instead, which needs no escaping: " +
                "Host=...;Port=5432;Database=postgres;Username=postgres;Password=...;SSL Mode=Require " +
                $"({e.Message})", e);
        }

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host     = uri.Host,
            Port     = uri.Port > 0 ? uri.Port : 5432,

            // A URI path is "/postgres"; the leading slash is not part of the name.
            // Empty means the server's default, which for Postgres is the username.
            Database = uri.AbsolutePath.TrimStart('/') is { Length: > 0 } name ? name : "postgres",
        };

        string[] credentials = uri.UserInfo.Split(':', 2);

        if (credentials.Length > 0 && credentials[0].Length > 0)
            builder.Username = Uri.UnescapeDataString(credentials[0]);

        if (credentials.Length > 1)
            builder.Password = Uri.UnescapeDataString(credentials[1]);

        // Query parameters, so ?sslmode=require and friends survive rather than being
        // silently dropped -- dropping sslmode against a host that demands TLS is a
        // connection refused with no clue why.
        foreach (string pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = pair.Split('=', 2);
            if (parts.Length != 2) continue;

            string key = Uri.UnescapeDataString(parts[0]);

            try   { builder[key] = Uri.UnescapeDataString(parts[1]); }
            catch (ArgumentException)
            {
                // A parameter Npgsql does not recognise. Dropped rather than fatal:
                // libpq accepts several that Npgsql does not, and refusing to start
                // over an unknown query string would be worse than ignoring it.
            }
        }

        return builder.ConnectionString;
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

/// <summary>
/// Turning "the database is momentarily out of room" into an answer a client can act on.
///
/// ══ WHY A 500 WAS THE WRONG ANSWER ════════════════════════════════════════════
///
/// Found by the load simulation. At three hundred players Postgres ran out of
/// connection slots and every affected request became a 500 — and a 500 tells a
/// client its request was broken. A well-behaved client stops retrying, which is
/// exactly backwards: the request was fine, and retrying in a second is the correct
/// thing to do.
///
/// 503 with a Retry-After says the true thing instead. Under a spike the server sheds
/// load and the clients come back, rather than a hundred players seeing an error and
/// a support ticket each.
/// </summary>
public sealed class TransientFaultMiddleware(RequestDelegate next,
                                             ILogger<TransientFaultMiddleware> log)
{
    private const string Body =
        "{\"title\":\"busy\",\"status\":503," +
        "\"detail\":\"The server is at capacity. Retry shortly.\"}";

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception e) when (IsTransient(e))
        {
            // A warning, not an error: this is capacity rather than a defect, and
            // burying genuine errors under a load spike is how a real one gets missed.
            log.LogWarning("Shedding a request: {Reason}", e.Message);

            // Too late to change the answer if the body has already started going out.
            if (context.Response.HasStarted) throw;

            context.Response.Clear();
            context.Response.StatusCode  = StatusCodes.Status503ServiceUnavailable;
            context.Response.ContentType = "application/problem+json";
            context.Response.Headers.RetryAfter = "1";

            await context.Response.WriteAsync(Body, context.RequestAborted);
        }
    }

    /// <summary>
    /// Whether this means "come back in a moment" rather than "your request was wrong".
    /// </summary>
    public static bool IsTransient(Exception e) => e switch
    {
        // 53300 too_many_connections · 53000 insufficient_resources · 57P03 cannot_connect_now
        PostgresException { SqlState: "53300" or "53000" or "57P03" } => true,

        // Npgsql's own wait for a pooled connection running out.
        NpgsqlException { InnerException: TimeoutException } => true,
        TimeoutException => true,

        _ => false,
    };
}
