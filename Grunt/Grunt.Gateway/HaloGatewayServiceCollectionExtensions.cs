using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Surprenant.Grunt.Core;

namespace Surprenant.Grunt.Gateway;

public static class HaloGatewayServiceCollectionExtensions
{
    /// <summary>
    /// Adds the generated <see cref="HaloInfiniteController"/> (from this assembly) and the response cache filter.
    /// The host must register <see cref="IHaloInfiniteClient"/> as <see cref="HaloInfiniteClientFactory"/> (singleton), so every
    /// action goes through the factory's auth retry and backoff.
    /// </summary>
    public static IMvcBuilder AddHaloGateway(this IServiceCollection services, IConfiguration? cacheSection = null)
    {
        services.AddMemoryCache();
        var options = services.AddOptions<HaloGatewayCacheOptions>();
        if (cacheSection is not null)
            options.Bind(cacheSection);
        services.AddSingleton<HaloGatewayCacheFilter>();
        return services.AddControllers(o => o.Filters.AddService<HaloGatewayCacheFilter>())
            .AddApplicationPart(typeof(HaloInfiniteController).Assembly);
    }
}
