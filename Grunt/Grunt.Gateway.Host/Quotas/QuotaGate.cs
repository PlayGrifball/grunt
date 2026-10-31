using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Threading.RateLimiting;

namespace Surprenant.Grunt.Gateway.Host.Quotas;

/// <summary>Outcome of <see cref="QuotaGate.TryAcquire"/>.</summary>
public readonly record struct QuotaDecision(bool Allowed, string? Reason, TimeSpan? RetryAfter);

/// <summary>
/// Per-key token buckets plus a global bucket. When the global bucket runs low (below <see cref="GlobalQuotaOptions.ReservedFraction"/>),
/// only keys with priority &lt;= <see cref="GlobalQuotaOptions.ReservedPriority"/> may take from it, so prod keys win when
/// constrained. Buckets are rebuilt when the configuration changes.
/// </summary>
public sealed class QuotaGate : IDisposable
{
    private readonly ConcurrentDictionary<string, TokenBucketRateLimiter> _perKey = new();
    private readonly IOptionsMonitor<GatewayOptions> _options;
    private readonly IDisposable? _subscription;
    private TokenBucketRateLimiter? _global;
    private int _globalLimit;

    public QuotaGate(IOptionsMonitor<GatewayOptions> options)
    {
        _options = options;
        Rebuild(options.CurrentValue);
        _subscription = options.OnChange(Rebuild);
    }

    private void Rebuild(GatewayOptions options)
    {
        var old = _perKey.Values.ToList();
        _perKey.Clear();
        foreach (var limiter in old)
            limiter.Dispose();
        _global?.Dispose();
        _globalLimit = options.GlobalQuota.PermitsPerMinute;
        _global = _globalLimit > 0 ? CreateBucket(_globalLimit) : null;
    }

    internal static TokenBucketRateLimiter CreateBucket(int permitsPerMinute)
    {
        // One token every 60s/permits (at least 1 ms), bursting up to a minute's worth.
        var period = TimeSpan.FromMilliseconds(Math.Max(1, 60_000.0 / permitsPerMinute));
        var perPeriod = Math.Max(1, (int)Math.Round(permitsPerMinute * period.TotalMilliseconds / 60_000.0));
        return new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit = permitsPerMinute,
            TokensPerPeriod = perPeriod,
            ReplenishmentPeriod = period,
            QueueLimit = 0,
            AutoReplenishment = true,
        });
    }

    /// <summary>Takes one permit for <paramref name="key"/>.</summary>
    public QuotaDecision TryAcquire(ApiKeyOptions key)
    {
        var global = _options.CurrentValue.GlobalQuota;

        TokenBucketRateLimiter? perKey = null;
        if (key.PermitsPerMinute > 0)
        {
            perKey = _perKey.GetOrAdd(key.Name, _ => CreateBucket(key.PermitsPerMinute));
            if (perKey.GetStatistics()?.CurrentAvailablePermits < 1)
                return new QuotaDecision(false, "key", RetryAfterFor(key.PermitsPerMinute));
        }

        if (_global is { } bucket)
        {
            var available = bucket.GetStatistics()?.CurrentAvailablePermits ?? 0;
            var reserved = (long)Math.Ceiling(_globalLimit * Math.Clamp(global.ReservedFraction, 0, 1));
            if (key.Priority > global.ReservedPriority && available <= reserved)
                return new QuotaDecision(false, "global-reserved", RetryAfterFor(_globalLimit));
            using var lease = bucket.AttemptAcquire(1);
            if (!lease.IsAcquired)
                return new QuotaDecision(false, "global", RetryAfterFor(_globalLimit));
        }

        if (perKey is not null)
        {
            using var lease = perKey.AttemptAcquire(1);
            if (!lease.IsAcquired)
                return new QuotaDecision(false, "key", RetryAfterFor(key.PermitsPerMinute));
        }
        return new QuotaDecision(true, null, null);
    }

    /// <summary>Remaining permits per key name, for the quota gauge.</summary>
    public IEnumerable<(string Key, long Available)> Remaining()
    {
        foreach (var pair in _perKey)
            yield return (pair.Key, pair.Value.GetStatistics()?.CurrentAvailablePermits ?? 0);
        if (_global is { } global)
            yield return ("(global)", global.GetStatistics()?.CurrentAvailablePermits ?? 0);
    }

    private static TimeSpan RetryAfterFor(int permitsPerMinute)
        => TimeSpan.FromSeconds(Math.Max(1, Math.Ceiling(60.0 / Math.Max(1, permitsPerMinute))));

    public void Dispose()
    {
        _subscription?.Dispose();
        foreach (var limiter in _perKey.Values)
            limiter.Dispose();
        _global?.Dispose();
    }
}
