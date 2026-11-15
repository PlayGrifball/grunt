using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Surprenant.Grunt.Util;

namespace Surprenant.Grunt.Gateway;

/// <summary>
/// Answers 400 at once to an action whose <c>{gamertag}</c> route value can't be a gamertag (<see cref="Gamertag.IsPossible"/>),
/// before the cache, the factory and Halo: Halo would answer 400 anyway, and every call counts against its rate limit. A gateway
/// error, so without <see cref="HaloGatewayProtocol.UpstreamStatusHeader"/>: the client returns it as code 400 and doesn't retry it.
/// </summary>
public sealed class HaloGatewayGamertagFilter : IResourceFilter
{
    /// <summary>The route value checked.</summary>
    public const string RouteValue = "gamertag";

    /// <summary>Runs before <see cref="HaloGatewayCacheFilter"/> (order 0).</summary>
    public const int Order = -100;

    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        if (context.RouteData.Values.TryGetValue(RouteValue, out var value) && !Gamertag.IsPossible(value as string))
        {
            context.Result = new ObjectResult(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Not a possible gamertag",
                Detail = "Gamertags can't contain symbols such as ( ) : _ . and are at most 15 characters, or 12 and a #suffix; Halo was not asked.",
            })
            { StatusCode = StatusCodes.Status400BadRequest };
        }
    }

    public void OnResourceExecuted(ResourceExecutedContext context)
    {
        // Nothing after the action
    }
}
