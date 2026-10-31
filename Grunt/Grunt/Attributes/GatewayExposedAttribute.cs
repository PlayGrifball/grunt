using System;

namespace Surprenant.Grunt.Attributes;

/// <summary>
/// How long the gateway may cache a successful response of a <see cref="GatewayExposedAttribute"/> method.
/// </summary>
public enum GatewayCachePolicy
{
    /// <summary>Never cached.</summary>
    None = 0,

    /// <summary>Cached for <see cref="GatewayExposedAttribute.CacheSeconds"/> seconds (default 60).</summary>
    ShortTtl = 1,

    /// <summary>The response never changes for the same request (e.g. a finished match); cached for the gateway's immutable TTL.</summary>
    Immutable = 2,
}

/// <summary>
/// Opts a <see cref="Core.HaloInfiniteClient"/> method into the Halo gateway. The gateway source generator emits a controller
/// action for it (projects with <see cref="GenerateGatewayControllerAttribute"/>) and an HTTP implementation in the gateway
/// client (projects with <see cref="GenerateGatewayClientAttribute"/>).
/// </summary>
/// <remarks>
/// Parameters named in <see cref="Route"/> bind from the route; other simple parameters (strings, numbers, bools, enums, Guids,
/// and collections of them) bind from the query string; a single complex parameter is sent as the JSON body, which needs a
/// non-GET <see cref="HttpMethod"/>. Generic methods cannot be exposed (diagnostic GRUNTGW001). Only expose read-only methods:
/// never auth/token, write or UGC-publishing ones.
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class GatewayExposedAttribute : Attribute
{
    /// <summary>
    /// Exposes the method at <paramref name="route"/>, relative to the gateway's route prefix (e.g. <c>stats/matches/{matchId}</c>).
    /// </summary>
    public GatewayExposedAttribute(string route)
    {
        Route = route;
    }

    /// <summary>Route template relative to the gateway prefix. Placeholders must match parameter names.</summary>
    public string Route { get; }

    /// <summary>HTTP method of the action: GET (default), POST, PUT, PATCH or DELETE.</summary>
    public string HttpMethod { get; set; } = "GET";

    /// <summary>Caching of successful responses at the gateway.</summary>
    public GatewayCachePolicy Cache { get; set; } = GatewayCachePolicy.None;

    /// <summary>TTL in seconds for <see cref="GatewayCachePolicy.ShortTtl"/>. 0 means 60.</summary>
    public int CacheSeconds { get; set; }

    /// <summary>
    /// Controller action name. Defaults to the method name; required to tell overloads apart.
    /// </summary>
    public string? Name { get; set; }
}

/// <summary>
/// Assembly-level marker for an ASP.NET Core project: the gateway source generator emits
/// <c>partial class HaloInfiniteController : ControllerBase</c> with one action per <see cref="GatewayExposedAttribute"/>
/// method of <see cref="ClientType"/>, each calling the injected <c>IHaloInfiniteClient</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class GenerateGatewayControllerAttribute : Attribute
{
    /// <summary>Default route prefix shared by the controller and the client.</summary>
    public const string DefaultRoutePrefix = "halo/v1";

    /// <summary>Generates a controller for the exposed methods of <paramref name="clientType"/>.</summary>
    public GenerateGatewayControllerAttribute(Type clientType)
    {
        ClientType = clientType;
    }

    /// <summary>The client class whose methods carry <see cref="GatewayExposedAttribute"/>.</summary>
    public Type ClientType { get; }

    /// <summary>Route prefix of the controller.</summary>
    public string RoutePrefix { get; set; } = DefaultRoutePrefix;

    /// <summary>Namespace of the generated controller. Defaults to the assembly name.</summary>
    public string? Namespace { get; set; }
}

/// <summary>
/// Assembly-level marker for the gateway client project: the gateway source generator emits
/// <c>partial class HaloGatewayClient : IHaloInfiniteClient</c> calling the gateway routes; methods without
/// <see cref="GatewayExposedAttribute"/> throw <see cref="NotSupportedException"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class GenerateGatewayClientAttribute : Attribute
{
    /// <summary>Generates a gateway client implementing the interface of <paramref name="clientType"/>.</summary>
    public GenerateGatewayClientAttribute(Type clientType)
    {
        ClientType = clientType;
    }

    /// <summary>The client class whose methods carry <see cref="GatewayExposedAttribute"/>.</summary>
    public Type ClientType { get; }

    /// <summary>Route prefix of the gateway; must match the controller's.</summary>
    public string RoutePrefix { get; set; } = GenerateGatewayControllerAttribute.DefaultRoutePrefix;

    /// <summary>Namespace of the generated client. Defaults to the assembly name.</summary>
    public string? Namespace { get; set; }
}
