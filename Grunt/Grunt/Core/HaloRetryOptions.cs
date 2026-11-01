using System;
using System.Collections.Generic;

namespace Surprenant.Grunt.Core;

/// <summary>
/// Retry and backoff settings for transient failures: HTTP 429, transient 5xx/408 status codes and network errors.
/// Bound from <c>ClientConfiguration:Retry</c> for <see cref="HaloInfiniteClientFactory"/>, and reused by the gateway client for
/// its own transport retries.
/// </summary>
/// <remarks>
/// The 401/403 auth retry of <see cref="HaloInfiniteClientFactory"/> is separate: it rebuilds the client once and does not count
/// against <see cref="MaxAttempts"/>.
/// </remarks>
public class HaloRetryOptions
{
    /// <summary>
    /// Total attempts for a transiently failing request, including the first one. 1 turns transient retries off.
    /// </summary>
    public int MaxAttempts { get; set; } = 4;

    /// <summary>
    /// Delay before the first retry. Each further retry doubles it (exponential backoff), up to <see cref="MaxDelay"/>.
    /// </summary>
    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Upper bound for a computed backoff delay (before jitter).
    /// </summary>
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// The longest <c>Retry-After</c> the policy waits for. A longer one is not waited out: the response is returned as is.
    /// </summary>
    public TimeSpan MaxRetryAfter { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Whether to add random jitter to computed delays (the delay becomes a random value between half and all of it).
    /// </summary>
    public bool UseJitter { get; set; } = true;

    /// <summary>
    /// How long an attempt may go unanswered before it is given up on and retried, as a timed-out network error. Every attempt
    /// but the last: the last runs to its end, so a slow call (a large blob) is never cut off, only retried sooner. Halo's
    /// services can sit on a cold request until their own front end answers 504 after 30 seconds, then answer the same
    /// request at once when it is repeated (its result cached by then): a few seconds here turn 30 seconds per attempt into
    /// those few. Null (the default) waits each attempt out (HttpClient's own timeout).
    /// </summary>
    public TimeSpan? AttemptTimeout { get; set; }

    /// <summary>
    /// Whether network failures (<see cref="System.Net.Http.HttpRequestException"/>, timeouts) are retried.
    /// </summary>
    public bool RetryOnNetworkErrors { get; set; } = true;

    /// <summary>
    /// The status codes retried when <see cref="RetryStatusCodes"/> is not set.
    /// </summary>
    public static IReadOnlySet<int> DefaultRetryStatusCodes { get; } = new HashSet<int> { 408, 429, 500, 502, 503, 504 };

    /// <summary>
    /// Status codes treated as transient. Null or empty means <see cref="DefaultRetryStatusCodes"/> (408, 429, 500, 502, 503, 504).
    /// Left null by default so configuration binding replaces rather than appends to the defaults.
    /// </summary>
    public List<int>? RetryStatusCodes { get; set; }

    /// <summary>
    /// Whether <paramref name="statusCode"/> is one of the transient status codes.
    /// </summary>
    public bool IsRetryableStatus(int statusCode)
        => RetryStatusCodes is { Count: > 0 } codes ? codes.Contains(statusCode) : DefaultRetryStatusCodes.Contains(statusCode);
}
