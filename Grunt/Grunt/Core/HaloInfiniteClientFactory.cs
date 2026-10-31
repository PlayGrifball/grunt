using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Surprenant.Grunt.Authentication;
using Surprenant.Grunt.Core.Storage;
using Surprenant.Grunt.Models;
using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Surprenant.Grunt.Core;

/// <summary>
/// Implementation of <see cref="IHaloInfiniteClientFactory"/> that creates instances of <see cref="IHaloInfiniteClient"/>.
/// </summary>
public partial class HaloInfiniteClientFactory : IHaloInfiniteClientFactory
{
    private readonly IOptionsMonitor<ClientConfiguration> _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<HaloInfiniteClientFactory> _logger;
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IAccountAuthorization _accountAuthorization;
    private readonly IOAuthStorage _oAuthStorage;

    // Semaphore to we only modify _cachedTokens with one thread at a time
    private readonly SemaphoreSlim semaphoreSlim = new(1, 1);
    private Tokens? _cachedTokens;

    /// <summary>
    /// Creates a new instance of <see cref="HaloInfiniteClientFactory"/>.
    /// </summary>
    public HaloInfiniteClientFactory(IOptionsMonitor<ClientConfiguration> optionsMonitor, ILoggerFactory loggerFactory, IConfiguration config,
        IHttpClientFactory httpClientFactory, IAccountAuthorization accountAuthorization, IOAuthStorage oAuthStorage)
    {
        _options = optionsMonitor;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<HaloInfiniteClientFactory>();
        _configuration = config;
        _httpClientFactory = httpClientFactory;
        _accountAuthorization = accountAuthorization;
        _oAuthStorage = oAuthStorage;
    }

    /// <inheritdoc/> 
    public async Task<IHaloInfiniteClient> CreateAsync()
    {
        return (await CreateInternalAsync()).Item2;
    }

    /// <summary>
    /// This method is for internal use only. Creates a client and returns all tokens but does not cache them, that is the job of the caller if desired.
    /// </summary>
    /// <returns></returns>
    /// <exception cref="Exception"></exception>
    /// <exception cref="InvalidOperationException"></exception>
    private async Task<(Tokens, IHaloInfiniteClient)> CreateInternalAsync()
    {
        var clientConfig = _options.CurrentValue;
        if (clientConfig is null) throw new Exception("ClientConfiguration is null!");
        if (string.IsNullOrEmpty(clientConfig.ClientId)) throw new Exception("ClientId is null or empty");
        if (string.IsNullOrEmpty(clientConfig.ClientSecret)) throw new Exception("ClientSecret is null or empty");
        if (string.IsNullOrEmpty(clientConfig.RedirectUrl)) throw new Exception("RedirectUrl is null or empty");

        XboxAuthenticationClient manager = new();

        HaloAuthenticationClient haloAuthClient = new();

        OAuthToken? currentOAuthToken = await _oAuthStorage.GetToken();

        var ticket = new XboxTicket();
        var haloTicket = new XboxTicket();
        var extendedTicket = new XboxTicket();
        var haloToken = new SpartanToken();

        if (currentOAuthToken is null)
        {
            _logger.LogDebug("Requesting new OAuth token");
            currentOAuthToken = await RequestNewToken(manager, clientConfig);
        }

        ticket = await manager.RequestUserToken(currentOAuthToken.AccessToken);
        if (ticket == null)
        {
            // There was a failure to obtain the user token, so likely we need to refresh.
            currentOAuthToken = await manager.RefreshOAuthToken(
                clientConfig.ClientId,
                currentOAuthToken.RefreshToken,
                clientConfig.RedirectUrl,
                clientConfig.ClientSecret);

            if (currentOAuthToken == null)
            {
                _logger.LogInformation("Could not get the token even with the refresh token.");
                currentOAuthToken = await RequestNewToken(manager, clientConfig);
            }
            else
            {
                // Persist the refreshed token: Microsoft may rotate the refresh token, and the stored one must stay usable.
                await _oAuthStorage.SetToken(currentOAuthToken);
            }
            ticket = await manager.RequestUserToken(currentOAuthToken.AccessToken)
                ?? throw new InvalidOperationException("Failed to get user token.");
        }

        haloTicket = await manager.RequestXstsToken(ticket.Token)
            ?? throw new InvalidOperationException("Failed to get halo ticket.");

        extendedTicket = await manager.RequestXstsToken(ticket.Token, false)
            ?? throw new InvalidOperationException("Failed to get extended ticket.");

        haloToken = await haloAuthClient.GetSpartanToken(haloTicket.Token)
            ?? throw new InvalidOperationException("Failed to get halo token.");

        _logger.LogDebug("Your Halo token: {HaloToken}", haloToken.Token);

        return (new Tokens()
        {
            OAuthToken = currentOAuthToken,
            Ticket = ticket,
            HaloTicket = haloTicket,
            ExtendedTicket = extendedTicket,
            HaloToken = haloToken,
        }, new HaloInfiniteClient(_httpClientFactory.CreateClient(nameof(IHaloInfiniteClient)), haloToken.Token, extendedTicket.DisplayClaims.Xui[0].Xid));
    }

