using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Surprenant.Grunt.Core;
using Surprenant.Grunt.Gateway;
using Surprenant.Grunt.Gateway.Client;
using Surprenant.Grunt.Gateway.Host.Auth;
using Surprenant.Grunt.Gateway.Host.HaloAccount;
using Surprenant.Grunt.Models;
using Surprenant.Grunt.Models.HaloInfinite;
using Surprenant.Grunt.Models.HaloInfinite.Medals;
using File = System.IO.File;
using MatchType = Surprenant.Grunt.Models.HaloInfinite.MatchType;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

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
        public LogCollector Logs { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Gateway:AdminKey", AdminKey);
            builder.UseSetting("Gateway:ApiKeys:0:Name", "grif-prod");
            builder.UseSetting("Gateway:ApiKeys:0:Key", ProdKey);
            builder.UseSetting("Gateway:ApiKeys:0:Environment", "prod");
            builder.UseSetting("Gateway:ApiKeys:1:Name", "grif-preview");
            builder.UseSetting("Gateway:ApiKeys:1:Key", PreviewKey);
            builder.UseSetting("Gateway:ApiKeys:1:Environment", "preview");
            builder.UseSetting("Gateway:Auth:UseInstanceLock", "false");
            builder.UseSetting("Gateway:Auth:InitialDelay", "01:00:00");
            builder.UseSetting("Gateway:Auth:TokenPath", Path.Combine(TokenDirectory, "tokens.json"));
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton(Halo);
                services.AddSingleton(OAuth);
                services.AddSingleton(Session);
                // LoggingExtensions reads sinks from the container (ReadFrom.Services).
                services.AddSingleton<Serilog.Core.ILogEventSink>(Logs);
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

    private sealed class LogCollector : Serilog.Core.ILogEventSink
    {
        public System.Collections.Concurrent.ConcurrentQueue<Serilog.Events.LogEvent> Events { get; } = new();
        public void Emit(Serilog.Events.LogEvent logEvent) => Events.Enqueue(logEvent);
    }

    private static object? Property(Serilog.Events.LogEvent logEvent, string name)
        => logEvent.Properties.TryGetValue(name, out var value) && value is Serilog.Events.ScalarValue scalar ? scalar.Value : null;

    private GatewayFactory _factory = null!;

    [SetUp]
    public void SetUp() => _factory = new GatewayFactory();

    [TearDown]
    public void TearDown() => _factory.Dispose();

    private static Task<HaloApiResultContainer<T, HaloApiErrorContainer>> Container<T>(T result, int code, string? message = null)
        => Task.FromResult(new HaloApiResultContainer<T, HaloApiErrorContainer>(result, new HaloApiErrorContainer { Code = code, Message = message }));

    private static User Grunt() => new() { xuid = "2533274800000000", gamertag = "Grunt", gamerpic = new Gamerpic { small = "s.png" } };

    [Test]
    public async Task ConsumerOpenApiIsCompleteOutsideDevelopment()
    {
        var client = _factory.CreateClient();

        var document = await client.GetAsync("swagger/v1/swagger.json");
        var ui = await client.GetAsync("swagger/index.html");
        var openApi = await document.Content.ReadFromJsonAsync<JsonDocument>();

        var root = openApi!.RootElement;
        var paths = root.GetProperty("paths");
        var controllerPrefix = typeof(HaloInfiniteController).GetCustomAttribute<RouteAttribute>()!.Template!.Trim('/');
        var expectedPaths = typeof(HaloInfiniteController).GetMethods()
            .SelectMany(method => method.GetCustomAttributes<HttpMethodAttribute>())
            .Select(attribute => "/" + controllerPrefix + "/" + Regex.Replace(attribute.Template!, @"\{([^}:]+):[^}]+\}", "{$1}"))
            .ToHashSet(StringComparer.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(document.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(document.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/json"));
            Assert.That(paths.EnumerateObject().Select(path => path.Name), Is.EquivalentTo(expectedPaths),
                "OpenAPI must contain every consumer route and no admin, OAuth, browser or health routes.");

            var operationIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var path in paths.EnumerateObject())
            foreach (var operationProperty in path.Value.EnumerateObject())
            {
                var operation = operationProperty.Value;
                var summary = RequiredText(operation, "summary");
                var description = RequiredText(operation, "description");
                var operationId = RequiredText(operation, "operationId");
                Assert.That(operationId, Is.Not.Empty,
                    $"{operationProperty.Name} {path.Name} must have a stable generated-client method name");
                Assert.That(operationIds.Add(operationId), Is.True,
                    $"{operationProperty.Name} {path.Name} operationId {operationId} must be unique");
                Assert.That(summary, Is.Not.Empty.And.Not.StartWith(operationProperty.Name.ToUpperInvariant() + " "),
                    $"{operationProperty.Name} {path.Name} must explain its purpose, not repeat its route");
                Assert.That(summary.Split(' ', StringSplitOptions.RemoveEmptyEntries), Has.Length.GreaterThanOrEqualTo(4),
                    $"{operationProperty.Name} {path.Name} summary must say what the endpoint is for");
                Assert.That(description, Is.Not.Empty.And.Not.Contain("Forwards ").And.Not.EqualTo(summary),
                    $"{operationProperty.Name} {path.Name} description");

                if (operation.TryGetProperty("parameters", out var parameters))
                    foreach (var parameter in parameters.EnumerateArray())
                        Assert.That(RequiredText(parameter, "description"), Is.Not.Empty,
                            $"{operationProperty.Name} {path.Name} parameter {RequiredText(parameter, "name")}");

                if (operation.TryGetProperty("requestBody", out var requestBody))
                    Assert.That(RequiredText(requestBody, "description"), Is.Not.Empty,
                        $"{operationProperty.Name} {path.Name} request body description");

                Assert.That(operation.GetProperty("responses").EnumerateObject()
                    .All(response => !string.IsNullOrWhiteSpace(RequiredText(response.Value, "description"))), Is.True,
                    $"{operationProperty.Name} {path.Name} response descriptions");
                Assert.That(operation.GetProperty("responses").EnumerateObject().Select(response => response.Name),
                    Does.Contain("400").And.Contain("401").And.Contain("404").And.Contain("429").And.Contain("502").And.Contain("503"),
                    $"{operationProperty.Name} {path.Name} documented error responses");
            }

            var apiKey = root.GetProperty("components").GetProperty("securitySchemes").GetProperty(GatewayAuth.ConsumerScheme);
            Assert.That(apiKey.GetProperty("name").GetString(), Is.EqualTo(HaloGatewayProtocol.DefaultApiKeyHeader));
            Assert.That(root.GetProperty("security").EnumerateArray()
                .Any(requirement => requirement.TryGetProperty(GatewayAuth.ConsumerScheme, out _)), Is.True,
                "The consumer API key must apply to the whole document.");
            Assert.That(ui.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(ui.Content.Headers.ContentType?.MediaType, Is.EqualTo("text/html"));
        });
    }

    [Test]
    public async Task WireMatchesTheOpenApiDocument()
    {
        var id = Guid.NewGuid();
        _factory.Halo.StatsGetMatchStats(id).Returns(_ => Container(new MatchStats
        {
            MatchId = id,
            MatchInfo = new MatchInfo { GameVariantCategory = GameVariantCategory.MultiplayerGrifball },
        }, 200));
        var http = _factory.CreateClient();
        http.DefaultRequestHeaders.Add("X-Api-Key", ProdKey);

        var body = (await (await http.GetAsync($"halo/v1/stats/matches/{id}")).Content.ReadFromJsonAsync<JsonDocument>())!.RootElement;
        var schemas = (await (await http.GetAsync("swagger/v1/swagger.json")).Content.ReadFromJsonAsync<JsonDocument>())!
            .RootElement.GetProperty("components").GetProperty("schemas");

        Assert.Multiple(() =>
        {
            Assert.That(body.GetProperty("error").GetProperty("code").GetInt32(), Is.EqualTo(200), "camelCase, like the documented schemas");
            Assert.That(body.GetProperty("result").GetProperty("matchId").GetGuid(), Is.EqualTo(id));
            Assert.That(body.GetProperty("result").GetProperty("matchInfo").GetProperty("gameVariantCategory").GetString(),
                Is.EqualTo(nameof(GameVariantCategory.MultiplayerGrifball)), "enums are names on the wire");
            Assert.That(schemas.GetProperty(typeof(GameVariantCategory).FullName!).GetProperty("type").GetString(), Is.EqualTo("string"),
                "the document describes enums as the names the wire carries");
        });
    }

    [Test]
    public async Task BinaryResultsAreDocumentedAsRawBytes()
    {
        var http = _factory.CreateClient();

        var document = (await (await http.GetAsync("swagger/v1/swagger.json")).Content.ReadFromJsonAsync<JsonDocument>())!.RootElement;
        var schema = document.GetProperty("paths").GetProperty("/halo/v1/gamecms/image").GetProperty("get").GetProperty("responses")
            .GetProperty("200").GetProperty("content").GetProperty(HaloGatewayProtocol.BinaryContentType).GetProperty("schema");

        Assert.Multiple(() =>
        {
            Assert.That(schema.GetProperty("type").GetString(), Is.EqualTo("string"));
            Assert.That(schema.GetProperty("format").GetString(), Is.EqualTo("binary"),
                "HaloGatewayResults.Binary writes the raw bytes, not the base64 string \"byte\" would mean");
        });
    }

    private static string RequiredText(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) ? value.GetString() ?? string.Empty : string.Empty;

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

    [TestCase("Bowdery(1)(G)")]
    [TestCase("Dr:Who")]
    [TestCase("ABCDEFGHIJKLMNOP")]
    public async Task ImpossibleGamertagIsAnswered400WithoutAskingHalo(string gamertag)
    {
        var http = _factory.CreateClient();
        http.DefaultRequestHeaders.Add("X-Api-Key", ProdKey);

        var raw = await http.GetAsync($"halo/v1/users/by-gamertag/{Uri.EscapeDataString(gamertag)}");
        var viaClient = await _factory.CreateGatewayClient(maxAttempts: 3).UserByGamertag(gamertag);

        Assert.Multiple(() =>
        {
            Assert.That(raw.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(raw.Headers.Contains(HaloGatewayProtocol.UpstreamStatusHeader), Is.False, "the gateway's answer, not Halo's");
            Assert.That(viaClient.Error.Code, Is.EqualTo(400));
            Assert.That(viaClient.Result, Is.Null);
        });
        await _factory.Halo.DidNotReceiveWithAnyArgs().UserByGamertag(default!);
    }

    [Test]
    public async Task PossibleGamertagWithASuffixIsStillAskedAbout()
    {
        _factory.Halo.UserByGamertag("Major Nelson#881").Returns(_ => Container(Grunt(), 200));

        var result = await _factory.CreateGatewayClient().UserByGamertag("Major Nelson#881");

        Assert.That(result.Error.Code, Is.EqualTo(200));
        await _factory.Halo.Received(1).UserByGamertag("Major Nelson#881");
    }

    [Test]
    public async Task ACallItsBudgetTurnedAway_IsTheGateways429_WithRetryAfter_AndNoUpstreamStatus()
    {
        _factory.Halo.UserByGamertag(Arg.Any<string>()).Returns(_ => Task.FromResult(new HaloApiResultContainer<User, HaloApiErrorContainer>(null!,
            new HaloApiErrorContainer { Code = 429, NotSent = true, RetryAfter = TimeSpan.FromSeconds(41.2) })));
        var http = _factory.CreateClient();
        http.DefaultRequestHeaders.Add("X-Api-Key", ProdKey);

        var raw = await http.GetAsync("halo/v1/users/by-gamertag/Grunt");
        var viaClient = await _factory.CreateGatewayClient(maxAttempts: 1).UserByGamertag("Grunt");

        Assert.Multiple(() =>
        {
            Assert.That(raw.StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests));
            Assert.That(raw.Headers.RetryAfter?.Delta, Is.EqualTo(TimeSpan.FromSeconds(42)), "rounded up");
            Assert.That(raw.Headers.Contains(HaloGatewayProtocol.UpstreamStatusHeader), Is.False, "Halo never saw it");
            Assert.That((viaClient.Error.Code, viaClient.Error.NotSent, viaClient.Error.RetryAfter), Is.EqualTo((429, true, (TimeSpan?)TimeSpan.FromSeconds(42))));
        });
    }

    [Test]
    public async Task HalosOwn429_StaysA503WithItsUpstreamStatus_AndTheLockoutAsRetryAfter()
    {
        _factory.Halo.Users(Arg.Any<IEnumerable<string>>()).Returns(_ => Task.FromResult(new HaloApiResultContainer<List<User>, HaloApiErrorContainer>(null!,
            new HaloApiErrorContainer { Code = 429, RetryAfter = TimeSpan.FromMinutes(5) })));
        var http = _factory.CreateClient();
        http.DefaultRequestHeaders.Add("X-Api-Key", ProdKey);

        var raw = await http.GetAsync("halo/v1/users?xuids=1");

        Assert.Multiple(() =>
        {
            Assert.That(raw.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
            Assert.That(raw.Headers.GetValues(HaloGatewayProtocol.UpstreamStatusHeader).Single(), Is.EqualTo("429"));
            Assert.That(raw.Headers.RetryAfter?.Delta, Is.EqualTo(TimeSpan.FromMinutes(5)));
        });
    }

    [Test]
    public async Task LogsWrittenDuringARequestNameTheConsumer()
    {
        _factory.Halo.Medals().ThrowsAsync(new HttpRequestException("Halo is down"));

        await _factory.CreateGatewayClient().Medals();

        var failure = _factory.Logs.Events.Single(e => e.MessageTemplate.Text.StartsWith("Gateway action", StringComparison.Ordinal));
        Assert.Multiple(() =>
        {
            Assert.That(Property(failure, "ConsumerKeyName"), Is.EqualTo("grif-prod"), "filterable by caller in Loki, not only via trace_id");
            Assert.That(Property(failure, "ConsumerEnvironment"), Is.EqualTo("prod"));
        });
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
    public async Task ResponsesLargerThanTheWholeCacheAreNotKept()
    {
        _factory.Halo.UserByGamertag("Grunt").Returns(_ => Container(Grunt(), 200));
        using var small = _factory.WithWebHostBuilder(b => b.UseSetting("Gateway:Cache:MaxCacheBytes", "16"));
        var http = small.CreateClient();
        http.DefaultRequestHeaders.Add("X-Api-Key", ProdKey);

        var first = await http.GetAsync("halo/v1/users/by-gamertag/Grunt");
        var second = await http.GetAsync("halo/v1/users/by-gamertag/Grunt");

        Assert.Multiple(() =>
        {
            Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(first.Headers.GetValues(HaloGatewayCacheFilter.CacheHeader).Single(), Is.EqualTo("miss"));
            Assert.That(second.Headers.GetValues(HaloGatewayCacheFilter.CacheHeader).Single(), Is.EqualTo("miss"));
        });
        await _factory.Halo.Received(2).UserByGamertag("Grunt");
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
    public async Task SkillGetMatchPlayerResultRoundTrips()
    {
        var matchId = Guid.NewGuid().ToString();
        _factory.Halo.SkillGetMatchPlayerResult(Arg.Any<string>(), Arg.Any<List<string>>())
            .Returns(_ => Container(new PlayerSkillResultContainer { Value = [new PlayerSkillResult { Id = "xuid(1)", ResultCode = 0 }] }, 200));

        var result = await _factory.CreateGatewayClient().SkillGetMatchPlayerResult(matchId, ["xuid(1)", "xuid(2)"]);

        Assert.Multiple(() =>
        {
            Assert.That(result.Error.Code, Is.EqualTo(200));
            Assert.That(result.Result.Value.Single().Id, Is.EqualTo("xuid(1)"));
        });
        await _factory.Halo.Received(1).SkillGetMatchPlayerResult(matchId, Arg.Is<List<string>>(x => x.SequenceEqual(new[] { "xuid(1)", "xuid(2)" })));
    }

    [Test]
    public async Task SkillGetPlaylistCsrRoundTrips()
    {
        var playlistId = Guid.NewGuid().ToString();
        _factory.Halo.SkillGetPlaylistCsr(Arg.Any<string>(), Arg.Any<List<string>>())
            .Returns(_ => Container(new PlaylistCsrResultContainer { Value = [new PlaylistCsrResults { Id = "xuid(2)", ResultCode = 0 }] }, 200));

        var result = await _factory.CreateGatewayClient().SkillGetPlaylistCsr(playlistId, ["xuid(1)", "xuid(2)"]);

        Assert.Multiple(() =>
        {
            Assert.That(result.Error.Code, Is.EqualTo(200));
            Assert.That(result.Result.Value.Single().Id, Is.EqualTo("xuid(2)"));
        });
        await _factory.Halo.Received(1).SkillGetPlaylistCsr(playlistId, Arg.Is<List<string>>(x => x.SequenceEqual(new[] { "xuid(1)", "xuid(2)" })));
    }

    [Test]
    public async Task StatsGetPlayerMatchProgressionRoundTrips()
    {
        var matchId = Guid.NewGuid().ToString();
        _factory.Halo.StatsGetPlayerMatchProgression("xuid(123)", matchId)
            .Returns(_ => Container(new MatchProgression { ClearanceId = "clearance", RewardId = "reward" }, 200));

        var result = await _factory.CreateGatewayClient().StatsGetPlayerMatchProgression("xuid(123)", matchId);

        Assert.Multiple(() =>
        {
            Assert.That(result.Error.Code, Is.EqualTo(200));
            Assert.That((result.Result.ClearanceId, result.Result.RewardId), Is.EqualTo(("clearance", "reward")));
        });
        await _factory.Halo.Received(1).StatsGetPlayerMatchProgression("xuid(123)", matchId);
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
    public async Task NoKeyIsLimitedOnItsOwn_FirstComeFirstServed()
    {
        _factory.Halo.StatsGetMatchCount(Arg.Any<string>()).Returns(_ => Container(new PlayerMatchCount(), 200));
        var preview = _factory.CreateGatewayClient(apiKey: PreviewKey);

        var codes = new List<int>();
        for (var i = 0; i < 30; i++)
            codes.Add((await preview.StatsGetMatchCount($"xuid({i})")).Error.Code);

        // Only the account's budgets with Halo limit calls (HaloBudgets), whoever makes them
        Assert.That(codes, Has.All.EqualTo(200));
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

    [Test]
    public async Task ServesNoPrometheusScrapeEndpoint()
    {
        // Metrics are pushed over OTLP only; /metrics must not exist (it would be public with ingressRoute.mode=all).
        var response = await _factory.CreateClient().GetAsync("metrics");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
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
