using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Surprenant.Grunt.Attributes;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Surprenant.Grunt.Gateway;

/// <summary>Response cache settings (section <c>Gateway:Cache</c>).</summary>
public sealed class HaloGatewayCacheOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>How long <see cref="GatewayCachePolicy.Immutable"/> responses stay cached.</summary>
    public TimeSpan ImmutableTtl { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Binary results larger than this are not cached.</summary>
    public int MaxCachedBinaryBytes { get; set; } = 2 * 1024 * 1024;
}

/// <summary>
/// Caches successful (HTTP 200) results of actions marked with <see cref="HaloGatewayCacheAttribute"/> in <see cref="IMemoryCache"/>,
/// keyed by method, path and sorted query string, shared by all API keys. Runs before model binding, so hits skip the action and
/// the factory entirely. In-process only: each replica has its own cache (a distributed cache is the scale-out option).
/// </summary>
public sealed class HaloGatewayCacheFilter : IAsyncResourceFilter
{
    public const string CacheHeader = "X-Grunt-Cache";

    private readonly IMemoryCache _cache;
    private readonly IOptionsMonitor<HaloGatewayCacheOptions> _options;

    public HaloGatewayCacheFilter(IMemoryCache cache, IOptionsMonitor<HaloGatewayCacheOptions> options)
    {
        _cache = cache;
        _options = options;
    }

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

        var executed = await next();
        Record(http, action, "miss", ttl);
        if (executed.Exception is not null && !executed.ExceptionHandled)
            return;

        var result = executed.Result;
        var cacheable = result switch
        {
            JsonResult json => json.StatusCode is null or StatusCodes.Status200OK,
            FileContentResult file => file.FileContents.Length <= options.MaxCachedBinaryBytes,
            _ => false,
        };
        if (cacheable && http.Response.Headers.TryGetValue(HaloGatewayProtocol.UpstreamStatusHeader, out var upstream))
        {
            _cache.Set(key, new Entry(result!, upstream.ToString()), ttl);
            http.Response.Headers.CacheControl = "private, max-age=" + ((int)ttl.TotalSeconds).ToString(CultureInfo.InvariantCulture);
        }
    }

    private static void Record(HttpContext http, string action, string outcome, TimeSpan ttl)
    {
        http.Response.Headers[CacheHeader] = outcome;
        HaloGatewayMetrics.CacheLookups.Add(1, new KeyValuePair<string, object?>("action", action), new KeyValuePair<string, object?>("result", outcome));
        Activity.Current?.SetTag("grunt.cache", outcome);
        Activity.Current?.SetTag("grunt.cache.ttl_seconds", (int)ttl.TotalSeconds);
        if (outcome == "hit")
            http.Response.Headers.CacheControl = "private, max-age=" + ((int)ttl.TotalSeconds).ToString(CultureInfo.InvariantCulture);
    }

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
