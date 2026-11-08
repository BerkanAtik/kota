using System.Text;
using System.Text.Json;

namespace Kota.Core;

/// <summary>
/// Codex CLI writes its current plan limits into each session log ("token_count" events).
/// We read the newest one, so no network call or login is needed.
/// </summary>
public sealed class CodexLimitsReader(string codexDir)
{
    const long TailBytes = 2 * 1024 * 1024;

    string? _cachedPath;
    long _cachedLength = -1;
    string? _cachedLine;

    public ProviderLimits Read(DateTimeOffset now)
    {
        if (!Directory.Exists(codexDir))
            return ProviderLimits.Failed(Provider.Codex, LimitErrorKind.NotInstalled, now);
        var sessions = Path.Combine(codexDir, "sessions");
        if (!Directory.Exists(sessions))
            return ProviderLimits.Failed(Provider.Codex, LimitErrorKind.NoData, now);

        IEnumerable<FileInfo> newest;
        try
        {
            newest = new DirectoryInfo(sessions)
                .EnumerateFiles("*.jsonl", SearchOption.AllDirectories)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(5)
                .ToList();
        }
        catch (IOException)
        {
            return ProviderLimits.Failed(Provider.Codex, LimitErrorKind.NoData, now);
        }

        foreach (var file in newest)
        {
            var line = FindLastRateLimitLine(file);
            if (line is null) continue;
            try
            {
                return Parse(line, now);
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                // Unknown format in this file; try an older one.
            }
        }
        return ProviderLimits.Failed(Provider.Codex, LimitErrorKind.NoData, now);
    }

    string? FindLastRateLimitLine(FileInfo file)
    {
        if (file.FullName == _cachedPath && file.Length == _cachedLength) return _cachedLine;

        string? last = null;
        void Handle(ReadOnlySpan<byte> line)
        {
            if (line.IndexOf("\"token_count\""u8) >= 0 && line.IndexOf("\"rate_limits\""u8) >= 0
                && line.IndexOf("\"primary\""u8) >= 0)
                last = Encoding.UTF8.GetString(line);
        }

        try
        {
            long start = Math.Max(0, file.Length - TailBytes);
            JsonlReader.ReadFrom(file.FullName, start, Handle);
            if (last is null && start > 0) JsonlReader.ReadFrom(file.FullName, 0, Handle);
        }
        catch (IOException)
        {
            return null;
        }

        _cachedPath = file.FullName;
        _cachedLength = file.Length;
        _cachedLine = last;
        return last;
    }

    public static ProviderLimits Parse(string line, DateTimeOffset now)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        var asOf = root.Time("timestamp");
        var rateLimits = root.GetProperty("payload").GetProperty("rate_limits");
        var plan = rateLimits.Str("plan_type");

        var windows = new List<LimitWindow>();
        foreach (var key in new[] { "primary", "secondary" })
        {
            if (rateLimits.Obj(key) is not { } w || w.Double("used_percent") is not { } percent) continue;
            var minutes = w.Long("window_minutes");
            DateTimeOffset? resetsAt = null;
            if (w.Long("resets_at") is > 0 and var unix) resetsAt = DateTimeOffset.FromUnixTimeSeconds(unix);
            else if (w.Long("resets_in_seconds") is > 0 and var secs && asOf is { } t) resetsAt = t.AddSeconds(secs);

            var window = new LimitWindow("codex_" + key, percent, resetsAt, TimeSpan.FromMinutes(minutes));
            windows.Add(window.Normalize(now));
        }
        if (windows.Count == 0)
            return ProviderLimits.Failed(Provider.Codex, LimitErrorKind.NoData, now, plan);
        return new ProviderLimits(Provider.Codex, windows, plan, now, asOf);
    }
}
