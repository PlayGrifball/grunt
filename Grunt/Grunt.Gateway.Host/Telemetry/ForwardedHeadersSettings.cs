using Microsoft.AspNetCore.HttpOverrides;
using System.Net;

namespace Surprenant.Grunt.Gateway.Host.Telemetry;

/// <summary>
/// The <c>ForwardedHeaders</c> configuration section. Ported from GrifballWebApp's
/// <c>Telemetry/AppForwardedHeaders.cs</c> (trimmed: this gateway sits behind the cluster's own Traefik ingress,
/// not directly behind Cloudflare, so the Cloudflare-range-fetch option is not needed here).
/// </summary>
public sealed class ForwardedHeadersSettings
{
    /// <summary>CIDRs whose immediate requests may set the client address, scheme and host, e.g. the cluster pod network.</summary>
    public string[] KnownIPNetworks { get; set; } = [];

    /// <summary>Single proxy addresses, same meaning as <see cref="KnownIPNetworks"/>.</summary>
    public string[] KnownProxies { get; set; } = [];

    /// <summary>
    /// Header carrying the client address. Default <c>X-Forwarded-For</c>. Behind Traefik use <c>X-Real-IP</c>:
    /// Traefik has already resolved it against the ranges it trusts.
    /// </summary>
    public string? ForwardedForHeaderName { get; set; }

    /// <summary>How many proxy entries to walk back through. Default 1.</summary>
    public int? ForwardLimit { get; set; }
}

public static class AppForwardedHeaders
{
    public const string SectionName = "ForwardedHeaders";

    /// <summary>
    /// Trusts forwarded headers only from the configured networks and proxies. With none configured, the
    /// framework default stays: loopback only, which for a pod behind a proxy means nothing is applied (the
    /// previous behavior here cleared <c>KnownNetworks</c>/<c>KnownProxies</c>, which trusted every peer's
    /// X-Forwarded-* unconditionally). Entries that do not parse are skipped (trusting less, never more) and
    /// returned so they can be logged.
    /// </summary>
    public static IReadOnlyList<string> ConfigureAppForwardedHeaders(this IServiceCollection services, IConfiguration configuration)
    {
        var settings = Settings(configuration);
        var (networks, proxies, invalid) = Parse(settings);

        services.Configure<ForwardedHeadersOptions>(options => Apply(options, settings, networks, proxies));
        return invalid;
    }

    private static ForwardedHeadersSettings Settings(IConfiguration configuration) =>
        configuration.GetSection(SectionName).Get<ForwardedHeadersSettings>() ?? new ForwardedHeadersSettings();

    public static (List<IPNetwork> Networks, List<IPAddress> Proxies, List<string> Invalid) Parse(ForwardedHeadersSettings settings)
    {
        var networks = new List<IPNetwork>();
        var proxies = new List<IPAddress>();
        var invalid = new List<string>();

        foreach (var entry in settings.KnownIPNetworks.Where(e => !string.IsNullOrWhiteSpace(e)))
        {
            if (IPNetwork.TryParse(entry.Trim(), out var network))
                networks.Add(network);
            else
                invalid.Add(entry);
        }

        foreach (var entry in settings.KnownProxies.Where(e => !string.IsNullOrWhiteSpace(e)))
        {
            if (IPAddress.TryParse(entry.Trim(), out var address))
                proxies.Add(address);
            else
                invalid.Add(entry);
        }

        return (networks, proxies, invalid);
    }

    public static void Apply(ForwardedHeadersOptions options, ForwardedHeadersSettings settings, IReadOnlyCollection<IPNetwork> networks, IReadOnlyCollection<IPAddress> proxies)
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;

        if (networks.Count > 0 || proxies.Count > 0)
        {
            // KnownNetworks/KnownProxies default to loopback only; replace that default with the configured
            // trust list instead of clearing it down to nothing (KnownNetworks itself is obsolete on .NET 10 in
            // favor of KnownIPNetworks).
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
            foreach (var network in networks)
                options.KnownIPNetworks.Add(network);
            foreach (var proxy in proxies)
                options.KnownProxies.Add(proxy);
        }

        if (!string.IsNullOrWhiteSpace(settings.ForwardedForHeaderName))
            options.ForwardedForHeaderName = settings.ForwardedForHeaderName.Trim();

        if (settings.ForwardLimit is { } limit && limit > 0)
            options.ForwardLimit = limit;
    }
}

/// <summary>Invalid <c>ForwardedHeaders</c> entries found at startup, logged once the app logger is available.</summary>
public sealed record ForwardedHeadersDiagnostics(IReadOnlyList<string> Invalid);
