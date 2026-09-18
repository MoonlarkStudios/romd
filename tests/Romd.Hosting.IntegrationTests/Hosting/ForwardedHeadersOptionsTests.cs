using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Romd.Hosting;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Hosting;

public sealed class ForwardedHeadersOptionsTests
{
    [Fact]
    public void AddRomdForwardedHeaders_EnabledWithoutTrust_ThrowsImmediately()
    {
        var services = new ServiceCollection();
        IConfiguration config = Config(("Romd:ForwardedHeaders:Enabled", "true"));

        // Refusing to start is the footgun guard: enabled but trusting no proxy would accept spoofed headers.
        Should.Throw<InvalidOperationException>(() => services.AddRomdForwardedHeaders(config));
    }

    [Fact]
    public void AddRomdForwardedHeaders_EnabledWithKnownNetwork_ConfiguresForwardedOptions()
    {
        var services = new ServiceCollection();
        IConfiguration config = Config(
            ("Romd:ForwardedHeaders:Enabled", "true"),
            ("Romd:ForwardedHeaders:ForwardLimit", "2"),
            ("Romd:ForwardedHeaders:KnownNetworks:0", "172.28.0.0/24"));

        services.AddRomdForwardedHeaders(config);
        ForwardedHeadersOptions options = Resolve(services);

        options.ForwardLimit.ShouldBe(2);
        options.KnownIPNetworks.Count.ShouldBe(1);
        options.KnownIPNetworks[0].PrefixLength.ShouldBe(24);
        (options.ForwardedHeaders & ForwardedHeaders.XForwardedProto).ShouldBe(ForwardedHeaders.XForwardedProto);
        (options.ForwardedHeaders & ForwardedHeaders.XForwardedHost).ShouldBe(ForwardedHeaders.XForwardedHost);
        // Loopback defaults are cleared so only the configured proxy network is trusted.
        options.KnownProxies.ShouldBeEmpty();
    }

    [Fact]
    public void AddRomdForwardedHeaders_TrustAllProxies_AcceptsWithoutExplicitTrust()
    {
        var services = new ServiceCollection();
        IConfiguration config = Config(
            ("Romd:ForwardedHeaders:Enabled", "true"),
            ("Romd:ForwardedHeaders:TrustAllProxies", "true"));

        services.AddRomdForwardedHeaders(config);
        ForwardedHeadersOptions options = Resolve(services);

        // Both allowlists empty => forwarded headers accepted from any peer (the documented escape hatch).
        options.KnownIPNetworks.ShouldBeEmpty();
        options.KnownProxies.ShouldBeEmpty();
    }

    [Fact]
    public void AddRomdForwardedHeaders_EnabledWithInvalidCidr_ThrowsOnResolve()
    {
        var services = new ServiceCollection();
        IConfiguration config = Config(
            ("Romd:ForwardedHeaders:Enabled", "true"),
            ("Romd:ForwardedHeaders:KnownNetworks:0", "not-a-cidr"));

        services.AddRomdForwardedHeaders(config);
        ServiceProvider provider = services.BuildServiceProvider();

        Should.Throw<InvalidOperationException>(
            () => provider.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value);
    }

    [Fact]
    public void AddRomdForwardedHeaders_Disabled_DoesNotProcessHeaders()
    {
        var services = new ServiceCollection();

        services.AddRomdForwardedHeaders(Config());
        ServiceProvider provider = services.BuildServiceProvider();

        provider.GetRequiredService<RomdForwardedHeadersOptions>().Enabled.ShouldBeFalse();
        // Disabled => no ForwardedHeadersOptions configuration is contributed, so the middleware is never
        // added and Kestrel keeps seeing the real (direct) scheme/host.
        provider.GetService<IConfigureOptions<ForwardedHeadersOptions>>().ShouldBeNull();
    }

    private static ForwardedHeadersOptions Resolve(IServiceCollection services) =>
        services.BuildServiceProvider().GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value))
            .Build();
}
