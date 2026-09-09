using System;
using System.Threading.Tasks;
using Npgsql;
using Xunit;

namespace IdleExplorers.Database.Tests
{
    /// <summary>
    /// Connecting to the local stack, and getting out of the way when it is not there.
    ///
    /// ══ WHY THESE SKIP INSTEAD OF FAILING ═════════════════════════════════════════
    ///
    /// The rest of the suite -- rules, content, both Unity assemblies -- needs no
    /// Docker at all, and that is worth protecting: a contributor who has not started
    /// the stack should still get a green run of everything else rather than a wall of
    /// connection errors they have to learn to ignore.
    ///
    /// But a check that quietly passes when it did not run is worse than no check. So
    /// these SKIP, which xunit reports separately from passing, and the reason names
    /// the command that fixes it.
    /// </summary>
    public static class Database
    {
        /// <summary>
        /// The local stack, as `supabase start` prints it.
        ///
        /// Not a secret: every Supabase CLI install has these same fixed local
        /// credentials, and the container listens on loopback only. The real
        /// connection string arrives through configuration and never touches the repo.
        /// </summary>
        private const string LocalStack =
            "Host=127.0.0.1;Port=54322;Database=postgres;Username=postgres;Password=postgres;Include Error Detail=true";

        public static string ConnectionString =>
            Environment.GetEnvironmentVariable("IDLE_EXPLORERS_DB") ?? LocalStack;

        private static bool? _reachable;

        /// <summary>Whether a database answered, checked once per run.</summary>
        public static bool Reachable
        {
            get
            {
                if (_reachable.HasValue) return _reachable.Value;

                try
                {
                    using var connection = new NpgsqlConnection(ConnectionString);
                    connection.Open();
                    _reachable = true;
                }
                catch (Exception)
                {
                    _reachable = false;
                }

                return _reachable.Value;
            }
        }

        public const string SkipReason =
            "No database reachable. Start the local stack with: supabase start";

        /// <summary>Opens a connection, or skips the calling test.</summary>
        public static async Task<NpgsqlConnection> OpenAsync()
        {
            Xunit.Skip.IfNot(Reachable, SkipReason);

            var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();

            return connection;
        }

        public static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
        {
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }

        public static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql)
        {
            await using var command = new NpgsqlCommand(sql, connection);
            object result = await command.ExecuteScalarAsync();

            if (result is DBNull or null) return default;

            // Converted rather than cast, because Postgres widens more freely than C#
            // does: sum(bigint) is numeric, count(*) is bigint, and a hard cast to long
            // throws on the first of those. An InvalidCastException in the helper reads
            // exactly like a failing invariant, which wastes the time of whoever is
            // looking at a red ledger check at the wrong moment.
            return (T)Convert.ChangeType(result, typeof(T));
        }

        /// <summary>
        /// Runs sql and returns the error message, or null if it succeeded.
        ///
        /// Most of these tests are asserting that something is REFUSED, and the
        /// message matters as much as the refusal -- a constraint that fires for the
        /// wrong reason still fails the test it was meant to pass.
        /// </summary>
        public static async Task<string> ErrorFromAsync(NpgsqlConnection connection, string sql)
        {
            try
            {
                await ExecuteAsync(connection, sql);
                return null;
            }
            catch (PostgresException e)
            {
                return e.MessageText;
            }
        }
    }

}
