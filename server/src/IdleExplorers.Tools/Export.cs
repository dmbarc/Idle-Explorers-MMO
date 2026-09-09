using System.Text;
using System.Text.Json;
using IdleExplorers.Rules;
using Npgsql;

namespace IdleExplorers.Tools;

/// <summary>
/// Telemetry out of Postgres and into a file somebody can read.
///
/// ══ WHY NDJSON ════════════════════════════════════════════════════════════════
///
/// One JSON object per line, no enclosing array. That single property is what makes
/// the format work for every consumer this needs:
///
///   * it streams -- a reader never holds the whole file, so an export that outgrows
///     memory is still readable
///   * it appends -- an incremental export concatenates, where a JSON array would
///     need its closing bracket rewritten
///   * jq, DuckDB and pandas all read it natively
///   * and a truncated write costs the last line rather than the whole document
///
/// ══ WHY THE PAYLOAD IS AN ARRAY OF PAIRS ══════════════════════════════════════
///
/// Because the Unity explorer reads these lines with JsonUtility, which cannot
/// deserialise a dictionary and does not say so -- it returns an empty one. The same
/// constraint shaped every API response in this project. See TelemetryRecord.
///
/// ══ WHY THE SHAPE IS THE SHARED TYPE ══════════════════════════════════════════
///
/// This writes TelemetryRecord and the explorer reads TelemetryRecord, so a field
/// renamed here is a field renamed there. Serialising an anonymous object with the
/// right field names today would be a shape that drifts the first time anybody
/// touches either end.
/// </summary>
public static class Export
{
    /// <summary>
    /// Rows fetched per round trip.
    ///
    /// The reader streams, so this only bounds how much Npgsql buffers. Large enough
    /// that a million-row export is not a million round trips.
    /// </summary>
    public const int FetchSize = 10_000;

    /// <summary>
    /// System.Text.Json options for a type made of FIELDS.
    ///
    /// IncludeFields is the entire reason this is here. Without it every record
    /// serialises as {} -- no error, no warning, just an export of empty objects. The
    /// type is fields rather than properties because JsonUtility on the other end only
    /// reads fields, so both serialisers have to be told about the same shape and only
    /// one of them needs telling.
    /// </summary>
    private static readonly JsonSerializerOptions Wire = new()
    {
        IncludeFields = true,
        WriteIndented = false,

        // ══ WHY READ-ONLY PROPERTIES ARE EXCLUDED ═════════════════════════════
        //
        // IncludeFields adds fields; it does not stop properties. TelemetryRecord has
        // OccurredAtUtc, a get-only convenience for the explorer -- and without this
        // every exported line carried a duplicate timestamp under a key JsonUtility
        // does not read. Harmless per row and megabytes across an export, and it hid
        // the real fields at the front of every line while somebody was reading one.
        IgnoreReadOnlyProperties = true,
    };

    public static async Task<int> RunAsync(Options options)
    {
        Directory.CreateDirectory(options.Out);

        string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        string path  = Path.Combine(options.Out, $"telemetry-{stamp}.ndjson");

        await using var connection = new NpgsqlConnection(options.Connection);
        await connection.OpenAsync();

        // UTF-8 without a BOM. A BOM on the first line makes it the one line jq
        // refuses to parse, and the failure reads as a malformed record rather than as
        // three bytes of encoding.
        await using var file = new StreamWriter(path, append: false, new UTF8Encoding(false));

        (long rows, long highest) gameplay =
            await WriteStreamAsync(connection, file, options, gameplay: true);

        (long rows, long highest) security =
            await WriteStreamAsync(connection, file, options, gameplay: false);

        await file.FlushAsync();

        Console.WriteLine($"{path}");
        Console.WriteLine($"  gameplay  {gameplay.rows,8:N0} rows");
        Console.WriteLine($"  security  {security.rows,8:N0} rows");

        // The exact flags for the next run, so an incremental export is copy and paste
        // rather than arithmetic. Printed even on an empty run, carrying forward the
        // cursors that were passed in -- an operator who has to reconstruct their
        // position after a night with no players will get it wrong.
        Console.WriteLine();
        Console.WriteLine($"  resume with: --after-gameplay {Math.Max(gameplay.highest, options.AfterGameplay)}" +
                          $" --after-security {Math.Max(security.highest, options.AfterSecurity)}");

        if (gameplay.rows + security.rows == 0L)
        {
            Console.WriteLine();
            Console.WriteLine("  Nothing matched. An empty export is a valid answer, but check");
            Console.WriteLine("  --since and --after before concluding nobody played.");
        }

        return 0;
    }

