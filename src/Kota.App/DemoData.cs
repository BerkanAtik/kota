using System;
using System.Collections.Generic;
using Kota.Core;

namespace Kota.App;

/// <summary>Sample data for screenshots and UI work: run Kota with --demo.</summary>
static class DemoData
{
    public static ProviderLimits Claude(DateTimeOffset now) => new(Provider.Claude,
    [
        new LimitWindow("five_hour", 72, now.AddMinutes(110), TimeSpan.FromHours(5)),
        new LimitWindow("seven_day", 41, now.AddHours(76), TimeSpan.FromDays(7)),
    ], "max", now, now);

    public static ProviderLimits Codex(DateTimeOffset now) => new(Provider.Codex,
    [
        new LimitWindow("codex_primary", 23, now.AddMinutes(185), TimeSpan.FromHours(5)),
        new LimitWindow("codex_secondary", 18, now.AddDays(4), TimeSpan.FromDays(7)),
    ], "plus", now, now.AddMinutes(-2));

    public static IReadOnlyDictionary<BucketKey, TokenTotals> Usage(DateTimeOffset now)
    {
        double[] dayScale = [0.6, 0.9, 0.3, 1.0, 0.8, 0.4, 1.1];
        (string Name, double Share, bool UsesCodex)[] projects =
        [
            ("api-gateway", 0.38, true), ("web-dashboard", 0.27, false), ("mobile-app", 0.18, false),
            ("docs-site", 0.10, false), ("infra", 0.07, true),
        ];

        var buckets = new Dictionary<BucketKey, TokenTotals>();
        var today = DateOnly.FromDateTime(now.ToLocalTime().DateTime);
        for (int i = 0; i < dayScale.Length; i++)
        {
            var noonLocal = today.AddDays(i - (dayScale.Length - 1)).ToDateTime(new TimeOnly(12, 0));
            var hourUtc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(noonLocal, DateTimeKind.Unspecified), TimeZoneInfo.Local);
            foreach (var (name, share, usesCodex) in projects)
            {
                double k = dayScale[i] * share;
                long input = (long)(20_000 * k), output = (long)(900_000 * k);
                long write = (long)(3_000_000 * k), read = (long)(60_000_000 * k);
                buckets[new BucketKey(hourUtc, Provider.Claude, name, "claude-opus-5-5")] = new TokenTotals
                {
                    Input = input, Output = output, CacheWrite = write, CacheRead = read, Requests = (int)(400 * k),
                    Cost = Pricing.ClaudeCost("claude-opus-5-5", input, output, write, 0, read),
                };
                if (usesCodex)
                    buckets[new BucketKey(hourUtc, Provider.Codex, name, "codex")] = new TokenTotals
                    {
                        Input = (long)(4_000_000 * k), CacheRead = (long)(12_000_000 * k), Output = (long)(150_000 * k),
                        Requests = (int)(120 * k),
                    };
            }
        }
        return buckets;
    }
}
