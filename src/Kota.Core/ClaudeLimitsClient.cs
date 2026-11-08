using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Kota.Core;

/// <summary>
/// Reads Claude plan limits (5-hour session, weekly) using the login that Claude Code already
/// stored on this machine. The token is only ever sent to api.anthropic.com, and Kota never
/// refreshes or modifies it.
/// </summary>
public sealed class ClaudeLimitsClient(HttpClient http, string claudeDir)
{
    public const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";

    static readonly (string Key, TimeSpan Length)[] KnownWindows =
    [
        ("five_hour", TimeSpan.FromHours(5)),
        ("seven_day", TimeSpan.FromDays(7)),
        ("seven_day_opus", TimeSpan.FromDays(7)),
        ("seven_day_sonnet", TimeSpan.FromDays(7)),
    ];

    public async Task<ProviderLimits> FetchAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var credentialsPath = Path.Combine(claudeDir, ".credentials.json");
        if (!File.Exists(credentialsPath))
        {
            var kind = Directory.Exists(claudeDir) ? LimitErrorKind.NotLoggedIn : LimitErrorKind.NotInstalled;
            return ProviderLimits.Failed(Provider.Claude, kind, now);
        }

        var credentials = ReadCredentials(credentialsPath);
        if (credentials is null || string.IsNullOrEmpty(credentials.AccessToken))
            return ProviderLimits.Failed(Provider.Claude, LimitErrorKind.NotLoggedIn, now);
        if (credentials.ExpiresAt is { } expires && expires <= now)
            return ProviderLimits.Failed(Provider.Claude, LimitErrorKind.TokenExpired, now, credentials.Plan);

        using var request = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);
        request.Headers.Add("anthropic-beta", "oauth-2025-04-20");
        try
        {
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return ProviderLimits.Failed(Provider.Claude, LimitErrorKind.TokenExpired, now, credentials.Plan);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                return ProviderLimits.Failed(Provider.Claude, LimitErrorKind.RateLimited, now, credentials.Plan);
            if (!response.IsSuccessStatusCode)
                return ProviderLimits.Failed(Provider.Claude, LimitErrorKind.Unknown, now, credentials.Plan,
                    "HTTP " + (int)response.StatusCode);

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return Parse(json, credentials.Plan, now);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return ProviderLimits.Failed(Provider.Claude, LimitErrorKind.Network, now, credentials.Plan);
        }
        catch (JsonException)
        {
            return ProviderLimits.Failed(Provider.Claude, LimitErrorKind.Unknown, now, credentials.Plan, "JSON");
        }
    }

    public static ProviderLimits Parse(string json, string? plan, DateTimeOffset now)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var windows = new List<LimitWindow>();
        foreach (var (key, length) in KnownWindows)
        {
            if (root.Obj(key) is not { } w || w.Double("utilization") is not { } percent) continue;
            windows.Add(new LimitWindow(key, percent, w.Time("resets_at"), length));
        }
        if (windows.Count == 0)
            return ProviderLimits.Failed(Provider.Claude, LimitErrorKind.NoData, now, plan);
        return new ProviderLimits(Provider.Claude, windows, plan, now, now);
    }

    internal sealed record Credentials(string? AccessToken, DateTimeOffset? ExpiresAt, string? Plan);

    internal static Credentials? ReadCredentials(string path)
    {
        // Claude Code may be rewriting the file at this moment, so retry briefly.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var doc = JsonDocument.Parse(fs);
                if (doc.RootElement.Obj("claudeAiOauth") is not { } oauth) return null;
                long expiresMs = oauth.Long("expiresAt");
                return new Credentials(
                    oauth.Str("accessToken"),
                    expiresMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(expiresMs) : null,
                    oauth.Str("subscriptionType"));
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                Thread.Sleep(150);
            }
        }
        return null;
    }
}
