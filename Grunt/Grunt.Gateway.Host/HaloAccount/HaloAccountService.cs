using Microsoft.Extensions.Options;
using Surprenant.Grunt.Authentication;
using Surprenant.Grunt.Core;
using Surprenant.Grunt.Core.Storage;
using Surprenant.Grunt.Models;
using System.Collections.Concurrent;
using System.Security.Cryptography;

using Surprenant.Grunt.Gateway.Host.Telemetry;

namespace Surprenant.Grunt.Gateway.Host.HaloAccount;

/// <summary>Microsoft OAuth calls (code redemption, refresh); a seam for tests.</summary>
public interface IHaloOAuthClient
{
    string BuildSignInUrl(string state);
    Task<OAuthToken?> RedeemCodeAsync(string code);
    Task<OAuthToken?> RefreshAsync(string refreshToken);
}

/// <summary><see cref="IHaloOAuthClient"/> over Grunt's <see cref="XboxAuthenticationClient"/> and <c>ClientConfiguration</c>.</summary>
public sealed class XboxOAuthClient : IHaloOAuthClient
{
    private readonly IOptionsMonitor<ClientConfiguration> _config;
    private readonly XboxAuthenticationClient _xbox = new();

    public XboxOAuthClient(IOptionsMonitor<ClientConfiguration> config) => _config = config;

    public string BuildSignInUrl(string state)
    {
        var c = _config.CurrentValue;
        return _xbox.GenerateAuthUrl(c.ClientId, c.RedirectUrl, state: state);
    }

    public Task<OAuthToken?> RedeemCodeAsync(string code)
    {
        var c = _config.CurrentValue;
        return _xbox.RequestOAuthToken(c.ClientId, code, c.RedirectUrl, c.ClientSecret);
    }

    public Task<OAuthToken?> RefreshAsync(string refreshToken)
    {
        var c = _config.CurrentValue;
        return _xbox.RefreshOAuthToken(c.ClientId, refreshToken, c.RedirectUrl, c.ClientSecret);
    }
}

/// <summary>The factory's cached session: reset after sign-in/clear, validated after sign-in; a seam for tests.</summary>
public interface IHaloSession
{
    Task ResetAsync();

    /// <summary>Builds a full Xbox/Halo token chain from the stored OAuth token; throws when it cannot.</summary>
    Task ValidateAsync();

    DateTimeOffset? SpartanTokenExpiresUtc { get; }
}

public sealed class FactoryHaloSession : IHaloSession
{
    private readonly HaloInfiniteClientFactory _factory;

    public FactoryHaloSession(HaloInfiniteClientFactory factory) => _factory = factory;

    public Task ResetAsync() => _factory.ResetAsync();

    public async Task ValidateAsync() => await _factory.CreateAsync().ConfigureAwait(false);

    public DateTimeOffset? SpartanTokenExpiresUtc
        => _factory.SpartanTokenExpiresUtc is { } d ? new DateTimeOffset(DateTime.SpecifyKind(d, DateTimeKind.Utc)) : null;
}

/// <summary>
/// Replaces Grunt's file-based <see cref="IAccountAuthorization"/>: the gateway never reads codes from AccountAuthorization.txt;
/// codes arrive through the OAuth callback or the admin API and are redeemed by <see cref="HaloAccountService"/>.
/// </summary>
public sealed class GatewayAccountAuthorization : IAccountAuthorization
{
    public Task<string> GetCodeAsync()
        => throw new InvalidOperationException("The Halo account needs to be signed in again: an admin must open the sign-in link from GET /admin/halo/signin.");

    public Task SetCodeAsync(string code) => throw new NotSupportedException("Use POST /admin/halo/code.");
}

public sealed record SignInResult(bool Success, string Message);

/// <summary>
/// Halo account lifecycle: sign-in links with one-time OAuth state, code redemption (callback or pasted), proactive refresh, and
/// clearing. Results land in <see cref="HaloAuthState"/>.
/// </summary>
public sealed class HaloAccountService
{
    private readonly IOAuthTokenStore _store;
    private readonly IHaloOAuthClient _oauth;
    private readonly IHaloSession _session;
    private readonly HaloAuthState _state;
    private readonly IOptionsMonitor<GatewayOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<HaloAccountService> _logger;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _pendingStates = new();

    public HaloAccountService(IOAuthTokenStore store, IHaloOAuthClient oauth, IHaloSession session, HaloAuthState state,
        IOptionsMonitor<GatewayOptions> options, TimeProvider time, ILogger<HaloAccountService> logger)
    {
        _store = store;
        _oauth = oauth;
        _session = session;
        _state = state;
        _options = options;
        _time = time;
        _logger = logger;
    }

