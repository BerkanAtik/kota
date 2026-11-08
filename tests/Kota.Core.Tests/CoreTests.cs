using Kota.Core;

namespace Kota.Core.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "kota-test-" + Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public string File(string relative, string content)
    {
        var full = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllText(full, content);
        return full;
    }
    public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
}

public class ClaudeLimitsTests
{
    [Fact]
    public void Parses_five_hour_and_weekly_windows()
    {
        const string json = """
        {"five_hour":{"utilization":87.0,"resets_at":"2026-11-08T16:10:00.156985+00:00"},
         "seven_day":{"utilization":40.0,"resets_at":"2026-11-10T09:00:00+00:00"},
         "seven_day_opus":null}
        """;
        var now = DateTimeOffset.Parse("2026-11-08T15:00:00Z");
        var limits = ClaudeLimitsClient.Parse(json, "pro", now);

        Assert.Null(limits.Error);
        Assert.Equal(2, limits.Windows.Count);
        Assert.Equal("five_hour", limits.Windows[0].Key);
        Assert.Equal(87.0, limits.Windows[0].Percent);
        Assert.Equal(TimeSpan.FromHours(5), limits.Windows[0].Length);
        Assert.Equal(87.0, limits.MaxPercent(now));
    }

    [Fact]
    public void Missing_windows_is_no_data()
    {
        var limits = ClaudeLimitsClient.Parse("{}", null, DateTimeOffset.UtcNow);
        Assert.Equal(LimitErrorKind.NoData, limits.Error?.Kind);
    }

    [Fact]
    public async Task Missing_credentials_reports_not_logged_in_without_network()
    {
        using var dir = new TempDir();
        using var http = new HttpClient(new FailingHandler());
        var limits = await new ClaudeLimitsClient(http, dir.Path).FetchAsync(CancellationToken.None);
        Assert.Equal(LimitErrorKind.NotLoggedIn, limits.Error?.Kind);
    }

    [Fact]
    public async Task Expired_token_is_not_sent()
    {
        using var dir = new TempDir();
        dir.File(".credentials.json", """{"claudeAiOauth":{"accessToken":"x","expiresAt":1000,"subscriptionType":"max"}}""");
        using var http = new HttpClient(new FailingHandler());
        var limits = await new ClaudeLimitsClient(http, dir.Path).FetchAsync(CancellationToken.None);
        Assert.Equal(LimitErrorKind.TokenExpired, limits.Error?.Kind);
        Assert.Equal("max", limits.Plan);
    }

    sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) =>
            throw new InvalidOperationException("No network call expected");
    }
}

public class CodexLimitsTests
{
    const string TokenCountLine = """{"timestamp":"2026-11-06T15:28:30.227Z","type":"event_msg","payload":{"type":"token_count","info":null,"rate_limits":{"primary":{"used_percent":16.0,"window_minutes":43200,"resets_at":1795881013},"secondary":{"used_percent":3.5,"window_minutes":300,"resets_at":1000},"plan_type":"go"}}}""";

    [Fact]
    public void Parses_rate_limits_and_resets_expired_windows()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1795000000);
        var limits = CodexLimitsReader.Parse(TokenCountLine, now);

        Assert.Equal("go", limits.Plan);
        Assert.Equal(16.0, limits.Windows[0].Percent);
        Assert.Equal(TimeSpan.FromDays(30), limits.Windows[0].Length);
        // The secondary window reset long ago, so it is back at 0%.
        Assert.Equal(0, limits.Windows[1].Percent);
        Assert.Null(limits.Windows[1].ResetsAt);
    }

    [Fact]
    public void Reads_newest_session_file()
    {
        using var dir = new TempDir();
        dir.File("sessions/2026/11/06/rollout-a.jsonl", "{\"type\":\"session_meta\",\"payload\":{}}\n" + TokenCountLine + "\n");
        var limits = new CodexLimitsReader(dir.Path).Read(DateTimeOffset.FromUnixTimeSeconds(1795000000));
        Assert.Null(limits.Error);
        Assert.Equal(16.0, limits.Windows[0].Percent);
    }

    [Fact]
    public void Missing_folder_is_not_installed()
    {
        var limits = new CodexLimitsReader(Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid())).Read(DateTimeOffset.UtcNow);
        Assert.Equal(LimitErrorKind.NotInstalled, limits.Error?.Kind);
    }
}

