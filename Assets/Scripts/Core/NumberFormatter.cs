/// <summary>
/// Formats long quantities into human-readable strings with K/M/B/T/Qa suffixes.
/// All item quantities in Idle Explorers are stored as long (up to ~9.2Qa).
/// </summary>
public static class NumberFormatter
{
    public static string Format(long n)
    {
        // long.MinValue has no positive counterpart — negating it overflows back to
        // itself and recurses forever, so it is handled before the general case.
        if (n == long.MinValue)  return "-9.22Qa";
        if (n < 0)               return "-" + Format(-n);
        if (n < 1_000)           return n.ToString("N0");
        if (n < 1_000_000L)      return $"{n / 1_000.0:0.##}K";
        if (n < 1_000_000_000L)  return $"{n / 1_000_000.0:0.##}M";
        if (n < 1_000_000_000_000L)     return $"{n / 1_000_000_000.0:0.##}B";
        if (n < 1_000_000_000_000_000L) return $"{n / 1_000_000_000_000.0:0.##}T";
        return $"{n / 1_000_000_000_000_000.0:0.##}Qa";
    }

    /// <summary>Format XP for the Skills panel — same suffix rules.</summary>
    public static string FormatXP(long xp) => Format(xp);

    /// <summary>Format a rate multiplier (e.g. 1.5x, 0.7x).</summary>
    public static string FormatRate(float rate) => $"{rate:0.##}x";

    /// <summary>Format a percentage (e.g. 12.5%).</summary>
    public static string FormatPercent(float value) => $"{value:0.##}%";

    /// <summary>Format elapsed seconds as AFK time string (e.g. "14hr 23m").</summary>
    public static string FormatAFKTime(long elapsedSeconds)
    {
        if (elapsedSeconds < 60)   return "Just now";
        if (elapsedSeconds < 3600) return $"{elapsedSeconds / 60}m AFK";
        if (elapsedSeconds < 86400)
        {
            long h = elapsedSeconds / 3600;
            long m = (elapsedSeconds % 3600) / 60;
            return m > 0 ? $"{h}hr {m}m AFK" : $"{h}hr AFK";
        }
        long d = elapsedSeconds / 86400;
        long hr = (elapsedSeconds % 86400) / 3600;
        return hr > 0 ? $"{d}d {hr}hr AFK" : $"{d}d AFK";
    }
}
