using Surprenant.Grunt.Core;
using Surprenant.Grunt.Gateway;

namespace Surprenant.Grunt.Gateway.Client;

/// <summary>Settings for <see cref="HaloGatewayClient"/> (e.g. section <c>HaloGateway</c>).</summary>
public sealed class HaloGatewayClientOptions
{
    /// <summary>The hosted public gateway.</summary>
    public static Uri DefaultBaseAddress { get; } = new("https://grunt.grifball.xyz/");

    /// <summary>
    /// Gateway base address, <see cref="DefaultBaseAddress"/> unless set (in-cluster, e.g. <c>http://halo-gateway.halo-gateway.svc:8080/</c>).
    /// Used when the HttpClient has none.
    /// </summary>
    public Uri? BaseAddress { get; set; } = DefaultBaseAddress;

    /// <summary>API key sent with every request; null sends none.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Header carrying <see cref="ApiKey"/>.</summary>
    public string ApiKeyHeader { get; set; } = HaloGatewayProtocol.DefaultApiKeyHeader;

    /// <summary>
    /// Transport retries only: the gateway unreachable, or 429/5xx answered by the gateway itself (its budgets, restarts, ingress) rather
    /// than a Halo result, which the gateway has already retried. 401/403 are never retried here.
    /// </summary>
    public HaloRetryOptions Retry { get; set; } = new() { MaxAttempts = 3 };
}
