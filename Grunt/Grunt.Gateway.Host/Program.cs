using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Surprenant.Grunt.Core;
using Surprenant.Grunt.Core.Storage;
using Surprenant.Grunt.Gateway;
using Surprenant.Grunt.Gateway.Host;
using Surprenant.Grunt.Gateway.Host.Auth;
using Surprenant.Grunt.Gateway.Host.HaloAccount;
using Surprenant.Grunt.Gateway.Host.Quotas;
using Surprenant.Grunt.Gateway.Host.Telemetry;
using Surprenant.Grunt.Util;

// The logger used until the host is built, and for a startup crash before that.
Log.Logger = LoggingExtensions.CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);
    GatewayHost.ConfigureServices(builder);
    var app = builder.Build();
    GatewayHost.Configure(app);
    app.Run();
}
// HostAbortedException: tooling (dotnet-ef) and WebApplicationFactory in tests stopping the host on purpose
// right after Build(), to read its services without actually running it.
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "The gateway host terminated unexpectedly");
    throw;
}
finally
{
    Log.CloseAndFlush();
}

/// <summary>Entry point type for WebApplicationFactory in tests.</summary>
public partial class Program
{
}

namespace Surprenant.Grunt.Gateway.Host
{
    public static class GatewayHost
    {
        public const string HealthPath = "/health";
        public const string LivePath = "/health/live";
        public const string ReadyPath = "/health/ready";
        public const string ReadyTag = "ready";

        public static void ConfigureServices(WebApplicationBuilder builder)
        {
            var services = builder.Services;
            var configuration = builder.Configuration;
            var gatewaySection = configuration.GetSection(GatewayOptions.SectionName);
            services.Configure<GatewayOptions>(gatewaySection);
            services.TryAddSingletonTimeProvider();

            // Halo account: token store (pluggable IOAuthStorage), no file-based code reading, sign-in/refresh service.
            services.AddSingleton<IOAuthTokenStore, FileOAuthTokenStore>();
            services.AddSingleton<IOAuthStorage>(sp => sp.GetRequiredService<IOAuthTokenStore>());
            services.AddSingleton<IAccountAuthorization, GatewayAccountAuthorization>();
            services.AddSingleton<HaloAuthState>();
            services.AddSingleton<IHaloOAuthClient, XboxOAuthClient>();
            services.AddSingleton<IHaloSession, FactoryHaloSession>();
            services.AddSingleton<HaloAccountService>();
            services.AddHostedService<HaloTokenRefresher>();

            // Grunt's factory (OAuth/Xbox/Spartan tokens, auth retry + backoff) is the IHaloInfiniteClient behind every action.
            builder.RegisterHaloInfiniteClientFactory();
            services.AddSingleton(sp => (HaloInfiniteClientFactory)sp.GetRequiredService<IHaloInfiniteClientFactory>());
            services.AddSingleton<IHaloInfiniteClient>(sp => sp.GetRequiredService<IHaloInfiniteClientFactory>());

            // Generated controller + response cache, admin/callback controllers from this assembly.
            services.AddHaloGateway(gatewaySection.GetSection("Cache"))
                .AddApplicationPart(typeof(GatewayHost).Assembly)
                // Admin/status JSON shows enum names (the generated controller uses HaloGatewayProtocol.JsonOptions instead).
                .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

            // Admin endpoints: the admin key, or (when Gateway:Oidc is configured) a browser session from the admin page.
            var oidc = gatewaySection.GetSection("Oidc").Get<OidcOptions>() ?? new OidcOptions();
            services.AddAuthentication(GatewayAuth.ConsumerScheme)
                .AddScheme<ApiKeySchemeOptions, ApiKeyAuthenticationHandler>(GatewayAuth.ConsumerScheme, o => o.Admin = false)
                .AddScheme<ApiKeySchemeOptions, ApiKeyAuthenticationHandler>(GatewayAuth.AdminScheme, o => o.Admin = true)
                .AddAdminSessions(oidc);
            var adminSchemes = oidc.Enabled ? new[] { GatewayAuth.AdminScheme, AdminSession.CookieScheme } : new[] { GatewayAuth.AdminScheme };
            services.AddAuthorizationBuilder()
                .AddPolicy(HaloInfiniteController.ConsumerPolicy, p => p
                    .AddAuthenticationSchemes(GatewayAuth.ConsumerScheme)
                    .RequireAuthenticatedUser()
                    .RequireRole(GatewayAuth.ConsumerRole))
                .AddPolicy(GatewayAuth.AdminPolicy, p => p
                    .AddAuthenticationSchemes(adminSchemes)
                    .RequireAuthenticatedUser()
                    .RequireRole(GatewayAuth.AdminRole));

            services.AddSingleton<QuotaGate>();
            services.AddSingleton<GatewayHostGauges>();

            services.AddHealthChecks()
                .AddCheck<HaloAuthHealthCheck>("halo_auth", HealthStatus.Degraded, new[] { ReadyTag });

            // Trust X-Forwarded-*/X-Real-IP only from configured networks/proxies (the ForwardedHeaders section),
            // e.g. the cluster pod network behind Traefik. Logged once the app logger is available (Configure).
            var forwardedHeadersInvalid = services.ConfigureAppForwardedHeaders(configuration);
            services.AddSingleton(new ForwardedHeadersDiagnostics(forwardedHeadersInvalid));

            builder.AddSerilogLogging();
            ConfigureTelemetry(builder);
        }

