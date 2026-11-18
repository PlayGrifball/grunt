using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Surprenant.Grunt.Core;
using Surprenant.Grunt.Gateway.Host.HaloAccount;
using Surprenant.Grunt.Models;
using Surprenant.Grunt.Models.HaloInfinite;
using Surprenant.Grunt.Models.HaloInfinite.Medals;
using System.Diagnostics;
using File = System.IO.File;
using MatchType = Surprenant.Grunt.Models.HaloInfinite.MatchType;

namespace Tests;

/// <summary>
/// The published TypeScript client (Grunt.Gateway.TypeScript/dist) against the real host on a Kestrel port, with a fake
/// IHaloInfiniteClient: Grunt.Gateway.TypeScript/e2e checks what the client returns, this checks what Halo was asked.
/// Runs when GRUNT_TYPESCRIPT_E2E=1 (the Helm chart workflow sets it after building the package), otherwise ignored.
/// </summary>
[Category("TypeScriptClient")]
public class TypeScriptClientEndToEndTests
{
    private const string ApiKey = "typescript-e2e-key";
    private const string MatchId = "2f5c4b0e-6a77-4a3c-9d0b-0d6a3c1f5e11";
    private const string PlaylistId = "edfef3ac-9cbe-4fa2-b949-8f29deafd483";

