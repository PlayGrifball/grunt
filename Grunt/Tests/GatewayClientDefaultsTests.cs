using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Surprenant.Grunt.Gateway.Client;
using System.Net;

namespace Tests;

/// <summary>What the .NET gateway client does when the consumer configures nothing but an API key.</summary>
public class GatewayClientDefaultsTests
{
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<Uri?> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    [Test]
    public void Options_default_to_the_hosted_gateway()
    {
        Assert.That(new HaloGatewayClientOptions().BaseAddress, Is.EqualTo(new Uri("https://grunt.grifball.xyz/")));
    }

    [Test]
    public async Task Client_registered_without_a_base_address_calls_the_hosted_gateway()
    {
        var handler = new RecordingHandler();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["HaloGateway:ApiKey"] = "key" })
            .Build();
        var services = new ServiceCollection();
        services.AddHaloGatewayClient(configuration.GetSection("HaloGateway"))
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        await using var provider = services.BuildServiceProvider();

        await provider.GetRequiredService<HaloGatewayClient>().Medals();

        Assert.That(handler.Requests, Is.EqualTo(new[] { new Uri("https://grunt.grifball.xyz/halo/v1/medals") }));
    }

    [Test]
    public async Task Configured_base_address_replaces_the_default()
    {
        var handler = new RecordingHandler();
        var services = new ServiceCollection();
        services.AddHaloGatewayClient(o => o.BaseAddress = new Uri("http://halo-gateway.halo-gateway.svc:8080/"))
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        await using var provider = services.BuildServiceProvider();

        await provider.GetRequiredService<HaloGatewayClient>().Medals();

        Assert.That(handler.Requests, Is.EqualTo(new[] { new Uri("http://halo-gateway.halo-gateway.svc:8080/halo/v1/medals") }));
    }
}
