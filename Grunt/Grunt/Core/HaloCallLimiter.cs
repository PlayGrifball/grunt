using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.RateLimiting;
using System.Threading.Tasks;

namespace Surprenant.Grunt.Core;

/// <summary>
/// A limit on one Halo operation's calls (<see cref="Models.ClientConfiguration.Limits"/>, keyed by the operation's name, e.g.
/// <c>StatsGetMatchHistory</c>).
/// </summary>
public sealed class HaloCallLimit
{
    /// <summary>Calls a second, spread evenly. 0 or less: not paced.</summary>
    public double PerSecond { get; set; }

    /// <summary>Calls at once. 0 or less: no limit.</summary>
    public int Concurrency { get; set; }

    /// <summary>The longest a call waits for its turn. After it, the call goes anyway (Halo may then answer 429, which is retried).</summary>
    public TimeSpan MaxWait { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Paces the calls of the Halo operations that have a <see cref="HaloCallLimit"/>, so a busy caller waits its turn rather than
/// being answered 429. Halo limits some endpoints on their own: the player match history (<c>StatsGetMatchHistory</c>) answers
/// 429 at a few calls a second while match stats take hundreds a minute, and its <c>Retry-After: 1</c> is no help while calls
/// keep coming at that rate. One for every call made with an account (<see cref="HaloInfiniteClientFactory"/>), retries
/// included: <see cref="HaloRequestPolicy"/> takes a turn before each attempt and gives it back when the attempt ends.
/// </summary>
public sealed class HaloCallLimiter : IDisposable
{
    private static readonly System.Diagnostics.Metrics.Histogram<double> Waits = HaloRequestPolicy.Meter.CreateHistogram<double>(
        "grunt.halo.limit.wait", unit: "s", description: "Time a Halo call waited for its turn under its operation's limit");

    private sealed class Gate(HaloCallLimit limit, RateLimiter? rate, SemaphoreSlim? slots)
    {
        public HaloCallLimit Limit { get; } = limit;
        public RateLimiter? Rate { get; } = rate;
        public SemaphoreSlim? Slots { get; } = slots;
    }

    private sealed class Turn(SemaphoreSlim slots) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                slots.Release();
        }
    }

    private sealed class NoTurn : IDisposable
    {
        public static readonly NoTurn Instance = new();

        public void Dispose()
        {
        }
    }

    private readonly Dictionary<string, Gate> _gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger _logger;

    /// <summary>Creates a limiter for <paramref name="limits"/> (by operation name); operations without one are not limited.</summary>
    public HaloCallLimiter(IReadOnlyDictionary<string, HaloCallLimit>? limits, ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
        foreach (var (operation, limit) in limits ?? new Dictionary<string, HaloCallLimit>())
        {
            var rate = limit.PerSecond > 0
                ? new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
                {
                    TokenLimit = 1,
                    TokensPerPeriod = 1,
                    ReplenishmentPeriod = TimeSpan.FromSeconds(1 / limit.PerSecond),
                    QueueLimit = int.MaxValue,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    AutoReplenishment = true,
                })
                : null;
            var slots = limit.Concurrency > 0 ? new SemaphoreSlim(limit.Concurrency, limit.Concurrency) : null;
            if (rate is not null || slots is not null)
                _gates[operation] = new Gate(limit, rate, slots);
        }
    }

    /// <summary>Whether <paramref name="operation"/> has a limit.</summary>
    public bool Limits(string operation) => _gates.ContainsKey(operation);

    /// <summary>
    /// Waits for <paramref name="operation"/>'s turn (at once without a limit) and returns it, to be disposed when the call ends.
    /// Past the limit's <see cref="HaloCallLimit.MaxWait"/> the call goes anyway, logged.
    /// </summary>
    public async ValueTask<IDisposable> AcquireAsync(string operation, CancellationToken cancellationToken = default)
    {
        if (!_gates.TryGetValue(operation, out var gate))
            return NoTurn.Instance;

        var started = Stopwatch.GetTimestamp();
        using var timeout = new CancellationTokenSource(gate.Limit.MaxWait);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        IDisposable turn = NoTurn.Instance;
        try
        {
            if (gate.Slots is { } slots)
            {
                await slots.WaitAsync(linked.Token).ConfigureAwait(false);
                turn = new Turn(slots);
            }
            if (gate.Rate is { } rate)
            {
                using var lease = await rate.AcquireAsync(1, linked.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("{Operation}: no turn within {MaxWait} under its limit; calling Halo anyway", operation, gate.Limit.MaxWait);
        }
        catch
        {
            turn.Dispose();
            throw;
        }
        Waits.Record(Stopwatch.GetElapsedTime(started).TotalSeconds, new KeyValuePair<string, object?>("operation", operation));
        return turn;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (var gate in _gates.Values)
        {
            gate.Rate?.Dispose();
            gate.Slots?.Dispose();
        }
    }
}
