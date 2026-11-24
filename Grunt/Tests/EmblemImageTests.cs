using System.Net;
using Surprenant.Grunt.Core;

namespace Tests;

/// <summary>
/// Emblem images are not on the item document: Halo's emblem mapping gives each emblem and configuration its image paths, which are then
/// downloaded from the Waypoint file store.
/// </summary>
public class EmblemImageTests
{
    private sealed class FixedHandler(string body) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    private static HaloInfiniteClient Create(HttpMessageHandler handler)
        => new(new HttpClient(handler), "spartan", "xuid(1)", "0e3a52e6-1f6b-4b72-8a5b-0123456789ab");

    [Test]
    public async Task TheEmblemMapping_IsReadFromTheWaypointStore_ByEmblemAndConfiguration()
    {
        var handler = new FixedHandler("""
            {"3627007-104":{"1198260756":{"emblemCmsPath":"images/emblems/a.png","nameplateCmsPath":"images/nameplates/a.png","textColor":"#ffffff"}}}
            """);

        var result = await Create(handler).GameCmsGetEmblemMapping();

        Assert.That(result.Error.Code, Is.EqualTo(200));
        Assert.That(handler.Requests.Single().AbsolutePath, Is.EqualTo("/hi/Waypoint/file/images/emblems/mapping.json"));
        var assets = result.Result!["3627007-104"]["1198260756"];
        Assert.That(assets.EmblemCmsPath, Is.EqualTo("images/emblems/a.png"));
        Assert.That(assets.NameplateCmsPath, Is.EqualTo("images/nameplates/a.png"));
        Assert.That(assets.TextColor, Is.EqualTo("#ffffff"));
    }

    [TestCase("images/emblems/a.png")]
    [TestCase("/images/nameplates/a-b_c.png", Description = "a leading slash is dropped")]
    public async Task AWaypointImagePath_IsDownloaded(string path)
    {
        var handler = new FixedHandler("png");

        var result = await Create(handler).GameCmsWaypointGetImage(path);

        Assert.That(result.Error.Code, Is.EqualTo(200));
        Assert.That(handler.Requests.Single().AbsolutePath, Is.EqualTo($"/hi/Waypoint/file/{path.TrimStart('/')}"));
    }

    [TestCase("")]
    [TestCase("../x.png")]
    [TestCase("images/../x.png")]
    [TestCase("images/a.png?x=1")]
    [TestCase("images/a.png#x")]
    [TestCase("images//a.png")]
    [TestCase("images/a b.png")]
    [TestCase("images/a.json", Description = "images only")]
    [TestCase("images/a..png")]
    public async Task AWaypointImagePathThatCannotBeOne_Gets400_WithoutAskingHalo(string path)
    {
        var handler = new FixedHandler("png");

        var result = await Create(handler).GameCmsWaypointGetImage(path);

        Assert.That(result.Error.Code, Is.EqualTo(400));
        Assert.That(handler.Requests, Is.Empty);
    }

    [Test]
    public void TheMappingAndTheWaypointImage_AreOnTheGateway()
    {
        var exposed = typeof(HaloInfiniteClient).GetMethods()
            .Where(m => m.GetCustomAttributes(typeof(Surprenant.Grunt.Attributes.GatewayExposedAttribute), false).Length > 0)
            .Select(m => m.Name);

        Assert.That(exposed, Does.Contain("GameCmsGetEmblemMapping"));
        Assert.That(exposed, Does.Contain("GameCmsWaypointGetImage"));
    }
}
