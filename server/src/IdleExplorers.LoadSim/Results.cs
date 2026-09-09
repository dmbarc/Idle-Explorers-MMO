using System.Collections.Concurrent;

namespace IdleExplorers.LoadSim;

/// <summary>
/// What the run measured.
///
/// ══ WHY PERCENTILES AND NOT AVERAGES ══════════════════════════════════════════
///
/// The mean hides exactly the failure this simulation exists to find. Lock contention
/// on a character row does not slow everything down a little — it leaves ninety-nine
/// requests untouched and makes one wait behind a queue. The mean stays beautiful and
/// every player sees a multi-second stall several times an hour.
///
/// So p95 and p99 are the numbers, and the mean is printed only because its absence
/// would look like an oversight.
/// </summary>
public sealed class Results
{
    private readonly ConcurrentDictionary<string, Route> _routes = new();
    private long _lastPrintedTotal;

    /// <summary>
    /// A ceiling worth failing over.
    ///
    /// An idle game is forgiving about latency — nobody is aiming at anything — but a
    /// settle that takes longer than this stops feeling like a game responding and
    /// starts feeling like one that has hung.
    /// </summary>
    public const int AcceptableP99Milliseconds = 1000;

    /// <summary>
    /// How many failures are tolerable. Zero: every non-2xx here is either a bug or a
    /// refusal the simulated player should not have earned.
    /// </summary>
    public const double AcceptableFailureRate = 0d;

    public void Record(string path, TimeSpan elapsed, bool succeeded, string status)
    {
        // Character ids in a path would produce one bucket per player, which is a
        // thousand rows of nothing. Collapsed to the shape of the route.
        string route = Normalise(path);

        _routes.GetOrAdd(route, _ => new Route()).Add(elapsed, succeeded, status);
    }

    private static string Normalise(string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

        for (int i = 0; i < parts.Length; i++)
            if (Guid.TryParse(parts[i], out _)) parts[i] = "{id}";

        return "/" + string.Join('/', parts);
    }

    public void PrintTick(TimeSpan elapsed)
    {
        long total  = _routes.Values.Sum(r => r.Count);
        long failed = _routes.Values.Sum(r => r.Failed);

        long sinceLast = total - _lastPrintedTotal;
        _lastPrintedTotal = total;

        Console.WriteLine($"  {elapsed.TotalSeconds,5:0}s   {total,7} requests   " +
                          $"{sinceLast / 5.0,6:0.0}/s   {failed} failed");
    }

    public void PrintSummary(TimeSpan elapsed)
    {
        Console.WriteLine("─────────────────────────────────────────────────────────────────────────");
        Console.WriteLine($"{"route",-34}{"n",8}{"p50",8}{"p95",8}{"p99",8}{"fail",7}");
        Console.WriteLine("─────────────────────────────────────────────────────────────────────────");

        foreach ((string route, Route stats) in _routes.OrderByDescending(r => r.Value.Count))
        {
            Console.WriteLine($"{route,-34}{stats.Count,8}" +
                              $"{stats.Percentile(50),7:0}ms" +
                              $"{stats.Percentile(95),7:0}ms" +
                              $"{stats.Percentile(99),7:0}ms" +
                              $"{stats.Failed,7}");
        }

        Console.WriteLine("─────────────────────────────────────────────────────────────────────────");

        long total   = _routes.Values.Sum(r => r.Count);
        long failed  = _routes.Values.Sum(r => r.Failed);
        double perSecond = elapsed.TotalSeconds > 0 ? total / elapsed.TotalSeconds : 0;

        Console.WriteLine($"  {total} requests in {elapsed.TotalSeconds:0.0}s  ({perSecond:0.0}/s)");
        Console.WriteLine($"  {failed} failed");

        if (failed > 0)
        {
            Console.WriteLine();
            Console.WriteLine("  failures by status:");

            var byStatus = new SortedDictionary<string, long>();

            foreach (var route in _routes.Values)
                foreach ((string status, long count) in route.Failures)
                    byStatus[status] = byStatus.GetValueOrDefault(status) + count;

            foreach ((string status, long count) in byStatus)
                Console.WriteLine($"    {status,-24}{count}");
        }
    }

    /// <summary>
    /// Zero when the run was acceptable, so this can gate a pipeline rather than
    /// needing somebody to read the numbers.
    /// </summary>
    public int Verdict()
    {
        long total  = _routes.Values.Sum(r => r.Count);
        long failed = _routes.Values.Sum(r => r.Failed);

        if (total == 0)
        {
            Console.Error.WriteLine("no requests were made");
            return 1;
        }

        double failureRate = failed / (double)total;
        double worstP99    = _routes.Values.Max(r => r.Percentile(99));

        bool ok = true;

        if (failureRate > AcceptableFailureRate)
        {
            Console.Error.WriteLine($"FAIL  {failureRate:P2} of requests failed");
            ok = false;
        }

        if (worstP99 > AcceptableP99Milliseconds)
        {
            Console.Error.WriteLine($"FAIL  worst p99 was {worstP99:0}ms, over {AcceptableP99Milliseconds}ms");
            ok = false;
        }

        Console.WriteLine();
        Console.WriteLine(ok ? "LOAD OK" : "LOAD FAILED");

        return ok ? 0 : 1;
    }

    private sealed class Route
    {
        private readonly ConcurrentBag<double> _milliseconds = new();
        private readonly ConcurrentDictionary<string, long> _failures = new();

        private long _count;
        private long _failed;

        public long Count  => Interlocked.Read(ref _count);
        public long Failed => Interlocked.Read(ref _failed);

        public IEnumerable<KeyValuePair<string, long>> Failures => _failures;

        public void Add(TimeSpan elapsed, bool succeeded, string status)
        {
            _milliseconds.Add(elapsed.TotalMilliseconds);
            Interlocked.Increment(ref _count);

            if (succeeded) return;

            Interlocked.Increment(ref _failed);
            _failures.AddOrUpdate(status, 1, (_, n) => n + 1);
        }

        public double Percentile(int percentile)
        {
            double[] sorted = _milliseconds.ToArray();
            if (sorted.Length == 0) return 0d;

            Array.Sort(sorted);

            int index = (int)Math.Ceiling(percentile / 100d * sorted.Length) - 1;

            return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
        }
    }
}