public class UsageHistoryTests
{
    static string ClaudeLine(string id, string req, int output, string ts = "2026-11-08T10:16:43.608Z") =>
        """{"type":"assistant","timestamp":"TS","requestId":"REQ","cwd":"C:\\code\\myapp","message":{"id":"ID","model":"claude-opus-5-5","usage":{"input_tokens":10,"output_tokens":OUT,"cache_creation_input_tokens":1000,"cache_read_input_tokens":2000,"cache_creation":{"ephemeral_1h_input_tokens":1000}}}}"""
            .Replace("TS", ts).Replace("REQ", req).Replace("\"ID\"", "\"" + id + "\"").Replace("OUT", output.ToString());

    [Fact]
    public void Counts_each_claude_message_once_across_files()
    {
        using var dir = new TempDir();
        var claude = Path.Combine(dir.Path, "claude");
        dir.File("claude/projects/C--code-myapp/a.jsonl", ClaudeLine("m1", "r1", 100) + "\n" + ClaudeLine("m1", "r1", 100) + "\n");
        // A resumed session copies m1 into a new file.
        dir.File("claude/projects/C--code-myapp/b.jsonl", ClaudeLine("m1", "r1", 100) + "\n" + ClaudeLine("m2", "r2", 50) + "\n");

        var history = new UsageHistory(claude, Path.Combine(dir.Path, "codex"), Path.Combine(dir.Path, "cache.bin"));
        var buckets = history.Refresh();

        var total = Assert.Single(buckets);
        Assert.Equal("myapp", total.Key.Project);
        Assert.Equal(150, total.Value.Output);
        Assert.Equal(2, total.Value.Requests);
        // 2 messages: input 10*$4 + output 75*$20 + 1h write 1000*$8 + read 2000*$0.20, per million.
        decimal expected = 2 * (10 * 4m + 1000 * 8m + 2000 * 0.20m) / 1_000_000m + 150 * 20m / 1_000_000m;
        Assert.Equal(expected, total.Value.Cost);
    }

    [Fact]
    public void Incremental_scan_and_cache_do_not_double_count()
    {
        using var dir = new TempDir();
        var claude = Path.Combine(dir.Path, "claude");
        var cache = Path.Combine(dir.Path, "cache.bin");
        var file = dir.File("claude/projects/p/a.jsonl", ClaudeLine("m1", "r1", 100) + "\n");

        new UsageHistory(claude, dir.Path, cache).Refresh();
        File.AppendAllText(file, ClaudeLine("m2", "r2", 7) + "\n" + "{\"partial");

        var reloaded = new UsageHistory(claude, dir.Path, cache);
        reloaded.Load();
        var buckets = reloaded.Refresh();
        Assert.Equal(107, buckets.Values.Sum(v => v.Output));

        // Completing the partial line later must not re-read earlier lines.
        File.AppendAllText(file, "\":1}\n");
        Assert.Equal(107, reloaded.Refresh().Values.Sum(v => v.Output));
    }

    [Fact]
    public void Codex_uses_deltas_of_cumulative_totals()
    {
        using var dir = new TempDir();
        static string Tokens(int input, int cached, int output) =>
            """{"timestamp":"2026-11-06T15:28:30Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":IN,"cached_input_tokens":CACHED,"output_tokens":OUT}}}}"""
                .Replace("IN,", input + ",").Replace("CACHED", cached.ToString()).Replace("OUT", output.ToString());
        dir.File("codex/sessions/2026/11/06/r.jsonl", string.Join("\n",
            """{"timestamp":"2026-11-06T15:00:00Z","type":"session_meta","payload":{"cwd":"D:\\work\\api"}}""",
            """{"timestamp":"2026-11-06T15:00:01Z","type":"turn_context","payload":{"model":"gpt-x"}}""",
            Tokens(1000, 800, 50),
            Tokens(1000, 800, 50), // repeated event
            Tokens(3000, 2500, 120)) + "\n");

        var buckets = new UsageHistory(Path.Combine(dir.Path, "claude"), Path.Combine(dir.Path, "codex"),
            Path.Combine(dir.Path, "c.bin")).Refresh();

        var (key, totals) = Assert.Single(buckets);
        Assert.Equal(Provider.Codex, key.Provider);
        Assert.Equal("api", key.Project);
        Assert.Equal("gpt-x", key.Model);
        Assert.Equal(3120, totals.Total);
        Assert.Equal(2500, totals.CacheRead);
    }

