namespace Kota.Core;

public enum PaceKind { Unknown, Safe, WillHitLimit, Full }

/// <param name="TimeToFull">When <see cref="PaceKind.WillHitLimit"/>: time until 100% at the current pace.</param>
/// <param name="ProjectedPercent">Expected usage at reset time if the pace stays the same.</param>
public sealed record PaceResult(PaceKind Kind, TimeSpan? TimeToFull = null, double? ProjectedPercent = null)
{
    public static readonly PaceResult Unknown = new(PaceKind.Unknown);
}

public static class Pace
{
    /// <summary>
    /// Right after a reset a single request can look like a huge rate, so wait until at least
    /// 10% of the window (and 30 minutes) has passed before projecting.
    /// </summary>
    static TimeSpan MinimumElapsed(TimeSpan length) =>
        TimeSpan.FromTicks(Math.Max(TimeSpan.FromMinutes(30).Ticks, length.Ticks / 10));

    /// <summary>
    /// Assumes usage so far in this window continues at the same average rate.
    /// Simple, but it needs no history and works the moment the app starts.
    /// </summary>
    public static PaceResult Estimate(LimitWindow window, DateTimeOffset now)
    {
        window = window.Normalize(now);
        if (window.Percent >= 100) return new PaceResult(PaceKind.Full);
        if (window.ResetsAt is not { } reset || window.Percent <= 0 || window.Length <= TimeSpan.Zero)
            return PaceResult.Unknown;

        var remaining = reset - now;
        var elapsed = window.Length - remaining;
        if (remaining <= TimeSpan.Zero || elapsed < MinimumElapsed(window.Length)) return PaceResult.Unknown;

        double percentPerHour = window.Percent / elapsed.TotalHours;
        var timeToFull = TimeSpan.FromHours((100 - window.Percent) / percentPerHour);
        double projected = window.Percent + percentPerHour * remaining.TotalHours;

        return timeToFull < remaining
            ? new PaceResult(PaceKind.WillHitLimit, timeToFull, projected)
            : new PaceResult(PaceKind.Safe, null, projected);
    }
}

public sealed record LimitAlert(Provider Provider, LimitWindow Window, int Threshold);

/// <summary>Decides when to notify, so each threshold fires once per limit window.</summary>
public sealed class AlertTracker
{
    readonly HashSet<string> _fired = [];
    readonly HashSet<Provider> _seeded = [];

    /// <summary>
    /// The first check for each provider only records the current state, so starting the app
    /// at 85% does not pop up a notification every time Windows starts.
    /// </summary>
    public IReadOnlyList<LimitAlert> Check(ProviderLimits limits, IReadOnlyCollection<int> thresholds)
    {
        bool silent = _seeded.Add(limits.Provider);
        var alerts = new List<LimitAlert>();
        foreach (var w in limits.Windows)
        {
            if (w.ResetsAt is not { } reset) continue;
            // Reset times can jitter by fractions of a second between calls; round to 10 minutes.
            long period = (long)Math.Round(reset.ToUnixTimeSeconds() / 600.0);
            LimitAlert? highest = null;
            foreach (int t in thresholds.OrderBy(t => t))
            {
                if (w.Percent < t) break;
                if (_fired.Add($"{limits.Provider}|{w.Key}|{period}|{t}")) highest = new LimitAlert(limits.Provider, w, t);
            }
            if (highest is not null && !silent) alerts.Add(highest);
        }
        return alerts;
    }
}
