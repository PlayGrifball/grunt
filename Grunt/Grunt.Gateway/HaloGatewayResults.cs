using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Surprenant.Grunt.Models;
using System.Diagnostics;
using System.Globalization;

namespace Surprenant.Grunt.Gateway;

/// <summary>
/// Turns a <see cref="HaloApiResultContainer{T, THaloApiErrorContainer}"/> into the gateway's HTTP response (used by the generated
/// controller). The body is always the serialised container (or raw bytes for successful binary results), the HTTP status comes from
/// <see cref="HaloGatewayProtocol.MapStatus"/>, and <see cref="HaloGatewayProtocol.UpstreamStatusHeader"/> carries the Halo status.
/// </summary>
public static class HaloGatewayResults
{
    /// <summary>JSON container response.</summary>
    public static IActionResult Json<T>(HaloApiResultContainer<T, HaloApiErrorContainer>? response, HttpContext context)
    {
        response ??= new HaloApiResultContainer<T, HaloApiErrorContainer>(default!, new HaloApiErrorContainer { Code = 0, Message = "Halo returned no response." });
        response.Error ??= new HaloApiErrorContainer { Code = 0 };
        var status = Annotate(response.Error, context);
        return new JsonResult(response, HaloGatewayProtocol.JsonOptions) { StatusCode = status };
    }

    /// <summary>Raw bytes on success (no base64 inflation, written straight to the response body); a JSON container otherwise.</summary>
    public static IActionResult Binary(HaloApiResultContainer<byte[], HaloApiErrorContainer>? response, HttpContext context)
    {
        if (response?.Error is { Code: >= 200 and < 300 } && response.Result is { } bytes)
        {
            Annotate(response.Error, context);
            return new FileContentResult(bytes, HaloGatewayProtocol.BinaryContentType);
        }
        return Json(response, context);
    }

    /// <summary>
    /// The factory threw (network errors after all retries, or auth that needs an admin to sign in again): 502 with a container whose
    /// code is 502, so the client sees an upstream failure rather than a transport one and does not retry it.
    /// </summary>
    public static IActionResult Failure<T>(Exception exception, HttpContext context, ILogger logger, string action)
    {
        logger.LogError(exception, "Gateway action {Action} failed calling Halo", action);
        HaloGatewayMetrics.UpstreamFailures.Add(1, new KeyValuePair<string, object?>("action", action),
            new KeyValuePair<string, object?>("exception", exception.GetType().Name));
        var error = new HaloApiErrorContainer
        {
            Code = StatusCodes.Status502BadGateway,
            Message = $"The gateway could not get a response from Halo ({exception.GetType().Name}). See the gateway logs.",
        };
        context.Response.Headers[HaloGatewayProtocol.UpstreamStatusHeader] = "0";
        Activity.Current?.SetTag("halo.upstream.status", 0);
        return new JsonResult(new HaloApiResultContainer<T, HaloApiErrorContainer>(default!, error), HaloGatewayProtocol.JsonOptions)
        {
            StatusCode = StatusCodes.Status502BadGateway,
        };
    }

    private static int Annotate(HaloApiErrorContainer error, HttpContext context)
    {
        var upstream = error.Code;
        var status = HaloGatewayProtocol.MapStatus(upstream);
        context.Response.Headers[HaloGatewayProtocol.UpstreamStatusHeader] = upstream.ToString(CultureInfo.InvariantCulture);
        if (status == StatusCodes.Status503ServiceUnavailable && error.RetryAfter is { } retryAfter)
        {
            context.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        }
        var action = context.GetRouteValue("action")?.ToString() ?? "unknown";
        HaloGatewayMetrics.UpstreamResponses.Add(1, new KeyValuePair<string, object?>("action", action),
            new KeyValuePair<string, object?>("status", upstream));
        Activity.Current?.SetTag("halo.upstream.status", upstream);
        return status;
    }
}
