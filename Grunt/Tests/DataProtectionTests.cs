using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Surprenant.Grunt.Gateway.Host;
using Surprenant.Grunt.Gateway.Host.HaloAccount;

namespace Tests;

/// <summary>
/// <see cref="GatewayHost.ResolveDataProtectionKeysPath"/> and the Data Protection DI wiring in
/// <see cref="GatewayHost.ConfigureServices"/>. In production, the chiseled image's read-only root filesystem makes the
/// framework default (~/.aspnet/DataProtection-Keys under HOME=/home/app) fail with EROFS, so the key ring must be
/// persisted to the same persistent volume as the OAuth token instead (<c>Gateway:Auth:TokenPath</c>'s directory, unless
/// <c>Gateway:DataProtection:KeysPath</c> overrides it).
/// </summary>
public class DataProtectionTests
{
    private static IConfiguration GatewaySection(params (string Key, string? Value)[] entries)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(entries.Select(e => new KeyValuePair<string, string?>(e.Key, e.Value)))
            .Build()
            .GetSection("Gateway");

    [Test]
    public void DefaultsToTheTokenPathsDirectory()
    {
        var gateway = GatewaySection(("Gateway:Auth:TokenPath", "/data/tokens.json"));

        var path = GatewayHost.ResolveDataProtectionKeysPath(gateway);

        Assert.That(path, Is.EqualTo(Path.Combine("/data", "data-protection-keys")));
    }

    [Test]
    public void FallsBackToHaloAuthOptionsDefaultTokenPathWhenUnconfigured()
    {
        var gateway = GatewaySection();

        var path = GatewayHost.ResolveDataProtectionKeysPath(gateway);

        var expectedDirectory = Path.GetDirectoryName(Path.GetFullPath("tokens.json"))!;
        Assert.That(path, Is.EqualTo(Path.Combine(expectedDirectory, "data-protection-keys")));
    }

    [Test]
    public void ExplicitKeysPathOverridesTheTokenPathDerivedDefault()
    {
        var gateway = GatewaySection(
            ("Gateway:Auth:TokenPath", "/data/tokens.json"),
            ("Gateway:DataProtection:KeysPath", "/data/keys-elsewhere"));

        var path = GatewayHost.ResolveDataProtectionKeysPath(gateway);

        Assert.That(path, Is.EqualTo("/data/keys-elsewhere"));
    }

    /// <summary>End to end: the real host persists the key ring under the resolved path, not the framework default.</summary>
    private sealed class Factory : WebApplicationFactory<Program>
    {
        public IHaloOAuthClient OAuth { get; } = Substitute.For<IHaloOAuthClient>();
        public IHaloSession Session { get; } = Substitute.For<IHaloSession>();
        public string TokenDirectory { get; } = Path.Combine(Path.GetTempPath(), "grunt-data-protection-tests", Guid.NewGuid().ToString("N"));

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Gateway:Auth:UseInstanceLock", "false");
            builder.UseSetting("Gateway:Auth:InitialDelay", "01:00:00");
            builder.UseSetting("Gateway:Auth:TokenPath", Path.Combine(TokenDirectory, "tokens.json"));
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton(OAuth);
                services.AddSingleton(Session);
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (Directory.Exists(TokenDirectory))
                Directory.Delete(TokenDirectory, recursive: true);
        }
    }

    [Test]
    public async Task KeyRingIsPersistedUnderTheTokenDirectoryNotTheFrameworkDefault()
    {
        using var factory = new Factory();
        using var client = factory.CreateClient();
        // Touch the host so its DI container is built.
        await client.GetAsync("/health/live");

        var provider = factory.Services.GetRequiredService<IDataProtectionProvider>();
        var protector = provider.CreateProtector("Tests.DataProtectionTests");
        var roundTripped = protector.Unprotect(protector.Protect("round-trip"));

        var keysDirectory = Path.Combine(factory.TokenDirectory, "data-protection-keys");
        Assert.That(roundTripped, Is.EqualTo("round-trip"));
        Assert.That(Directory.Exists(keysDirectory), Is.True, "the resolved keys directory should have been created");
        Assert.That(Directory.EnumerateFiles(keysDirectory, "key-*.xml"), Is.Not.Empty, "Data Protection should have written a key under it");
    }
}