        private static void TryAddSingletonTimeProvider(this IServiceCollection services)
        {
            if (!services.Any(d => d.ServiceType == typeof(TimeProvider)))
                services.AddSingleton(TimeProvider.System);
        }

        /// <summary>
        /// OpenTelemetry traces (incoming requests minus health probes, outgoing Halo/Microsoft HttpClient calls, cache/quota tags)
        /// and metrics (ASP.NET Core, HttpClient, runtime, Grunt retries, gateway upstream/cache/quota/token meters). OTLP export
        /// is the only export, like GrifballWebApp: the standard OTEL_* variables configure it (on when OTEL_EXPORTER_OTLP_ENDPOINT
        /// is set), and nothing is served for scraping (no /metrics). Logs go through Serilog (<see cref="Telemetry.LoggingExtensions"/>) instead of the OTel SDK's
        /// own logging bridge, but still carry the same trace/span ids (Serilog reads them from Activity.Current natively).
        /// </summary>
        private static void ConfigureTelemetry(WebApplicationBuilder builder)
        {
            var otel = builder.Services.AddOpenTelemetry()
                .ConfigureResource(r => r.AddService(builder.Configuration["OTEL_SERVICE_NAME"] ?? "halo-gateway"))
                .WithTracing(t => t
                    .AddAspNetCoreInstrumentation(o => o.Filter = ctx => !IsInfrastructurePath(ctx.Request.Path))
                    .AddHttpClientInstrumentation())
                .WithMetrics(m => m
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation()
                    .AddMeter(HaloRequestPolicy.MeterName, HaloGatewayMetrics.MeterName, GatewayHostMetrics.MeterName));

            if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
                otel.UseOtlpExporter();
        }

        public static bool IsInfrastructurePath(PathString path)
            => path.StartsWithSegments(HealthPath);

        public static void Configure(WebApplication app)
        {
            var options = app.Services.GetRequiredService<IOptions<GatewayOptions>>().Value;
            if (options.Auth.UseInstanceLock)
            {
                var instanceLock = InstanceLock.Acquire(options.Auth.TokenPath);
                app.Lifetime.ApplicationStopped.Register(instanceLock.Dispose);
            }
            if (string.IsNullOrEmpty(options.AdminKey))
                app.Logger.LogWarning("Gateway:AdminKey is not set: the Halo account admin endpoints reject every request");
            if (options.ApiKeys.Count == 0)
                app.Logger.LogWarning("Gateway:ApiKeys is empty: every Halo gateway request will be rejected");
            if (options.Oidc.Enabled && options.Oidc.AllowedGroups.Count == 0)
                app.Logger.LogWarning("Gateway:Oidc:AllowedGroups is empty: nobody can use the admin page");
            foreach (var entry in app.Services.GetRequiredService<ForwardedHeadersDiagnostics>().Invalid)
                app.Logger.LogWarning("Ignoring invalid ForwardedHeaders entry {Entry}", entry);

            app.Services.GetRequiredService<GatewayHostGauges>();

            app.UseForwardedHeaders();
            // After UseForwardedHeaders so ClientIp/the request summary log carry the real (trusted) client address.
            app.UseRequestTelemetry();
            app.UseRouting();
            app.UseAuthentication();
            app.UseAuthorization();
            app.UseMiddleware<QuotaMiddleware>();

            // /health/live runs no checks (a Halo outage or missing sign-in never restarts the pod); /health/ready and /health run
            // the Halo account check, which is at worst Degraded (still 200). Status words only, no details.
            app.MapHealthChecks(LivePath, new HealthCheckOptions { Predicate = _ => false });
            app.MapHealthChecks(ReadyPath, new HealthCheckOptions { Predicate = c => c.Tags.Contains(ReadyTag) });
            app.MapHealthChecks(HealthPath);
            app.MapControllers();
        }
    }
}
