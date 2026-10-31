using Serilog;
using Serilog.AspNetCore;
using Serilog.Context;
using Serilog.Events;
using System.Diagnostics;
using System.Security.Claims;

namespace Surprenant.Grunt.Gateway.Host.Telemetry;

/// <summary>
/// Per-request telemetry, ported from GrifballWebApp's <c>Telemetry/RequestTelemetryExtensions.cs</c>: a W3C
/// <c>traceresponse</c> header, the client address on every log event, and one summary log line per request.
/// Must run after <c>UseForwardedHeaders</c> so the address is the real client.
/// </summary>
public sealed class RequestTelemetryMiddleware
{
    public const string TraceResponseHeader = "traceresponse";
    public const string ClientIpProperty = "ClientIp";

    private readonly RequestDelegate _next;

    public RequestTelemetryMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // The request's server span. Its trace id is the one the collector has, and Serilog already stamps it
        // (and the span id) on every event as trace_id/span_id, so it is not pushed again as a property.
        if (FormatTraceResponse(Activity.Current) is { } traceResponse)
            context.Response.Headers[TraceResponseHeader] = traceResponse;

        using (LogContext.PushProperty(ClientIpProperty, context.Connection.RemoteIpAddress?.ToString()))
        {
            await _next(context);
        }
    }

    /// <summary><c>00-{trace-id}-{span-id}-{flags}</c>, per W3C Trace Context Level 2; null without an activity.</summary>
    public static string? FormatTraceResponse(Activity? activity)
    {
        if (activity is null || activity.IdFormat != ActivityIdFormat.W3C)
            return null;
        var flags = activity.Recorded ? "01" : "00";
        return $"00-{activity.TraceId.ToHexString()}-{activity.SpanId.ToHexString()}-{flags}";
    }
}

public static class RequestTelemetryExtensions
{
    /// <summary>The consumer API key's configured name (<see cref="ClaimTypes.Name"/>), never the key itself.</summary>
    public const string ConsumerKeyNameProperty = "ConsumerKeyName";

    /// <summary>The request middleware, then Serilog's one-line-per-request log.</summary>
    public static IApplicationBuilder UseRequestTelemetry(this IApplicationBuilder app)
    {
        app.UseMiddleware<RequestTelemetryMiddleware>();
        app.UseSerilogRequestLogging(ConfigureRequestLogging);
        return app;
    }

    public static void ConfigureRequestLogging(RequestLoggingOptions options)
    {
        options.GetLevel = GetLevel;
        options.EnrichDiagnosticContext = EnrichDiagnosticContext;
    }

    /// <summary>Errors for 5xx and exceptions, Verbose (dropped) for health/metrics probes, Information otherwise.</summary>
    public static LogEventLevel GetLevel(HttpContext context, double elapsedMilliseconds, Exception? exception)
    {
        if (exception is not null || context.Response.StatusCode >= StatusCodes.Status500InternalServerError)
            return LogEventLevel.Error;
        if (GatewayHost.IsInfrastructurePath(context.Request.Path))
            return LogEventLevel.Verbose;
        return LogEventLevel.Information;
    }

    public static void EnrichDiagnosticContext(IDiagnosticContext diagnostics, HttpContext context)
    {
        diagnostics.Set(RequestTelemetryMiddleware.ClientIpProperty, context.Connection.RemoteIpAddress?.ToString());
        diagnostics.Set("RequestHost", context.Request.Host.Value);
        diagnostics.Set("RequestScheme", context.Request.Scheme);

        if (context.GetEndpoint()?.DisplayName is { } endpoint)
            diagnostics.Set("EndpointName", endpoint);

        // Set once ApiKeyAuthenticationHandler has authenticated the request; the key's configured Name claim,
        // never the key/hash itself.
        if (context.User.Identity?.IsAuthenticated == true && context.User.FindFirstValue(ClaimTypes.Name) is { } keyName)
            diagnostics.Set(ConsumerKeyNameProperty, keyName);
    }
}
