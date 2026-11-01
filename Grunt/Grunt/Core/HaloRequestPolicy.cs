using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Surprenant.Grunt.Models;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Surprenant.Grunt.Core;

/// <summary>
/// The request policy behind every generated <see cref="HaloInfiniteClientFactory"/> method:
/// <list type="bullet">
/// <item>401/403: rebuild the client (fresh tokens) and retry once. A second 401/403 is logged as critical and returned.</item>
/// <item>Transient status codes (429, 5xx, 408 by default) and network errors: retry with exponential backoff and jitter,
/// honouring <c>Retry-After</c> (<see cref="HaloApiErrorContainer.RetryAfter"/>), up to <see cref="HaloRetryOptions.MaxAttempts"/>.</item>
/// </list>
/// </summary>
public sealed class HaloRequestPolicy
{
    /// <summary>Meter for Grunt client metrics; export with OpenTelemetry <c>AddMeter(HaloRequestPolicy.MeterName)</c>.</summary>
    public const string MeterName = "Surprenant.Grunt";

    private static readonly System.Diagnostics.Metrics.Meter Meter = new(MeterName);

    private static readonly System.Diagnostics.Metrics.Counter<long> Retries = Meter.CreateCounter<long>(
        "grunt.halo.retries", description: "Halo request retries by reason (auth, status code, network)");

    private static void CountRetry(string operation, string reason, int status)
        => Retries.Add(1, new KeyValuePair<string, object?>("operation", operation), new KeyValuePair<string, object?>("reason", reason),
            new KeyValuePair<string, object?>("status", status));

    private readonly HaloRetryOptions _options;
    private readonly ILogger _logger;
    private readonly Func<TimeSpan, Task> _delay;
    private readonly Func<double> _random;
    private readonly Func<TimeSpan, Task> _attemptTimer;

    /// <summary>
    /// Creates a policy.
    /// </summary>
    /// <param name="options">Retry settings; null means defaults.</param>
    /// <param name="logger">Logger; null means no logging.</param>
    /// <param name="delay">Waits between attempts; defaults to <see cref="Task.Delay(TimeSpan)"/>. Tests pass a recorder.</param>
    /// <param name="random">Source of jitter in [0, 1); defaults to <see cref="Random.Shared"/>.</param>
    /// <param name="attemptTimer">Completes when an attempt has had <see cref="HaloRetryOptions.AttemptTimeout"/>; defaults to
    /// <see cref="Task.Delay(TimeSpan)"/>. Tests pass one that completes when they say.</param>
    public HaloRequestPolicy(HaloRetryOptions? options = null, ILogger? logger = null, Func<TimeSpan, Task>? delay = null, Func<double>? random = null, Func<TimeSpan, Task>? attemptTimer = null)
    {
        _options = options ?? new HaloRetryOptions();
        _logger = logger ?? NullLogger.Instance;
        _delay = delay ?? (d => Task.Delay(d));
        _random = random ?? (() => Random.Shared.NextDouble());
        _attemptTimer = attemptTimer ?? (d => Task.Delay(d));
    }

