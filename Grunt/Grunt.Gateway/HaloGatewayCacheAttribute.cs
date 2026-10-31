using Surprenant.Grunt.Attributes;

namespace Surprenant.Grunt.Gateway;

/// <summary>
/// Emitted by the gateway generator on actions whose [GatewayExposed] method has a cache policy; read by
/// <see cref="HaloGatewayCacheFilter"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class HaloGatewayCacheAttribute : Attribute
{
    public HaloGatewayCacheAttribute(GatewayCachePolicy policy, int seconds)
    {
        Policy = policy;
        Seconds = seconds;
    }

    public GatewayCachePolicy Policy { get; }

    /// <summary>TTL for <see cref="GatewayCachePolicy.ShortTtl"/>; 0 means 60.</summary>
    public int Seconds { get; }
}