    private async Task<OAuthToken> RequestNewToken(XboxAuthenticationClient manager, ClientConfiguration clientConfig)
    {
        var code = await _accountAuthorization.GetCodeAsync();

        // If no local token file exists, request a new set of tokens.
        var currentOAuthToken = await manager.RequestOAuthToken(clientConfig.ClientId, code, clientConfig.RedirectUrl, clientConfig.ClientSecret);

        if (currentOAuthToken is null)
        {
            _logger.LogWarning("No token was obtained. There is no valid token to be used right now.");
            return null;
        }

        await _oAuthStorage.SetToken(currentOAuthToken);

        return currentOAuthToken;
    }

    /// <summary>
    /// This method is for internal use only, will attempt to use cached client / tokens if they are valid.
    /// </summary>
    /// <returns></returns>
    private async Task<IHaloInfiniteClient> GetOrCreateClient(bool forceCreate = false)
    {
        _logger.LogTrace("Acquiring semaphore to create or get cached HaloInfiniteClient");
        await semaphoreSlim.WaitAsync();
        _logger.LogTrace("Semaphore lock acquired to create or get cached HaloInfiniteClient");
        try
        {
            // If we do not have cached tokens, then we have no choice but to create them
            if (_cachedTokens is null || forceCreate)
            {
                if (forceCreate)
                {
                    _logger.LogDebug("Detected force create this was likely due to a 401 or 405 error, creating new client and tokens");
                }
                else
                {
                    _logger.LogDebug("No cached tokens, creating new client and tokens");
                }
                var (newTokens, newClient) = await CreateInternalAsync();
                _cachedTokens = newTokens;
                return newClient;
            }

            // We will check if halo token is expired, since it is what we use to authenticate nearly every request
            var expires = _cachedTokens?.HaloToken.ExpiresUtc.ISO8601Date;
            var expired = expires is null || expires <= DateTime.UtcNow.AddMinutes(10); // If it expires in the next 10 minutes we will refresh
            if (expired)
            {
                _logger.LogDebug("Halo Token appears to have expired (or will expire soon) at {time}, creating new client and tokens", expires);
                var (newTokens, newClient) = await CreateInternalAsync();
                _cachedTokens = newTokens;
                return newClient;
            }

            // The token appears to be valid, we will make a new client but use the existing token
            var haloToken = _cachedTokens!.HaloToken;
            var extendedTicket = _cachedTokens.ExtendedTicket;
            _logger.LogDebug("Halo Token appears valid, expires at {time}, reusing existing tokens", expires);
            return new HaloInfiniteClient(_httpClientFactory.CreateClient(nameof(IHaloInfiniteClient)), haloToken.Token, extendedTicket.DisplayClaims.Xui[0].Xid);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in GetOrCreateClient");
            throw;
        }
        finally
        {
            _logger.LogTrace("Releasing semaphore lock in GetOrCreateClient");
            semaphoreSlim.Release();
            _logger.LogTrace("Just released semaphore lock in GetOrCreateClient");
        }
    }

    /// <summary>
    /// Expiry of the cached Spartan token, or null when no client has been created yet (or after <see cref="ResetAsync"/>).
    /// </summary>
    public DateTime? SpartanTokenExpiresUtc => _cachedTokens?.HaloToken.ExpiresUtc.ISO8601Date;

    /// <summary>
    /// Drops the cached tokens so the next call builds a new client from <see cref="IOAuthStorage"/> (e.g. after an admin signed in
    /// again or cleared the tokens).
    /// </summary>
    public async Task ResetAsync()
    {
        await semaphoreSlim.WaitAsync().ConfigureAwait(false);
        try
        {
            _cachedTokens = null;
        }
        finally
        {
            semaphoreSlim.Release();
        }
    }

    /// <summary>
    /// Used by every generated <see cref="IHaloInfiniteClient"/> method: runs <paramref name="call"/> on a cached or new client
    /// under <see cref="HaloRequestPolicy"/> (auth retry on 401/403, backoff on 429/5xx/network errors), configured from
    /// <c>ClientConfiguration:Retry</c>.
    /// </summary>
    private Task<TResponse> ExecuteWithPolicyAsync<TResponse>(string operation, Func<IHaloInfiniteClient, Task<TResponse>> call,
        Func<TResponse, HaloApiErrorContainer?> error)
    {
        var policy = new HaloRequestPolicy(_options.CurrentValue?.Retry, _logger);
        return policy.ExecuteAsync(operation, GetOrCreateClient, call, error);
    }

    private class Tokens
    {
        public required OAuthToken OAuthToken { get; set; }
        public required XboxTicket Ticket { get; set; }
        public required XboxTicket HaloTicket { get; set; }
        public required XboxTicket ExtendedTicket { get; set; }
        public required SpartanToken HaloToken { get; set; }
    }
}
