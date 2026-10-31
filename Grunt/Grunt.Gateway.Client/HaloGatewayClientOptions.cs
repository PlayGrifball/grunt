using Surprenant.Grunt.Core;
using Surprenant.Grunt.Gateway;

namespace Surprenant.Grunt.Gateway.Client;

/// <summary>Settings for <see cref="HaloGatewayClient"/> (e.g. section <c>HaloGateway</c>).</summary>
public sealed class HaloGatewayClientOptions
{
    /// <summary>Gateway base address, e.g. <c>http://halo-gateway.halo-gateway.svc:8080/</c>. Used when the HttpClient has none.</summary>
    public Uri? BaseAddress { get; set; }

    /// <summary>API key sent with every request; null sends none.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Header carrying <see cref="ApiKey"/>.</summary>
    public string ApiKeyHeader { get; set; } = HaloGatewayProtocol.DefaultApiKeyHeader;

    /// <summary>
    /// Transport retries only: the gateway unreachable, or 429/5xx answered by the gateway itself (quotas, restarts, ingress) rather
    /// than a Halo result, which the gateway has already retried. 401/403 are never retried here.
    /// </summary>
    public HaloRetryOptions Retry { get; set; } = new() { MaxAttempts = 3 };
}
