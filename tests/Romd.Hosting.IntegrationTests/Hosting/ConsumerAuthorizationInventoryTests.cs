using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Romd.Hosting.Vfs;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Hosting;

/// <summary>
///     Enforces the protected-by-default decision
///     (docs/decisions/api-contract-policy.md, "Protected By Default") on the consumer host:
///     every /api route carries an explicit authorization decision and sits inside the
///     group-level default-deny fallback, and the anonymous surface is exactly the reviewed
///     inventory below. WebDAV authenticates with its own connection credentials.
/// </summary>
public sealed class ConsumerAuthorizationInventoryTests : IDisposable
{
    private const string TestJwtSecret = "consumer-authorization-inventory-secret-at-least-32-chars";

    /// <summary>
    ///     Intentionally anonymous /api routes: server identity discovery, public playback
    ///     configuration, and immutable public reference display assets.
    /// </summary>
    private static readonly string[] AllowedAnonymousApiRoutes =
        ["/api/assets/{hash}", "/api/playback/config", "/api/server/identity"];

    /// <summary>
    ///     The complete intentional anonymous surface outside /api: liveness and readiness probes,
    ///     public artwork and title media, the OAuth/OIDC protocol endpoints driven by oidc-client-ts
    ///     and the device flow, and the capability-authenticated delivery grant redemptions.
    /// </summary>
    private static readonly string[] ExpectedAnonymousNonApiRoutes =
    [
        "/artwork/{assetId}/{variantName}/{contentVersion}",
        "/connect/authorize",
        "/connect/login",
        "/connect/logout",
        "/connect/token",
        "/connect/verify",
        "/delivery/bios/{token}",
        "/delivery/content/{token}",
        "/delivery/derived-descriptor/{token}",
        "/delivery/original-layout/{token}",
        "/health",
        "/health/ready",
        "/media/{mediaId}"
    ];

    private const string VfsRoute = "/vfs/{**path}";

    private readonly string _dataDirectory = Path.Combine(
        Path.GetTempPath(),
        $"romd-consumer-auth-inventory-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dataDirectory))
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ConsumerHost_EveryApiRouteCarriesExplicitAuthorizationDecision()
    {
        await using var factory = CreateConsumerHostFactory();

        AuthorizationInventory.AssertApiRoutes(
            AuthorizationInventory.GetRouteEndpoints(factory.Services),
            "consumer",
            "MapRomdConsumerHttp",
            AllowedAnonymousApiRoutes);
    }

    [Fact]
    public async Task ConsumerHost_NonApiSurfaceIsAnonymousInventoryPlusVfsBasicAuthentication()
    {
        await using var factory = CreateConsumerHostFactory();
        var endpoints = AuthorizationInventory.GetRouteEndpoints(factory.Services);

        AuthorizationInventory.AnonymousNonApiRoutes(endpoints).ShouldBe(ExpectedAnonymousNonApiRoutes);

        // Every other routed non-/api endpoint is the WebDAV surface; SPA static-file fallbacks
        // (order int.MaxValue) serve index.html and are not operations.
        endpoints
            .Where(endpoint => !AuthorizationInventory.RoutePatternOf(endpoint).StartsWith("/api", StringComparison.Ordinal))
            .Where(endpoint => endpoint.Order != int.MaxValue)
            .Where(endpoint => !AuthorizationInventory.AllowsAnonymous(endpoint))
            .Select(AuthorizationInventory.RoutePatternOf)
            .Distinct(StringComparer.Ordinal)
            .ShouldBe([VfsRoute]);

        var vfs = endpoints.Single(endpoint => AuthorizationInventory.RoutePatternOf(endpoint) == VfsRoute);
        vfs.Metadata.GetOrderedMetadata<IAuthorizeData>()
            .Select(data => data.Policy)
            .ShouldBe([VfsBasicAuthenticationHandler.PolicyName]);
    }

    [Fact]
    public async Task ProtectedApiRoute_AnonymousRequest_ReceivesBearerChallenge()
    {
        await using var factory = CreateConsumerHostFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/me");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ToString().ShouldContain("Bearer");
    }

    [Fact]
    public async Task ApiGroupFallback_EndpointDeclaringNoAuthorization_ChallengesAnonymousCaller()
    {
        await using var factory = CreateConsumerHostFactory();
        // A second pipeline over the real consumer services: the probe is mapped on the group
        // MapRomdConsumerHttp configured and declares no authorization of its own, so only the
        // group-level default-deny fallback can decide it.
        var app = new ApplicationBuilder(factory.Services);
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseEndpoints(endpoints => endpoints
            .MapGroup("/api")
            .MapRomdConsumerHttp()
            .MapGet("undeclared-probe", () => "reached"));
        var pipeline = app.Build();
        await using var scope = factory.Services.CreateAsyncScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Request.Method = HttpMethods.Get;
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("localhost");
        context.Request.Path = "/api/undeclared-probe";

        await pipeline(context);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status401Unauthorized);
        context.Response.Headers.WWWAuthenticate.ToString().ShouldContain("Bearer");
    }

    private WebApplicationFactory<Romd.Consumer.Host.Program> CreateConsumerHostFactory() =>
        RomdHostTestFactory.Create<Romd.Consumer.Host.Program>(_dataDirectory, "Testing", TestJwtSecret);
}
