using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Surprenant.Grunt.Gateway.Host.Telemetry;
using System.Net;
// ForwardedHeadersOptions.KnownIPNetworks is IList<System.Net.IPNetwork>, which otherwise collides with
// Microsoft.AspNetCore.HttpOverrides.IPNetwork.
using IPNetwork = System.Net.IPNetwork;

namespace Tests;

/// <summary>
/// <see cref="AppForwardedHeaders"/>: only requests whose immediate peer is a configured network/proxy may set
/// the client address via X-Forwarded-For/X-Real-IP; an untrusted peer's own address is kept instead. Replaces
/// the previous always-clear-the-trust-list setup, which trusted every peer unconditionally.
/// </summary>
public class ForwardedHeadersTests
{
    [Test]
    public void ParseSkipsInvalidEntriesAndKeepsValidOnes()
    {
        var settings = new ForwardedHeadersSettings
        {
            KnownIPNetworks = ["10.42.0.0/16", "not-a-cidr"],
            KnownProxies = ["10.0.0.5", "not-an-ip"],
        };

        var (networks, proxies, invalid) = AppForwardedHeaders.Parse(settings);

        Assert.That(networks.Select(n => n.ToString()), Is.EqualTo(new[] { "10.42.0.0/16" }));
        Assert.That(proxies.Select(p => p.ToString()), Is.EqualTo(new[] { "10.0.0.5" }));
        Assert.That(invalid, Is.EquivalentTo(new[] { "not-a-cidr", "not-an-ip" }));
    }

    [Test]
    public void ApplyWithNoEntriesLeavesTheFrameworkDefaultOfTrustingOnlyLoopback()
    {
        var options = new ForwardedHeadersOptions();
        var defaultNetworkCount = options.KnownIPNetworks.Count;
        var defaultProxyCount = options.KnownProxies.Count;

        AppForwardedHeaders.Apply(options, new ForwardedHeadersSettings(), [], []);

        Assert.That(options.KnownIPNetworks.Count, Is.EqualTo(defaultNetworkCount), "no configured networks: the default is left alone, not cleared");
        Assert.That(options.KnownProxies.Count, Is.EqualTo(defaultProxyCount));
        Assert.That(options.ForwardedHeaders, Is.EqualTo(ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost));
    }

    [Test]
    public void ApplyWithConfiguredEntriesReplacesTheDefaultTrustListAndSettings()
    {
        var options = new ForwardedHeadersOptions();
        var networks = new List<IPNetwork> { IPNetwork.Parse("10.42.0.0/16") };

        AppForwardedHeaders.Apply(options, new ForwardedHeadersSettings { ForwardedForHeaderName = "X-Real-IP", ForwardLimit = 2 }, networks, []);

        Assert.That(options.KnownIPNetworks.Select(n => n.ToString()), Is.EqualTo(new[] { "10.42.0.0/16" }));
        Assert.That(options.KnownProxies, Is.Empty);
        Assert.That(options.ForwardedForHeaderName, Is.EqualTo("X-Real-IP"));
        Assert.That(options.ForwardLimit, Is.EqualTo(2));
    }

    /// <summary>Full pipeline: UseForwardedHeaders() wired by ConfigureAppForwardedHeaders() from configuration.</summary>
    private static async Task<string?> RemoteIpAfterForwardedHeaders(string peer, string header, string headerValue, string[] knownIPNetworks)
    {
        using var host = await new HostBuilder()
            .ConfigureWebHost(webHost => webHost
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    var configuration = new ConfigurationBuilder()
                        .AddInMemoryCollection(knownIPNetworks.Select((n, i) =>
                            new KeyValuePair<string, string?>($"ForwardedHeaders:KnownIPNetworks:{i}", n)))
                        .Build();
                    services.ConfigureAppForwardedHeaders(configuration);
                })
                .Configure(app =>
                {
                    // TestServer has no real socket; simulate the immediate TCP peer before the forwarded-headers
                    // middleware decides whether to trust it.
                    app.Use((context, next) =>
                    {
                        context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
                        return next();
                    });
                    app.UseForwardedHeaders();
                    app.Run(context => context.Response.WriteAsync(context.Connection.RemoteIpAddress?.ToString() ?? "null"));
                }))
            .StartAsync();

        using var client = host.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.TryAddWithoutValidation(header, headerValue);
        using var response = await client.SendAsync(request);
        return await response.Content.ReadAsStringAsync();
    }

    [Test]
    public async Task UntrustedPeersForwardedForIsIgnored()
    {
        var result = await RemoteIpAfterForwardedHeaders("203.0.113.9", "X-Forwarded-For", "198.51.100.7", ["10.42.0.0/16"]);

        Assert.That(result, Is.EqualTo("203.0.113.9"), "the untrusted peer's own address is kept, not the header's");
    }

    [Test]
    public async Task TrustedProxysForwardedForIsApplied()
    {
        var result = await RemoteIpAfterForwardedHeaders("10.42.1.23", "X-Forwarded-For", "198.51.100.7", ["10.42.0.0/16"]);

        Assert.That(result, Is.EqualTo("198.51.100.7"), "the trusted proxy's forwarded address is applied");
    }
}
