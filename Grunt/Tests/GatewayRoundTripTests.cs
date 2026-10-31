using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Surprenant.Grunt.Core;
using Surprenant.Grunt.Gateway;
using Surprenant.Grunt.Gateway.Client;
using Surprenant.Grunt.Gateway.Host.HaloAccount;
using Surprenant.Grunt.Models;
using Surprenant.Grunt.Models.HaloInfinite;
using Surprenant.Grunt.Models.HaloInfinite.Medals;
using File = System.IO.File;
using MatchType = Surprenant.Grunt.Models.HaloInfinite.MatchType;
using System.Net;
using System.Net.Http.Json;

namespace Tests;

/// <summary>
/// Host + generated controller + generated client end to end, with a fake IHaloInfiniteClient standing in for the factory.
/// </summary>
public class GatewayRoundTripTests
{
    private const string ProdKey = "prod-key";
    private const string PreviewKey = "preview-key";
    private const string AdminKey = "admin-key";

    private sealed class GatewayFactory : WebApplicationFactory<Program>
    {
        public IHaloInfiniteClient Halo { get; } = Substitute.For<IHaloInfiniteClient>();
        public IHaloOAuthClient OAuth { get; } = Substitute.For<IHaloOAuthClient>();
        public IHaloSession Session { get; } = Substitute.For<IHaloSession>();
        public string TokenDirectory { get; } = Path.Combine(Path.GetTempPath(), "grunt-gateway-tests", Guid.NewGuid().ToString("N"));

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Gateway:AdminKey", AdminKey);
            builder.UseSetting("Gateway:ApiKeys:0:Name", "grif-prod");
            builder.UseSetting("Gateway:ApiKeys:0:Key", ProdKey);
            builder.UseSetting("Gateway:ApiKeys:0:Environment", "prod");
            builder.UseSetting("Gateway:ApiKeys:0:Priority", "0");
            builder.UseSetting("Gateway:ApiKeys:0:PermitsPerMinute", "1000");
            builder.UseSetting("Gateway:ApiKeys:1:Name", "grif-preview");
            builder.UseSetting("Gateway:ApiKeys:1:Key", PreviewKey);
            builder.UseSetting("Gateway:ApiKeys:1:Environment", "preview");
            builder.UseSetting("Gateway:ApiKeys:1:Priority", "2");
            builder.UseSetting("Gateway:ApiKeys:1:PermitsPerMinute", "2");
            builder.UseSetting("Gateway:GlobalQuota:PermitsPerMinute", "0");
            builder.UseSetting("Gateway:Auth:UseInstanceLock", "false");
            builder.UseSetting("Gateway:Auth:InitialDelay", "01:00:00");
            builder.UseSetting("Gateway:Auth:TokenPath", Path.Combine(TokenDirectory, "tokens.json"));
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton(Halo);
                services.AddSingleton(OAuth);
                services.AddSingleton(Session);
            });
        }

        public HaloGatewayClient CreateGatewayClient(string? apiKey = ProdKey, int maxAttempts = 1)
            => new(new HaloGatewayTransport(CreateClient(), new HaloGatewayClientOptions
            {
                ApiKey = apiKey,
                Retry = new HaloRetryOptions { MaxAttempts = maxAttempts, BaseDelay = TimeSpan.Zero, UseJitter = false },
            }));

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (Directory.Exists(TokenDirectory))
                Directory.Delete(TokenDirectory, recursive: true);
        }
    }

    private GatewayFactory _factory = null!;

    [SetUp]
    public void SetUp() => _factory = new GatewayFactory();

    [TearDown]
    public void TearDown() => _factory.Dispose();

    private static Task<HaloApiResultContainer<T, HaloApiErrorContainer>> Container<T>(T result, int code, string? message = null)
        => Task.FromResult(new HaloApiResultContainer<T, HaloApiErrorContainer>(result, new HaloApiErrorContainer { Code = code, Message = message }));

    private static User Grunt() => new() { xuid = "2533274800000000", gamertag = "Grunt", gamerpic = new Gamerpic { small = "s.png" } };

    [Test]
    public async Task UserByGamertagRoundTripsAndIsCached()
    {
        _factory.Halo.UserByGamertag("Grunt Lord").Returns(_ => Container(Grunt(), 200));
        var client = _factory.CreateGatewayClient();

        var first = await client.UserByGamertag("Grunt Lord");
        var second = await client.UserByGamertag("Grunt Lord");

        Assert.Multiple(() =>
        {
            Assert.That(first.Error.Code, Is.EqualTo(200));
            Assert.That(first.Result.gamertag, Is.EqualTo("Grunt"));
            Assert.That(first.Result.gamerpic.small, Is.EqualTo("s.png"));
            Assert.That(second.Result.xuid, Is.EqualTo("2533274800000000"));
        });
        await _factory.Halo.Received(1).UserByGamertag("Grunt Lord");
    }

    [Test]
    public async Task CacheHitIsMarkedOnTheResponse()
    {
        _factory.Halo.UserByGamertag("Grunt").Returns(_ => Container(Grunt(), 200));
        var http = _factory.CreateClient();
        http.DefaultRequestHeaders.Add("X-Api-Key", ProdKey);

        var miss = await http.GetAsync("halo/v1/users/by-gamertag/Grunt");
        var hit = await http.GetAsync("halo/v1/users/by-gamertag/Grunt");

        Assert.That(miss.Headers.GetValues(HaloGatewayCacheFilter.CacheHeader).Single(), Is.EqualTo("miss"));
        Assert.That(hit.Headers.GetValues(HaloGatewayCacheFilter.CacheHeader).Single(), Is.EqualTo("hit"));
        Assert.That(hit.Headers.GetValues(HaloGatewayProtocol.UpstreamStatusHeader).Single(), Is.EqualTo("200"));
        Assert.That(hit.Headers.CacheControl?.MaxAge, Is.EqualTo(TimeSpan.FromSeconds(300)));
    }

    [Test]
    public async Task QueryAndEnumParametersBind()
    {
        _factory.Halo.StatsGetMatchHistory("xuid(123)", 25, 10, MatchType.Custom)
            .Returns(_ => Container(new MatchHistoryResponse(), 200));
        var client = _factory.CreateGatewayClient();

        var result = await client.StatsGetMatchHistory("xuid(123)", 25, 10, MatchType.Custom);

        Assert.That(result.Error.Code, Is.EqualTo(200));
        await _factory.Halo.Received(1).StatsGetMatchHistory("xuid(123)", 25, 10, MatchType.Custom);
    }

    [Test]
    public async Task CollectionParametersBindFromTheQuery()
    {
        _factory.Halo.Users(Arg.Any<IEnumerable<string>>()).Returns(_ => Container(new List<User> { Grunt() }, 200));
        var client = _factory.CreateGatewayClient();

        var result = await client.Users(new[] { "1", "2" });

        Assert.That(result.Result, Has.Count.EqualTo(1));
        await _factory.Halo.Received(1).Users(Arg.Is<IEnumerable<string>>(x => x.SequenceEqual(new[] { "1", "2" })));
    }

    [Test]
    public async Task GuidOverloadUsesTheGuidRoute()
    {
        var id = Guid.NewGuid();
        _factory.Halo.StatsGetMatchStats(id).Returns(_ => Container(new MatchStats(), 200));
        var client = _factory.CreateGatewayClient();

        var result = await client.StatsGetMatchStats(id);

        Assert.That(result.Error.Code, Is.EqualTo(200));
        await _factory.Halo.Received(1).StatsGetMatchStats(id);
        await _factory.Halo.DidNotReceive().StatsGetMatchStats(Arg.Any<string>());
    }

    [Test]
    public async Task NotFoundPassesThrough()
    {
        _factory.Halo.UserByGamertag("nobody").Returns(_ => Container<User>(null!, 404, "not found"));
        var http = _factory.CreateClient();
        http.DefaultRequestHeaders.Add("X-Api-Key", ProdKey);

        var raw = await http.GetAsync("halo/v1/users/by-gamertag/nobody");
        var result = await _factory.CreateGatewayClient().UserByGamertag("nobody");

        Assert.That(raw.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That(result.Error.Code, Is.EqualTo(404));
        Assert.That(result.Error.Message, Is.EqualTo("not found"));
        Assert.That(result.Result, Is.Null);
    }

    [Test]
    public async Task UpstreamFailureIs502AndNotRetriedByTheClient()
    {
        _factory.Halo.Medals().Returns(_ => Container<MedalMetadataResponse>(null!, 503));
        var http = _factory.CreateClient();
        http.DefaultRequestHeaders.Add("X-Api-Key", ProdKey);

        var raw = await http.GetAsync("halo/v1/medals");
        var result = await _factory.CreateGatewayClient(maxAttempts: 3).Medals();

        Assert.That(raw.StatusCode, Is.EqualTo(HttpStatusCode.BadGateway));
        Assert.That(result.Error.Code, Is.EqualTo(503));
        await _factory.Halo.Received(2).Medals();
    }

    [Test]
    public async Task FactoryExceptionBecomes502Container()
    {
        _factory.Halo.StatsGetMatchCount("xuid(1)").ThrowsAsync(new HttpRequestException("down"));

        var result = await _factory.CreateGatewayClient().StatsGetMatchCount("xuid(1)");

        Assert.That(result.Error.Code, Is.EqualTo(502));
        Assert.That(result.Error.Message, Does.Contain("HttpRequestException"));
    }

    [Test]
    public async Task BinaryResultsRoundTripAsRawBytes()
    {
        var bytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0, 1, 2, 3 };
        _factory.Halo.GameCmsGetImage("progression/a b.png").Returns(_ => Container(bytes, 200));
        var http = _factory.CreateClient();
        http.DefaultRequestHeaders.Add("X-Api-Key", ProdKey);

        var raw = await http.GetAsync("halo/v1/gamecms/image?filePath=progression%2Fa%20b.png");
        var result = await _factory.CreateGatewayClient().GameCmsGetImage("progression/a b.png");

        Assert.That(raw.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/octet-stream"));
        Assert.That(result.Error.Code, Is.EqualTo(200));
        Assert.That(result.Result, Is.EqualTo(bytes));
    }

    [TestCase(null)]
    [TestCase("wrong-key")]
    public async Task MissingOrWrongApiKeyIs401(string? key)
    {
        var result = await _factory.CreateGatewayClient(apiKey: key).UserByGamertag("Grunt");

        Assert.That(result.Error.Code, Is.EqualTo(401));
        await _factory.Halo.DidNotReceive().UserByGamertag(Arg.Any<string>());
    }

    [Test]
    public async Task AdminKeyIsNotAConsumerKey()
    {
        var result = await _factory.CreateGatewayClient(apiKey: AdminKey).UserByGamertag("Grunt");

        Assert.That(result.Error.Code, Is.EqualTo(401));
    }

    [Test]
    public async Task PerKeyQuotaReturns429()
    {
        _factory.Halo.StatsGetMatchCount(Arg.Any<string>()).Returns(_ => Container(new PlayerMatchCount(), 200));
        var client = _factory.CreateGatewayClient(apiKey: PreviewKey);

        var first = await client.StatsGetMatchCount("xuid(1)");
        var second = await client.StatsGetMatchCount("xuid(2)");
        var third = await client.StatsGetMatchCount("xuid(3)");

        Assert.That(new[] { first.Error.Code, second.Error.Code }, Has.All.EqualTo(200));
        Assert.That(third.Error.Code, Is.EqualTo(429));
        Assert.That(third.Error.RetryAfter, Is.Not.Null);
        // The prod key has its own bucket.
        Assert.That((await _factory.CreateGatewayClient().StatsGetMatchCount("xuid(4)")).Error.Code, Is.EqualTo(200));
    }

    [Test]
    public void MethodsNotExposedThrowNotSupported()
    {
        var client = _factory.CreateGatewayClient();

        var ex = Assert.Throws<NotSupportedException>(() => client.HIUGCDeleteAsset("t", "Map", "id"));
        Assert.That(ex!.Message, Does.Contain("HIUGCDeleteAsset").And.Contain("GatewayExposed"));
    }

    [Test]
    public async Task HealthLiveAnswersWithoutChecks()
    {
        var http = _factory.CreateClient();

        var live = await http.GetAsync("health/live");
        var ready = await http.GetAsync("health/ready");

        Assert.That(live.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(ready.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    // ---- Admin / OAuth flow ----

    private static readonly System.Text.Json.JsonSerializerOptions AdminJson = new(System.Text.Json.JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private HttpClient Admin()
    {
        var http = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        http.DefaultRequestHeaders.Add("X-Admin-Key", AdminKey);
        return http;
    }

    [Test]
    public async Task AdminEndpointsNeedTheAdminKey()
    {
        var anonymous = _factory.CreateClient();
        anonymous.DefaultRequestHeaders.Add("X-Api-Key", ProdKey);

        Assert.That((await anonymous.GetAsync("admin/halo/status")).StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That((await Admin().GetAsync("admin/halo/status")).StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task SignInCallbackRedeemsTheCodeOnceForAnIssuedState()
    {
        string? issuedState = null;
        _factory.OAuth.BuildSignInUrl(Arg.Do<string>(s => issuedState = s)).Returns(c => "https://login.live.com/oauth20_authorize.srf?state=" + c.Arg<string>());
        _factory.OAuth.RedeemCodeAsync("the-code").Returns(new OAuthToken { AccessToken = "a", RefreshToken = "r", ExpiresIn = 3600 });
        var admin = Admin();

        var signIn = await admin.GetFromJsonAsync<Dictionary<string, string>>("admin/halo/signin");
        var anonymous = _factory.CreateClient();
        var forged = await anonymous.GetAsync("oauth/callback?code=the-code&state=forged");
        var ok = await anonymous.GetAsync($"oauth/callback?code=the-code&state={issuedState}");
        var replay = await anonymous.GetAsync($"oauth/callback?code=the-code&state={issuedState}");
        var status = await admin.GetFromJsonAsync<HaloAuthSnapshot>("admin/halo/status", AdminJson);

        Assert.Multiple(() =>
        {
            Assert.That(signIn!["url"], Does.Contain("state=" + issuedState));
            Assert.That(forged.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(ok.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(replay.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(status!.Status, Is.EqualTo(HaloAuthStatus.Authorized));
            Assert.That(File.Exists(Path.Combine(_factory.TokenDirectory, "tokens.json")), Is.True);
        });
        await _factory.OAuth.Received(1).RedeemCodeAsync("the-code");
        await _factory.Session.Received(1).ValidateAsync();
    }

    [Test]
    public async Task PastedRedirectUrlCodeIsRedeemedAndTokensCanBeCleared()
    {
        _factory.OAuth.RedeemCodeAsync("M.C123").Returns(new OAuthToken { AccessToken = "a", RefreshToken = "r", ExpiresIn = 3600 });
        var admin = Admin();

        var pasted = await admin.PostAsJsonAsync("admin/halo/code", new { code = "https://gateway.example/oauth/callback?code=M.C123&state=x" });
        var cleared = await admin.DeleteAsync("admin/halo/tokens");
        var status = await admin.GetFromJsonAsync<HaloAuthSnapshot>("admin/halo/status", AdminJson);

        Assert.That(pasted.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(cleared.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(status!.Status, Is.EqualTo(HaloAuthStatus.ReauthRequired));
        Assert.That(File.Exists(Path.Combine(_factory.TokenDirectory, "tokens.json")), Is.False);
    }

    [Test]
    public async Task SignInRedirectOption()
    {
        _factory.OAuth.BuildSignInUrl(Arg.Any<string>()).Returns("https://login.live.com/oauth20_authorize.srf?x=1");

        var response = await Admin().GetAsync("admin/halo/signin?redirect=true");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Redirect));
        Assert.That(response.Headers.Location?.Host, Is.EqualTo("login.live.com"));
    }
}
