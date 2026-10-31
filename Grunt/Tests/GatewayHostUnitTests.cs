using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Surprenant.Grunt.Gateway.Host;
using Surprenant.Grunt.Gateway.Host.Auth;
using Surprenant.Grunt.Gateway.Host.HaloAccount;
using Surprenant.Grunt.Gateway.Host.Quotas;
using Surprenant.Grunt.Models;

namespace Tests;

/// <summary>Halo account status/refresh/health logic, quotas and API key matching, with fakes.</summary>
public class GatewayHostUnitTests
{
    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Monitor<T> : IOptionsMonitor<T>
    {
        public Monitor(T value) => CurrentValue = value;
        public T CurrentValue { get; set; }
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private string _dir = null!;
    private ManualTime _time = null!;
    private FileOAuthTokenStore _store = null!;
    private IHaloOAuthClient _oauth = null!;
    private IHaloSession _session = null!;
    private HaloAuthState _state = null!;
    private HaloAccountService _account = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "grunt-gateway-unit", Guid.NewGuid().ToString("N"));
        _time = new ManualTime();
        _store = new FileOAuthTokenStore(Path.Combine(_dir, "tokens.json"));
        _oauth = Substitute.For<IHaloOAuthClient>();
        _session = Substitute.For<IHaloSession>();
        _state = new HaloAuthState(_time);
        _account = new HaloAccountService(_store, _oauth, _session, _state, new Monitor<GatewayOptions>(new GatewayOptions()), _time,
            NullLogger<HaloAccountService>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private static OAuthToken Token(string refresh = "r1", int expiresIn = 3600)
        => new() { AccessToken = "access", RefreshToken = refresh, ExpiresIn = expiresIn, TokenType = "bearer" };

    [Test]
    public async Task RefreshWithoutStoredTokenRequiresReauth()
    {
        var ok = await _account.RefreshAsync();

        var status = _account.Status();
        Assert.That(ok, Is.False);
        Assert.That(status.Status, Is.EqualTo(HaloAuthStatus.ReauthRequired));
        Assert.That(status.TokenStored, Is.False);
        Assert.That(HaloAuthHealthCheck.Evaluate(status).Status, Is.EqualTo(HealthStatus.Degraded));
        Assert.That(HaloAuthHealthCheck.Evaluate(status).Description, Does.Contain("re-authorization required"));
    }

    [Test]
    public async Task RejectedRefreshTokenRequiresReauth()
    {
        await _store.SetToken(Token());
        _oauth.RefreshAsync("r1").Returns((OAuthToken?)null);

        await _account.RefreshAsync();

        var status = _account.Status();
        Assert.That(status.Status, Is.EqualTo(HaloAuthStatus.ReauthRequired));
        Assert.That(status.TokenStored, Is.True);
        Assert.That(status.Reason, Does.Contain("refresh token was rejected"));
    }

    [Test]
    public async Task SuccessfulRefreshStoresRotatedTokenAndReportsExpiry()
    {
        await _store.SetToken(Token("r1"));
        _oauth.RefreshAsync("r1").Returns(Token("r2", expiresIn: 3600));

        var ok = await _account.RefreshAsync();

        var status = _account.Status();
        Assert.That(ok, Is.True);
        Assert.That(status.Status, Is.EqualTo(HaloAuthStatus.Authorized));
        Assert.That(status.LastRefreshUtc, Is.EqualTo(_time.Now));
        Assert.That(status.AccessTokenExpiresUtc, Is.EqualTo(_time.Now.AddHours(1)));
        Assert.That((await _store.GetToken())!.RefreshToken, Is.EqualTo("r2"));
        Assert.That(HaloAuthHealthCheck.Evaluate(status).Status, Is.EqualTo(HealthStatus.Healthy));
    }

    [Test]
    public async Task RefreshWithoutNewRefreshTokenKeepsTheOldOne()
    {
        await _store.SetToken(Token("r1"));
        _oauth.RefreshAsync("r1").Returns(Token(refresh: ""));

        await _account.RefreshAsync();

        Assert.That((await _store.GetToken())!.RefreshToken, Is.EqualTo("r1"));
    }

    [Test]
    public async Task TransientRefreshErrorsDegradeOnlyAfterRepeatedFailures()
    {
        await _store.SetToken(Token());
        _oauth.RefreshAsync(Arg.Any<string>()).ThrowsAsync(new HttpRequestException("login.live.com down"));

        await _account.RefreshAsync();
        var afterOne = _account.Status();
        await _account.RefreshAsync();
        await _account.RefreshAsync();
        var afterThree = _account.Status();

        Assert.That(afterOne.Status, Is.EqualTo(HaloAuthStatus.Error));
        Assert.That(HaloAuthHealthCheck.Evaluate(afterOne).Status, Is.EqualTo(HealthStatus.Healthy));
        Assert.That(afterThree.ConsecutiveFailures, Is.EqualTo(3));
        Assert.That(HaloAuthHealthCheck.Evaluate(afterThree).Status, Is.EqualTo(HealthStatus.Degraded));
    }

    [Test]
    public async Task SuccessAfterFailuresResetsTheCounter()
    {
        await _store.SetToken(Token());
        _oauth.RefreshAsync(Arg.Any<string>()).Returns(_ => throw new HttpRequestException(), _ => Task.FromResult<OAuthToken?>(Token("r2")));

        await _account.RefreshAsync();
        await _account.RefreshAsync();

        Assert.That(_account.Status().ConsecutiveFailures, Is.EqualTo(0));
        Assert.That(_account.Status().Status, Is.EqualTo(HaloAuthStatus.Authorized));
    }

    [Test]
    public void SignInStateIsSingleUseAndExpires()
    {
        var states = new List<string>();
        _oauth.BuildSignInUrl(Arg.Do<string>(states.Add)).Returns("https://login.live.com/");

        _account.CreateSignInUrl();
        _account.CreateSignInUrl();

        Assert.That(states, Has.Count.EqualTo(2));
        Assert.That(states[0], Is.Not.EqualTo(states[1]));
        Assert.That(_account.ConsumeState(states[0]), Is.True);
        Assert.That(_account.ConsumeState(states[0]), Is.False, "state is single-use");
        Assert.That(_account.ConsumeState("unknown"), Is.False);
        Assert.That(_account.ConsumeState(null), Is.False);

        _time.Now += TimeSpan.FromMinutes(16);
        Assert.That(_account.ConsumeState(states[1]), Is.False, "state expired");
    }

    [Test]
    public async Task RedeemStoresTokenResetsAndValidatesTheSession()
    {
        _oauth.RedeemCodeAsync("code").Returns(Token());

        var result = await _account.RedeemCodeAsync(" code ");

        Assert.That(result.Success, Is.True);
        Assert.That(await _store.GetToken(), Is.Not.Null);
        await _session.Received(1).ResetAsync();
        await _session.Received(1).ValidateAsync();
        Assert.That(_account.Status().Status, Is.EqualTo(HaloAuthStatus.Authorized));
    }

    [Test]
    public async Task RejectedCodeIsReportedAndNothingIsStored()
    {
        _oauth.RedeemCodeAsync("bad").Returns((OAuthToken?)null);

        var result = await _account.RedeemCodeAsync("bad");

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("rejected"));
        Assert.That(await _store.GetToken(), Is.Null);
    }

