using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Romd.Infrastructure.Identity;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

public sealed class ConsumerPlaybackEndpointsTests : IDisposable
{
    private const string TestJwtSecret = "consumer-playback-endpoint-test-secret-at-least-32-characters";
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"romd-consumer-playback-endpoints-{Guid.NewGuid():N}");

    [Fact]
    public async Task GetPlaybackConfig_AnonymousExactParentMatch_ReturnsConfiguredOriginWithoutCaching()
    {
        await using var factory = CreateFactory(new Dictionary<string, string?>
        {
            ["Romd:BrowserPlayback:PlayerOrigins:0:Parent"] = "http://portal.example",
            ["Romd:BrowserPlayback:PlayerOrigins:0:Player"] = "https://play.romd.example"
        });
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/playback/config");
        request.Headers.Host = "portal.example";

        using var response = await client.SendAsync(request);
        string content = await response.Content.ReadAsStringAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        content.ShouldBe("{\"playerOrigin\":\"https://play.romd.example\"}");
        response.Headers.CacheControl.ShouldNotBeNull();
        response.Headers.CacheControl.NoStore.ShouldBeTrue();
        response.Headers.CacheControl.ToString().ShouldBe("no-store");
        response.Headers.WwwAuthenticate.ShouldBeEmpty();

        var endpoint = factory.Services
            .GetRequiredService<EndpointDataSource>()
            .Endpoints
            .OfType<RouteEndpoint>()
            .Single(candidate => candidate.RoutePattern.RawText == "/api/playback/config");
        endpoint.Metadata.GetMetadata<IAllowAnonymous>().ShouldNotBeNull();
        endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().ShouldBeEmpty();
    }

    [Fact]
    public async Task GetPlaybackConfig_ForwardedSchemeAndHostMatch_UsesPostForwardedRequestOrigin()
    {
        await using var factory = CreateFactory(new Dictionary<string, string?>
        {
            ["Romd:ForwardedHeaders:Enabled"] = "true",
            ["Romd:ForwardedHeaders:TrustAllProxies"] = "true",
            ["Romd:BrowserPlayback:PlayerOrigins:0:Parent"] = "https://portal.romd.example",
            ["Romd:BrowserPlayback:PlayerOrigins:0:Player"] = "https://play.romd.example",
            ["Romd:BrowserPlayback:DefaultPlayerOrigin"] = "https://fallback.romd.example"
        });
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/playback/config");
        request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");
        request.Headers.TryAddWithoutValidation("X-Forwarded-Host", "portal.romd.example");

        using var response = await client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync())
            .ShouldBe("{\"playerOrigin\":\"https://play.romd.example\"}");
    }

    [Fact]
    public async Task GetPlaybackConfig_NoExactParentMatch_ReturnsConfiguredDefault()
    {
        await using var factory = CreateFactory(new Dictionary<string, string?>
        {
            ["Romd:BrowserPlayback:PlayerOrigins:0:Parent"] = "https://portal.romd.example",
            ["Romd:BrowserPlayback:PlayerOrigins:0:Player"] = "https://play.romd.example",
            ["Romd:BrowserPlayback:DefaultPlayerOrigin"] = "https://fallback.romd.example"
        });
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/playback/config");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync())
            .ShouldBe("{\"playerOrigin\":\"https://fallback.romd.example\"}");
    }

    [Fact]
    public async Task GetPlaybackConfig_NoExactParentMatchOrDefault_ReturnsRequiredNullProperty()
    {
        await using var factory = CreateFactory(new Dictionary<string, string?>());
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/playback/config");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).ShouldBe("{\"playerOrigin\":null}");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private WebApplicationFactory<Romd.Consumer.Host.Program> CreateFactory(
        IReadOnlyDictionary<string, string?> playbackConfiguration)
    {
        string dataDirectory = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        string webRoot = Path.Combine(dataDirectory, "wwwroot");
        Directory.CreateDirectory(webRoot);
        File.WriteAllText(Path.Combine(webRoot, "index.html"), "<!doctype html><html></html>");
        OpenIddictSigningKey.EnsureCreated(dataDirectory);

        return new WebApplicationFactory<Romd.Consumer.Host.Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.UseContentRoot(AppContext.BaseDirectory);
                builder.UseWebRoot(webRoot);
                builder.UseSetting("hostBuilder:reloadConfigOnChange", "false");
                builder.UseSetting("Romd:DataDirectory", dataDirectory);
                builder.UseSetting("Romd:JwtSecret", TestJwtSecret);

                foreach ((string key, string? value) in playbackConfiguration)
                {
                    builder.UseSetting(key, value);
                }

                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IHostedService>();
                    services.AddHostedService<ServerInstanceIdentityInitializer>();
                });
            });
    }
}
