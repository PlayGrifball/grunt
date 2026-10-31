namespace Surprenant.Grunt.Gateway.Host.HaloAccount;

public enum HaloAuthStatus
{
    /// <summary>Not checked yet (startup).</summary>
    Unknown,

    /// <summary>A token is stored and the last refresh worked.</summary>
    Authorized,

    /// <summary>No token, or the refresh token was rejected: an admin must sign in again.</summary>
    ReauthRequired,

    /// <summary>The last refresh failed for another reason (network, Microsoft outage); retried on the next cycle.</summary>
    Error,
}

/// <summary>Point-in-time view of the Halo account, returned by the admin status endpoint.</summary>
public sealed record HaloAuthSnapshot(
    HaloAuthStatus Status,
    string? Reason,
    bool TokenStored,
    DateTimeOffset? LastRefreshUtc,
    DateTimeOffset? AccessTokenExpiresUtc,
    DateTimeOffset? SpartanTokenExpiresUtc,
    DateTimeOffset? LastErrorUtc,
    string? LastError,
    int ConsecutiveFailures);

/// <summary>Thread-safe Halo account state shared by the account service, refresher, health check, metrics and admin API.</summary>
public sealed class HaloAuthState
{
    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private HaloAuthStatus _status = HaloAuthStatus.Unknown;
    private string? _reason = "Not checked yet.";
    private bool _tokenStored;
    private DateTimeOffset? _lastRefresh, _accessExpires, _lastErrorAt;
    private string? _lastError;
    private int _failures;

    public HaloAuthState(TimeProvider time) => _time = time;

    public void MarkAuthorized(int accessTokenLifetimeSeconds, string reason)
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            _status = HaloAuthStatus.Authorized;
            _reason = reason;
            _tokenStored = true;
            _lastRefresh = now;
            _accessExpires = accessTokenLifetimeSeconds > 0 ? now.AddSeconds(accessTokenLifetimeSeconds) : null;
            _failures = 0;
        }
    }

    public void MarkReauthRequired(string reason, bool tokenStored)
    {
        lock (_gate)
        {
            _status = HaloAuthStatus.ReauthRequired;
            _reason = reason;
            _tokenStored = tokenStored;
            _lastErrorAt = _time.GetUtcNow();
            _lastError = reason;
            _failures++;
        }
    }

    public void MarkError(string error)
    {
        lock (_gate)
        {
            if (_status != HaloAuthStatus.ReauthRequired)
            {
                _status = HaloAuthStatus.Error;
                _reason = error;
            }
            _lastErrorAt = _time.GetUtcNow();
            _lastError = error;
            _failures++;
        }
    }

    public HaloAuthSnapshot Snapshot(DateTimeOffset? spartanExpires = null)
    {
        lock (_gate)
        {
            return new HaloAuthSnapshot(_status, _reason, _tokenStored, _lastRefresh, _accessExpires, spartanExpires, _lastErrorAt, _lastError, _failures);
        }
    }
}
