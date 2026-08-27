using System;
using System.Threading.Tasks;
using Npgsql;
using Xunit;

namespace IdleExplorers.Database.Tests
{
    /// <summary>
    /// What a leaked key can reach.
    ///
    /// ══ WHY THIS IS TESTED AND NOT ASSUMED ════════════════════════════════════════
    ///
    /// The design says the Unity client never talks to Postgres: Cloudflare fronts an
    /// ASP.NET Core API and only the API holds privileged credentials. That is true
    /// of the deployment as designed — and deployments change, someone adds a
    /// "quick" direct query, and anything shipped in a WebGL build is public forever.
    ///
    /// So the claim is asserted where it cannot be undone by a config change: every
    /// game table has RLS enabled and FORCED with no policies at all. anon and
    /// authenticated are denied outright; service_role has BYPASSRLS and is the only
    /// thing the API uses.
    ///
    /// The FORCE matters as much as the enable. Without it, the table owner is exempt,
    /// and a future migration that runs as the owner would leave a hole nobody sees.
    ///
    /// Reads under RLS come back EMPTY rather than erroring — a filtered query, not a
    /// refusal — which is why these assert on row counts and not on exceptions.
    /// </summary>
    [Collection("database")]
    public class RowLevelSecurityTests
    {
        /// <summary>
        /// Every table holding something a player could want to forge.
        ///
        /// Listed rather than discovered, so ADDING a table is a deliberate decision
        /// about whether it belongs here. A new table quietly appearing without RLS is
        /// exactly the failure this file exists to catch.
        /// </summary>
        public static TheoryData<string> GameTables => new()
        {
            "account", "account_identity", "character", "character_skill",
            "inventory_slot", "bank_slot", "equipment", "stored_durability",
            "activity", "kill_counter", "unlock",
            "wallet", "wallet_ledger", "item_ledger", "purchase",
            "idempotency_record", "feature_flag", "telemetry_event", "security_event",
        };

        [SkippableTheory]
        [MemberData(nameof(GameTables))]
        public async Task EveryGameTableHasSecurityEnabledAndForced(string table)
        {
            await using var db = await Database.OpenAsync();

            bool enabled = await Database.ScalarAsync<bool>(db,
                $"select relrowsecurity from pg_class where relname = '{table}';");
            bool forced = await Database.ScalarAsync<bool>(db,
                $"select relforcerowsecurity from pg_class where relname = '{table}';");

            Assert.True(enabled, $"'{table}' does not have row level security enabled");
            Assert.True(forced,  $"'{table}' has RLS but not FORCE — the owner is exempt");
        }

        /// <summary>
        /// No policies is the policy. A permissive one added by accident — copied from
        /// a Supabase tutorial, most likely — would open the table to every logged-in
        /// player at once.
        /// </summary>
        [SkippableTheory]
        [MemberData(nameof(GameTables))]
        public async Task NoTableGrantsAnybodyAPolicy(string table)
        {
            await using var db = await Database.OpenAsync();

            long policies = await Database.ScalarAsync<long>(db,
                $"select count(*) from pg_policies where tablename = '{table}';");

            Assert.Equal(0L, policies);
        }

        /// <summary>
        /// The one role that must still work. If this fails, the API is locked out of
        /// its own database and nothing else in the suite is meaningful.
        /// </summary>
        [SkippableFact]
        public async Task TheApiRoleBypassesSecurity()
        {
            await using var db = await Database.OpenAsync();

            bool bypasses = await Database.ScalarAsync<bool>(db,
                "select rolbypassrls from pg_roles where rolname = 'service_role';");

            Assert.True(bypasses, "service_role cannot bypass RLS — the API cannot read anything");
        }

        [SkippableTheory]
        [InlineData("anon")]
        [InlineData("authenticated")]
        public async Task ALeakedKeyCannotReadAnything(string role)
        {
            await using var db = await Database.OpenAsync();
            var account = Guid.NewGuid();

            await Seed(db, account);

            try
            {
                await Database.ExecuteAsync(db, $"set role {role};");

                Assert.Equal(0L, await Database.ScalarAsync<long>(db, "select count(*) from account;"));
                Assert.Equal(0L, await Database.ScalarAsync<long>(db, "select count(*) from wallet;"));
                Assert.Equal(0L, await Database.ScalarAsync<long>(db, "select count(*) from character;"));
                Assert.Equal(0L, await Database.ScalarAsync<long>(db, "select count(*) from inventory_slot;"));
            }
            finally
            {
                await Database.ExecuteAsync(db, "reset role;");
                await Database.ExecuteAsync(db, $"delete from auth.users where id = '{account}';");
            }
        }

