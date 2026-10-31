using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;
using Serilog.Settings.Configuration;
using Serilog.Sinks.OpenTelemetry;

namespace Surprenant.Grunt.Gateway.Host.Telemetry;

/// <summary>
/// Serilog logging, ported from GrifballWebApp's <c>Telemetry/LoggingExtensions.cs</c> so gateway logs look the
/// same in Loki: compact JSON on the console, with trace and span ids carried natively by Serilog from
/// <see cref="System.Diagnostics.Activity.Current"/> (no extra wiring needed for that part), plus the same logs
/// over OTLP that <c>Program.ConfigureTelemetry</c> already sends traces and metrics to.
/// </summary>
public static class LoggingExtensions
{
    /// <summary>The logger used until the host is built, and for a crash before that: console only, compact JSON.</summary>
    public static Serilog.Extensions.Hosting.ReloadableLogger CreateBootstrapLogger() => new LoggerConfiguration()
        .MinimumLevel.Information()
        .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
        .Enrich.FromLogContext()
        .WriteTo.Console(new RenderedCompactJsonFormatter())
        .CreateBootstrapLogger();

    /// <summary>Replaces the bootstrap logger with the configured one once the host is built.</summary>
    public static WebApplicationBuilder AddSerilogLogging(this WebApplicationBuilder builder)
    {
        builder.Services.AddSerilog((services, configuration) => configuration
            .ConfigureLogging(builder.Configuration, builder.Environment, services));
        return builder;
    }

    /// <summary>
    /// Compact JSON on the console in every environment (what kubectl logs and Alloy/Promtail see), plus the
    /// debug output in Development; when <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set, OTLP to the same collector
    /// traces and metrics already go to.
    /// </summary>
    /// <remarks>
    /// The whole <c>Serilog</c> section is read, so configuration can add sinks or level overrides on top of
    /// these, e.g. <c>Serilog__MinimumLevel__Override__Surprenant.Grunt=Debug</c>. Do not configure a Console
    /// sink there: it would print every line twice.
    /// </remarks>
    public static LoggerConfiguration ConfigureLogging(this LoggerConfiguration logger, IConfiguration configuration,
        IHostEnvironment environment, IServiceProvider? services = null)
    {
        logger.ReadFrom.Configuration(configuration, new ConfigurationReaderOptions { SectionName = "Serilog" });
        if (services is not null)
            logger.ReadFrom.Services(services);

        logger.Enrich.FromLogContext();

        // JSON everywhere, Development included, so every line carries its trace and span ids (@tr, @sp).
        var json = new RenderedCompactJsonFormatter();
        logger.WriteTo.Console(json);
        if (environment.IsDevelopment())
            logger.WriteTo.Debug(json);

        if (OtlpLogsEndpoint(configuration) is { } endpoint)
        {
            var protocol = IsHttpProtobuf(configuration) ? OtlpProtocol.HttpProtobuf : OtlpProtocol.Grpc;
            logger.WriteTo.OpenTelemetry(options =>
            {
                options.Endpoint = endpoint;
                options.Protocol = protocol;
                options.ResourceAttributes = ResourceAttributes(configuration, environment);
                // The sink's own export requests would otherwise show up as HttpClient spans.
                options.OnBeginSuppressInstrumentation = OpenTelemetry.SuppressInstrumentationScope.Begin;
            }, ignoreEnvironment: true);
        }

        return logger;
    }

    /// <summary>
    /// gRPC takes the base endpoint; http/protobuf needs the signal path, which the exporter does not add when
    /// the endpoint is set in code. Null when <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is unset or invalid.
    /// </summary>
    private static string? OtlpLogsEndpoint(IConfiguration configuration)
    {
        var raw = configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        if (string.IsNullOrWhiteSpace(raw) || !Uri.TryCreate(raw, UriKind.Absolute, out var endpoint))
            return null;
        return IsHttpProtobuf(configuration) ? endpoint.AbsoluteUri.TrimEnd('/') + "/v1/logs" : endpoint.AbsoluteUri;
    }

    private static bool IsHttpProtobuf(IConfiguration configuration) =>
        string.Equals(configuration["OTEL_EXPORTER_OTLP_PROTOCOL"]?.Trim(), "http/protobuf", StringComparison.OrdinalIgnoreCase);

    /// <summary>The same resource every log line carries: service.name plus whatever OTEL_RESOURCE_ATTRIBUTES sets.</summary>
    private static Dictionary<string, object> ResourceAttributes(IConfiguration configuration, IHostEnvironment environment)
    {
        var attributes = new Dictionary<string, object>
        {
            ["service.name"] = configuration["OTEL_SERVICE_NAME"] ?? environment.ApplicationName,
        };

        var raw = configuration["OTEL_RESOURCE_ATTRIBUTES"];
        if (string.IsNullOrWhiteSpace(raw))
            return attributes;

        foreach (var pair in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0)
                continue;
            var key = pair[..separator].Trim();
            var value = Uri.UnescapeDataString(pair[(separator + 1)..].Trim());
            if (value.Length > 0)
                attributes[key] = value;
        }

        return attributes;
    }
}
