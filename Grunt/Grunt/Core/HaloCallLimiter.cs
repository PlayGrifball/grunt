using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Surprenant.Grunt.Core;

/// <summary>
/// A limit on one Halo operation's calls (<see cref="Models.ClientConfiguration.Limits"/>, keyed by the operation's name, e.g.
/// <c>StatsGetMatchHistory</c>).
/// </summary>
public sealed class HaloCallLimit
{
    /// <summary>Calls a second, spread evenly; with <see cref="Adaptive"/>, the pace it starts at. 0 or less: not paced.</summary>
    public double PerSecond { get; set; }

    /// <summary>Calls at once. 0 or less: no limit.</summary>
    public int Concurrency { get; set; }

    /// <summary>The longest a call waits for its turn. After it, the call goes anyway (Halo may then answer 429, which is retried).</summary>
    public TimeSpan MaxWait { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Find the pace Halo takes: halved when Halo answers 429 (at most once a second, never under <see cref="MinPerSecond"/>), and
    /// raised by <see cref="IncreasePerSuccess"/> with each call it answers, up to <see cref="MaxPerSecond"/>. Needs a
    /// <see cref="PerSecond"/> to start from.
    /// </summary>
    public bool Adaptive { get; set; }

    /// <summary>The slowest an adaptive pace goes.</summary>
    public double MinPerSecond { get; set; } = 0.5;

    /// <summary>The fastest an adaptive pace goes.</summary>
    public double MaxPerSecond { get; set; } = 20;

    /// <summary>Calls a second an adaptive pace gains with each call Halo answers (not 429).</summary>
    public double IncreasePerSuccess { get; set; } = 0.05;
}

/// <summary>
/// Paces the calls of the Halo operations that have a <see cref="HaloCallLimit"/>, so a busy caller waits its turn rather than
/// being answered 429. Halo limits some endpoints on their own: the player match history (<c>StatsGetMatchHistory</c>) answers
/// 429 at a few calls a second while match stats take hundreds a minute, and its <c>Retry-After: 1</c> is no help while calls
/// keep coming at that rate. An adaptive limit finds the pace itself (<see cref="HaloCallLimit.Adaptive"/>, told how each call
/// went by <see cref="Report"/>). One for every call made with an account (<see cref="HaloInfiniteClientFactory"/>), retries
/// included: <see cref="HaloRequestPolicy"/> takes a turn before each attempt and gives it back when the attempt ends.
/// </summary>
public sealed class HaloCallLimiter : IDisposable
{
    private static readonly System.Diagnostics.Metrics.Histogram<double> Waits = HaloRequestPolicy.Meter.CreateHistogram<double>(
        "grunt.halo.limit.wait", unit: "s", description: "Time a Halo call waited for its turn under its operation's limit");

    private static readonly TimeSpan SlowDownAtMostEvery = TimeSpan.FromSeconds(1);

    private sealed class Gate(string operation, HaloCallLimit limit, SemaphoreSlim? slots)
    {
        public string Operation { get; } = operation;
        public HaloCallLimit Limit { get; } = limit;
        public SemaphoreSlim? Slots { get; } = slots;
        public object Lock { get; } = new();

        /// <summary>Calls a second now (0: not paced).</summary>
        public double PerSecond { get; set; } = limit.PerSecond > 0 ? limit.PerSecond : 0;

        /// <summary>When the next call may go (Stopwatch ticks).</summary>
        public long NextTurn { get; set; }

        /// <summary>When the pace was last halved (Stopwatch ticks).</summary>
        public long LastSlowDown { get; set; } = long.MinValue;
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
            var slots = limit.Concurrency > 0 ? new SemaphoreSlim(limit.Concurrency, limit.Concurrency) : null;
            if (limit.PerSecond > 0 || slots is not null)
                _gates[operation] = new Gate(operation, limit, slots);
        }
        if (_gates.Count > 0)
        {
            HaloRequestPolicy.Meter.CreateObservableGauge("grunt.halo.limit.rate",
                () => _gates.Values.Where(g => g.PerSecond > 0).Select(g => new System.Diagnostics.Metrics.Measurement<double>(
                    g.PerSecond, new KeyValuePair<string, object?>("operation", g.Operation))).ToArray(),
                unit: "{call}/s", description: "Calls a second a limited Halo operation is paced to now");
        }
    }

    /// <summary>Whether <paramref name="operation"/> has a limit.</summary>
    public bool Limits(string operation) => _gates.ContainsKey(operation);

    /// <summary>The calls a second <paramref name="operation"/> is paced to now; 0 when it isn't paced.</summary>
    public double PerSecond(string operation) => _gates.TryGetValue(operation, out var gate) ? gate.PerSecond : 0;

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
            var wait = TakeTurn(gate);
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, linked.Token).ConfigureAwait(false);
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

    /// <summary>Books the next turn at the pace now; returns how long until it.</summary>
    private static TimeSpan TakeTurn(Gate gate)
    {
        lock (gate.Lock)
        {
            if (gate.PerSecond <= 0)
                return TimeSpan.Zero;
            var now = Stopwatch.GetTimestamp();
            var turn = Math.Max(now, gate.NextTurn);
            gate.NextTurn = turn + (long)(Stopwatch.Frequency / gate.PerSecond);
            return Stopwatch.GetElapsedTime(now, turn);
        }
    }

    /// <summary>
    /// How a call of <paramref name="operation"/> went (its status; 0 for no answer), for an adaptive limit: a 429 halves its
    /// pace, any other answer raises it a little.
    /// </summary>
    public void Report(string operation, int status)
    {
        if (!_gates.TryGetValue(operation, out var gate) || !gate.Limit.Adaptive || gate.PerSecond <= 0 || status == 0)
            return;
        var limit = gate.Limit;
        double before, after;
        lock (gate.Lock)
        {
            before = gate.PerSecond;
            if (status == 429)
            {
                var now = Stopwatch.GetTimestamp();
                // The calls already sent at the old pace answer 429 too: one slowdown for them all
                if (gate.LastSlowDown != long.MinValue && Stopwatch.GetElapsedTime(gate.LastSlowDown, now) < SlowDownAtMostEvery)
                    return;
                gate.LastSlowDown = now;
                gate.PerSecond = Math.Max(limit.MinPerSecond, before / 2);
            }
            else
            {
                gate.PerSecond = Math.Min(limit.MaxPerSecond, before + limit.IncreasePerSuccess);
            }
            after = gate.PerSecond;
        }
        if (status == 429 && after < before)
            _logger.LogInformation("{Operation}: Halo answered 429, pacing it at {After:0.##}/s (was {Before:0.##}/s)", operation, after, before);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (var gate in _gates.Values)
            gate.Slots?.Dispose();
    }
}
