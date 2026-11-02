using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Surprenant.Grunt.Attributes;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Surprenant.Grunt.Gateway;

/// <summary>Response cache settings (section <c>Gateway:Cache</c>).</summary>
public sealed class HaloGatewayCacheOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>How long <see cref="GatewayCachePolicy.Immutable"/> responses stay cached.</summary>
    public TimeSpan ImmutableTtl { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Binary results larger than this are not cached.</summary>
    public int MaxCachedBinaryBytes { get; set; } = 2 * 1024 * 1024;

    /// <summary>
    /// Total size of the cache, counted as the bytes of the cached responses (JSON measured serialized). The objects held in
    /// memory take a few times that. When full, entries are compacted out, oldest and least-used first. Read at startup.
    /// </summary>
    public long MaxCacheBytes { get; set; } = 32 * 1024 * 1024;
}

/// <summary>
/// Caches successful (HTTP 200) results of actions marked with <see cref="HaloGatewayCacheAttribute"/> in its own
/// <see cref="MemoryCache"/>, keyed by method, path and sorted query string, shared by all API keys. Runs before model binding,
/// so hits skip the action and the factory entirely. In-process only: each replica has its own cache (a distributed cache is
/// the scale-out option).
/// </summary>
public sealed class HaloGatewayCacheFilter : IAsyncResourceFilter, IDisposable
{
    public const string CacheHeader = "X-Grunt-Cache";

    private readonly MemoryCache _cache;
    private readonly IOptionsMonitor<HaloGatewayCacheOptions> _options;
    private readonly PostEvictionCallbackRegistration _evicted = new() { EvictionCallback = RecordEviction };

    public HaloGatewayCacheFilter(IOptionsMonitor<HaloGatewayCacheOptions> options, IMeterFactory meterFactory)
    {
        _options = options;
        _cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = options.CurrentValue.MaxCacheBytes, TrackStatistics = true });
        var meter = meterFactory.Create(HaloGatewayMetrics.MeterName);
        meter.CreateObservableGauge("grunt.gateway.cache.size", () => _cache.GetCurrentStatistics()?.CurrentEstimatedSize ?? 0,
            unit: "By", description: "Response cache size, as the bytes of the cached responses");
        meter.CreateObservableGauge("grunt.gateway.cache.entries", () => _cache.GetCurrentStatistics()?.CurrentEntryCount ?? 0,
            description: "Responses in the cache");
    }

    public void Dispose() => _cache.Dispose();

    private sealed record Entry(IActionResult Result, string UpstreamStatus);

    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        var policy = context.ActionDescriptor.EndpointMetadata.OfType<HaloGatewayCacheAttribute>().FirstOrDefault();
        var options = _options.CurrentValue;
        if (policy is null || policy.Policy == GatewayCachePolicy.None || !options.Enabled)
        {
            await next();
            return;
        }

        var ttl = policy.Policy == GatewayCachePolicy.Immutable
            ? options.ImmutableTtl
            : TimeSpan.FromSeconds(policy.Seconds > 0 ? policy.Seconds : 60);
        var http = context.HttpContext;
        var action = context.ActionDescriptor.RouteValues.TryGetValue("action", out var a) ? a ?? "unknown" : "unknown";
        var key = BuildKey(http.Request);

        if (_cache.TryGetValue(key, out Entry? hit) && hit is not null)
        {
            Record(http, action, "hit", ttl);
            http.Response.Headers[HaloGatewayProtocol.UpstreamStatusHeader] = hit.UpstreamStatus;
            context.Result = hit.Result;
            return;
        }

        // Resource filters wrap result execution: headers must be set before the response starts.
        Record(http, action, "miss", ttl);
        http.Response.OnStarting(() =>
        {
            if (http.Response.StatusCode == StatusCodes.Status200OK)
                http.Response.Headers.CacheControl = CacheControl(ttl);
            return Task.CompletedTask;
        });
        var executed = await next();
        if (executed.Exception is not null && !executed.ExceptionHandled)
            return;

        var result = executed.Result;
        long? size = result switch
        {
            JsonResult json when json.StatusCode is null or StatusCodes.Status200OK => JsonSize(json),
            FileContentResult file when file.FileContents.Length <= options.MaxCachedBinaryBytes => file.FileContents.Length,
            _ => null,
        };
        if (size is not null && http.Response.Headers.TryGetValue(HaloGatewayProtocol.UpstreamStatusHeader, out var upstream))
        {
            var entry = new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl, Size = size.Value + key.Length * sizeof(char) };
            entry.PostEvictionCallbacks.Add(_evicted);
            _cache.Set(key, new Entry(result!, upstream.ToString()), entry);
        }
    }

    // A second serialization on a miss only: the response itself is written by the JsonResult later.
    private static long JsonSize(JsonResult json)
        => json.Value is null
            ? 4
            : JsonSerializer.SerializeToUtf8Bytes(json.Value, json.Value.GetType(), json.SerializerSettings as JsonSerializerOptions ?? HaloGatewayProtocol.JsonOptions).LongLength;

    private static void RecordEviction(object key, object? value, EvictionReason reason, object? state)
        => HaloGatewayMetrics.CacheEvictions.Add(1, new KeyValuePair<string, object?>("reason", reason.ToString()));

    private static void Record(HttpContext http, string action, string outcome, TimeSpan ttl)
    {
        http.Response.Headers[CacheHeader] = outcome;
        HaloGatewayMetrics.CacheLookups.Add(1, new KeyValuePair<string, object?>("action", action), new KeyValuePair<string, object?>("result", outcome));
        Activity.Current?.SetTag("grunt.cache", outcome);
        Activity.Current?.SetTag("grunt.cache.ttl_seconds", (int)ttl.TotalSeconds);
        if (outcome == "hit")
            http.Response.Headers.CacheControl = CacheControl(ttl);
    }

    private static string CacheControl(TimeSpan ttl)
        => "private, max-age=" + ((int)ttl.TotalSeconds).ToString(CultureInfo.InvariantCulture);

    internal static string BuildKey(HttpRequest request)
    {
        var sb = new StringBuilder("halo-gateway:");
        sb.Append(request.Method).Append(' ').Append(request.Path.Value?.ToLowerInvariant());
        foreach (var pair in request.Query.OrderBy(q => q.Key, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var value in pair.Value)
            {
                sb.Append('&').Append(Uri.EscapeDataString(pair.Key.ToLowerInvariant())).Append('=').Append(Uri.EscapeDataString(value ?? string.Empty));
            }
        }
        return sb.ToString();
    }
}