    /// <summary>
    /// One stream into the open file.
    ///
    /// Both streams land in the same file, distinguished by the `stream` field. They
    /// stay separate TABLES, which is where the separation earns its keep -- but the
    /// question "what was this account doing when it got refused" needs both, and
    /// answering it across two files means writing a join by hand.
    /// </summary>
    private static async Task<(long Rows, long Highest)> WriteStreamAsync(
        NpgsqlConnection connection, StreamWriter file, Options options, bool gameplay)
    {
        string sql = gameplay
            ? """
              select e.id, e.event, e.account_id, e.character_id, c.name, e.occurred_at, e.payload::text
                from telemetry_event e
                left join character c on c.id = e.character_id
               where e.id > $1
                 and ($2::timestamptz is null or e.occurred_at >= $2)
               order by e.id
               limit $3;
              """
            : """
              select e.id, e.kind, e.account_id, null::uuid, null::text, e.occurred_at, e.detail::text
                from security_event e
               where e.id > $1
                 and ($2::timestamptz is null or e.occurred_at >= $2)
               order by e.id
               limit $3;
              """;

        await using var command = new NpgsqlCommand(sql, connection);

        command.Parameters.AddWithValue(gameplay ? options.AfterGameplay : options.AfterSecurity);
        command.Parameters.AddWithValue(options.Since.HasValue ? options.Since.Value : DBNull.Value);
        command.Parameters.AddWithValue(options.Limit);

        // Sequential access: the payload is the widest column and the last one read, so
        // nothing buffers a row that has already been written out.
        await using var reader = await command.ExecuteReaderAsync();

        long written = 0L, highest = 0L;

        while (await reader.ReadAsync())
        {
            var record = new TelemetryRecord
            {
                stream        = gameplay ? TelemetryStreams.Gameplay : TelemetryStreams.Security,
                id            = reader.GetInt64(0),
                eventName     = reader.GetString(1),
                accountId     = reader.IsDBNull(2) ? "" : reader.GetGuid(2).ToString(),
                characterId   = reader.IsDBNull(3) ? "" : reader.GetGuid(3).ToString(),
                characterName = reader.IsDBNull(4) ? "" : reader.GetString(4),

                // Round-trip format, always UTC. "o" is the one format DateTime.TryParse
                // reads back without ambiguity in every culture, which matters because
                // the reader is an editor window on somebody else's machine.
                occurredAt    = reader.GetFieldValue<DateTimeOffset>(5)
                                      .ToUniversalTime().ToString("o"),

                payload       = Pairs(reader.IsDBNull(6) ? "" : reader.GetString(6)),
            };

            await file.WriteLineAsync(JsonSerializer.Serialize(record, Wire));

            written++;
            highest = record.id;   // ordered by id, so the last one is the highest
        }

        return (written, highest);
    }

    /// <summary>
    /// Flattens a jsonb object into key/value pairs, values as strings.
    ///
    /// ══ WHY EVERYTHING BECOMES A STRING ═══════════════════════════════════════
    ///
    /// A settled payload holds longs; a map_enter holds a name. JsonUtility has no
    /// variant type, so the choice is one string field or a different class per event.
    /// The explorer parses the handful of numbers it charts.
    ///
    /// ══ WHY NESTED VALUES ARE KEPT AS THEIR JSON TEXT ═════════════════════════
    ///
    /// Rather than flattened with dotted keys, or dropped. A payload should never
    /// contain an object -- the ingest endpoint only accepts flat pairs -- but "should
    /// never" is not "cannot", and an export that silently loses a field is worse than
    /// one with an unreadable field in it. Keeping the raw text means the data survives
    /// even when nothing pretty can be done with it.
    /// </summary>
    public static TelemetryPair[] Pairs(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];

        List<TelemetryPair> pairs;

        try
        {
            using var document = JsonDocument.Parse(json);

            if (document.RootElement.ValueKind != JsonValueKind.Object) return [];

            pairs = [];

            foreach (var field in document.RootElement.EnumerateObject())
            {
                pairs.Add(new TelemetryPair
                {
                    k = field.Name,
                    v = field.Value.ValueKind switch
                    {
                        JsonValueKind.String => field.Value.GetString() ?? "",
                        JsonValueKind.Null   => "",
                        JsonValueKind.True   => "true",
                        JsonValueKind.False  => "false",
                        _                    => field.Value.GetRawText(),
                    },
                });
            }
        }
        catch (JsonException)
        {
            // The column is jsonb, so this cannot happen from the database. It can
            // happen from a test handing this function a string, and returning nothing
            // is a better answer than taking down an export of ten thousand good rows.
            return [];
        }

        return pairs.ToArray();
    }
}
