namespace Romd.Hosting;

/// <summary>
///     Reverse-proxy trust configuration, bound from the <c>Romd:ForwardedHeaders</c> section.
///     Disabled by default: honoring <c>X-Forwarded-*</c> headers without a proxy in front lets any
///     client spoof the scheme/host (and so defeat the HTTPS transport requirement), so forwarded-header
///     processing must be enabled explicitly for proxied deployments and paired with a trusted-proxy
///     allowlist (<see cref="KnownNetworks" />/<see cref="KnownProxies" />) or the deliberate
///     <see cref="TrustAllProxies" /> escape hatch.
/// </summary>
public sealed class RomdForwardedHeadersOptions
{
    public const string SectionName = "Romd:ForwardedHeaders";

    /// <summary>Whether to process forwarded headers at all. Leave false for direct (non-proxied) hosting.</summary>
    public bool Enabled { get; init; }

    /// <summary>
    ///     Trust forwarded headers from any peer. Only safe when the application is reachable
    ///     <em>exclusively</em> through the proxy (e.g. unpublished container on an internal network).
    ///     Prefer <see cref="KnownNetworks" />/<see cref="KnownProxies" />, which keep loopback/LAN peers
    ///     from spoofing forwarded headers.
    /// </summary>
    public bool TrustAllProxies { get; init; }

    /// <summary>Individual proxy IP addresses whose forwarded headers are honored.</summary>
    public string[] KnownProxies { get; init; } = [];

    /// <summary>Proxy networks in CIDR notation (e.g. <c>172.28.0.0/24</c>) whose forwarded headers are honored.</summary>
    public string[] KnownNetworks { get; init; } = [];

    /// <summary>
    ///     Number of trusted proxy hops to walk back when reading forwarded headers. Default 1, which is
    ///     correct for a single reverse proxy that <em>sets</em> (replaces) the forwarded headers rather
    ///     than appending to a client-supplied chain.
    /// </summary>
    public int ForwardLimit { get; init; } = 1;

    /// <summary>True when at least one explicit proxy or network has been configured.</summary>
    public bool HasExplicitTrust => KnownProxies.Length > 0 || KnownNetworks.Length > 0;

    public static RomdForwardedHeadersOptions FromConfiguration(IConfiguration? configuration) =>
        configuration?.GetSection(SectionName).Get<RomdForwardedHeadersOptions>()
        ?? new RomdForwardedHeadersOptions();
}
