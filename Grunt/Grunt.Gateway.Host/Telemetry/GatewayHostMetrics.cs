using Surprenant.Grunt.Gateway.Host.HaloAccount;
using Surprenant.Grunt.Gateway.Host.Quotas;
using System.Diagnostics.Metrics;

namespace Surprenant.Grunt.Gateway.Host.Telemetry;

/// <summary>Host metrics (meter <see cref="MeterName"/>); Prometheus names get a <c>_total</c> suffix for counters.</summary>
public static class GatewayHostMetrics
{
    public const string MeterName = "Surprenant.Grunt.Gateway.Host";

    internal static readonly Meter Meter = new(MeterName);

    /// <summary>Gateway API requests by consumer key, environment and action.</summary>
    public static readonly Counter<long> Requests = Meter.CreateCounter<long>("grunt.gateway.requests", description: "Gateway API requests by consumer key");

    /// <summary>429s from the gateway's own quotas, by key and reason (key, global, global-reserved).</summary>
    public static readonly Counter<long> QuotaRejections = Meter.CreateCounter<long>("grunt.gateway.quota.rejections", description: "Requests rejected by gateway quotas");

    /// <summary>OAuth refresh attempts by result (success, error, reauth_required, store_error).</summary>
    public static readonly Counter<long> TokenRefreshes = Meter.CreateCounter<long>("grunt.gateway.halo.token.refreshes", description: "Halo OAuth token refresh attempts by result");
}

public static class GatewayRefreshMetrics
{
    public static void Record(string result) => GatewayHostMetrics.TokenRefreshes.Add(1, new KeyValuePair<string, object?>("result", result));
}

/// <summary>Observable gauges over live state; created once at startup.</summary>
public sealed class GatewayHostGauges
{
    public GatewayHostGauges(QuotaGate quotas, HaloAccountService account, TimeProvider time)
    {
        GatewayHostMetrics.Meter.CreateObservableGauge("grunt.gateway.quota.remaining",
            () => quotas.Remaining().Select(r => new Measurement<long>(r.Available, new KeyValuePair<string, object?>("key", r.Key))),
            description: "Permits left in each quota bucket");

        GatewayHostMetrics.Meter.CreateObservableGauge("grunt.gateway.halo.reauth_required",
            () => account.Status().Status == HaloAuthStatus.ReauthRequired ? 1 : 0,
            description: "1 when an admin must sign the Halo account in again");

        GatewayHostMetrics.Meter.CreateObservableGauge("grunt.gateway.halo.token.expiry",
            () => Expiries(account.Status(), time.GetUtcNow()), unit: "s",
            description: "Seconds until the Halo access and Spartan tokens expire");
    }

    private static IEnumerable<Measurement<double>> Expiries(HaloAuthSnapshot s, DateTimeOffset now)
    {
        if (s.AccessTokenExpiresUtc is { } access)
            yield return new Measurement<double>((access - now).TotalSeconds, new KeyValuePair<string, object?>("token", "access"));
        if (s.SpartanTokenExpiresUtc is { } spartan)
            yield return new Measurement<double>((spartan - now).TotalSeconds, new KeyValuePair<string, object?>("token", "spartan"));
    }
}
