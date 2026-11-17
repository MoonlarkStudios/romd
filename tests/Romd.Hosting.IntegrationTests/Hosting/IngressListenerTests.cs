using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Romd.Hosting;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Hosting;

public sealed class IngressListenerTests
{
    private const string Alias = "romd-admin.ingress";
    private static readonly IPAddress Ingress = IPAddress.Parse("172.21.0.2");
    private static readonly IPAddress Backend = IPAddress.Parse("172.24.0.2");

    private static readonly RomdIngressListenerExtensions.LocalAddress[] Local =
    [
        new(IPAddress.Loopback, 8), new(Backend, 16), new(Ingress, 16)
    ];

    [Fact]
    public void Resolve_AliasOfThisContainerOnOneNetwork_BindsThatAddressAndTrustsThatNetwork()
    {
        var binding = RomdIngressListenerExtensions.Resolve(Alias, _ => [Ingress], Local);

        binding.Address.ShouldBe(Ingress);
        binding.Network.ToString().ShouldBe("172.21.0.0/16");
    }

    [Theory]
    [InlineData("10.213.7.130", 25, "10.213.7.128/25")]
    [InlineData("192.168.5.9", 20, "192.168.0.0/20")]
    [InlineData("fd00:1:2:3::9", 64, "fd00:1:2:3::/64")]
    public void Resolve_AnyPrefixLength_TrustsExactlyTheNetworkOfTheBoundAddress(string address, int prefix, string network)
    {
        IPAddress own = IPAddress.Parse(address);

        RomdIngressListenerExtensions.Resolve(Alias, _ => [own], [new(own, prefix)]).Network.ToString().ShouldBe(network);
    }

    public static TheoryData<IPAddress[], string> Refused => new()
    {
        { [], "no address" },
        // A name known on every network (the service name) would bind more than the ingress network.
        { [Ingress, Backend], "2 addresses" },
        // Another container's address: the alias was declared on the wrong service.
        { [IPAddress.Parse("172.21.0.9")], "not an address of this container" },
        { [IPAddress.Loopback], "loopback" }
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public void Resolve_AnythingButOneOwnAddress_RefusesToStart(IPAddress[] resolved, string reason)
    {
        var exception = Should.Throw<InvalidOperationException>(
            () => RomdIngressListenerExtensions.Resolve(Alias, _ => resolved, Local));

        exception.Message.ShouldContain(reason);
        exception.Message.ShouldContain(Alias);
    }

    [Fact]
    public void Resolve_UnknownAlias_RefusesToStart()
    {
        var exception = Should.Throw<InvalidOperationException>(() => RomdIngressListenerExtensions.Resolve(
            Alias, _ => throw new SocketException((int)SocketError.HostNotFound), Local));

        exception.Message.ShouldContain("does not resolve");
    }

    [Fact]
    public void UseRomdIngressListener_UnresolvableAlias_FailsBeforeTheHostIsBuilt()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Romd:IngressListener:NetworkAlias"] = "romd-unit-test.invalid"
        });

        Should.Throw<InvalidOperationException>(() => builder.UseRomdIngressListener());
    }

    [Fact]
    public void UseRomdIngressListener_NoAlias_LeavesTrustToTheExplicitConfiguration()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Romd:ForwardedHeaders:Enabled"] = "true"
        });

        builder.UseRomdIngressListener();

        // Without a listener network nothing is trusted implicitly: the host still refuses to start.
        Should.Throw<InvalidOperationException>(() => builder.Services.AddRomdForwardedHeaders(builder.Configuration));
    }
}
