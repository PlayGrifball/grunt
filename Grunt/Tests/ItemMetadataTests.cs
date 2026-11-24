using System.Net;
using Surprenant.Grunt.Core;

namespace Tests;

/// <summary>
/// The per-item metadata (names, images) is asked of GameCms by item path with the account's flight; a path that can't be an item's is
/// answered 400 without a call.
/// </summary>
public class ItemMetadataTests
{
    private const string Flight = "0e3a52e6-1f6b-4b72-8a5b-0123456789ab";

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
        return (new HaloInfiniteClient(new HttpClient(handler), "spartan", "xuid(1)", Flight), handler);
    }

    [TestCase("Inventory/Spartan/Emblems/3627007-SpartanEmblem.json")]
    [TestCase("inventory/armor/emblems/013-001-363f4a25.json")]
    [TestCase("/inventory/armor/emblems/013-001-363f4a25.json", Description = "a leading slash is dropped")]
    public async Task AnItemPath_IsAskedAbout_WithTheClientsFlightByDefault(string itemPath)
    {
        var (client, handler) = Create();

        var result = await client.GameCmsGetItem(itemPath, "");

        Assert.That(result.Error.Code, Is.EqualTo(200));
        Assert.That(handler.Requests.Single().PathAndQuery, Is.EqualTo($"/hi/Progression/file/{itemPath.TrimStart('/')}?flight={Flight}"));
    }

    [Test]
    public async Task AFlightGiven_IsUsed()
    {
        var (client, handler) = Create();
        var other = "1f3a52e6-1f6b-4b72-8a5b-0123456789ab";

        await client.GameCmsGetItem("inventory/a.json", other);

        Assert.That(handler.Requests.Single().Query, Is.EqualTo($"?flight={other}"));
    }

    [TestCase("")]
    [TestCase("../x.json")]
    [TestCase("inventory/../../x.json")]
    [TestCase("inventory/a.json?flight=x")]
    [TestCase("inventory/a.json&x=1")]
    [TestCase("inventory/a b.json")]
    [TestCase("inventory//a.json")]
    [TestCase("inventory/a.txt", Description = "only json documents")]
    [TestCase("inventory/a.json#x")]
    public async Task AnItemPathThatCannotBeOne_Gets400_WithoutAskingHalo(string itemPath)
    {
        var (client, handler) = Create();

        var result = await client.GameCmsGetItem(itemPath, "");

        Assert.That(result.Error.Code, Is.EqualTo(400));
        Assert.That(handler.Requests, Is.Empty);
    }

    [Test]
    public async Task AFlightThatIsNotAGuid_Gets400_WithoutAskingHalo()
    {
        var (client, handler) = Create();

        var result = await client.GameCmsGetItem("inventory/a.json", "x&y=1");

        Assert.That(result.Error.Code, Is.EqualTo(400));
        Assert.That(handler.Requests, Is.Empty);
    }

    [Test]
    public void TheItemMetadata_IsOnTheGateway()
    {
        var exposed = typeof(HaloInfiniteClient).GetMethods()
            .Where(m => m.GetCustomAttributes(typeof(Surprenant.Grunt.Attributes.GatewayExposedAttribute), false).Length > 0)
            .Select(m => m.Name);

        Assert.That(exposed, Does.Contain("GameCmsGetItem"));
    }
}
