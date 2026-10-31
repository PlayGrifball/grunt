using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Surprenant.Grunt.Gateway.Host.HaloAccount;

/// <summary>
/// Halo account health: Healthy when authorized (or not checked yet), Degraded when an admin must sign in again or refreshes keep
/// failing. Never Unhealthy: a restart cannot fix authorization, and cached responses can still be served.
/// </summary>
public sealed class HaloAuthHealthCheck : IHealthCheck
{
    public const int FailureThreshold = 3;
    private readonly HaloAccountService _account;

    public HaloAuthHealthCheck(HaloAccountService account) => _account = account;

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        => Task.FromResult(Evaluate(_account.Status()));

    public static HealthCheckResult Evaluate(HaloAuthSnapshot s) => s.Status switch
    {
        HaloAuthStatus.ReauthRequired => HealthCheckResult.Degraded("Halo re-authorization required: " + s.Reason),
        HaloAuthStatus.Error when s.ConsecutiveFailures >= FailureThreshold
            => HealthCheckResult.Degraded($"Halo token refresh failed {s.ConsecutiveFailures} times in a row: {s.LastError}"),
        HaloAuthStatus.Unknown => HealthCheckResult.Healthy("Halo account not checked yet."),
        _ => HealthCheckResult.Healthy(s.Reason),
    };
}
