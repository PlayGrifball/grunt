using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.Options;
using Surprenant.Grunt.Gateway.Host.Auth;
using Surprenant.Grunt.Gateway.Host.Telemetry;
using System.Diagnostics;
using System.Globalization;
using System.Security.Claims;

namespace Surprenant.Grunt.Gateway.Host.Quotas;

/// <summary>
/// Applies <see cref="QuotaGate"/> to authenticated requests for <see cref="HaloInfiniteController"/> actions and counts requests
/// per consumer key. Rejections are 429 with Retry-After and no <c>X-Halo-Upstream-Status</c>, so gateway clients back off and retry.
/// </summary>
public sealed class QuotaMiddleware
{
    private readonly RequestDelegate _next;

    public QuotaMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, QuotaGate gate, IOptionsMonitor<GatewayOptions> options)
    {
        var action = context.GetEndpoint()?.Metadata.GetMetadata<ControllerActionDescriptor>();
        if (action?.ControllerTypeInfo.AsType() != typeof(HaloInfiniteController) || context.User.Identity?.IsAuthenticated != true ||
            !context.User.IsInRole(GatewayAuth.ConsumerRole))
        {
            await _next(context);
            return;
        }

        var name = context.User.FindFirstValue(ClaimTypes.Name) ?? "unknown";
        var key = options.CurrentValue.ApiKeys.FirstOrDefault(k => k.Name == name) ?? new ApiKeyOptions { Name = name };
        Activity.Current?.SetTag("grunt.api_key", key.Name);
        Activity.Current?.SetTag("grunt.environment", key.Environment);
        GatewayHostMetrics.Requests.Add(1, new("key", key.Name), new("environment", key.Environment), new("action", action.ActionName));

        var decision = gate.TryAcquire(key);
        if (!decision.Allowed)
        {
            GatewayHostMetrics.QuotaRejections.Add(1, new("key", key.Name), new("environment", key.Environment), new("reason", decision.Reason));
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            if (decision.RetryAfter is { } retryAfter)
                context.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            await context.Response.WriteAsJsonAsync(new { error = "quota_exceeded", scope = decision.Reason, key = key.Name });
            return;
        }

        await _next(context);
    }
}