    [Test]
    public async Task FailedTokenChainAfterSignInIsAnError()
    {
        _oauth.RedeemCodeAsync("code").Returns(Token());
        _session.ValidateAsync().ThrowsAsync(new InvalidOperationException("Failed to get halo token."));

        var result = await _account.RedeemCodeAsync("code");

        Assert.That(result.Success, Is.False);
        Assert.That(_account.Status().Status, Is.EqualTo(HaloAuthStatus.Error));
        Assert.That(_account.Status().LastError, Does.Contain("Failed to get halo token."));
    }

    [Test]
    public async Task ClearDeletesTokensAndRequiresReauth()
    {
        await _store.SetToken(Token());

        await _account.ClearAsync();

        Assert.That(await _store.GetToken(), Is.Null);
        await _session.Received(1).ResetAsync();
        Assert.That(_account.Status().Status, Is.EqualTo(HaloAuthStatus.ReauthRequired));
    }

    [Test]
    public async Task UnknownStateIsHealthyAndRefresherSurvivesExceptions()
    {
        Assert.That(HaloAuthHealthCheck.Evaluate(_account.Status()).Status, Is.EqualTo(HealthStatus.Healthy));

        var throwingStore = Substitute.For<IOAuthTokenStore>();
        throwingStore.GetToken().ThrowsAsync(new IOException("disk"));
        var account = new HaloAccountService(throwingStore, _oauth, _session, _state, new Monitor<GatewayOptions>(new GatewayOptions()), _time,
            NullLogger<HaloAccountService>.Instance);
        var refresher = new HaloTokenRefresher(account, new Monitor<GatewayOptions>(new GatewayOptions()), _time, NullLogger<HaloTokenRefresher>.Instance);

        await refresher.RunOnceAsync();

        Assert.That(_state.Snapshot().Status, Is.EqualTo(HaloAuthStatus.Error));
    }

