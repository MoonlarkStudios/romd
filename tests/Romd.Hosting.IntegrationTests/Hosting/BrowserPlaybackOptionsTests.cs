using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Romd.Hosting;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Hosting;

public sealed class BrowserPlaybackOptionsTests
{
    [Fact]
    public void AddRomdBrowserPlayback_ValidOrigins_BindsExactMappingsAndOptionalDefault()
    {
        var services = new ServiceCollection();
        IConfiguration configuration = Config(
            ("Romd:BrowserPlayback:PlayerOrigins:0:Parent", "https://romd.example"),
            ("Romd:BrowserPlayback:PlayerOrigins:0:Player", "https://play.romd.example"),
            ("Romd:BrowserPlayback:PlayerOrigins:1:Parent", "http://romd.lan:8080"),
            ("Romd:BrowserPlayback:PlayerOrigins:1:Player", "http://romd.lan:8091"),
            ("Romd:BrowserPlayback:DefaultPlayerOrigin", "https://fallback.romd.example"));

        services.AddRomdBrowserPlayback(configuration);
        BrowserPlaybackOptions options = services
            .BuildServiceProvider()
            .GetRequiredService<BrowserPlaybackOptions>();

        options.ResolvePlayerOrigin("https://romd.example").ShouldBe("https://play.romd.example");
        options.ResolvePlayerOrigin("http://romd.lan:8080").ShouldBe("http://romd.lan:8091");
        options.ResolvePlayerOrigin("https://unknown.example").ShouldBe("https://fallback.romd.example");
        options.ResolvePlayerOrigin("https://ROMD.example").ShouldBe("https://fallback.romd.example");
        options.ResolvePlayerOrigin("https://romd.example:443").ShouldBe("https://fallback.romd.example");
    }

    [Fact]
    public void AddRomdBrowserPlayback_EmptyDefault_TreatsDefaultAsUnconfigured()
    {
        var services = new ServiceCollection();

        services.AddRomdBrowserPlayback(Config(("Romd:BrowserPlayback:DefaultPlayerOrigin", "")));
        BrowserPlaybackOptions options = services
            .BuildServiceProvider()
            .GetRequiredService<BrowserPlaybackOptions>();

        options.ResolvePlayerOrigin("https://unknown.example").ShouldBeNull();
    }

    [Theory]
    [InlineData("ftp://play.romd.example")]
    [InlineData("https://user@play.romd.example")]
    [InlineData("https://play.romd.example/")]
    [InlineData("https://play.romd.example/player")]
    [InlineData("https://play.romd.example?source=romd")]
    [InlineData("https://play.romd.example#player")]
    [InlineData("https://play.romd.example:443")]
    [InlineData("not-an-origin")]
    public void AddRomdBrowserPlayback_InvalidDefaultOrigin_ThrowsImmediately(string origin)
    {
        var services = new ServiceCollection();
        IConfiguration configuration = Config(("Romd:BrowserPlayback:DefaultPlayerOrigin", origin));

        InvalidOperationException exception = Should.Throw<InvalidOperationException>(
            () => services.AddRomdBrowserPlayback(configuration));

        exception.Message.ShouldContain("Romd:BrowserPlayback:DefaultPlayerOrigin");
        exception.Message.ShouldContain("canonical HTTP(S) origin");
    }

    [Fact]
    public void AddRomdBrowserPlayback_InvalidMappedOrigin_ThrowsImmediately()
    {
        var services = new ServiceCollection();
        IConfiguration configuration = Config(
            ("Romd:BrowserPlayback:PlayerOrigins:0:Parent", "https://romd.example"),
            ("Romd:BrowserPlayback:PlayerOrigins:0:Player", "/player"));

        InvalidOperationException exception = Should.Throw<InvalidOperationException>(
            () => services.AddRomdBrowserPlayback(configuration));

        exception.Message.ShouldContain("Romd:BrowserPlayback:PlayerOrigins:0:Player");
    }

    [Fact]
    public void AddRomdBrowserPlayback_DuplicateParentOrigin_ThrowsImmediately()
    {
        var services = new ServiceCollection();
        IConfiguration configuration = Config(
            ("Romd:BrowserPlayback:PlayerOrigins:0:Parent", "https://romd.example"),
            ("Romd:BrowserPlayback:PlayerOrigins:0:Player", "https://play-one.romd.example"),
            ("Romd:BrowserPlayback:PlayerOrigins:1:Parent", "https://romd.example"),
            ("Romd:BrowserPlayback:PlayerOrigins:1:Player", "https://play-two.romd.example"));

        InvalidOperationException exception = Should.Throw<InvalidOperationException>(
            () => services.AddRomdBrowserPlayback(configuration));

        exception.Message.ShouldContain("duplicate parent origin 'https://romd.example'");
    }

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(value => value.Key, value => (string?)value.Value))
            .Build();
}
