using Microsoft.AspNetCore.HttpOverrides;
using IPAddress = System.Net.IPAddress;
using IPNetwork = System.Net.IPNetwork;

namespace Romd.Hosting;

/// <summary>
///     Registers and applies <see cref="ForwardedHeadersOptions" /> from <see cref="RomdForwardedHeadersOptions" />.
///     Behind a reverse proxy (nginx, Cloudflare Tunnel) Kestrel still sees the connection as plain HTTP, so
///     OpenIddict's production transport-security check and its issuer/host derivation depend on honoring the
///     proxy's <c>X-Forwarded-Proto</c>/<c>X-Forwarded-Host</c>. ASP.NET trusts forwarded headers only from
///     loopback by default, which a non-loopback container proxy is not — so the proxy must be allowlisted here.
/// </summary>
public static class RomdForwardedHeadersExtensions
{
    public static IServiceCollection AddRomdForwardedHeaders(
        this IServiceCollection services,
        IConfiguration? configuration)
    {
        var options = RomdForwardedHeadersOptions.FromConfiguration(configuration);
        services.AddSingleton(options);

        if (!options.Enabled)
        {
            return services;
        }

        if (!options.TrustAllProxies && !options.HasExplicitTrust)
        {
            throw new InvalidOperationException(
                $"{RomdForwardedHeadersOptions.SectionName}:Enabled is true but no trusted proxies are configured. "
                + "Set KnownNetworks/KnownProxies to the reverse-proxy address(es), or set TrustAllProxies=true only "
                + "when the app is reachable exclusively through the proxy. Refusing to start: trusting forwarded "
                + "headers from any peer would let clients spoof X-Forwarded-Proto/Host and bypass the HTTPS "
                + "transport requirement.");
        }

        services.Configure<ForwardedHeadersOptions>(forwarded =>
        {
            forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedFor
                                         | ForwardedHeaders.XForwardedProto
                                         | ForwardedHeaders.XForwardedHost;
            forwarded.ForwardLimit = options.ForwardLimit;

            // The defaults trust loopback only; replace them with the explicit proxy allowlist so a
            // non-loopback proxy is trusted while loopback/LAN peers cannot spoof forwarded headers.
            // KnownIPNetworks and the deprecated KnownNetworks share one backing store.
            forwarded.KnownIPNetworks.Clear();
            forwarded.KnownProxies.Clear();

            if (options.TrustAllProxies)
            {
                // Empty allowlists => forwarded headers are accepted from any peer. Safe only because the
                // app is reachable exclusively through the proxy; the deliberate simple-setup escape hatch.
                return;
            }

            foreach (string proxy in options.KnownProxies)
            {
                forwarded.KnownProxies.Add(ParseProxy(proxy));
            }

            foreach (string network in options.KnownNetworks)
            {
                forwarded.KnownIPNetworks.Add(ParseNetwork(network));
            }
        });

        return services;
    }

    public static WebApplication UseRomdForwardedHeaders(this WebApplication app)
    {
        // Must run before any middleware that reads the scheme/host (request logging, auth, OpenIddict).
        if (app.Services.GetRequiredService<RomdForwardedHeadersOptions>().Enabled)
        {
            app.UseForwardedHeaders();
        }

        return app;
    }

    private static IPAddress ParseProxy(string proxy)
    {
        if (!IPAddress.TryParse(proxy, out IPAddress? address))
        {
            throw new InvalidOperationException(
                $"Invalid IP address '{proxy}' in {RomdForwardedHeadersOptions.SectionName}:KnownProxies.");
        }

        return address;
    }

    private static IPNetwork ParseNetwork(string cidr)
    {
        if (!IPNetwork.TryParse(cidr, out IPNetwork network))
        {
            throw new InvalidOperationException(
                $"Invalid CIDR '{cidr}' in {RomdForwardedHeadersOptions.SectionName}:KnownNetworks. "
                + "Expected canonical prefix/length notation with host bits zeroed, e.g. '172.28.0.0/24'.");
        }

        return network;
    }
}
