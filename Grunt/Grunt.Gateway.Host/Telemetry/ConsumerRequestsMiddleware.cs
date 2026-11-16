using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.Options;
using Surprenant.Grunt.Gateway.Host.Auth;
using System.Diagnostics;
using System.Security.Claims;

namespace Surprenant.Grunt.Gateway.Host.Telemetry;

/// <summary>
/// Counts authenticated requests for <see cref="HaloInfiniteController"/> actions per consumer key and tags their trace. Nothing
/// is limited per key: callers are served first come, first served, within the account's budgets with Halo
/// (<see cref="Core.HaloBudgets"/>), which only count calls that reach Halo (a cache hit takes nothing).
/// </summary>
public sealed class ConsumerRequestsMiddleware
{
    private readonly RequestDelegate _next;

    public ConsumerRequestsMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, IOptionsMonitor<GatewayOptions> options)
    {
        var action = context.GetEndpoint()?.Metadata.GetMetadata<ControllerActionDescriptor>();
        if (action?.ControllerTypeInfo.AsType() == typeof(HaloInfiniteController) && context.User.Identity?.IsAuthenticated == true &&
            context.User.IsInRole(GatewayAuth.ConsumerRole))
        {
            var name = context.User.FindFirstValue(ClaimTypes.Name) ?? "unknown";
            var key = options.CurrentValue.ApiKeys.FirstOrDefault(k => k.Name == name) ?? new ApiKeyOptions { Name = name };
            Activity.Current?.SetTag("grunt.api_key", key.Name);
            Activity.Current?.SetTag("grunt.environment", key.Environment);
            GatewayHostMetrics.Requests.Add(1, new("key", key.Name), new("environment", key.Environment), new("action", action.ActionName));
        }
        await _next(context);
    }
}
