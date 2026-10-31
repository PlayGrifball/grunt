using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;

namespace Surprenant.Grunt.Gateway.Host.Auth;

public static class GatewayAuth
{
    public const string ConsumerScheme = "ApiKey";
    public const string AdminScheme = "AdminKey";
    public const string AdminPolicy = "HaloGatewayAdmin";
    public const string EnvironmentClaim = "grunt:environment";
    public const string PriorityClaim = "grunt:priority";
    public const string ConsumerRole = "consumer";
    public const string AdminRole = "admin";

    public static byte[] Hash(string key) => SHA256.HashData(Encoding.UTF8.GetBytes(key));

    /// <summary>Finds the configured key matching <paramref name="presented"/> in constant time per candidate.</summary>
    public static ApiKeyOptions? Match(IEnumerable<ApiKeyOptions> keys, string presented)
    {
        var hash = Hash(presented);
        ApiKeyOptions? match = null;
        foreach (var key in keys)
        {
            byte[]? expected = null;
            if (!string.IsNullOrEmpty(key.Key))
                expected = Hash(key.Key);
            else if (!string.IsNullOrEmpty(key.KeySha256))
            {
                try { expected = Convert.FromHexString(key.KeySha256); } catch (FormatException) { }
            }
            if (expected is not null && CryptographicOperations.FixedTimeEquals(hash, expected))
                match ??= key;
        }
        return match;
    }
}

public sealed class ApiKeySchemeOptions : AuthenticationSchemeOptions
{
    /// <summary>True for the admin scheme (single admin key), false for consumer keys.</summary>
    public bool Admin { get; set; }
}

/// <summary>
/// API key authentication: consumer keys from <c>Gateway:ApiKeys</c> (header <c>X-Api-Key</c>) or the admin key
/// <c>Gateway:AdminKey</c> (header <c>X-Admin-Key</c>). Keys are compared as SHA-256 hashes in constant time.
/// </summary>
public sealed class ApiKeyAuthenticationHandler : AuthenticationHandler<ApiKeySchemeOptions>
{
    private readonly IOptionsMonitor<GatewayOptions> _gateway;

    public ApiKeyAuthenticationHandler(IOptionsMonitor<ApiKeySchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
        IOptionsMonitor<GatewayOptions> gateway)
        : base(options, logger, encoder)
    {
        _gateway = gateway;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var gateway = _gateway.CurrentValue;
        var header = Options.Admin ? gateway.AdminKeyHeader : gateway.ApiKeyHeader;
        if (!Request.Headers.TryGetValue(header, out var values) || string.IsNullOrEmpty(values.ToString()))
            return Task.FromResult(AuthenticateResult.NoResult());

        var presented = values.ToString();
        Claim[] claims;
        if (Options.Admin)
        {
            if (string.IsNullOrEmpty(gateway.AdminKey) ||
                !CryptographicOperations.FixedTimeEquals(GatewayAuth.Hash(presented), GatewayAuth.Hash(gateway.AdminKey)))
                return Task.FromResult(AuthenticateResult.Fail("Invalid admin key."));
            claims = new[] { new Claim(ClaimTypes.Name, "admin"), new Claim(ClaimTypes.Role, GatewayAuth.AdminRole) };
        }
        else
        {
            var key = GatewayAuth.Match(gateway.ApiKeys, presented);
            if (key is null)
                return Task.FromResult(AuthenticateResult.Fail("Invalid API key."));
            claims = new[]
            {
                new Claim(ClaimTypes.Name, key.Name),
                new Claim(ClaimTypes.Role, GatewayAuth.ConsumerRole),
                new Claim(GatewayAuth.EnvironmentClaim, key.Environment),
                new Claim(GatewayAuth.PriorityClaim, key.Priority.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            };
        }

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}