    private sealed class GatewayFactory : WebApplicationFactory<Program>
    {
        public IHaloInfiniteClient Halo { get; } = Substitute.For<IHaloInfiniteClient>();
        public string TokenDirectory { get; } = Path.Combine(Path.GetTempPath(), "grunt-gateway-tests", Guid.NewGuid().ToString("N"));

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Gateway:AdminKey", "admin-key");
            builder.UseSetting("Gateway:ApiKeys:0:Name", "typescript-e2e");
            builder.UseSetting("Gateway:ApiKeys:0:Key", ApiKey);
            builder.UseSetting("Gateway:ApiKeys:0:Environment", "prod");
            builder.UseSetting("Gateway:Auth:UseInstanceLock", "false");
            builder.UseSetting("Gateway:Auth:InitialDelay", "01:00:00");
            builder.UseSetting("Gateway:Auth:TokenPath", Path.Combine(TokenDirectory, "tokens.json"));
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton(Halo);
                services.AddSingleton(Substitute.For<IHaloOAuthClient>());
                services.AddSingleton(Substitute.For<IHaloSession>());
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (Directory.Exists(TokenDirectory))
                Directory.Delete(TokenDirectory, recursive: true);
        }
    }

    private static Task<HaloApiResultContainer<T, HaloApiErrorContainer>> Container<T>(T result, int code, string? message = null)
        => Task.FromResult(new HaloApiResultContainer<T, HaloApiErrorContainer>(result, new HaloApiErrorContainer { Code = code, Message = message }));

    private static User User(string xuid, string gamertag) => new() { xuid = xuid, gamertag = gamertag, gamerpic = new Gamerpic { small = "s.png" } };

    private static bool Same(IEnumerable<string> actual, params string[] expected) => actual.SequenceEqual(expected);

    private static string PackageDirectory()
    {
        for (var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); dir is not null; dir = dir.Parent)
        {
            var package = Path.Combine(dir.FullName, "Grunt.Gateway.TypeScript");
            if (File.Exists(Path.Combine(package, "package.json")))
                return package;
        }

        throw new DirectoryNotFoundException("Grunt.Gateway.TypeScript not found above " + TestContext.CurrentContext.TestDirectory);
    }

    [Test]
    public async Task GeneratedTypeScriptClientCallsEveryOperationThroughTheHost()
    {
        if (Environment.GetEnvironmentVariable("GRUNT_TYPESCRIPT_E2E") != "1")
            Assert.Ignore("Set GRUNT_TYPESCRIPT_E2E=1 after `npm run build` in Grunt.Gateway.TypeScript to run the TypeScript client end to end.");

        var package = PackageDirectory();
        Assert.That(File.Exists(Path.Combine(package, "dist", "index.js")), Is.True, "Build the TypeScript client first (npm run build).");

        using var factory = new GatewayFactory();
        var halo = factory.Halo;
        var image = new byte[] { 137, 80, 78, 71 };
        halo.UserByGamertag("Major Nelson#881").Returns(_ => Container(User("2533274800000000", "Major Nelson#881"), 200));
        halo.UserByGamertag("nobody").Returns(_ => Container<User>(null!, 404, "not found"));
        halo.UserByGamertag("Busy").Returns(_ => Task.FromResult(new HaloApiResultContainer<User, HaloApiErrorContainer>(null!,
            new HaloApiErrorContainer { Code = 429, NotSent = true, RetryAfter = TimeSpan.FromSeconds(41.2) })));
        halo.Users(Arg.Any<IEnumerable<string>>()).Returns(_ => Container(new List<User> { User("1", "One"), User("2", "Two") }, 200));
        halo.StatsGetMatchHistory(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<MatchType>())
            .Returns(_ => Container(new MatchHistoryResponse(), 200));
        halo.StatsGetMatchCount(Arg.Any<string>()).Returns(_ => Container(new PlayerMatchCount { MatchesPlayedCount = 42 }, 200));
        halo.StatsGetMatchStats(Arg.Any<Guid>()).Returns(_ => Container(new MatchStats(), 200));
        halo.StatsGetPlayerMatchProgression(Arg.Any<string>(), Arg.Any<string>()).Returns(_ => Container(new MatchProgression(), 200));
        halo.SkillGetMatchPlayerResult(Arg.Any<string>(), Arg.Any<List<string>>()).Returns(_ => Container(new PlayerSkillResultContainer(), 200));
        halo.SkillGetPlaylistCsr(Arg.Any<string>(), Arg.Any<List<string>>()).Returns(_ => Container(new PlaylistCsrResultContainer(), 200));
        halo.Medals().Returns(_ => Container(new MedalMetadataResponse(), 200));
        halo.GameCmsGetImage(Arg.Any<string>()).Returns(_ => Container(image, 200));

        factory.UseKestrel(0);
        factory.StartServer();
        var address = factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();

        var (exitCode, output) = await RunNodeAsync(package, address);

        Assert.That(exitCode, Is.Zero, output);
        await halo.Received(1).UserByGamertag("Major Nelson#881");
        await halo.Received(1).Users(Arg.Is<IEnumerable<string>>(x => Same(x, "1", "2")));
        await halo.Received(1).StatsGetMatchHistory("xuid(123)", 25, 10, MatchType.Custom);
        await halo.Received(1).StatsGetMatchCount("xuid(123)");
        await halo.Received(1).StatsGetMatchStats(Guid.Parse(MatchId));
        await halo.Received(1).StatsGetPlayerMatchProgression("xuid(123)", MatchId);
        await halo.Received(1).SkillGetMatchPlayerResult(MatchId, Arg.Is<List<string>>(x => Same(x, "xuid(1)", "xuid(2)")));
        await halo.Received(1).SkillGetPlaylistCsr(PlaylistId, Arg.Is<List<string>>(x => Same(x, "xuid(1)", "xuid(2)")));
        await halo.Received(1).Medals();
        await halo.Received(1).GameCmsGetImage("progression/a b.png");
    }

    private static async Task<(int ExitCode, string Output)> RunNodeAsync(string package, string address)
    {
        var start = new ProcessStartInfo("node")
        {
            WorkingDirectory = package,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("--test");
        start.ArgumentList.Add("--test-reporter=spec");
        start.ArgumentList.Add(Path.Combine("e2e", "gateway.e2e.test.mjs"));
        start.Environment["GATEWAY_URL"] = address;
        start.Environment["GATEWAY_API_KEY"] = ApiKey;

        using var node = Process.Start(start)!;
        var stdout = node.StandardOutput.ReadToEndAsync();
        var stderr = node.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            await node.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            node.Kill(entireProcessTree: true);
            throw new TimeoutException("node --test did not finish in 2 minutes:\n" + await stdout + await stderr);
        }

        return (node.ExitCode, await stdout + await stderr);
    }
}