    [Test]
    public void SecondInstanceLockFails()
    {
        var path = Path.Combine(_dir, "tokens.json");
        using var first = InstanceLock.Acquire(path);

        Assert.Throws<InvalidOperationException>(() => InstanceLock.Acquire(path));
    }

    // ---- Quotas ----

    private static QuotaGate Gate(int globalPerMinute, double reserved = 0.5)
        => new(new Monitor<GatewayOptions>(new GatewayOptions
        {
            GlobalQuota = new GlobalQuotaOptions { PermitsPerMinute = globalPerMinute, ReservedFraction = reserved, ReservedPriority = 0 },
        }));

    [Test]
    public void LowPriorityKeysCannotUseTheReservedShare()
    {
        using var gate = Gate(globalPerMinute: 4, reserved: 0.5);
        var prod = new ApiKeyOptions { Name = "prod", Priority = 0, PermitsPerMinute = 0 };
        var preview = new ApiKeyOptions { Name = "preview", Priority = 2, PermitsPerMinute = 0 };

        var previewResults = Enumerable.Range(0, 3).Select(_ => gate.TryAcquire(preview)).ToList();
        var prodResults = Enumerable.Range(0, 3).Select(_ => gate.TryAcquire(prod)).ToList();

        Assert.That(previewResults.Select(r => r.Allowed), Is.EqualTo(new[] { true, true, false }));
        Assert.That(previewResults[2].Reason, Is.EqualTo("global-reserved"));
        Assert.That(prodResults.Select(r => r.Allowed), Is.EqualTo(new[] { true, true, false }));
        Assert.That(prodResults[2].Reason, Is.EqualTo("global"));
        Assert.That(prodResults[2].RetryAfter, Is.Not.Null);
    }

    [Test]
    public void PerKeyBucketsAreIndependent()
    {
        using var gate = Gate(globalPerMinute: 0);
        var a = new ApiKeyOptions { Name = "a", PermitsPerMinute = 2 };
        var b = new ApiKeyOptions { Name = "b", PermitsPerMinute = 2 };

        Assert.That(gate.TryAcquire(a).Allowed, Is.True);
        Assert.That(gate.TryAcquire(a).Allowed, Is.True);
        Assert.That(gate.TryAcquire(a), Is.EqualTo(new QuotaDecision(false, "key", TimeSpan.FromSeconds(30))));
        Assert.That(gate.TryAcquire(b).Allowed, Is.True);
        Assert.That(gate.Remaining().Single(r => r.Key == "a").Available, Is.EqualTo(0));
    }

    // ---- API keys ----

    [Test]
    public void ApiKeysMatchInClearOrHashedForm()
    {
        var keys = new[]
        {
            new ApiKeyOptions { Name = "clear", Key = "secret-1" },
            new ApiKeyOptions { Name = "hashed", KeySha256 = Convert.ToHexString(GatewayAuth.Hash("secret-2")) },
            new ApiKeyOptions { Name = "broken", KeySha256 = "not-hex" },
        };

        Assert.That(GatewayAuth.Match(keys, "secret-1")?.Name, Is.EqualTo("clear"));
        Assert.That(GatewayAuth.Match(keys, "secret-2")?.Name, Is.EqualTo("hashed"));
        Assert.That(GatewayAuth.Match(keys, "secret-3"), Is.Null);
        Assert.That(GatewayAuth.Match(keys, "not-hex"), Is.Null);
    }

    [TestCase("https://gw/oauth/callback?code=M.C1&state=s", "M.C1")]
    [TestCase("code=M.C2", "M.C2")]
    [TestCase("state=s&code=a%2Bb", "a+b")]
    [TestCase("nothing", null)]
    public void PastedCodeIsExtractedFromRedirectUrls(string input, string? expected)
    {
        Assert.That(AdminController.ExtractCode(input), Is.EqualTo(expected));
    }
}
