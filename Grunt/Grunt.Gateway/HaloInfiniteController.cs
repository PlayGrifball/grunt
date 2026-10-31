using Microsoft.AspNetCore.Authorization;

namespace Surprenant.Grunt.Gateway;

/// <summary>
/// Hand-written half of the generated controller: every action requires the <see cref="ConsumerPolicy"/> authorization policy,
/// which the host defines (API key authentication in Surprenant.Grunt.Gateway.Host).
/// </summary>
[Authorize(Policy = ConsumerPolicy)]
public partial class HaloInfiniteController
{
    /// <summary>Authorization policy name the host must register for gateway consumers.</summary>
    public const string ConsumerPolicy = "HaloGatewayConsumer";
}
