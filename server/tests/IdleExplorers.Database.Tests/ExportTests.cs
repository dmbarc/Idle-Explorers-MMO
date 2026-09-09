using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using IdleExplorers.Rules;
using IdleExplorers.Tools;
using Npgsql;
using Xunit;

namespace IdleExplorers.Database.Tests
{
    /// <summary>
    /// The telemetry export, end to end, against real rows.
    ///
    /// ══ WHY THIS IS A DATABASE TEST ═══════════════════════════════════════════════
    ///
    /// Because the parts that break are the parts a fake cannot have. The payload is
    /// jsonb, so what comes back out of it is Postgres's normalisation of what went in.
    /// The character name comes from a LEFT JOIN, so a security row with no character
    /// has to survive it. The timestamp is timestamptz, so the format written to the
    /// file is whatever Npgsql hands back and not whatever the author assumed.
    ///
    /// ══ WHAT IS ACTUALLY BEING PINNED ═════════════════════════════════════════════
    ///
    /// The literal JSON KEYS. The Unity explorer reads these lines with JsonUtility,
    /// which matches on field name and silently defaults anything it does not find --
    /// so an export whose keys drift loads as thousands of blank rows rather than as an
    /// error. Asserting on the raw text is the only way to catch a rename here, because
    /// deserialising with the same library that serialised it would agree with itself
    /// whatever the keys were called.
    /// </summary>
    public class ExportTests : IAsyncLifetime
    {
        private NpgsqlConnection _db;
        private Guid   _account;
        private Guid   _character;
        private string _scratch;

        public async Task InitializeAsync()
        {
            if (!Database.Reachable) return;

            _db        = await Database.OpenAsync();
            _account   = Guid.NewGuid();
            _character = Guid.NewGuid();
            _scratch   = Path.Combine(Path.GetTempPath(), "idle-export-" + Guid.NewGuid().ToString("N"));

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

                insert into telemetry_event (account_id, character_id, event, payload)
                values ('{_account}', '{_character}', 'settled',
                        '{{""kind"":""gather"",""skill"":""mining"",""actions"":412,""elapsed"":3600}}');

                insert into telemetry_event (account_id, character_id, event, payload)
                values ('{_account}', '{_character}', 'char_create',
                        '{{""classId"":""warrior""}}');

                insert into security_event (account_id, kind, detail)
                values ('{_account}', 'telemetry_not_reportable',
                        '{{""event"":""boss_defeated""}}');
            ");
        }

        public async Task DisposeAsync()
        {
            if (_db == null) return;

            await Database.ExecuteAsync(_db, $"delete from auth.users where id = '{_account}';");
            await _db.DisposeAsync();

            if (_scratch != null && Directory.Exists(_scratch)) Directory.Delete(_scratch, recursive: true);
        }

        // ── The file ──────────────────────────────────────────────────────────

        [SkippableFact]
        public async Task An_export_writes_one_parsable_object_per_line()
        {
            Skip.IfNot(Database.Reachable, Database.SkipReason);

            List<TelemetryRecord> mine = await ExportMineAsync();

            Assert.Equal(3, mine.Count);

            foreach (var record in mine)
            {
                Assert.False(string.IsNullOrEmpty(record.eventName));
                Assert.False(string.IsNullOrEmpty(record.occurredAt));
                Assert.True(record.id > 0L);
            }
        }

        [SkippableFact]
        public async Task The_json_keys_are_the_ones_JsonUtility_looks_for()
        {
            Skip.IfNot(Database.Reachable, Database.SkipReason);

            string[] lines = await RunAsync();

            // Found by MY character, not by event name. The export is whole-table and
            // the local stack is shared with every other test in the suite, so the
            // first "settled" line is somebody else's -- which is how the first version
            // of this test failed on a characterName it had never written.
            string line = FindLine(lines, "settled", _character);

            // Field for field. A rename on the server is silent on the client, so this
            // is the assertion that makes it loud.
            Assert.Contains("\"stream\":", line);
            Assert.Contains("\"id\":", line);
            Assert.Contains("\"eventName\":\"settled\"", line);
            Assert.Contains("\"accountId\":", line);
            Assert.Contains("\"characterId\":", line);
            Assert.Contains("\"characterName\":\"Bricta\"", line);
            Assert.Contains("\"occurredAt\":", line);

            // The payload is an ARRAY of {k,v}, never an object. JsonUtility cannot read
            // an object into a dictionary and does not say so.
            Assert.Contains("\"payload\":[{", line);
            Assert.Contains("\"k\":", line);
            Assert.Contains("\"v\":", line);
        }

