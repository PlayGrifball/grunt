namespace Surprenant.Grunt.Gateway.Host;

/// <summary>Gateway host settings (section <c>Gateway</c>).</summary>
public sealed class GatewayOptions
{
    public const string SectionName = "Gateway";

    /// <summary>Consumer API keys, one or more per environment (e.g. grif-prod, grif-test, grif-preview).</summary>
    public List<ApiKeyOptions> ApiKeys { get; set; } = new();

    /// <summary>Header carrying consumer keys.</summary>
    public string ApiKeyHeader { get; set; } = HaloGatewayProtocol.DefaultApiKeyHeader;

    /// <summary>Admin key for <c>/admin/halo/*</c>; distinct from consumer keys. Admin endpoints are disabled while it is empty.</summary>
    public string? AdminKey { get; set; }

    /// <summary>Header carrying the admin key.</summary>
    public string AdminKeyHeader { get; set; } = "X-Admin-Key";

    public GlobalQuotaOptions GlobalQuota { get; set; } = new();

    public HaloAuthOptions Auth { get; set; } = new();
}

/// <summary>One consumer API key.</summary>
public sealed class ApiKeyOptions
{
    /// <summary>Name used in logs and metrics (never the key itself).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The key in clear text (from a secret). Either this or <see cref="KeySha256"/>.</summary>
    public string? Key { get; set; }

    /// <summary>Hex SHA-256 of the key, so the clear key need not be stored in gateway config.</summary>
    public string? KeySha256 { get; set; }

    /// <summary>Environment label (prod, test, preview, ...), added to metrics.</summary>
    public string Environment { get; set; } = "unknown";

    /// <summary>0 is the highest priority. Keys above <see cref="GlobalQuotaOptions.ReservedPriority"/> cannot use the reserved share of the global budget.</summary>
    public int Priority { get; set; } = 1;

    /// <summary>Per-key request budget per minute (token bucket, bursts up to the same amount). 0 means unlimited.</summary>
    public int PermitsPerMinute { get; set; } = 600;
}

/// <summary>Budget shared by all keys, protecting Halo (and the gateway's single Halo account) from bursts.</summary>
public sealed class GlobalQuotaOptions
{
    /// <summary>Requests per minute across all keys. 0 means unlimited.</summary>
    public int PermitsPerMinute { get; set; } = 1200;

    /// <summary>
    /// Share (0-1) of the global budget held back for keys with priority &lt;= <see cref="ReservedPriority"/>: when fewer permits than
    /// this remain, lower-priority keys get 429 while prod keys keep going.
    /// </summary>
    public double ReservedFraction { get; set; } = 0.25;

    /// <summary>Highest priority number that may use the reserved share (default 0: prod keys only).</summary>
    public int ReservedPriority { get; set; }
}

/// <summary>Halo account sign-in and token refresh (section <c>Gateway:Auth</c>).</summary>
public sealed class HaloAuthOptions
{
    /// <summary>File holding the OAuth tokens (put it on a persistent volume).</summary>
    public string TokenPath { get; set; } = "tokens.json";

    /// <summary>How often the background service refreshes the OAuth token (access tokens last about an hour).</summary>
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Delay before the first refresh after startup.</summary>
    public TimeSpan InitialDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How long a sign-in link (its OAuth state) stays valid.</summary>
    public TimeSpan SignInStateLifetime { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Hold an exclusive lock file next to the token file so a second instance on the same volume refuses to start (the file
    /// store is single-writer).
    /// </summary>
    public bool UseInstanceLock { get; set; } = true;
}
