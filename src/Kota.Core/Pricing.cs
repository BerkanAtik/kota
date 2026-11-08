namespace Kota.Core;

/// <summary>Price per million tokens in USD.</summary>
public sealed record ModelPrice(decimal Input, decimal Output, decimal CacheRead)
{
    public decimal CacheWrite5m => Input * 1.25m;
    public decimal CacheWrite1h => Input * 2m;
}

/// <summary>
/// Anthropic API list prices, used to show what your subscription usage would have cost on the API.
/// This is an estimate: subscriptions are not billed per token.
/// </summary>
public static class Pricing
{
    // Longest prefix wins, so "claude-opus-5-5" is matched before "claude-opus-5".
    static readonly (string Prefix, ModelPrice Price)[] Table =
    [
        ("claude-fable-5-1", new(10m, 50m, 0.25m)),
        ("claude-fable-5", new(10m, 50m, 1.00m)),
        ("claude-mythos", new(10m, 50m, 0.25m)),
        ("claude-opus-5-5", new(4m, 20m, 0.20m)),
        ("claude-opus-5", new(5m, 25m, 0.50m)),
        ("claude-opus-4-8", new(5m, 25m, 0.50m)),
        ("claude-opus-4-7", new(5m, 25m, 0.50m)),
        ("claude-opus-4-6", new(5m, 25m, 0.50m)),
        ("claude-opus-4-5", new(5m, 25m, 0.50m)),
        ("claude-opus-4", new(15m, 75m, 1.50m)),
        ("claude-sonnet-5-5", new(2m, 10m, 0.20m)),
        ("claude-sonnet-5", new(2m, 10m, 0.20m)),
        ("claude-sonnet-4", new(3m, 15m, 0.30m)),
        ("claude-3-7-sonnet", new(3m, 15m, 0.30m)),
        ("claude-haiku-5-5", new(0.10m, 0.50m, 0.01m)),
        ("claude-haiku-4-5", new(1m, 5m, 0.10m)),
        ("claude-3-5-haiku", new(0.80m, 4m, 0.08m)),
    ];

    public static ModelPrice? Find(string model)
    {
        ModelPrice? best = null;
        int bestLength = -1;
        foreach (var (prefix, price) in Table)
        {
            if (prefix.Length > bestLength && model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                best = price;
                bestLength = prefix.Length;
            }
        }
        return best;
    }

    public static decimal ClaudeCost(string model, long input, long output, long cacheWrite5m, long cacheWrite1h,
        long cacheRead, bool fastMode = false)
    {
        if (Find(model) is not { } p) return 0m;
        decimal cost = (input * p.Input + output * p.Output + cacheWrite5m * p.CacheWrite5m
                        + cacheWrite1h * p.CacheWrite1h + cacheRead * p.CacheRead) / 1_000_000m;
        return fastMode ? cost * 2m : cost;
    }
}