        [SkippableFact]
        public async Task Values_of_every_json_type_survive_as_strings()
        {
            Skip.IfNot(Database.Reachable, Database.SkipReason);

            List<TelemetryRecord> mine = await ExportMineAsync();
            TelemetryRecord settled = Find(mine, "settled");

            Assert.Equal("gather", settled.Get("kind"));
            Assert.Equal("mining", settled.Get("skill"));

            // A jsonb number arrives as a number and leaves as its digits, so the
            // explorer can parse it back without knowing which fields are numeric.
            Assert.Equal("412", settled.Get("actions"));
            Assert.Equal(412L,  settled.GetLong("actions"));
            Assert.Equal(3600L, settled.GetLong("elapsed"));

            // A key that is not there is empty, never null -- the explorer prints it.
            Assert.Equal("",  settled.Get("nonsense"));
            Assert.Equal(0L,  settled.GetLong("nonsense"));
        }

        [SkippableFact]
        public async Task Both_streams_land_in_one_file_and_stay_distinguishable()
        {
            Skip.IfNot(Database.Reachable, Database.SkipReason);

            List<TelemetryRecord> mine = await ExportMineAsync();

            int gameplay = 0, security = 0;

            foreach (var record in mine)
            {
                if (record.stream == TelemetryStreams.Gameplay) gameplay++;
                if (record.stream == TelemetryStreams.Security) security++;
            }

            Assert.Equal(2, gameplay);
            Assert.Equal(1, security);

            // A security row has no character, and the LEFT JOIN must not drop it.
            TelemetryRecord refused = Find(mine, "telemetry_not_reportable");

            Assert.Equal("", refused.characterId);
            Assert.Equal("", refused.characterName);
            Assert.Equal(_account.ToString(), refused.accountId);
            Assert.Equal("boss_defeated", refused.Get("event"));
        }

        [SkippableFact]
        public async Task The_timestamp_reads_back_as_a_date()
        {
            Skip.IfNot(Database.Reachable, Database.SkipReason);

            List<TelemetryRecord> mine = await ExportMineAsync();

            foreach (var record in mine)
            {
                DateTime when = record.OccurredAtUtc;

                // The specific failure this catches: a format the writer produces and
                // the reader cannot parse comes back as MinValue, and every row in the
                // explorer then sorts and filters as the year one.
                Assert.NotEqual(DateTime.MinValue, when);
                Assert.Equal(DateTimeKind.Utc, when.Kind);
                Assert.True(when > new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            }
        }

        [SkippableFact]
        public async Task Each_stream_resumes_from_its_own_id()
        {
            Skip.IfNot(Database.Reachable, Database.SkipReason);

            List<TelemetryRecord> everything = await ExportMineAsync();

            long lastGameplay = 0L, lastSecurity = 0L;

            foreach (var record in everything)
            {
                if (record.stream == TelemetryStreams.Gameplay && record.id > lastGameplay)
                    lastGameplay = record.id;

                if (record.stream == TelemetryStreams.Security && record.id > lastSecurity)
                    lastSecurity = record.id;
            }

            Assert.True(lastGameplay > 0L && lastSecurity > 0L);

            // ══ THE BUG THIS EXISTS FOR ═══════════════════════════════════════
            //
            // telemetry_event and security_event have independent bigserial sequences,
            // and on this stack the gameplay one is hundreds ahead. A single shared
            // cursor therefore cannot be right for both: resuming security from the
            // gameplay id skips every security row ever written.
            //
            // The first version of the export took one --after and did exactly that.
            Assert.True(lastGameplay != lastSecurity,
                        "the two sequences are meant to be independent; this test proves nothing if they agree");

            List<TelemetryRecord> nothingLeft =
                await ExportMineAsync(afterGameplay: lastGameplay, afterSecurity: lastSecurity);

            Assert.Empty(nothingLeft);

            // Advance only one, and only that stream is exhausted.
            List<TelemetryRecord> securityOnly = await ExportMineAsync(afterGameplay: lastGameplay);

            Assert.Single(securityOnly);
            Assert.Equal(TelemetryStreams.Security, securityOnly[0].stream);

            List<TelemetryRecord> gameplayOnly = await ExportMineAsync(afterSecurity: lastSecurity);

            Assert.Equal(2, gameplayOnly.Count);
            foreach (var record in gameplayOnly)
                Assert.Equal(TelemetryStreams.Gameplay, record.stream);
        }

        [SkippableFact]
        public async Task One_shared_after_flag_is_refused_rather_than_guessed()
        {
            Skip.IfNot(Database.Reachable, Database.SkipReason);

            // An operator scripting a nightly export reaches for --after. Silently
            // ignoring it would re-export the whole table every night; silently
            // applying it to both streams would lose one of them. Neither is
            // discoverable, so it is an error with the right flags in the message.
            var thrown = Assert.Throws<ArgumentException>(() =>
                Options.Parse(new[] { "export", "--after", "42" }));

            Assert.Contains("--after-gameplay", thrown.Message);
            Assert.Contains("--after-security", thrown.Message);
        }

        [SkippableFact]
        public async Task No_read_only_property_leaks_into_the_wire()
        {
            Skip.IfNot(Database.Reachable, Database.SkipReason);

            string[] lines = await RunAsync();
            string   line  = FindLine(lines, "settled", _character);

            using var document = System.Text.Json.JsonDocument.Parse(line);

            var keys = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var field in document.RootElement.EnumerateObject()) keys.Add(field.Name);

            // Exactly the eight fields, and nothing else. System.Text.Json serialises
            // read-only PROPERTIES unless told not to, and TelemetryRecord has
            // OccurredAtUtc -- which shipped a duplicate timestamp on every line of
            // every export until this assertion was written.
            Assert.Equal(
                new[] { "accountId", "characterId", "characterName", "eventName",
                        "id", "occurredAt", "payload", "stream" },
                keys);
        }

