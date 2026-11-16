using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Surprenant.Grunt.Core;

/// <summary>
/// How much one Halo service takes from one account (<see cref="Models.ClientConfiguration.Budgets"/>, by a name of its own):
/// <see cref="Limit"/> calls in any <see cref="Window"/>, shared by its <see cref="Operations"/>. Measured on 2026-11-15
/// (GATEWAY.md, "Halo's limits"): the profile service (gamertags and users) takes about 20 calls a minute, then answers 429 to
/// everything for about 5 minutes; the player match history takes 2 to 3 a second and answers <c>Retry-After: 1</c>.
/// </summary>
public sealed class HaloBudget
{
    /// <summary>The operations (<see cref="HaloInfiniteClient"/> method names, e.g. <c>UserByGamertag</c>) that share it.</summary>
    public List<string> Operations { get; set; } = new();

    /// <summary>Calls Halo is sent in any <see cref="Window"/>. 0 or less: no limit (lockouts still apply).</summary>
    public int Limit { get; set; }

    /// <summary>The sliding window <see cref="Limit"/> counts calls in.</summary>
    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How long Halo turns everything away after it answers one of these 429: no call is sent until it has passed. Halo's own
    /// <c>Retry-After</c>, when it sends a longer one, is used instead.
    /// </summary>
    public TimeSpan Lockout { get; set; }

    /// <summary>
    /// How long a call may wait for room (in the window, or for a lockout to end) before it is turned away with the time it
    /// would have to wait. Zero: turned away at once.
    /// </summary>
    public TimeSpan MaxWait { get; set; }
}

/// <summary>The answer of <see cref="HaloBudgets.AcquireAsync"/>: go, or don't and try again after <see cref="RetryAfter"/>.</summary>
/// <param name="Allowed">Whether the call may be sent to Halo now.</param>
/// <param name="RetryAfter">When not: how long until it could be.</param>
/// <param name="Budget">The budget the operation is in, if any.</param>
/// <param name="Reason">When not: <c>window</c> (its <see cref="HaloBudget.Limit"/> is used up) or <c>lockout</c> (Halo answered 429).</param>
public readonly record struct HaloBudgetTurn(bool Allowed, TimeSpan RetryAfter, string? Budget, string? Reason)
{
    /// <summary>Go: an operation without a budget, or one with room.</summary>
    public static HaloBudgetTurn Go(string? budget = null) => new(true, TimeSpan.Zero, budget, null);
}

/// <summary>
/// One account's budgets (<see cref="HaloBudget"/>): every call made with the account takes room in its operation's budget
/// first, retries included, so the account keeps under Halo's limits rather than being answered 429 and locked out. A call
/// that would go over waits up to the budget's <see cref="HaloBudget.MaxWait"/>, then is turned away with how long it would
/// have to wait (<see cref="HaloRequestPolicy"/> returns that as a 429 Halo never saw). A 429 Halo sends anyway
/// (<see cref="Throttled"/>) locks the budget for its <see cref="HaloBudget.Lockout"/>. Cache hits never get here: only calls
/// that go to Halo count. Operations in no budget are not limited.
/// </summary>
public sealed class HaloBudgets
{
    // Boundaries in seconds, around the waits a budget allows (MaxWait: 2 s for matchHistory): the OpenTelemetry SDK's
    // default ones (0, 5, 10, ... 10000) are for milliseconds and would put every wait in (0, 5]. 0 keeps the calls that
    // didn't wait apart.
    private static readonly System.Diagnostics.Metrics.Histogram<double> Waits = HaloRequestPolicy.Meter.CreateHistogram<double>(
        "grunt.halo.budget.wait", unit: "s", description: "Time a Halo call waited for room in its budget", tags: null,
        advice: new System.Diagnostics.Metrics.InstrumentAdvice<double> { HistogramBucketBoundaries = [0, 0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2, 5, 10] });

    private static readonly System.Diagnostics.Metrics.Counter<long> TurnedAway = HaloRequestPolicy.Meter.CreateCounter<long>(
        "grunt.halo.budget.rejections", description: "Halo calls turned away, never sent, by budget and reason (window, lockout)");

    private sealed class State(string name, HaloBudget budget)
    {
        public string Name { get; } = name;
        public HaloBudget Budget { get; } = budget;
        public object Lock { get; } = new();
        /// <summary>When each call in the window was sent.</summary>
        public Queue<DateTimeOffset> Sent { get; } = new();
        public DateTimeOffset LockedUntil { get; set; } = DateTimeOffset.MinValue;
    }

