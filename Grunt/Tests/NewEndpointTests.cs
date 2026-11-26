using System.Net;
using System.Text.Json;
using Surprenant.Grunt.Core;
using Surprenant.Grunt.Models.HaloInfinite;

namespace Tests;

public class NewEndpointTests
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

    [TestCase("matchmade", "Matchmade")]
    [TestCase("Custom", "Custom")]
    public async Task AServiceRecordMode_IsAskedAboutByItsName(string mode, string expected)
    {
        var (client, handler) = Create();

        var result = await client.StatsGetServiceRecord("xuid(1)", mode, "");

        Assert.That(result.Error.Code, Is.EqualTo(200));
        Assert.That(handler.Requests.Single().AbsolutePath, Is.EqualTo($"/hi/players/xuid(1)/{expected}/servicerecord"));
    }

    [Test]
    public async Task AServiceRecordSeason_IsEscapedIntoTheQuery()
    {
        var (client, handler) = Create();

        await client.StatsGetServiceRecord("xuid(1)", "Matchmade", "Seasons/Season7.json");

        Assert.That(handler.Requests.Single().Query, Is.EqualTo("?seasonId=Seasons%2FSeason7.json"));
    }

    [TestCase("xuid(1)", "Ranked", "")]
    [TestCase("xuid(1)", "Matchmade", "../x.json")]
    [TestCase("xuid(1)", "Matchmade", "Seasons/Season7")]
    [TestCase("nope", "Matchmade", "")]
    public async Task AnInvalidServiceRecordRequest_Gets400_WithoutAskingHalo(string player, string mode, string season)
    {
        var (client, handler) = Create();

        var result = await client.StatsGetServiceRecord(player, mode, season);

        Assert.That(result.Error.Code, Is.EqualTo(400));
        Assert.That(handler.Requests, Is.Empty);
    }

    [Test]
    public async Task InvalidCareerRankAndPlaylistInputs_Get400_WithoutAskingHalo()
    {
        var (client, handler) = Create();

        var codes = new[]
        {
            (await client.GameCmsGetCareerRanks("../x")).Error.Code,
            (await client.GameCmsGetPlaylistAsset("a/../b")).Error.Code,
            (await client.EconomyGetCareerRanks(new List<string> { "xuid(1)" }, "a/b")).Error.Code,
            (await client.EconomyGetCareerRanks(new List<string>(), "careerRank1")).Error.Code,
            (await client.EconomyGetSoftCurrencyOffers("x")).Error.Code,
            (await client.EconomyGetCustomizationOffers("x")).Error.Code,
            (await client.StatsGetCustomExperience("x")).Error.Code,
        };

        Assert.That(codes, Is.All.EqualTo(400));
        Assert.That(handler.Requests, Is.Empty);
    }

    [Test]
    public async Task CareerRanksOfPlayers_AreAskedAboutInOneCall()
    {
        var (client, handler) = Create();

        var result = await client.EconomyGetCareerRanks(new List<string> { "xuid(1)", "xuid(2)" }, "careerRank1");

        Assert.That(result.Error.Code, Is.EqualTo(200));
        Assert.That(handler.Requests.Single().PathAndQuery, Is.EqualTo("/hi/careerranks/careerRank1?players=xuid(1),xuid(2)"));
    }

    [Test]
    public void FractionalCameraAndOrientationValues_Deserialize()
    {
        var json = """{"DOFFStop":2.8,"CameraFOV":42.5,"FocalDistance":1.5,"Orientation":{"x":0.5,"y":-0.25,"z":0}}""";

        var item = JsonSerializer.Deserialize<CommonItemData>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        Assert.That(item.DOFFStop, Is.EqualTo(2.8));
        Assert.That(item.CameraFOV, Is.EqualTo(42.5));
        Assert.That(item.Orientation!.Y, Is.EqualTo(-0.25));
    }

    [Test]
    public void ASearchableDataWithNulls_Deserializes()
    {
        var json = """{"PublishedVersion":null,"IsBanned":null,"FavoritesCount":null,"RatingScore":null}""";

        var data = JsonSerializer.Deserialize<SearchableData>(json)!;

        Assert.That(data.IsBanned, Is.Null);
    }
}
