using System;
using System.Threading.Tasks;
using Npgsql;
using Xunit;

namespace IdleExplorers.Database.Tests
{
    /// <summary>
    /// The invariants the database enforces better than any application can.
    ///
    /// Everything here is a rule that has to hold even when the API is wrong. An
    /// application-level check is a promise about code that exists today; a
    /// constraint is a promise about every row that will ever be written, including
    /// by a migration, a hotfix, or somebody at a psql prompt at two in the morning.
    ///
    /// These run against a real Postgres deliberately. SQLite would pass most of them
    /// and prove nothing about the ones that matter.
    /// </summary>
    [Collection("database")]
    public class SchemaInvariantTests : IAsyncLifetime
    {
        private NpgsqlConnection _db;
        private Guid             _account;
        private Guid             _character;

        public async Task InitializeAsync()
        {
            if (!Database.Reachable) return;

            _db        = await Database.OpenAsync();
            _account   = Guid.NewGuid();
            _character = Guid.NewGuid();

            // A real auth user, because account.id references it — the same way the
            // API will have to create one.
            await Database.ExecuteAsync(_db, $@"
                insert into auth.users (id, instance_id, aud, role, email,
                                        encrypted_password, created_at, updated_at)
                values ('{_account}', '00000000-0000-0000-0000-000000000000',
                        'authenticated', 'authenticated', '{_account}@test.invalid',
                        '', now(), now());

                insert into account (id, display_name) values ('{_account}', 'Tester');

                insert into character (id, account_id, name)
                values ('{_character}', '{_account}', 'Bricta');

                insert into activity (character_id) values ('{_character}');
            ");
        }

        public async Task DisposeAsync()
        {
            if (_db == null) return;

            // Cascades all the way down from auth.users.
            await Database.ExecuteAsync(_db, $"delete from auth.users where id = '{_account}';");
            await _db.DisposeAsync();
        }

        // ── Time ──────────────────────────────────────────────────────────────

        /// <summary>
        /// The cheapest duplication exploit there is: settle a window, wind the clock
        /// back, settle it again. The API must never do it, and the database must not
        /// let it even if the API tries.
        /// </summary>
        [SkippableFact]
        public async Task SettlementTimeCannotMoveBackwards()
        {
            string error = await Database.ErrorFromAsync(_db, $@"
                update activity set last_settled_at = now() - interval '1 hour'
                 where character_id = '{_character}';");

            Assert.NotNull(error);
            Assert.Contains("may not move backwards", error);
        }

        [SkippableFact]
        public async Task SettlementTimeMovesForwardFreely()
        {
            string error = await Database.ErrorFromAsync(_db, $@"
                update activity set last_settled_at = now() + interval '1 minute'
                 where character_id = '{_character}';");

            Assert.Null(error);
        }

        /// <summary>
        /// Progress is a FRACTION of an action. A value at or above one is a whole
        /// action that was never paid out, and it would sit there being re-counted.
        /// </summary>
        [SkippableFact]
        public async Task ProgressCannotHoldAWholeAction()
        {
            Assert.NotNull(await Database.ErrorFromAsync(_db,
                $"update activity set progress = 1.0 where character_id = '{_character}';"));

            Assert.NotNull(await Database.ErrorFromAsync(_db,
                $"update activity set progress = -0.5 where character_id = '{_character}';"));

            Assert.Null(await Database.ErrorFromAsync(_db,
                $"update activity set progress = 0.999 where character_id = '{_character}';"));
        }

        /// <summary>
        /// Purchased AFK time adds to a credit the settlement drains. Rewinding
        /// last_settled_at instead would be a currency printer, which is why the
        /// column exists at all.
        /// </summary>
        [SkippableFact]
        public async Task PurchasedTimeIsACreditRatherThanARewind()
        {
            Assert.Null(await Database.ErrorFromAsync(_db,
                $"update activity set credited_seconds = 259200 where character_id = '{_character}';"));

            Assert.NotNull(await Database.ErrorFromAsync(_db,
                $"update activity set credited_seconds = -1 where character_id = '{_character}';"));
        }

        // ── Idempotency ───────────────────────────────────────────────────────

        /// <summary>
        /// WebGL tabs are suspended mid-request constantly. The second arrival of one
        /// craft must not be a second craft, and the constraint is what guarantees it
        /// even if the middleware has a hole.
        /// </summary>
        [SkippableFact]
        public async Task OneRequestIdIsAcceptedOnce()
        {
            string insert = $@"
                insert into idempotency_record (account_id, request_id, endpoint, status_code, response)
                values ('{_account}', 'craft-42', '/craft/batch', 200, '{{}}');";

            Assert.Null(await Database.ErrorFromAsync(_db, insert));

            string second = await Database.ErrorFromAsync(_db, insert);
            Assert.NotNull(second);
            Assert.Contains("duplicate key", second);
        }

        [SkippableFact]
        public async Task TheSameRequestIdOnAnotherAccountIsFine()
        {
            var other = Guid.NewGuid();

            await Database.ExecuteAsync(_db, $@"
                insert into auth.users (id, instance_id, aud, role, email,
                                        encrypted_password, created_at, updated_at)
                values ('{other}', '00000000-0000-0000-0000-000000000000',
                        'authenticated', 'authenticated', '{other}@test.invalid', '', now(), now());
                insert into account (id, display_name) values ('{other}', 'Other');

                insert into idempotency_record (account_id, request_id, endpoint, status_code, response)
                values ('{_account}', 'shared-id', '/craft/batch', 200, '{{}}');
                insert into idempotency_record (account_id, request_id, endpoint, status_code, response)
                values ('{other}', 'shared-id', '/craft/batch', 200, '{{}}');
            ");

            await Database.ExecuteAsync(_db, $"delete from auth.users where id = '{other}';");
        }

        // ── Money ─────────────────────────────────────────────────────────────

        [SkippableFact]
        public async Task ABalanceCannotGoNegative()
        {
            string error = await Database.ErrorFromAsync(_db,
                $"insert into wallet (account_id, currency, balance) values ('{_account}', 'relic_coins', -5);");

            Assert.NotNull(error);
            Assert.Contains("check constraint", error);
        }

        [SkippableFact]
        public async Task AnInventedCurrencyIsRefused()
        {
            Assert.NotNull(await Database.ErrorFromAsync(_db,
                $"insert into wallet (account_id, currency, balance) values ('{_account}', 'doubloons', 1);"));
        }

        /// <summary>
        /// THE invariant: a balance has to be explainable by its ledger. Asserted here
        /// on a small worked example, and again after every scenario test on whatever
        /// the scenario produced.
        /// </summary>
        [SkippableFact]
        public async Task TheLedgerExplainsTheBalance()
        {
            await Database.ExecuteAsync(_db, $@"
                insert into wallet (account_id, currency, balance) values ('{_account}', 'coins', 0);

                insert into wallet_ledger (account_id, currency, delta, reason) values
                    ('{_account}', 'coins',  500, 'loot'),
                    ('{_account}', 'coins',  250, 'loot'),
                    ('{_account}', 'coins', -300, 'shop_purchase');

                update wallet set balance = 450 where account_id = '{_account}' and currency = 'coins';
            ");

            long drift = await Database.ScalarAsync<long>(_db, $@"
                select w.balance - coalesce(sum(l.delta), 0)
                  from wallet w
                  left join wallet_ledger l
                    on l.account_id = w.account_id and l.currency = w.currency
                 where w.account_id = '{_account}' and w.currency = 'coins'
                 group by w.balance;");

            Assert.Equal(0L, drift);
        }

        /// <summary>
        /// Sending the same receipt thirty-seven times must grant one purchase. The
        /// unique constraint is the whole mechanism -- everything above it is
        /// convenience.
        /// </summary>
        [SkippableFact]
        public async Task OneStoreReceiptGrantsOnce()
        {
            string insert = $@"
                insert into purchase (account_id, provider, purchase_token, product_id)
                values ('{_account}', 'google_play', 'GPA.1234-5678', 'relic_pack_medium');";

            Assert.Null(await Database.ErrorFromAsync(_db, insert));
            Assert.NotNull(await Database.ErrorFromAsync(_db, insert));
        }

        /// <summary>
        /// The same receipt claimed by a DIFFERENT account is the interesting case:
        /// scoping the constraint per account would make a leaked receipt a free
        /// purchase for everyone who saw it.
        /// </summary>
        [SkippableFact]
        public async Task AReceiptCannotBeReusedByAnotherAccount()
        {
            var thief = Guid.NewGuid();

            await Database.ExecuteAsync(_db, $@"
                insert into auth.users (id, instance_id, aud, role, email,
                                        encrypted_password, created_at, updated_at)
                values ('{thief}', '00000000-0000-0000-0000-000000000000',
                        'authenticated', 'authenticated', '{thief}@test.invalid', '', now(), now());
                insert into account (id, display_name) values ('{thief}', 'Thief');

                insert into purchase (account_id, provider, purchase_token, product_id)
                values ('{_account}', 'google_play', 'GPA.shared', 'relic_pack_small');
            ");

            string error = await Database.ErrorFromAsync(_db, $@"
                insert into purchase (account_id, provider, purchase_token, product_id)
                values ('{thief}', 'google_play', 'GPA.shared', 'relic_pack_small');");

            await Database.ExecuteAsync(_db, $"delete from auth.users where id = '{thief}';");

            Assert.NotNull(error);
            Assert.Contains("duplicate key", error);
        }

        // ── Identity ──────────────────────────────────────────────────────────

        /// <summary>
        /// One Play Games profile cannot be two accounts. Without this a re-link races
        /// itself and the player ends up with their characters split across two.
        /// </summary>
        [SkippableFact]
        public async Task OneExternalIdentityBelongsToOneAccount()
        {
            var second = Guid.NewGuid();

            await Database.ExecuteAsync(_db, $@"
                insert into auth.users (id, instance_id, aud, role, email,
                                        encrypted_password, created_at, updated_at)
                values ('{second}', '00000000-0000-0000-0000-000000000000',
                        'authenticated', 'authenticated', '{second}@test.invalid', '', now(), now());
                insert into account (id, display_name) values ('{second}', 'Second');

                insert into account_identity (account_id, provider, provider_user_id)
                values ('{_account}', 'play_games', 'g-player-1');
            ");

            string error = await Database.ErrorFromAsync(_db, $@"
                insert into account_identity (account_id, provider, provider_user_id)
                values ('{second}', 'play_games', 'g-player-1');");

            await Database.ExecuteAsync(_db, $"delete from auth.users where id = '{second}';");

            Assert.NotNull(error);
        }

        // ── Characters ────────────────────────────────────────────────────────

        [SkippableFact]
        public async Task TwoLivingCharactersCannotShareAName()
        {
            string error = await Database.ErrorFromAsync(_db,
                $"insert into character (account_id, name) values ('{_account}', 'Bricta');");

            Assert.NotNull(error);
            Assert.Contains("duplicate key", error);
        }

        /// <summary>Deleting a character frees the name, without a table of tombstones.</summary>
        [SkippableFact]
        public async Task DeletingACharacterFreesItsName()
        {
            await Database.ExecuteAsync(_db,
                $"update character set deleted_at = now() where id = '{_character}';");

            Assert.Null(await Database.ErrorFromAsync(_db,
                $"insert into character (account_id, name) values ('{_account}', 'Bricta');"));
        }

        [SkippableFact]
        public async Task QuantitiesAreAlwaysPositive()
        {
            // A zero-quantity row is an empty slot pretending to hold something, and a
            // negative one is an item duplication bug wearing a rounding error.
            Assert.NotNull(await Database.ErrorFromAsync(_db, $@"
                insert into inventory_slot (character_id, slot_index, item_id, quantity)
                values ('{_character}', 0, 'tin_ore', 0);"));

            Assert.NotNull(await Database.ErrorFromAsync(_db, $@"
                insert into inventory_slot (character_id, slot_index, item_id, quantity)
                values ('{_character}', 1, 'tin_ore', -100);"));
        }

        [SkippableFact]
        public async Task KillCountsAreAlwaysPositive()
        {
            Assert.NotNull(await Database.ErrorFromAsync(_db, $@"
                insert into kill_counter (character_id, monster_id, active_kills)
                values ('{_character}', 'goblin', -1);"));
        }

        /// <summary>
        /// Deleting an account has to take everything with it, or a deletion request
        /// leaves orphaned rows nobody can find and nobody can explain.
        /// </summary>
        [SkippableFact]
        public async Task DeletingAnAccountCascades()
        {
            var doomed = Guid.NewGuid();

            await Database.ExecuteAsync(_db, $@"
                insert into auth.users (id, instance_id, aud, role, email,
                                        encrypted_password, created_at, updated_at)
                values ('{doomed}', '00000000-0000-0000-0000-000000000000',
                        'authenticated', 'authenticated', '{doomed}@test.invalid', '', now(), now());
                insert into account (id, display_name) values ('{doomed}', 'Doomed');
                insert into character (account_id, name) values ('{doomed}', 'Ghost');
                insert into wallet (account_id, currency, balance) values ('{doomed}', 'coins', 10);
            ");

            await Database.ExecuteAsync(_db, $"delete from auth.users where id = '{doomed}';");

            Assert.Equal(0L, await Database.ScalarAsync<long>(_db,
                $"select count(*) from character where account_id = '{doomed}';"));
            Assert.Equal(0L, await Database.ScalarAsync<long>(_db,
                $"select count(*) from wallet where account_id = '{doomed}';"));
        }
    }
}
