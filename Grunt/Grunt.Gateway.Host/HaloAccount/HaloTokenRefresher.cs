using Microsoft.Extensions.Options;

namespace Surprenant.Grunt.Gateway.Host.HaloAccount;

/// <summary>
/// Refreshes the Halo OAuth token shortly after startup and then every <see cref="HaloAuthOptions.RefreshInterval"/>, so the
/// access token is always fresh and the refresh token keeps rolling. Failures are surfaced through <see cref="HaloAuthState"/>
/// (health check, metric, Error log), never by crashing.
/// </summary>
public sealed class HaloTokenRefresher : BackgroundService
{
    private readonly HaloAccountService _account;
    private readonly IOptionsMonitor<GatewayOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<HaloTokenRefresher> _logger;

    public HaloTokenRefresher(HaloAccountService account, IOptionsMonitor<GatewayOptions> options, TimeProvider time, ILogger<HaloTokenRefresher> logger)
    {
        _account = account;
        _options = options;
        _time = time;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(_options.CurrentValue.Auth.InitialDelay, _time, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                await RunOnceAsync();
                await Task.Delay(_options.CurrentValue.Auth.RefreshInterval, _time, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    internal async Task RunOnceAsync()
    {
        try
        {
            await _account.RefreshAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Halo token refresh cycle failed");
        }
    }
}
