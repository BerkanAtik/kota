namespace Kota.Core;

public enum Provider { Claude, Codex }

public enum LimitErrorKind { NotInstalled, NotLoggedIn, TokenExpired, RateLimited, Network, NoData, Unknown }

public sealed record LimitError(LimitErrorKind Kind, string? Detail = null);

/// <summary>One rate-limit window, e.g. Claude's 5-hour session or Codex's weekly limit.</summary>
public sealed record LimitWindow(string Key, double Percent, DateTimeOffset? ResetsAt, TimeSpan Length)
{
    /// <summary>If the reset time has passed, the window has started over at 0%.</summary>
    public LimitWindow Normalize(DateTimeOffset now) =>
        ResetsAt is { } r && r <= now ? this with { Percent = 0, ResetsAt = null } : this;
}

public sealed record ProviderLimits(
    Provider Provider,
    IReadOnlyList<LimitWindow> Windows,
    string? Plan,
    DateTimeOffset FetchedAt,
    DateTimeOffset? DataAsOf = null,
    LimitError? Error = null)
{
    public static ProviderLimits Failed(Provider provider, LimitErrorKind kind, DateTimeOffset now,
        string? plan = null, string? detail = null) =>
        new(provider, [], plan, now, null, new LimitError(kind, detail));

    public double? MaxPercent(DateTimeOffset now) =>
        Windows.Count == 0 ? null : Windows.Max(w => w.Normalize(now).Percent);
}

public static class KnownPaths
{
    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string ClaudeDir => FirstPath(Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR"))
                                      ?? Path.Combine(Home, ".claude");

    public static string CodexDir => FirstPath(Environment.GetEnvironmentVariable("CODEX_HOME"))
                                     ?? Path.Combine(Home, ".codex");

    public static string KotaDataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kota");

    static string? FirstPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var first = value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        return string.IsNullOrEmpty(first) ? null : first;
    }
}