    public HaloAuthSnapshot Status() => _state.Snapshot(_session.SpartanTokenExpiresUtc);

    /// <summary>A Microsoft sign-in URL carrying a fresh one-time state, valid for <see cref="HaloAuthOptions.SignInStateLifetime"/>.</summary>
    public string CreateSignInUrl()
    {
        var now = _time.GetUtcNow();
        foreach (var expired in _pendingStates.Where(p => p.Value < now).Select(p => p.Key).ToList())
            _pendingStates.TryRemove(expired, out _);

        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        _pendingStates[state] = now + _options.CurrentValue.Auth.SignInStateLifetime;
        return _oauth.BuildSignInUrl(state);
    }

    /// <summary>True once for a state issued by <see cref="CreateSignInUrl"/> that has not expired.</summary>
    public bool ConsumeState(string? state)
        => !string.IsNullOrEmpty(state) && _pendingStates.TryRemove(state, out var expires) && expires >= _time.GetUtcNow();

    /// <summary>Redeems an authorization code, stores the token, resets and validates the factory session.</summary>
    public async Task<SignInResult> RedeemCodeAsync(string code)
    {
        OAuthToken? token;
        try
        {
            token = await _oauth.RedeemCodeAsync(code.Trim());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Redeeming the Halo authorization code failed");
            _state.MarkError("Redeeming the authorization code failed: " + ex.GetType().Name);
            return new SignInResult(false, "Microsoft could not be reached to redeem the code. Try again.");
        }

        if (token is null || string.IsNullOrEmpty(token.RefreshToken))
        {
            _logger.LogError("Microsoft rejected the Halo authorization code (expired, already used, or redirect URI mismatch)");
            return new SignInResult(false, "Microsoft rejected the code (expired, already used, or the redirect URI does not match). Start the sign-in again.");
        }

        await _store.SetToken(token);
        await _session.ResetAsync();
        _state.MarkAuthorized(token.ExpiresIn, "Signed in by an admin.");
        _logger.LogInformation("Halo account signed in; token stored");

        try
        {
            await _session.ValidateAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Halo sign-in stored a token but building the Xbox/Halo token chain failed");
            _state.MarkError("Signed in, but the Xbox/Halo token chain failed: " + ex.Message);
            return new SignInResult(false, "Signed in, but Xbox/Halo rejected the account: " + ex.Message);
        }
        return new SignInResult(true, "Signed in. The gateway can call Halo now.");
    }

    /// <summary>Refreshes the stored OAuth token (keeping the refresh token rolling). Returns true when the account is usable.</summary>
    public async Task<bool> RefreshAsync()
    {
        OAuthToken? current;
        try
        {
            current = await _store.GetToken();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reading the Halo token store failed");
            _state.MarkError("Reading the token store failed: " + ex.GetType().Name);
            GatewayRefreshMetrics.Record("store_error");
            return false;
        }

        if (current is null || string.IsNullOrEmpty(current.RefreshToken))
        {
            _logger.LogError("Halo re-authorization required: no token is stored. An admin must sign in (GET /admin/halo/signin)");
            _state.MarkReauthRequired("No token stored: an admin must sign in.", tokenStored: false);
            GatewayRefreshMetrics.Record("reauth_required");
            return false;
        }

        OAuthToken? refreshed;
        try
        {
            refreshed = await _oauth.RefreshAsync(current.RefreshToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Refreshing the Halo OAuth token failed (will retry)");
            _state.MarkError("Refresh failed: " + ex.GetType().Name);
            GatewayRefreshMetrics.Record("error");
            return false;
        }

        if (refreshed is null)
        {
            _logger.LogError("Halo re-authorization required: Microsoft rejected the refresh token. An admin must sign in again (GET /admin/halo/signin)");
            _state.MarkReauthRequired("The refresh token was rejected: an admin must sign in again.", tokenStored: true);
            GatewayRefreshMetrics.Record("reauth_required");
            return false;
        }

        if (string.IsNullOrEmpty(refreshed.RefreshToken))
            refreshed.RefreshToken = current.RefreshToken;
        await _store.SetToken(refreshed);
        _state.MarkAuthorized(refreshed.ExpiresIn, "Token refreshed.");
        GatewayRefreshMetrics.Record("success");
        _logger.LogInformation("Halo OAuth token refreshed");
        return true;
    }

    /// <summary>Deletes the stored token and the factory's cached session.</summary>
    public async Task ClearAsync()
    {
        await _store.ClearAsync();
        await _session.ResetAsync();
        _state.MarkReauthRequired("Tokens cleared by an admin: sign in again.", tokenStored: false);
        _logger.LogWarning("Halo tokens cleared by an admin");
    }
}
