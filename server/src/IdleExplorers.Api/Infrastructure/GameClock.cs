namespace IdleExplorers.Api.Infrastructure;

/// <summary>
/// What time it is, according to the only party allowed an opinion.
///
/// ══ WHY THIS IS AN INTERFACE ══════════════════════════════════════════════════
///
/// An idle game is a function of elapsed time, so a test that cannot move time can
/// only test the first three seconds of it. Every rule takes elapsed time as an
/// argument for that reason, and every endpoint gets "now" from here — so a scenario
/// test advances eight hours in a microsecond and asserts on the payout.
///
/// ══ WHY THE PRODUCTION ONE READS THE DATABASE ═════════════════════════════════
///
/// Not the process clock. Several instances of this API behind Cloudflare do not
/// share a wall clock, and a machine whose NTP has drifted forward by a minute would
/// pay every player who reached it a minute of free progress — then the next request
/// on a correct machine would try to settle backwards and hit the trigger.
///
/// One clock, and it is the one the rows are written against.
/// </summary>
public interface IGameClock
{
    Task<DateTimeOffset> NowAsync(CancellationToken cancellation = default);
}

/// <summary>
/// Postgres <c>now()</c>. The clock of record.
///
/// ══ ONE INSTANT PER REQUEST ═══════════════════════════════════════════════════
///
/// Registered scoped and cached after the first read, which is both faster and more
/// correct. Faster because reading it was a whole extra connection on a path that had
/// six already — at three hundred concurrent players that mattered. More correct
/// because a request that settles, then checks a gate, then settles again should be
/// reasoning about ONE moment: two reads microseconds apart can straddle a boundary
/// and make the same request disagree with itself about what time it is.
/// </summary>
public sealed class DatabaseClock(Db db) : IGameClock
{
    private DateTimeOffset? _now;

    public async Task<DateTimeOffset> NowAsync(CancellationToken cancellation = default)
    {
        if (_now is { } cached) return cached;

        _now = await ReadAsync(cancellation);
        return _now.Value;
    }

    private async Task<DateTimeOffset> ReadAsync(CancellationToken cancellation)
    {
        await using var connection = await db.OpenAsync(cancellation);
        await using var command    = connection.CreateCommand();

        command.CommandText = "select now();";

        // Read as DateTimeOffset explicitly. Npgsql maps timestamptz to a DateTime
        // with Kind=Utc by default, and a blind cast to DateTimeOffset throws -- which
        // is a readiness probe failing for a type error rather than a database
        // problem, at the exact moment somebody is trying to work out which it is.
        await using var reader = await command.ExecuteReaderAsync(cancellation);
        await reader.ReadAsync(cancellation);

        return reader.GetFieldValue<DateTimeOffset>(0);
    }
}

/// <summary>
/// A clock tests can wind forward.
///
/// Deliberately NOT registered in the production container. A test clock reachable in
/// production is a way to mint currency, and "we only register it in Development" is
/// a configuration promise rather than a structural one — so it lives in the test
/// assembly's own registration and cannot be switched on from a config file.
/// </summary>
public sealed class FixedClock(DateTimeOffset start) : IGameClock
{
    private DateTimeOffset _now = start;

    public DateTimeOffset Now => _now;

    public void Advance(TimeSpan by) => _now = _now.Add(by);

    public Task<DateTimeOffset> NowAsync(CancellationToken cancellation = default) =>
        Task.FromResult(_now);
}