        /// <summary>
        /// The attack this whole architecture exists to stop: give myself a million
        /// relic coins.
        /// </summary>
        [SkippableTheory]
        [InlineData("anon")]
        [InlineData("authenticated")]
        public async Task ALeakedKeyCannotGrantItselfCurrency(string role)
        {
            await using var db = await Database.OpenAsync();
            var account = Guid.NewGuid();

            await Seed(db, account);

            try
            {
                await Database.ExecuteAsync(db, $"set role {role};");

                string error = await Database.ErrorFromAsync(db,
                    $"insert into wallet (account_id, currency, balance) values ('{account}', 'relic_coins', 1000000);");

                Assert.NotNull(error);
                Assert.Contains("row-level security", error);
            }
            finally
            {
                await Database.ExecuteAsync(db, "reset role;");
                await Database.ExecuteAsync(db, $"delete from auth.users where id = '{account}';");
            }
        }

        [SkippableTheory]
        [InlineData("anon")]
        [InlineData("authenticated")]
        public async Task ALeakedKeyCannotGrantItselfItems(string role)
        {
            await using var db = await Database.OpenAsync();
            var account = Guid.NewGuid();

            Guid character = await Seed(db, account);

            try
            {
                await Database.ExecuteAsync(db, $"set role {role};");

                string error = await Database.ErrorFromAsync(db, $@"
                    insert into inventory_slot (character_id, slot_index, item_id, quantity)
                    values ('{character}', 0, 'goblin_destroyer', 99);");

                Assert.NotNull(error);
                Assert.Contains("row-level security", error);
            }
            finally
            {
                await Database.ExecuteAsync(db, "reset role;");
                await Database.ExecuteAsync(db, $"delete from auth.users where id = '{account}';");
            }
        }

        /// <summary>
        /// Opening the boss portal without doing the work. The kill counter is state,
        /// so it is protected like any other state rather than by the endpoint alone.
        /// </summary>
        [SkippableTheory]
        [InlineData("anon")]
        [InlineData("authenticated")]
        public async Task ALeakedKeyCannotOpenTheBossGate(string role)
        {
            await using var db = await Database.OpenAsync();
            var account = Guid.NewGuid();

            Guid character = await Seed(db, account);

            try
            {
                await Database.ExecuteAsync(db, $"set role {role};");

                string error = await Database.ErrorFromAsync(db, $@"
                    insert into kill_counter (character_id, monster_id, active_kills)
                    values ('{character}', 'goblin', 1000);");

                Assert.NotNull(error);
                Assert.Contains("row-level security", error);
            }
            finally
            {
                await Database.ExecuteAsync(db, "reset role;");
                await Database.ExecuteAsync(db, $"delete from auth.users where id = '{account}';");
            }
        }

        private static async Task<Guid> Seed(NpgsqlConnection db, Guid account)
        {
            var character = Guid.NewGuid();

            await Database.ExecuteAsync(db, $@"
                insert into auth.users (id, instance_id, aud, role, email,
                                        encrypted_password, created_at, updated_at)
                values ('{account}', '00000000-0000-0000-0000-000000000000',
                        'authenticated', 'authenticated', '{account}@test.invalid', '', now(), now());

                insert into account (id, display_name) values ('{account}', 'Victim');

                insert into character (id, account_id, name)
                values ('{character}', '{account}', 'Victim-{character.ToString()[..8]}');

                insert into wallet (account_id, currency, balance) values ('{account}', 'coins', 100);
            ");

            return character;
        }
    }

    /// <summary>
    /// One database at a time. These tests set and reset the session role, and xunit
    /// would otherwise run them in parallel against connections from the same pool.
    /// </summary>
    [CollectionDefinition("database", DisableParallelization = true)]
    public class DatabaseCollection { }
}
