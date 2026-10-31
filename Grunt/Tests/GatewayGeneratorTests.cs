using Grunt.SourceGenerator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Surprenant.Grunt.Core;
using Surprenant.Grunt.Gateway;
using Surprenant.Grunt.Gateway.Client;

namespace Tests;

/// <summary>
/// Drives <see cref="GatewayGenerator"/> in memory: snapshots for the real HaloInfiniteClient, and route/verb/body mapping plus
/// diagnostics for a fixture client.
/// </summary>
public class GatewayGeneratorTests
{
    private static readonly Lazy<MetadataReference[]> References = new(() =>
    {
        var paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Concat(new[]
            {
                typeof(HaloInfiniteClient).Assembly.Location,
                typeof(HaloGatewayResults).Assembly.Location,
                typeof(HaloGatewayTransport).Assembly.Location,
            })
            .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Last());
        return paths.Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToArray();
    });

    private sealed record Run(Compilation Output, IReadOnlyList<Diagnostic> GeneratorDiagnostics, IReadOnlyDictionary<string, string> Sources);

    private static Run Generate(string assemblyName, params string[] sources)
    {
        var parse = new CSharpParseOptions(LanguageVersion.Latest);
        var compilation = CSharpCompilation.Create(assemblyName,
            sources.Select(s => CSharpSyntaxTree.ParseText(s, parse)),
            References.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(new[] { new GatewayGenerator().AsSourceGenerator() }, parseOptions: parse);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics);
        var result = driver.GetRunResult().Results.Single();
        return new Run(output, diagnostics,
            result.GeneratedSources.ToDictionary(s => s.HintName, s => s.SourceText.ToString()));
    }

    private static string Normalize(string text) => text.TrimStart('﻿').Replace("\r\n", "\n").TrimEnd();

    private static IEnumerable<Diagnostic> Errors(Compilation compilation)
        => compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error);

    // ---- Snapshots of the real client ----

    [Test]
    public void ControllerForHaloInfiniteClientMatchesSnapshot()
    {
        var run = Generate("Surprenant.Grunt.Gateway",
            "[assembly: Surprenant.Grunt.Attributes.GenerateGatewayController(typeof(Surprenant.Grunt.Core.HaloInfiniteClient))]");

        Assert.That(run.GeneratorDiagnostics, Is.Empty);
        var expected = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Snapshots", "HaloInfiniteController.generated.cs"));
        Assert.That(Normalize(run.Sources["HaloInfiniteController.generated.cs"]), Is.EqualTo(Normalize(expected)),
            "Generated controller changed; if intended, rebuild with -p:EmitCompilerGeneratedFiles=true and copy it into Tests/Snapshots.");
    }

    [Test]
    public void ClientForHaloInfiniteClientMatchesSnapshot()
    {
        var run = Generate("Surprenant.Grunt.Gateway.Client",
            "[assembly: Surprenant.Grunt.Attributes.GenerateGatewayClient(typeof(Surprenant.Grunt.Core.HaloInfiniteClient))]");

        Assert.That(run.GeneratorDiagnostics, Is.Empty);
        var expected = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Snapshots", "HaloGatewayClient.generated.cs"));
        Assert.That(Normalize(run.Sources["HaloGatewayClient.generated.cs"]), Is.EqualTo(Normalize(expected)),
            "Generated client changed; if intended, rebuild with -p:EmitCompilerGeneratedFiles=true and copy it into Tests/Snapshots.");
    }

    [Test]
    public void GeneratedClientImplementsEveryInterfaceMethod()
    {
        var exposed = typeof(HaloInfiniteClient).GetMethods()
            .Count(m => m.GetCustomAttributes(typeof(Surprenant.Grunt.Attributes.GatewayExposedAttribute), false).Length > 0);
        var interfaceMethods = typeof(IHaloInfiniteClient).GetMethods().Length;

        Assert.That(typeof(IHaloInfiniteClient).IsAssignableFrom(typeof(HaloGatewayClient)), Is.True);
        Assert.That(exposed, Is.GreaterThan(0));
        Assert.That(typeof(HaloGatewayClient).GetInterfaceMap(typeof(IHaloInfiniteClient)).TargetMethods, Has.Length.EqualTo(interfaceMethods));
    }

    // ---- Fixture client: mapping and diagnostics ----

    private const string FixtureCommon = """
        using System;
        using System.Collections.Generic;
        using System.Threading.Tasks;
        using Surprenant.Grunt.Attributes;
        using Surprenant.Grunt.Models;

        namespace Fixture;

        public class Payload { public string Name { get; set; } = ""; }
        """;

    private const string FixtureValid = FixtureCommon + """

        public interface IFixtureClient
        {
            Task<HaloApiResultContainer<User, HaloApiErrorContainer>> GetUser(string gamertag, int start, MatchKind kind, List<string> ids);
            Task<HaloApiResultContainer<User, HaloApiErrorContainer>> GetUser(Guid id);
            Task<HaloApiResultContainer<User, HaloApiErrorContainer>> CreateThing(string folder, Payload payload);
            Task<HaloApiResultContainer<bool, HaloApiErrorContainer>> RemoveThing(string id);
            Task<HaloApiResultContainer<byte[], HaloApiErrorContainer>> GetBlob(string path);
            Task<HaloApiResultContainer<string, HaloApiErrorContainer>> NotExposed(string value);
            Task<HaloApiResultContainer<T, HaloApiErrorContainer>> GetFile<T>(string path);
        }

        public enum MatchKind { All, Custom }

        public class FixtureClient : IFixtureClient
        {
            [GatewayExposed("users/{gamertag}", Cache = GatewayCachePolicy.ShortTtl, CacheSeconds = 30)]
            public Task<HaloApiResultContainer<User, HaloApiErrorContainer>> GetUser(string gamertag, int start, MatchKind kind, List<string> ids) => throw null!;

            [GatewayExposed("users/{id:guid}", Name = "GetUserById", Cache = GatewayCachePolicy.Immutable)]
            public Task<HaloApiResultContainer<User, HaloApiErrorContainer>> GetUser(Guid id) => throw null!;

            [GatewayExposed("folders/{folder}/things", HttpMethod = "POST")]
            public Task<HaloApiResultContainer<User, HaloApiErrorContainer>> CreateThing(string folder, Payload payload) => throw null!;

            [GatewayExposed("things/{id}", HttpMethod = "delete")]
            public Task<HaloApiResultContainer<bool, HaloApiErrorContainer>> RemoveThing(string id) => throw null!;

            [GatewayExposed("blobs")]
            public Task<HaloApiResultContainer<byte[], HaloApiErrorContainer>> GetBlob(string path) => throw null!;

            public Task<HaloApiResultContainer<string, HaloApiErrorContainer>> NotExposed(string value) => throw null!;

            public Task<HaloApiResultContainer<T, HaloApiErrorContainer>> GetFile<T>(string path) => throw null!;
        }
        """;

    [Test]
    public void ControllerMapsRoutesVerbsAndBindingSources()
    {
        var run = Generate("Fixture.Gateway", FixtureValid,
            "[assembly: Surprenant.Grunt.Attributes.GenerateGatewayController(typeof(Fixture.FixtureClient), RoutePrefix = \"/fixture/v2/\", Namespace = \"Fixture.Gateway\")]");

        Assert.That(run.GeneratorDiagnostics, Is.Empty);
        Assert.That(Errors(run.Output), Is.Empty);
        var code = run.Sources["HaloInfiniteController.generated.cs"];
        Assert.Multiple(() =>
        {
            Assert.That(code, Does.Contain("namespace Fixture.Gateway;"));
            Assert.That(code, Does.Contain("[global::Microsoft.AspNetCore.Mvc.Route(\"fixture/v2\")]"));
            Assert.That(code, Does.Contain("private readonly global::Fixture.IFixtureClient _client;"));
            Assert.That(code, Does.Contain("[global::Microsoft.AspNetCore.Mvc.HttpGet(\"users/{gamertag}\")]"));
            Assert.That(code, Does.Contain("[global::Microsoft.AspNetCore.Mvc.FromRoute(Name = \"gamertag\")] string gamertag"));
            Assert.That(code, Does.Contain("[global::Microsoft.AspNetCore.Mvc.FromQuery(Name = \"start\")] int start"));
            Assert.That(code, Does.Contain("[global::Microsoft.AspNetCore.Mvc.FromQuery(Name = \"kind\")] global::Fixture.MatchKind kind"));
            Assert.That(code, Does.Contain("[global::Microsoft.AspNetCore.Mvc.FromQuery(Name = \"ids\")] string[] ids"));
            Assert.That(code, Does.Contain("HaloGatewayCacheAttribute((global::Surprenant.Grunt.Attributes.GatewayCachePolicy)1, 30)"));
            Assert.That(code, Does.Contain("GetUserById([global::Microsoft.AspNetCore.Mvc.FromRoute(Name = \"id\")] global::System.Guid id)"));
            Assert.That(code, Does.Contain("[global::Microsoft.AspNetCore.Mvc.HttpPost(\"folders/{folder}/things\")]"));
            Assert.That(code, Does.Contain("[global::Microsoft.AspNetCore.Mvc.FromBody] global::Fixture.Payload payload"));
            Assert.That(code, Does.Contain("[global::Microsoft.AspNetCore.Mvc.HttpDelete(\"things/{id}\")]"));
            Assert.That(code, Does.Contain("HaloGatewayResults.Binary(__response, HttpContext)"));
            Assert.That(code, Does.Not.Contain("NotExposed"));
            Assert.That(code, Does.Not.Contain("GetFile"));
        });
    }

    [Test]
    public void ClientCallsRoutesAndThrowsForTheRest()
    {
        var run = Generate("Fixture.Client", FixtureValid,
            "[assembly: Surprenant.Grunt.Attributes.GenerateGatewayClient(typeof(Fixture.FixtureClient), RoutePrefix = \"fixture/v2\", Namespace = \"Fixture.Client\")]");

        Assert.That(run.GeneratorDiagnostics, Is.Empty);
        Assert.That(Errors(run.Output), Is.Empty);
        var code = run.Sources["HaloGatewayClient.generated.cs"];
        Assert.Multiple(() =>
        {
            Assert.That(code, Does.Contain("public partial class HaloGatewayClient : global::Fixture.IFixtureClient"));
            Assert.That(code, Does.Contain("var __path = \"fixture/v2/users/\" + global::Surprenant.Grunt.Gateway.Client.HaloGatewayUri.Segment(gamertag);"));
            Assert.That(code, Does.Contain("__query.Add(\"start\", start);"));
            Assert.That(code, Does.Contain("__query.AddAll(\"ids\", ids);"));
            Assert.That(code, Does.Contain("_transport.SendAsync<global::Surprenant.Grunt.Models.User>(global::System.Net.Http.HttpMethod.Post, __path + __query.ToString(), payload, \"CreateThing\")"));
            Assert.That(code, Does.Contain("global::System.Net.Http.HttpMethod.Delete"));
            Assert.That(code, Does.Contain("_transport.SendBinaryAsync(global::System.Net.Http.HttpMethod.Get"));
            Assert.That(code, Does.Contain("IFixtureClient.NotExposed is not exposed by the Halo gateway"));
            Assert.That(code, Does.Contain("GetFile<T>(string path)"));
        });
    }

    private static Run GenerateInvalid(string method)
    {
        var source = FixtureCommon + $$"""

            public interface IBadClient { }

            public class BadClient : IBadClient
            {
                {{method}}
            }
            """;
        return Generate("Fixture.Bad", source,
            "[assembly: Surprenant.Grunt.Attributes.GenerateGatewayController(typeof(Fixture.BadClient), Namespace = \"Fixture.Bad\")]");
    }

    [TestCase("[GatewayExposed(\"files\")] public Task<HaloApiResultContainer<T, HaloApiErrorContainer>> GetFile<T>(string path) => throw null!;", "GRUNTGW001")]
    [TestCase("[GatewayExposed(\"things\")] public Task<HaloApiResultContainer<User, HaloApiErrorContainer>> Create(Payload payload) => throw null!;", "GRUNTGW002")]
    [TestCase("[GatewayExposed(\"things\", HttpMethod = \"POST\")] public Task<HaloApiResultContainer<User, HaloApiErrorContainer>> Create(Payload a, Payload b) => throw null!;", "GRUNTGW002")]
    [TestCase("[GatewayExposed(\"a/{id}\")] public Task<HaloApiResultContainer<User, HaloApiErrorContainer>> Get(string id) => throw null!; [GatewayExposed(\"b/{id:guid}\")] public Task<HaloApiResultContainer<User, HaloApiErrorContainer>> Get(Guid id) => throw null!;", "GRUNTGW003")]
    [TestCase("[GatewayExposed(\"things/{missing}\")] public Task<HaloApiResultContainer<User, HaloApiErrorContainer>> Get(string id) => throw null!;", "GRUNTGW004")]
    [TestCase("[GatewayExposed(\"things/{ids}\")] public Task<HaloApiResultContainer<User, HaloApiErrorContainer>> Get(List<string> ids) => throw null!;", "GRUNTGW004")]
    [TestCase("[GatewayExposed(\"things\")] public Task<User> Get(string id) => throw null!;", "GRUNTGW005")]
    [TestCase("[GatewayExposed(\"things\", HttpMethod = \"TRACE\")] public Task<HaloApiResultContainer<User, HaloApiErrorContainer>> Get(string id) => throw null!;", "GRUNTGW007")]
    public void InvalidMethodsReportDiagnostics(string method, string id)
    {
        var run = GenerateInvalid(method);

        Assert.That(run.GeneratorDiagnostics.Select(d => d.Id), Does.Contain(id));
        Assert.That(run.GeneratorDiagnostics.Where(d => d.Id == id).Select(d => d.Severity), Has.All.EqualTo(DiagnosticSeverity.Error));
        Assert.That(run.GeneratorDiagnostics.First(d => d.Id == id).Location.IsInSource, Is.True, "diagnostic should point at the method");
    }

    [Test]
    public void MissingInterfaceIsReported()
    {
        var source = FixtureCommon + """

            public class LonelyClient
            {
                [GatewayExposed("x")] public Task<HaloApiResultContainer<User, HaloApiErrorContainer>> Get(string id) => throw null!;
            }
            """;
        var run = Generate("Fixture.Lonely", source,
            "[assembly: Surprenant.Grunt.Attributes.GenerateGatewayClient(typeof(Fixture.LonelyClient))]");

        Assert.That(run.GeneratorDiagnostics.Select(d => d.Id), Is.EqualTo(new[] { "GRUNTGW006" }));
        Assert.That(run.Sources, Is.Empty);
    }

    [Test]
    public void NoMarkerAttributeGeneratesNothing()
    {
        var run = Generate("Fixture.None", FixtureValid);

        Assert.That(run.Sources, Is.Empty);
        Assert.That(run.GeneratorDiagnostics, Is.Empty);
    }

    [TestCase("stats/{a}/x/{b:guid}/{c?}/{*d}", new[] { "a", "b", "c", "d" })]
    [TestCase("plain/route", new string[0])]
    public void RouteParametersAreParsed(string route, string[] expected)
    {
        Assert.That(GatewayModelBuilder.TryParseRouteParameters(route, out var names, out _), Is.True);
        Assert.That(names, Is.EquivalentTo(expected));
    }

    [TestCase("bad/{x")]
    [TestCase("bad/x}")]
    [TestCase("bad/{}")]
    public void MalformedRoutesAreRejected(string route)
    {
        Assert.That(GatewayModelBuilder.TryParseRouteParameters(route, out _, out var error), Is.False);
        Assert.That(error, Is.Not.Empty);
    }
}