    [Fact]
    public void Daily_totals_use_local_days()
    {
        var buckets = new Dictionary<BucketKey, TokenTotals>
        {
            [new(new DateTime(2026, 11, 8, 23, 0, 0, DateTimeKind.Utc), Provider.Claude, "p", "m")] = new() { Output = 5 },
        };
        var istanbul = TimeZoneInfo.CreateCustomTimeZone("UTC+3", TimeSpan.FromHours(3), "UTC+3", "UTC+3");
        var days = UsageStats.LastDays(buckets, 7, DateTimeOffset.Parse("2026-11-09T09:00:00+03:00"), istanbul);
        Assert.Equal(new DateOnly(2026, 11, 9), days[^1].Day);
        Assert.Equal(5, days[^1].Claude.Output); // 23:00 UTC is 02:00 next day in Istanbul
    }
}

public class PaceAndPricingTests
{
    [Fact]
    public void Predicts_hitting_the_limit()
    {
        var now = DateTimeOffset.Parse("2026-11-08T12:00:00Z");
        // 2h into a 5h window at 60% -> 30%/h -> full in 1h20m, before the reset in 3h.
        var w = new LimitWindow("five_hour", 60, now.AddHours(3), TimeSpan.FromHours(5));
        var pace = Pace.Estimate(w, now);
        Assert.Equal(PaceKind.WillHitLimit, pace.Kind);
        Assert.Equal(80, pace.TimeToFull!.Value.TotalMinutes, 3);
    }

    [Fact]
    public void Predicts_safe_with_projection()
    {
        var now = DateTimeOffset.Parse("2026-11-08T12:00:00Z");
        var w = new LimitWindow("seven_day", 20, now.AddDays(5), TimeSpan.FromDays(7));
        var pace = Pace.Estimate(w, now);
        Assert.Equal(PaceKind.Safe, pace.Kind);
        Assert.Equal(70, pace.ProjectedPercent!.Value, 3);
    }

    [Fact]
    public void Too_early_in_window_is_unknown()
    {
        var now = DateTimeOffset.UtcNow;
        // 6 minutes after a reset at 4%: projecting would claim "full in 2 hours".
        var w = new LimitWindow("five_hour", 4, now.AddHours(5).AddMinutes(-6), TimeSpan.FromHours(5));
        Assert.Equal(PaceKind.Unknown, Pace.Estimate(w, now).Kind);

        var weekly = new LimitWindow("seven_day", 5, now.AddDays(7).AddHours(-12), TimeSpan.FromDays(7));
        Assert.Equal(PaceKind.Unknown, Pace.Estimate(weekly, now).Kind);
    }

    [Theory]
    [InlineData("claude-opus-5-5", 4)]
    [InlineData("claude-opus-5", 5)]
    [InlineData("claude-opus-4-1-20250805", 15)]
    [InlineData("claude-sonnet-4-5-20250929", 3)]
    [InlineData("claude-haiku-5-5", 0.10)]
    public void Longest_prefix_price_wins(string model, double input) =>
        Assert.Equal((decimal)input, Pricing.Find(model)!.Input);

    [Fact]
    public void Unknown_model_costs_nothing() => Assert.Equal(0m, Pricing.ClaudeCost("gpt-x", 1, 1, 1, 1, 1));
}

public class AlertTrackerTests
{
    static ProviderLimits At(double percent, DateTimeOffset reset) =>
        new(Provider.Claude, [new LimitWindow("five_hour", percent, reset, TimeSpan.FromHours(5))], null, DateTimeOffset.UtcNow);

    [Fact]
    public void Fires_once_per_threshold_and_skips_startup_state()
    {
        var reset = DateTimeOffset.Parse("2026-11-08T16:10:00.15Z");
        var tracker = new AlertTracker();
        int[] thresholds = [80, 95];

        Assert.Empty(tracker.Check(At(85, reset), thresholds));          // already at 85% on startup: silent
        Assert.Empty(tracker.Check(At(88, reset.AddSeconds(0.3)), thresholds)); // same window, 80 already fired
        var alert = Assert.Single(tracker.Check(At(96, reset), thresholds));
        Assert.Equal(95, alert.Threshold);
        Assert.Empty(tracker.Check(At(99, reset), thresholds));

        // A new window starts over.
        var next = Assert.Single(tracker.Check(At(81, reset.AddHours(5)), thresholds));
        Assert.Equal(80, next.Threshold);
    }
}
