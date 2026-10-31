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

    /// <summary>Browser sign-in for the /admin page (OpenID Connect); off until <see cref="OidcOptions.Authority"/> and <see cref="OidcOptions.ClientId"/> are set.</summary>
    public OidcOptions Oidc { get; set; } = new();
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

/// <summary>
/// OpenID Connect sign-in for the browser admin page (section <c>Gateway:Oidc</c>): Authorization Code + PKCE against an external
/// identity provider (Authentik, ...), then an HttpOnly cookie session. Only members of <see cref="AllowedGroups"/> become admins.
/// The <c>X-Admin-Key</c> header keeps working beside it for automation.
/// </summary>
public sealed class OidcOptions
{
    /// <summary>Issuer URL (its <c>/.well-known/openid-configuration</c> is read). Ex: https://auth.example.com/application/o/halo-gateway/</summary>
    public string? Authority { get; set; }

    public string? ClientId { get; set; }

    /// <summary>Client secret of a confidential client. Empty: a public client (PKCE only).</summary>
    public string? ClientSecret { get; set; }

    /// <summary>Scopes to request. Empty: openid, profile and email (Authentik puts <c>groups</c> in profile).</summary>
    public List<string> Scopes { get; set; } = new();

    /// <summary>Claim (ID token or userinfo) listing the user's groups.</summary>
    public string GroupClaim { get; set; } = "groups";

    /// <summary>Groups whose members may use the admin page (exact, case-sensitive match). Empty: nobody.</summary>
    public List<string> AllowedGroups { get; set; } = new();

    /// <summary>How long a browser session lasts before signing in again (not sliding).</summary>
    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromHours(8);

    /// <summary>Require https for the discovery document (only turn off for a local test IdP).</summary>
    public bool RequireHttpsMetadata { get; set; } = true;

    public bool Enabled => !string.IsNullOrWhiteSpace(Authority) && !string.IsNullOrWhiteSpace(ClientId);
}
