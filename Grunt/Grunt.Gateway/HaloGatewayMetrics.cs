using System.Diagnostics.Metrics;

namespace Surprenant.Grunt.Gateway;

/// <summary>
/// Gateway metrics (meter <see cref="MeterName"/>). Export them with OpenTelemetry: <c>AddMeter(HaloGatewayMetrics.MeterName)</c>.
/// </summary>
public static class HaloGatewayMetrics
{
    public const string MeterName = "Surprenant.Grunt.Gateway";

    private static readonly Meter Meter = new(MeterName);

    /// <summary>Halo responses seen by gateway actions, tagged with action and Halo status code.</summary>
    public static readonly Counter<long> UpstreamResponses = Meter.CreateCounter<long>("grunt.gateway.upstream.responses", description: "Halo responses by action and status code");

    /// <summary>Actions where the factory threw (no Halo response after retries, or re-auth needed).</summary>
    public static readonly Counter<long> UpstreamFailures = Meter.CreateCounter<long>("grunt.gateway.upstream.failures", description: "Gateway actions that got no Halo response");

    /// <summary>Response cache lookups, tagged with action and result (hit/miss).</summary>
    public static readonly Counter<long> CacheLookups = Meter.CreateCounter<long>("grunt.gateway.cache.lookups", description: "Response cache lookups by result");
}