    private readonly Dictionary<string, State> _byOperation = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<State> _states = new();
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    /// <summary>Creates an account's budgets from <paramref name="budgets"/> (by name).</summary>
    /// <exception cref="ArgumentException">An operation is in two budgets.</exception>
    public HaloBudgets(IReadOnlyDictionary<string, HaloBudget>? budgets, TimeProvider? time = null, ILogger? logger = null)
    {
        _time = time ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
        foreach (var (name, budget) in budgets ?? new Dictionary<string, HaloBudget>())
        {
            var state = new State(name, budget);
            _states.Add(state);
            foreach (var operation in budget.Operations)
            {
                if (_byOperation.TryGetValue(operation, out var other))
                    throw new ArgumentException($"Halo operation {operation} is in two budgets, {other.Name} and {name}");
                _byOperation[operation] = state;
            }
        }
        if (_states.Count > 0)
        {
            HaloRequestPolicy.Meter.CreateObservableGauge("grunt.halo.budget.remaining",
                () => _states.Where(s => s.Budget.Limit > 0).Select(s => new System.Diagnostics.Metrics.Measurement<long>(
                    Remaining(s), new KeyValuePair<string, object?>("budget", s.Name))).ToArray(),
                unit: "{call}", description: "Calls a budget can still send in its window now");
            HaloRequestPolicy.Meter.CreateObservableGauge("grunt.halo.budget.lockout",
                () => _states.Select(s => new System.Diagnostics.Metrics.Measurement<double>(
                    LockRemaining(s).TotalSeconds, new KeyValuePair<string, object?>("budget", s.Name))).ToArray(),
                unit: "s", description: "Time left on a budget's lockout after Halo answered 429 (0: none)");
        }
    }

    /// <summary>The budget <paramref name="operation"/> is in; null when it is in none.</summary>
    public string? BudgetOf(string operation) => _byOperation.TryGetValue(operation, out var state) ? state.Name : null;

    /// <summary>
    /// Takes room for one call of <paramref name="operation"/> now, waiting up to its budget's <see cref="HaloBudget.MaxWait"/>
    /// for it; when there is none by then, turns the call away with the time until there would be (taking nothing).
    /// </summary>
    public async ValueTask<HaloBudgetTurn> AcquireAsync(string operation, CancellationToken cancellationToken = default)
    {
        if (!_byOperation.TryGetValue(operation, out var state))
            return HaloBudgetTurn.Go();

        var started = _time.GetUtcNow();
        while (true)
        {
            var (wait, reason) = TryTake(state);
            if (reason is null)
            {
                Waits.Record((_time.GetUtcNow() - started).TotalSeconds, new KeyValuePair<string, object?>("budget", state.Name));
                return HaloBudgetTurn.Go(state.Name);
            }
            var waited = _time.GetUtcNow() - started;
            if (waited + wait > state.Budget.MaxWait)
            {
                TurnedAway.Add(1, new KeyValuePair<string, object?>("budget", state.Name), new KeyValuePair<string, object?>("reason", reason));
                _logger.LogDebug("{Operation}: turned away by budget {Budget} ({Reason}), room in {Wait}", operation, state.Name, reason, wait);
                return new HaloBudgetTurn(false, wait, state.Name, reason);
            }
            await Task.Delay(wait, _time, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Takes room if there is some (reason null), else says how long until there is and why.</summary>
    private (TimeSpan Wait, string? Reason) TryTake(State state)
    {
        lock (state.Lock)
        {
            var now = _time.GetUtcNow();
            if (state.LockedUntil > now)
                return (state.LockedUntil - now, "lockout");
            var budget = state.Budget;
            if (budget.Limit > 0)
            {
                Prune(state, now);
                if (state.Sent.Count >= budget.Limit)
                    return (state.Sent.Peek() + budget.Window - now, "window");
                state.Sent.Enqueue(now);
            }
            return (TimeSpan.Zero, null);
        }
    }

    private static void Prune(State state, DateTimeOffset now)
    {
        while (state.Sent.Count > 0 && state.Sent.Peek() + state.Budget.Window <= now)
            state.Sent.Dequeue();
    }

    /// <summary>
    /// Halo answered a call of <paramref name="operation"/> 429: locks its budget for its <see cref="HaloBudget.Lockout"/>, or
    /// <paramref name="haloRetryAfter"/> when Halo asked for longer. Returns how long the lockout has left (zero for an
    /// operation in no budget).
    /// </summary>
    public TimeSpan Throttled(string operation, TimeSpan? haloRetryAfter)
    {
        if (!_byOperation.TryGetValue(operation, out var state))
            return TimeSpan.Zero;
        var length = haloRetryAfter is { } ra && ra > state.Budget.Lockout ? ra : state.Budget.Lockout;
        DateTimeOffset until;
        bool extended;
        lock (state.Lock)
        {
            var proposed = _time.GetUtcNow() + length;
            extended = proposed > state.LockedUntil;
            if (extended)
                state.LockedUntil = proposed;
            until = state.LockedUntil;
        }
        if (extended && length > TimeSpan.Zero)
            _logger.LogWarning("{Operation}: Halo answered 429; budget {Budget} sends nothing until {Until:u}", operation, state.Name, until);
        return LockRemaining(state);
    }

    /// <summary>How long <paramref name="operation"/>'s budget is locked for (zero when it isn't, or the operation is in none).</summary>
    public TimeSpan LockRemaining(string operation) => _byOperation.TryGetValue(operation, out var state) ? LockRemaining(state) : TimeSpan.Zero;

    /// <summary>The longest a call of <paramref name="operation"/> waits for room (zero for an operation in no budget).</summary>
    public TimeSpan MaxWait(string operation) => _byOperation.TryGetValue(operation, out var state) ? state.Budget.MaxWait : TimeSpan.Zero;

    private TimeSpan LockRemaining(State state)
    {
        var left = state.LockedUntil - _time.GetUtcNow();
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    private long Remaining(State state)
    {
        lock (state.Lock)
        {
            Prune(state, _time.GetUtcNow());
            return Math.Max(0, state.Budget.Limit - state.Sent.Count);
        }
    }
}
