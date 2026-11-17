using System.Net.NetworkInformation;
using System.Net.Sockets;
using IPAddress = System.Net.IPAddress;
using IPNetwork = System.Net.IPNetwork;

namespace Romd.Hosting;

/// <summary>Applies <see cref="RomdIngressListenerOptions" /> to the server.</summary>
public static class RomdIngressListenerExtensions
{
    /// <summary>
    ///     Binds the listener to the ingress network and, unless an allowlist is configured, trusts forwarded
    ///     headers from that network only. Call before <see cref="RomdForwardedHeadersExtensions.AddRomdForwardedHeaders" />.
    /// </summary>
    public static WebApplicationBuilder UseRomdIngressListener(this WebApplicationBuilder builder)
    {
        var options = RomdIngressListenerOptions.FromConfiguration(builder.Configuration);
        if (!options.Enabled)
        {
            return builder;
        }

        IngressBinding binding = Resolve(options.NetworkAlias, System.Net.Dns.GetHostAddresses, LocalAddresses());
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            // Loopback serves the container's own health check; nothing outside the container reaches it.
            kestrel.ListenLocalhost(options.Port);
            kestrel.Listen(binding.Address, options.Port);
        });

        if (!RomdForwardedHeadersOptions.FromConfiguration(builder.Configuration).HasExplicitTrust)
        {
            // The only peers this listener can have are that network's members, so the network is the
            // allowlist. Loopback stays untrusted: a local request cannot claim HTTPS or another client.
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{RomdForwardedHeadersOptions.SectionName}:KnownNetworks:0"] = binding.Network.ToString()
            });
        }

        return builder;
    }

    /// <summary>
    ///     This container's address on the network that declares <paramref name="alias" />, and that network.
    ///     Anything other than exactly one of this container's own addresses is refused: listening elsewhere
    ///     would accept forwarded headers from peers that are not the ingress.
    /// </summary>
    internal static IngressBinding Resolve(
        string alias,
        Func<string, IPAddress[]> resolve,
        IReadOnlyCollection<LocalAddress> localAddresses)
    {
        IPAddress[] resolved;
        try
        {
            resolved = resolve(alias);
        }
        catch (SocketException exception)
        {
            throw Refusal(alias, $"it does not resolve ({exception.SocketErrorCode})");
        }

        return resolved switch
        {
            [] => throw Refusal(alias, "it resolves to no address"),
            [var address] when IPAddress.IsLoopback(address) => throw Refusal(alias, "it resolves to loopback"),
            [var address] when localAddresses.FirstOrDefault(local => local.Address.Equals(address)) is { } local =>
                new IngressBinding(address, NetworkOf(local)),
            [var address] => throw Refusal(alias, $"it resolves to {address}, which is not an address of this container"),
            _ => throw Refusal(alias, $"it resolves to {resolved.Length} addresses")
        };
    }

    private static IPNetwork NetworkOf(LocalAddress local)
    {
        byte[] bytes = local.Address.GetAddressBytes();
        for (int index = 0; index < bytes.Length; index++)
        {
            int networkBits = Math.Clamp(local.PrefixLength - (index * 8), 0, 8);
            bytes[index] &= (byte)(0xFF << (8 - networkBits));
        }

        return new IPNetwork(new IPAddress(bytes), local.PrefixLength);
    }

    private static InvalidOperationException Refusal(string alias, string reason) => new(
        $"{RomdIngressListenerOptions.SectionName}:NetworkAlias '{alias}' must resolve to exactly one address of this "
        + $"container, but {reason}. Declare the alias for this service on the ingress network only. Refusing to "
        + "start: listening on other networks would accept forwarded headers from peers that are not the ingress.");

    private static LocalAddress[] LocalAddresses() =>
    [
        .. NetworkInterface.GetAllNetworkInterfaces()
            .SelectMany(networkInterface => networkInterface.GetIPProperties().UnicastAddresses)
            .Select(unicast => new LocalAddress(unicast.Address, unicast.PrefixLength))
    ];

    internal sealed record LocalAddress(IPAddress Address, int PrefixLength);

    internal sealed record IngressBinding(IPAddress Address, IPNetwork Network);
}
