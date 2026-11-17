namespace Romd.Hosting;

/// <summary>
///     Binds the HTTP listener to one container network, from the <c>Romd:IngressListener</c> section.
///     A container attached to several networks otherwise listens on all of them, so every peer on any
///     of them could reach the application directly and supply its own <c>X-Forwarded-*</c> headers.
///     With <see cref="NetworkAlias" /> set, the application listens only on its address on the network
///     that declares that alias (and on loopback, for its own health check), so the peers able to reach
///     it are exactly the members of that network, and forwarded headers are trusted from that network.
/// </summary>
public sealed class RomdIngressListenerOptions
{
    public const string SectionName = "Romd:IngressListener";

    /// <summary>
    ///     A DNS alias declared for this container on the ingress network only. Empty leaves the listener
    ///     to the standard server configuration (<c>ASPNETCORE_URLS</c>).
    /// </summary>
    public string NetworkAlias { get; init; } = "";

    /// <summary>The port the ingress connects to.</summary>
    public int Port { get; init; } = 8080;

    public bool Enabled => !string.IsNullOrWhiteSpace(NetworkAlias);

    public static RomdIngressListenerOptions FromConfiguration(IConfiguration? configuration) =>
        configuration?.GetSection(SectionName).Get<RomdIngressListenerOptions>()
        ?? new RomdIngressListenerOptions();
}
