using System.Net;
using System.Text.Json;
using Surprenant.Grunt.Core;

namespace Tests;

/// <summary>
/// The customization reads put caller-supplied values into the URL Halo is asked for with the gateway's account: inputs that
/// can't be valid are answered 400 without a call, and the gateway's player view is the public one.
/// </summary>
public class CustomizationInputTests
{
    private sealed class CountingHandler : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        }
    }

    private static (HaloInfiniteClient Client, CountingHandler Handler) Create()
    {
        var handler = new CountingHandler();
        return (new HaloInfiniteClient(new HttpClient(handler), "spartan", "", "clearance"), handler);
    }

    [TestCase("xuid(2533274793169437)")]
    [TestCase("xuid(1)")]
    public async Task APlayerThatIsAnXuid_IsAskedAbout(string player)
    {
        var (client, handler) = Create();

        var result = await client.EconomyArmorCoresCustomization(player);

        Assert.That(result.Error.Code, Is.EqualTo(200));
        Assert.That(handler.Requests.Single().AbsolutePath, Is.EqualTo($"/hi/players/{player}/customization/armors"));
    }

    [TestCase("")]
    [TestCase("2533274793169437", Description = "no xuid(...) wrapper")]
    [TestCase("xuid(abc)")]
    [TestCase("xuid(1)/../../x")]
    [TestCase("xuid(1)?view=private")]
    [TestCase("xuid()")]
    [TestCase("xuid(123456789012345678901)", Description = "21 digits")]
    public async Task APlayerThatIsNotAnXuid_Gets400_WithoutAskingHalo(string player)
    {
        var (client, handler) = Create();

        var armors = await client.EconomyArmorCoresCustomization(player);
        var core = await client.EconomyArmorCoreCustomization(player, "017-001-eag-c13d0b38");
        var appearance = await client.EconomyPlayerCustomization(player, "public");

        Assert.That(new[] { armors.Error.Code, core.Error.Code, appearance.Error.Code }, Is.All.EqualTo(400));
        Assert.That(handler.Requests, Is.Empty);
    }

    [TestCase("017-001-eag-c13d0b38")]
    [TestCase("017-001-olympus-c13d0b38")]
    public async Task ACoreIdOfLettersDigitsAndDashes_IsAskedAbout(string coreId)
    {
        var (client, handler) = Create();

        var result = await client.EconomyArmorCoreCustomization("xuid(1)", coreId);

        Assert.That(result.Error.Code, Is.EqualTo(200));
        Assert.That(handler.Requests.Single().AbsolutePath, Does.EndWith($"/armors/{coreId}"));
    }

    [TestCase("")]
    [TestCase("../x")]
    [TestCase("a/b")]
    [TestCase("a?b=c")]
    [TestCase("a&b=c")]
    [TestCase("a b")]
    public async Task ACoreIdWithOtherCharacters_Gets400_WithoutAskingHalo(string coreId)
    {
        var (client, handler) = Create();

        var result = await client.EconomyArmorCoreCustomization("xuid(1)", coreId);

        Assert.That(result.Error.Code, Is.EqualTo(400));
        Assert.That(handler.Requests, Is.Empty);
    }

    [TestCase("public")]
    [TestCase("private")]
    public async Task APublicOrPrivateView_IsAskedAbout(string view)
    {
        var (client, handler) = Create();

        var result = await client.EconomyPlayerCustomization("xuid(1)", view);

        Assert.That(result.Error.Code, Is.EqualTo(200));
        Assert.That(handler.Requests.Single().Query, Is.EqualTo($"?view={view}"));
    }

    [TestCase("")]
    [TestCase("public&x=1")]
    [TestCase("public/../x")]
    [TestCase("everything")]
    public async Task AnyOtherView_Gets400_WithoutAskingHalo(string view)
    {
        var (client, handler) = Create();

        var result = await client.EconomyPlayerCustomization("xuid(1)", view);

        Assert.That(result.Error.Code, Is.EqualTo(400));
        Assert.That(handler.Requests, Is.Empty);
    }

    [Test]
    public async Task ThePublicPlayerView_AsksHaloForTheViewPublic()
    {
        var (client, handler) = Create();

        var result = await client.EconomyPlayerPublicCustomization("xuid(1)");

        Assert.That(result.Error.Code, Is.EqualTo(200));
        Assert.That(handler.Requests.Single().PathAndQuery, Is.EqualTo("/hi/players/xuid(1)/customization?view=public"));
    }

    [Test]
    public void OnlyThePublicPlayerView_IsOnTheGateway()
    {
        var exposed = typeof(HaloInfiniteClient).GetMethods()
            .Where(m => m.GetCustomAttributes(typeof(Surprenant.Grunt.Attributes.GatewayExposedAttribute), false).Length > 0)
            .Select(m => m.Name)
            .ToList();

        Assert.That(exposed, Does.Contain("EconomyPlayerPublicCustomization"));
        Assert.That(exposed, Does.Not.Contain("EconomyPlayerCustomization"));
    }

    [Test]
    public async Task SeveralPlayers_AreAskedAboutTogether_OneCallForTheWholeList()
    {
        var (client, handler) = Create();

        var result = await client.EconomyGetMultiplePlayersCustomization(["xuid(1)", "xuid(2)"]);

        Assert.That(result.Error.Code, Is.EqualTo(200));
        Assert.That(handler.Requests.Single().PathAndQuery, Is.EqualTo("/hi/customization?players=xuid(1),xuid(2)"));
    }

    [Test]
    public async Task TooManyPlayers_Gets400_WithoutAskingHalo()
    {
        var (client, handler) = Create();

        var result = await client.EconomyGetMultiplePlayersCustomization(Enumerable.Range(1, 21).Select(i => $"xuid({i})").ToList());

        Assert.That(result.Error.Code, Is.EqualTo(400));
        Assert.That(handler.Requests, Is.Empty);
    }

    [Test]
    public async Task TwentyPlayers_AreAskedAbout()
    {
        var (client, handler) = Create();

        var result = await client.EconomyGetMultiplePlayersCustomization(Enumerable.Range(1, 20).Select(i => $"xuid({i})").ToList());

        Assert.That(result.Error.Code, Is.EqualTo(200));
        Assert.That(handler.Requests, Has.Count.EqualTo(1));
    }

    [TestCase("", Description = "no players")]
    [TestCase("xuid(1)|")]
    [TestCase("xuid(1)|xuid(2)&x=1")]
    [TestCase("xuid(1),xuid(2)", Description = "a comma smuggles a second player past the count")]
    public async Task APlayerListThatIsEmptyOrHoldsANonXuid_Gets400_WithoutAskingHalo(string joined)
    {
        var (client, handler) = Create();

        var result = await client.EconomyGetMultiplePlayersCustomization((joined.Length == 0 ? new List<string>() : joined.Split('|').ToList()));

        Assert.That(result.Error.Code, Is.EqualTo(400));
        Assert.That(handler.Requests, Is.Empty);
    }

    [TestCase("0e3a52e6-1f6b-4b72-8a5b-0123456789ab")]
    public async Task AFlightIdThatIsAGuid_IsAskedAbout(string flightId)
    {
        var (client, handler) = Create();

        var result = await client.GameCmsGetCustomizationCatalog(flightId);

        Assert.That(result.Error.Code, Is.EqualTo(200));
        Assert.That(handler.Requests, Has.Count.EqualTo(1));
    }

    [TestCase("x&y=1")]
    [TestCase("../x")]
    [TestCase("a b")]
    [TestCase("0e3a52e6-1f6b-4b72-8a5b-0123456789ab-0e3a52e6-1f6b-4b72-8a5b-0123456789ab")]
    public async Task AFlightIdThatIsNotAGuid_Gets400_WithoutAskingHalo(string flightId)
    {
        var (client, handler) = Create();

        var result = await client.GameCmsGetCustomizationCatalog(flightId);

        Assert.That(result.Error.Code, Is.EqualTo(400));
        Assert.That(handler.Requests, Is.Empty);
    }

    [Test]
    public void TheGatewaysBudgets_CoverEveryCustomizationOperation()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Grunt.slnx")))
            dir = dir.Parent;
        Assert.That(dir, Is.Not.Null, "Grunt.slnx not found above the test output");

        var settings = File.ReadAllText(Path.Combine(dir!.FullName, "Grunt.Gateway.Host", "appsettings.json"));
        using var document = JsonDocument.Parse(settings, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        var budgets = document.RootElement.GetProperty("ClientConfiguration").GetProperty("Budgets").EnumerateObject()
            .ToDictionary(b => b.Name, b => new HaloBudget
            {
                Operations = b.Value.GetProperty("Operations").EnumerateArray().Select(o => o.GetString()!).ToList(),
                Limit = b.Value.GetProperty("Limit").GetInt32(),
            });
        var halo = new HaloBudgets(budgets);

        foreach (var operation in new[]
                 {
                     "EconomyPlayerPublicCustomization", "EconomyPlayerAppearanceCustomization", "EconomyPlayerCustomization", "EconomyGetMultiplePlayersCustomization", "EconomyArmorCoresCustomization",
                     "EconomyArmorCoreCustomization", "GameCmsGetCustomizationCatalog", "GameCmsGetItem", "GameCmsGetEmblemMapping", "GameCmsWaypointGetImage",
                 })
            Assert.That(halo.BudgetOf(operation), Is.EqualTo("customization"), operation);
    }
}