    /// <summary>
    /// Runs <paramref name="call"/> against a client from <paramref name="clientProvider"/> under the policy.
    /// </summary>
    /// <typeparam name="TClient">The client type.</typeparam>
    /// <typeparam name="TResponse">The response type, normally <c>HaloApiResultContainer&lt;T, HaloApiErrorContainer&gt;</c>.</typeparam>
    /// <param name="operation">Name used in logs.</param>
    /// <param name="clientProvider">Returns a client; the argument is true when the client must be rebuilt (after a 401/403).</param>
    /// <param name="call">The API call.</param>
    /// <param name="error">Reads the error container (status code, Retry-After) from a response.</param>
    public async Task<TResponse> ExecuteAsync<TClient, TResponse>(
        string operation,
        Func<bool, Task<TClient>> clientProvider,
        Func<TClient, Task<TResponse>> call,
        Func<TResponse, HaloApiErrorContainer?> error)
    {
        var maxAttempts = Math.Max(1, _options.MaxAttempts);
        var transientAttempt = 1;
        var authRetried = false;
        var forceNewClient = false;

        while (true)
        {
            TResponse response;
            try
            {
                var client = await clientProvider(forceNewClient).ConfigureAwait(false);
                forceNewClient = false;
                var pending = call(client);
                if (_options.AttemptTimeout is { } limit && limit > TimeSpan.Zero && transientAttempt < maxAttempts
                    && await Task.WhenAny(pending, _attemptTimer(limit)).ConfigureAwait(false) != pending)
                {
                    // Given up on (it can't be cancelled): left to finish on its own, its failure observed
                    _ = pending.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    var wait = ComputeDelay(_options, transientAttempt, null, _random);
                    _logger.LogWarning("{Operation}: no answer within {Timeout} on attempt {Attempt}/{MaxAttempts}, retrying in {Delay}",
                        operation, limit, transientAttempt, maxAttempts, wait);
                    CountRetry(operation, "timeout", 0);
                    transientAttempt++;
                    await _delay(wait).ConfigureAwait(false);
                    continue;
                }
                response = await pending.ConfigureAwait(false);
            }
            catch (Exception ex) when (_options.RetryOnNetworkErrors && IsTransientException(ex) && transientAttempt < maxAttempts)
            {
                var wait = ComputeDelay(_options, transientAttempt, null, _random);
                _logger.LogWarning(ex, "{Operation}: transient error on attempt {Attempt}/{MaxAttempts}, retrying in {Delay}",
                    operation, transientAttempt, maxAttempts, wait);
                CountRetry(operation, "network", 0);
                transientAttempt++;
                await _delay(wait).ConfigureAwait(false);
                continue;
            }

            var errorContainer = error(response);
            var code = errorContainer?.Code ?? 0;

            if (code == 401 || code == 403)
            {
                if (!authRetried)
                {
                    authRetried = true;
                    forceNewClient = true;
                    CountRetry(operation, "auth", code);
                    _logger.LogWarning("Detected auth issue, going to retry request");
                    continue;
                }
                _logger.LogCritical("Auth retry failed!");
            }
            else if (_options.IsRetryableStatus(code) && transientAttempt < maxAttempts)
            {
                var retryAfter = errorContainer?.RetryAfter;
                if (retryAfter is { } ra && ra > _options.MaxRetryAfter)
                {
                    _logger.LogWarning("{Operation}: status {Code} asks to retry after {RetryAfter}, longer than MaxRetryAfter {Max}; not retrying",
                        operation, code, ra, _options.MaxRetryAfter);
                }
                else
                {
                    var wait = ComputeDelay(_options, transientAttempt, retryAfter, _random);
                    _logger.LogWarning("{Operation}: status {Code} on attempt {Attempt}/{MaxAttempts}, retrying in {Delay}",
                        operation, code, transientAttempt, maxAttempts, wait);
                    CountRetry(operation, "status", code);
                    transientAttempt++;
                    await _delay(wait).ConfigureAwait(false);
                    continue;
                }
            }

            if (code > 299)
            {
                _logger.LogError("Status code does not indicate success: {Code}", code);
            }
            else
            {
                _logger.LogTrace("Status code appears successful: {Code}", code);
            }
            return response;
        }
    }

    /// <summary>
    /// Delay before retry number <paramref name="attempt"/> (1-based): <c>Retry-After</c> when given (capped at
    /// <see cref="HaloRetryOptions.MaxRetryAfter"/>), otherwise <c>BaseDelay * 2^(attempt-1)</c> capped at
    /// <see cref="HaloRetryOptions.MaxDelay"/>, with "equal jitter" (a random value between half and all of it).
    /// </summary>
    public static TimeSpan ComputeDelay(HaloRetryOptions options, int attempt, TimeSpan? retryAfter, Func<double>? random = null)
    {
        if (retryAfter is { } ra && ra >= TimeSpan.Zero)
        {
            return ra > options.MaxRetryAfter ? options.MaxRetryAfter : ra;
        }

        var exponent = Math.Min(Math.Max(attempt - 1, 0), 30);
        var ms = options.BaseDelay.TotalMilliseconds * Math.Pow(2, exponent);
        ms = Math.Min(ms, options.MaxDelay.TotalMilliseconds);
        if (options.UseJitter)
        {
            var r = (random ?? (() => Random.Shared.NextDouble()))();
            ms = ms / 2 + ms / 2 * r;
        }
        return TimeSpan.FromMilliseconds(Math.Max(0, ms));
    }

    /// <summary>
    /// Network-level failures worth retrying: <see cref="HttpRequestException"/>, <see cref="TimeoutException"/> and
    /// HttpClient timeouts (a <see cref="TaskCanceledException"/> wrapping a <see cref="TimeoutException"/>).
    /// Plain cancellation is not retried.
    /// </summary>
    public static bool IsTransientException(Exception ex)
        => ex is HttpRequestException or TimeoutException
           || ex is OperationCanceledException { InnerException: TimeoutException };
}
