using Surprenant.Grunt.Converters;
using System.Text.Json;

namespace Surprenant.Grunt.Gateway;

/// <summary>
/// The wire contract shared by the gateway controller (Surprenant.Grunt.Gateway) and the gateway client
/// (Surprenant.Grunt.Gateway.Client): header names, JSON options and the upstream-to-HTTP status mapping.
/// </summary>
public static class HaloGatewayProtocol
{
    /// <summary>
    /// Response header carrying the Halo API status code (<see cref="Models.HaloApiErrorContainer.Code"/>). Its presence tells the client
    /// the body is a serialised <c>HaloApiResultContainer</c> from the gateway (already retried upstream), not a proxy/ingress error.
    /// 0 means the gateway got no response from Halo.
    /// </summary>
    public const string UpstreamStatusHeader = "X-Halo-Upstream-Status";

    /// <summary>Default header for the gateway API key.</summary>
    public const string DefaultApiKeyHeader = "X-Api-Key";

    /// <summary>Content type of binary (<c>byte[]</c>) results.</summary>
    public const string BinaryContentType = "application/octet-stream";

    /// <summary>
    /// JSON options for serialised result containers on both sides: C# property names (or their <c>JsonPropertyName</c>),
    /// case-insensitive reads, and TimeSpans as XML durations like the Halo API itself.
    /// </summary>
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new XmlDurationToTimeSpanJsonConverter() },
    };

    /// <summary>
    /// HTTP status the gateway answers with for a Halo status code. The body always carries the original code, so the client
    /// rebuilds the exact container; the HTTP status is for humans, proxies and metrics:
    /// <list type="bullet">
    /// <item>2xx → 200.</item>
    /// <item>401/403 → 502: the gateway's own Halo credentials failed after the auth retry, not the caller's.</item>
    /// <item>429 → 503 (with Retry-After when Halo sent one): Halo is still throttling after the gateway's backoff. Plain 429 is
    /// reserved for the gateway's own per-key quotas.</item>
    /// <item>Other 4xx (404, 400, ...) → passed through.</item>
    /// <item>5xx, 0 (no response) and anything else → 502.</item>
    /// </list>
    /// </summary>
    public static int MapStatus(int upstreamStatus) => upstreamStatus switch
    {
        >= 200 and < 300 => 200,
        401 or 403 => 502,
        429 => 503,
        >= 400 and < 500 => upstreamStatus,
        _ => 502,
    };
}
