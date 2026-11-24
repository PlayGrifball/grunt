using System.Net;
using Microsoft.Extensions.Time.Testing;
using Surprenant.Grunt.Core;

namespace Tests;

/// <summary>
/// Halo answers the Economy and catalog calls only when they carry the account's current flight (the <c>343-clearance</c> header),
/// so the gateway fetches it once and sends it, and keeps the last good one when Halo is down.
/// </summary>
public class ClearanceTests
{
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"FlightConfigurationId":"0e3a52e6-1f6b-4b72-8a5b-0123456789ab"}"""),
            });
        }
    }

    [Test]
    public async Task TheActiveClearance_IsReadFromTheSettingsServiceWithoutAClearanceOfItsOwn()
    {
        var handler = new RecordingHandler();
        var client = new HaloInfiniteClient(new HttpClient(handler), "spartan", "xuid(1)", "stale");

        var result = await client.SettingsGetActiveClearance("xuid(1)");

        var request = handler.Requests.Single();
        Assert.That(request.RequestUri!.ToString(), Is.EqualTo(
            "https://settings.svc.halowaypoint.com/oban/flight-configurations/titles/hi/audiences/retail/players/xuid(1)/active"));
        Assert.That(request.Headers.GetValues("x-343-authorization-spartan").Single(), Is.EqualTo("spartan"));
        Assert.That(request.Headers.Contains("343-clearance"), Is.False);
        Assert.That(result.Result!.FlightConfigurationId, Is.EqualTo("0e3a52e6-1f6b-4b72-8a5b-0123456789ab"));
    }

    [TestCase("")]
    [TestCase("2533274793169437")]
    [TestCase("xuid(1)/../x")]
    public async Task AnActiveClearanceForANonXuid_Gets400_WithoutAskingHalo(string player)
    {
        var handler = new RecordingHandler();
        var client = new HaloInfiniteClient(new HttpClient(handler), "spartan");

        var result = await client.SettingsGetActiveClearance(player);

        Assert.That(result.Error.Code, Is.EqualTo(400));
        Assert.That(handler.Requests, Is.Empty);
    }

    [Test]
    public async Task AnEconomyCall_SendsTheClientsClearance()
    {
        var handler = new RecordingHandler();
        var client = new HaloInfiniteClient(new HttpClient(handler), "spartan", "xuid(1)", "flight-1");

        await client.EconomyArmorCoresCustomization("xuid(2)");

        Assert.That(handler.Requests.Single().Headers.GetValues("343-clearance").Single(), Is.EqualTo("flight-1"));
    }

    [TestCase(null)]
    [TestCase("")]
    public async Task ACatalogWithNoFlight_UsesTheClientsClearance(string? flightId)
    {
        var handler = new RecordingHandler();
        var client = new HaloInfiniteClient(new HttpClient(handler), "spartan", "xuid(1)", "0e3a52e6-1f6b-4b72-8a5b-0123456789ab");

        var result = await client.GameCmsGetCustomizationCatalog(flightId!);

        Assert.That(result.Error.Code, Is.EqualTo(200));
        Assert.That(handler.Requests.Single().RequestUri!.Query, Is.EqualTo("?flight=0e3a52e6-1f6b-4b72-8a5b-0123456789ab"));
    }

    [Test]
    public async Task ACatalogWithAFlight_UsesThatFlight()
    {
        var handler = new RecordingHandler();
        var client = new HaloInfiniteClient(new HttpClient(handler), "spartan", "xuid(1)", "1f3a52e6-1f6b-4b72-8a5b-0123456789ab");

        await client.GameCmsGetCustomizationCatalog("0e3a52e6-1f6b-4b72-8a5b-0123456789ab");

        Assert.That(handler.Requests.Single().RequestUri!.Query, Is.EqualTo("?flight=0e3a52e6-1f6b-4b72-8a5b-0123456789ab"));
    }

    [Test]
    public async Task TheClearance_IsFetchedOnce_WhileItLasts()
    {
        var time = new FakeTimeProvider();
        var fetches = 0;
        var cache = new HaloClearanceCache(() => Task.FromResult<string?>($"flight-{++fetches}"), TimeSpan.FromMinutes(10), time);

        var first = await cache.GetAsync();
        time.Advance(TimeSpan.FromMinutes(9));
        var second = await cache.GetAsync();

        Assert.That(new[] { first, second }, Is.All.EqualTo("flight-1"));
        Assert.That(fetches, Is.EqualTo(1));
    }

    [Test]
    public async Task TheClearance_IsFetchedAgain_OnceItHasExpired()
    {
        var time = new FakeTimeProvider();
        var fetches = 0;
        var cache = new HaloClearanceCache(() => Task.FromResult<string?>($"flight-{++fetches}"), TimeSpan.FromMinutes(10), time);

        await cache.GetAsync();
        time.Advance(TimeSpan.FromMinutes(10));
        var again = await cache.GetAsync();

        Assert.That(again, Is.EqualTo("flight-2"));
    }

    [Test]
    public async Task WhenHaloCannotBeAsked_TheLastClearanceIsKept_AndAskedAgainNextCall()
    {
        var time = new FakeTimeProvider();
        var calls = 0;
        var cache = new HaloClearanceCache(() =>
        {
            calls++;
            if (calls == 2) throw new HttpRequestException("down");
            return Task.FromResult<string?>($"flight-{calls}");
        }, TimeSpan.FromMinutes(10), time);

        await cache.GetAsync();
        time.Advance(TimeSpan.FromMinutes(11));
        var whileDown = await cache.GetAsync();
        var straightAfter = await cache.GetAsync();
        time.Advance(HaloClearanceCache.RetryAfterFailure);
        var afterwards = await cache.GetAsync();

        Assert.That(whileDown, Is.EqualTo("flight-1"));
        Assert.That(straightAfter, Is.EqualTo("flight-1"));
        Assert.That(calls, Is.EqualTo(3), "a failed fetch is not repeated for every call");
        Assert.That(afterwards, Is.EqualTo("flight-3"));
    }

    [Test]
    public async Task WithNoClearanceYet_AFailureIsNull_NotAnException()
    {
        var cache = new HaloClearanceCache(() => Task.FromResult<string?>(null), TimeSpan.FromMinutes(10), new FakeTimeProvider());

        Assert.That(await cache.GetAsync(), Is.Null);
    }

    [Test]
    public async Task CallersAtTheSameTime_ShareOneFetch()
    {
        var release = new TaskCompletionSource<string?>();
        var fetches = 0;
        var cache = new HaloClearanceCache(() => { Interlocked.Increment(ref fetches); return release.Task; }, TimeSpan.FromMinutes(10), new FakeTimeProvider());

        var all = Enumerable.Range(0, 5).Select(_ => cache.GetAsync()).ToList();
        release.SetResult("flight-1");

        Assert.That(await Task.WhenAll(all), Is.All.EqualTo("flight-1"));
        Assert.That(fetches, Is.EqualTo(1));
    }

    [Test]
    public async Task ThePlayersAppearance_AsksHaloForTheAppearancePath()
    {
        var handler = new RecordingHandler();
        var client = new HaloInfiniteClient(new HttpClient(handler), "spartan", "xuid(1)", "flight-1");

        await client.EconomyPlayerAppearanceCustomization("xuid(2)");

        Assert.That(handler.Requests.Single().RequestUri!.AbsolutePath, Is.EqualTo("/hi/players/xuid(2)/customization/appearance"));
    }

    [TestCase("")]
    [TestCase("2533274793169437")]
    [TestCase("xuid(1)/../../x")]
    [TestCase("xuid(1)?x=1")]
    public async Task AnAppearanceForANonXuid_Gets400_WithoutAskingHalo(string player)
    {
        var handler = new RecordingHandler();
        var client = new HaloInfiniteClient(new HttpClient(handler), "spartan", "xuid(1)", "flight-1");

        var result = await client.EconomyPlayerAppearanceCustomization(player);

        Assert.That(result.Error.Code, Is.EqualTo(400));
        Assert.That(handler.Requests, Is.Empty);
    }

    [Test]
    public void TheAppearance_IsOnTheGateway()
    {
        var exposed = typeof(HaloInfiniteClient).GetMethods()
            .Where(m => m.GetCustomAttributes(typeof(Surprenant.Grunt.Attributes.GatewayExposedAttribute), false).Length > 0)
            .Select(m => m.Name);

        Assert.That(exposed, Does.Contain("EconomyPlayerAppearanceCustomization"));
    }
}
