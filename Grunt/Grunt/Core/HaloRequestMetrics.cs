using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Surprenant.Grunt.Core;

/// <summary>
/// Counts every HTTP request sent to Halo (meter <see cref="HaloRequestPolicy.MeterName"/>): one per attempt, so retries,
/// auth retries and attempts given up on after <see cref="HaloRetryOptions.AttemptTimeout"/> are all in it. It is what Halo
/// sees, which is what its rate limits count.
/// </summary>
internal static class HaloRequestMetrics
{
    private static readonly Counter<long> Requests = HaloRequestPolicy.Meter.CreateCounter<long>(
        "grunt.halo.requests", description: "HTTP requests sent to Halo, one per attempt (retries included), by operation, host and status");

    /// <summary>Records one request. <paramref name="status"/> is 0 when Halo gave no response; <paramref name="error"/> then says why.</summary>
    public static void Count(string operation, Uri? uri, int status, Exception? error = null)
    {
        var tags = new TagList
        {
            new KeyValuePair<string, object?>("operation", operation),
            new KeyValuePair<string, object?>("host", uri?.Host ?? "unknown"),
            new KeyValuePair<string, object?>("status", status),
        };
        if (error is not null)
            tags.Add(new KeyValuePair<string, object?>("error", error.GetType().Name));
        Requests.Add(1, tags);
    }
}
