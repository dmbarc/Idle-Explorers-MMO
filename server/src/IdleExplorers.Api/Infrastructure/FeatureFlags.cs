using Npgsql;

namespace IdleExplorers.Api.Infrastructure;

/// <summary>
/// Turning something off without shipping a Unity build.
///
/// ══ WHY THIS IS WORTH A TABLE ═════════════════════════════════════════════════
///
/// During a fast playtest the difference between "we disabled it in four minutes" and
/// "we took the game down for a day" is entirely whether the switch is data or code.
/// A WebGL build is a compile, an upload and a cache purge; a row is an UPDATE.
///
/// The things this is for are the things most likely to misbehave in front of the
/// first five players: a minigame that pays wrong, a boss that is unbeatable, a
/// weapon that one-shots everything. Each is worth turning off in isolation rather
/// than rolling back the whole deploy.
///
/// ══ SERVER-SIDE IS THE GATE; THE CLIENT COPY IS COURTESY ══════════════════════
///
/// Every flag with an EFFECT is checked here, in the handler that would do the thing.
/// The copy handed to the client exists so a disabled feature is hidden rather than
/// visible-and-broken -- which is a better experience and no part of the enforcement.
/// A client that ignores the copy gets a refusal, not a reward.
///
/// ══ WHY DEFAULT-ON FOR AN UNKNOWN FLAG ════════════════════════════════════════
///
/// A flag nobody has inserted means "nobody has turned this off", and that has to be
/// the answer -- otherwise adding a flag check to a handler silently disables the
/// feature the moment it deploys, before anybody has had a chance to insert the row.
/// The failure mode of default-off is "the game quietly stopped working and the
/// deploy looked fine", which is the worst kind.
///
/// A row that exists and says false is the only thing that turns anything off.
/// </summary>
public sealed class FeatureFlags(Db db)
{
    /// <summary>
    /// How long a read is trusted.
    ///
    /// ══ WHY THERE IS A CACHE AND WHY IT IS SHORT ══════════════════════════════
    ///
    /// Flags are checked on hot paths -- the minigame endpoint, the boss engage -- and
    /// a query per request would put the operations table in the middle of the game
    /// loop. But the whole value of a flag is reacting in minutes, so a long cache
    /// gives back exactly what the table was for.
    ///
    /// Ten seconds: unmeasurable load, and "we disabled it" is still true by the time
    /// somebody has finished saying it.
    /// </summary>
    public static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(10);

    // ── The flags that exist ──────────────────────────────────────────────────
    //
    // Named constants rather than string literals at the call sites, because a typo in
    // a flag name reads as "the flag is not set" and therefore as "the feature is on"
    // -- a switch that silently does nothing is worse than no switch.

    public const string GoblinKing     = "goblin_king";
    public const string Minigames      = "minigames";
    public const string Shop           = "shop";
    public const string TelemetryIngest = "telemetry_ingest";

    /// <summary>
    /// Every flag the client is told about.
    ///
    /// Listed, not discovered from the table, so the bootstrap payload has a stable
    /// shape whether or not anybody has inserted a row -- and so a client can rely on
    /// a flag being present rather than having to treat missing as a third state.
    /// </summary>
    public static readonly string[] Known =
    {
        GoblinKing, Minigames, Shop, TelemetryIngest,
    };

    private readonly Db _db = db;

    private Dictionary<string, bool> _cached = new(StringComparer.Ordinal);
    private DateTimeOffset _readAt = DateTimeOffset.MinValue;

    // Guards the refresh, so a burst of requests arriving on a cold cache issues one
    // query rather than one each. The lock is never held across the query itself.
    private readonly SemaphoreSlim _refreshing = new(1, 1);

    /// <summary>
    /// Whether a feature is on. Unknown flags are ON -- see the class comment.
    /// </summary>
    public async Task<bool> IsEnabledAsync(string flag, CancellationToken cancellation = default)
    {
        Dictionary<string, bool> flags = await CurrentAsync(cancellation);

        return !flags.TryGetValue(flag, out bool enabled) || enabled;
    }

    /// <summary>Every known flag and its state, for the bootstrap payload.</summary>
    public async Task<Dictionary<string, bool>> AllAsync(CancellationToken cancellation = default)
    {
        Dictionary<string, bool> flags = await CurrentAsync(cancellation);

        var answer = new Dictionary<string, bool>(StringComparer.Ordinal);

        foreach (string flag in Known)
            answer[flag] = !flags.TryGetValue(flag, out bool enabled) || enabled;

        // Anything set in the table but not in Known, so a flag added for an
        // experiment still reaches the client without a code change.
        foreach (var entry in flags) answer[entry.Key] = entry.Value;

        return answer;
    }

    /// <summary>Drops the cache. For tests, and for a deploy that wants a clean read.</summary>
    public void Forget() => _readAt = DateTimeOffset.MinValue;

    private async Task<Dictionary<string, bool>> CurrentAsync(CancellationToken cancellation)
    {
        // Read without the lock first. On the overwhelmingly common path the cache is
        // warm and this costs a comparison.
        if (DateTimeOffset.UtcNow - _readAt < CacheFor) return _cached;

        await _refreshing.WaitAsync(cancellation);

        try
        {
            // Checked again inside: whoever was queued behind the refresh does not need
            // to repeat it.
            if (DateTimeOffset.UtcNow - _readAt < CacheFor) return _cached;

            var fresh = new Dictionary<string, bool>(StringComparer.Ordinal);

            await using var connection = await _db.OpenAsync(cancellation);
            await using var command = connection.Sql("select flag, enabled from feature_flag;");
            await using var reader = await command.ExecuteReaderAsync(cancellation);

            while (await reader.ReadAsync(cancellation))
                fresh[reader.GetString(0)] = reader.GetBoolean(1);

            _cached = fresh;
            _readAt = DateTimeOffset.UtcNow;

            return _cached;
        }
        catch (Exception)
        {
            // ══ WHY A FAILED READ KEEPS THE LAST ANSWER ═══════════════════════
            //
            // A momentary database blip must not flip every feature in the game, in
            // either direction. Serving the previous answer for another ten seconds is
            // strictly better than a stampede of state changes triggered by one
            // connection timeout -- and the request that actually needs the database
            // is about to fail on its own terms anyway, with a message about the
            // database rather than about a feature being off.
            return _cached;
        }
        finally
        {
            _refreshing.Release();
        }
    }
}