        // ── The flattener, without a database ─────────────────────────────────

        [Fact]
        public void Nested_and_null_payload_values_are_kept_rather_than_dropped()
        {
            TelemetryPair[] pairs = Export.Pairs(
                """
                {"text":"hi","number":7,"yes":true,"no":false,"nothing":null,
                 "nested":{"a":1},"list":[1,2]}
                """);

            var found = new Dictionary<string, string>();
            foreach (var pair in pairs) found[pair.k] = pair.v;

            Assert.Equal("hi",    found["text"]);
            Assert.Equal("7",     found["number"]);
            Assert.Equal("true",  found["yes"]);
            Assert.Equal("false", found["no"]);
            Assert.Equal("",      found["nothing"]);

            // Kept as raw JSON. A payload should never hold these -- the ingest
            // endpoint only accepts flat pairs -- but an export that silently loses a
            // field is worse than one with an ugly field in it.
            Assert.Contains("\"a\"", found["nested"]);
            Assert.Contains("1",     found["list"]);
        }

        [Fact]
        public void Junk_flattens_to_nothing_rather_than_throwing()
        {
            // Not reachable from the database, which enforces jsonb. Reachable from a
            // caller, and one bad string must not take down an export of good rows.
            Assert.Empty(Export.Pairs("not json"));
            Assert.Empty(Export.Pairs("[1,2,3]"));
            Assert.Empty(Export.Pairs(""));
            Assert.Empty(Export.Pairs(null));
        }

        // ── Running it ────────────────────────────────────────────────────────

        private async Task<string[]> RunAsync(long afterGameplay = 0L, long afterSecurity = 0L)
        {
            Directory.CreateDirectory(_scratch);

            foreach (string stale in Directory.GetFiles(_scratch, "*.ndjson")) File.Delete(stale);

            int exit = await Export.RunAsync(Options.Parse(new[]
            {
                "export",
                "--out", _scratch,
                "--db",  Database.ConnectionString,
                "--after-gameplay", afterGameplay.ToString(),
                "--after-security", afterSecurity.ToString(),
            }));

            Assert.Equal(0, exit);

            string[] written = Directory.GetFiles(_scratch, "*.ndjson");
            Assert.Single(written);

            return File.ReadAllLines(written[0]);
        }

        /// <summary>
        /// Every line, filtered to the rows this test created.
        ///
        /// Filtered because the export is whole-table by design and the local stack is
        /// shared with every other test in the suite. A test asserting on a total row
        /// count would pass alone and fail in CI, which is the worst kind of test.
        /// </summary>
        private async Task<List<TelemetryRecord>> ExportMineAsync(long afterGameplay = 0L,
                                                                  long afterSecurity = 0L)
        {
            string[] lines = await RunAsync(afterGameplay, afterSecurity);
            var mine = new List<TelemetryRecord>();

            foreach (string line in lines)
            {
                if (line.Length == 0) continue;

                var record = System.Text.Json.JsonSerializer.Deserialize<TelemetryRecord>(
                    line, new System.Text.Json.JsonSerializerOptions { IncludeFields = true });

                if (record != null && record.accountId == _account.ToString()) mine.Add(record);
            }

            return mine;
        }

        private static TelemetryRecord Find(List<TelemetryRecord> records, string eventName)
        {
            foreach (var record in records)
                if (record.eventName == eventName) return record;

            Assert.Fail($"No '{eventName}' row in the export.");
            return null;
        }

        private static string FindLine(string[] lines, string eventName, Guid characterId)
        {
            foreach (string line in lines)
            {
                if (line.Contains($"\"eventName\":\"{eventName}\"") &&
                    line.Contains(characterId.ToString()))
                {
                    return line;
                }
            }

            Assert.Fail($"No '{eventName}' line for {characterId} in the export.");
            return null;
        }
    }
}
