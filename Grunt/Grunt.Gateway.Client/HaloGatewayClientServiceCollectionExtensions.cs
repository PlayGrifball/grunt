using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Surprenant.Grunt.Core;

namespace Surprenant.Grunt.Gateway.Client;

public static class HaloGatewayClientServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="HaloGatewayClient"/> as a typed HttpClient and as <see cref="IHaloInfiniteClient"/>, configured from
    /// <paramref name="section"/> (BaseAddress, ApiKey, ApiKeyHeader, Retry).
    /// </summary>
    public static IHttpClientBuilder AddHaloGatewayClient(this IServiceCollection services, IConfiguration section)
    {
        services.AddOptions<HaloGatewayClientOptions>().Bind(section);
        return AddCore(services);
    }

    /// <summary>Registers <see cref="HaloGatewayClient"/> configured in code.</summary>
    public static IHttpClientBuilder AddHaloGatewayClient(this IServiceCollection services, Action<HaloGatewayClientOptions> configure)
    {
        services.AddOptions<HaloGatewayClientOptions>().Configure(configure);
        return AddCore(services);
    }

    private static IHttpClientBuilder AddCore(IServiceCollection services)
    {
        var builder = services.AddHttpClient<HaloGatewayClient>();
        services.AddTransient<IHaloInfiniteClient>(sp => sp.GetRequiredService<HaloGatewayClient>());
        return builder;
    }
}
