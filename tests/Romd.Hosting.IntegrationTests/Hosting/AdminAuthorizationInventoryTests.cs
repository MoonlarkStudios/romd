using System.Net;
using Hangfire;
using Hangfire.InMemory;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Romd.Infrastructure.Identity;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Hosting;

/// <summary>
///     Enforces the protected-by-default decision in
///     docs/decisions/admin-api-contract-policy.md: every admin /api route carries an explicit
///     authorization decision (a named policy or an explicit AllowAnonymous), the group-level
///     default-deny fallback is present on every /api route, and the anonymous surface of the
///     admin host is exactly the reviewed inventory below. A future route added without an
///     authorization decision fails these tests even though the group fallback already denies it
///     at runtime.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class AdminAuthorizationInventoryTests
{
    private const string TestJwtSecret = "admin-authorization-inventory-secret-at-least-32-chars";

    /// <summary>
    ///     Intentionally anonymous /api routes. The protected-by-default ADR requires this list
    ///     to stay explicit. Reference assets are public display facts, never library content.
    /// </summary>
    private static readonly string[] AllowedAnonymousApiRoutes = ["/api/auth/account-link", "/api/assets/{hash}"];

    /// <summary>
    ///     The complete intentional anonymous surface outside /api: the liveness and readiness
    ///     probes, public media delivery, and the OAuth/OIDC protocol endpoints driven by
    ///     oidc-client-ts and the device flow directly.
    /// </summary>
    private static readonly string[] ExpectedAnonymousNonApiRoutes =
    [
        "/artwork/{assetId}/{variantName}/{contentVersion}",
        "/connect/authorize",
        "/connect/login",
        "/connect/logout",
        "/connect/token",
        "/connect/verify",
        "/health",
        "/health/ready",
        "/media/{mediaId}"
    ];

    private readonly IntegrationTestFixture _fixture;

    public AdminAuthorizationInventoryTests(IntegrationTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task AdminHost_EveryApiRouteCarriesExplicitAuthorizationDecision()
    {
        await using var factory = CreateAdminHostFactory();

        AssertAuthorizationInventory(GetRouteEndpoints(factory.Services));
    }

    [Fact]
    public async Task ProtectedApiRoute_AnonymousRequest_ReceivesBearerChallenge()
    {
        using var client = _fixture.CreateClient();

        using var response = await client.GetAsync("/api/catalog/filters");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ToString().ShouldContain("Bearer");
    }

    [Fact]
    public async Task ProtectedApiRoute_AuthenticatedRequest_Succeeds()
    {
        using var client = _fixture.CreateAuthenticatedClient();

        using var response = await client.GetAsync("/api/catalog/filters");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task HealthRoute_AnonymousRequest_Succeeds()
    {
        using var client = _fixture.CreateClient();

        using var response = await client.GetAsync("/health");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static void AssertAuthorizationInventory(IReadOnlyList<RouteEndpoint> endpoints)
    {
        // The /api 404 catch-all fallback is mapped outside the admin group and serves a static
        // NotFound; it is not an admin operation and the group fallback does not apply to it.
        var apiEndpoints = endpoints
            .Where(endpoint => RoutePatternOf(endpoint).StartsWith("/api", StringComparison.Ordinal))
            .Where(endpoint => endpoint.Order != int.MaxValue)
            .ToArray();

        apiEndpoints.ShouldNotBeEmpty();

        string[] undecidedRoutes = apiEndpoints
            .Where(endpoint => !HasNamedPolicy(endpoint) && !AllowsAnonymous(endpoint))
            .Select(RoutePatternOf)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        undecidedRoutes.ShouldBeEmpty(
            "Every admin /api route must declare a named authorization policy or an explicit " +
            $"AllowAnonymous. Undecided routes: {string.Join(", ", undecidedRoutes)}");

        string[] routesWithoutGroupFallback = apiEndpoints
            .Where(endpoint => !HasGroupDefaultAuthorization(endpoint))
            .Select(RoutePatternOf)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        routesWithoutGroupFallback.ShouldBeEmpty(
            "Every admin /api route must sit inside the default-deny authorization group " +
            $"(MapRomdAdminHttp). Missing fallback: {string.Join(", ", routesWithoutGroupFallback)}");

        string[] anonymousApiRoutes = apiEndpoints
            .Where(AllowsAnonymous)
            .Select(RoutePatternOf)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        anonymousApiRoutes.ShouldBe(AllowedAnonymousApiRoutes.Order(StringComparer.Ordinal).ToArray());

        string[] anonymousNonApiRoutes = endpoints
            .Where(endpoint => !RoutePatternOf(endpoint).StartsWith("/api", StringComparison.Ordinal))
            .Where(AllowsAnonymous)
            .Select(RoutePatternOf)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        anonymousNonApiRoutes.ShouldBe(ExpectedAnonymousNonApiRoutes);
    }

    private static RouteEndpoint[] GetRouteEndpoints(IServiceProvider services) =>
        services.GetRequiredService<EndpointDataSource>()
            .Endpoints
            .OfType<RouteEndpoint>()
            .ToArray();

    private static string RoutePatternOf(RouteEndpoint endpoint) =>
        endpoint.RoutePattern.RawText ?? string.Empty;

    private static bool HasNamedPolicy(RouteEndpoint endpoint) =>
        endpoint.Metadata
            .GetOrderedMetadata<IAuthorizeData>()
            .Any(data => !string.IsNullOrEmpty(data.Policy));

    private static bool AllowsAnonymous(RouteEndpoint endpoint) =>
        endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null;

    private static bool HasGroupDefaultAuthorization(RouteEndpoint endpoint) =>
        endpoint.Metadata
            .GetOrderedMetadata<IAuthorizeData>()
            .Any(data => string.IsNullOrEmpty(data.Policy)
                && string.IsNullOrEmpty(data.Roles)
                && string.IsNullOrEmpty(data.AuthenticationSchemes));

    private static WebApplicationFactory<Romd.Admin.Host.Program> CreateAdminHostFactory() =>
        new WebApplicationFactory<Romd.Admin.Host.Program>()
            .WithWebHostBuilder(builder =>
            {
                string dataDirectory = Path.Combine(
                    Path.GetTempPath(),
                    $"romd-admin-auth-inventory-{Guid.NewGuid():N}");
                string webRoot = Path.Combine(dataDirectory, "wwwroot");

                Directory.CreateDirectory(webRoot);
                File.WriteAllText(Path.Combine(webRoot, "index.html"), "<!doctype html><html></html>");
                OpenIddictSigningKey.EnsureCreated(dataDirectory);

                builder.UseEnvironment("Testing");
                builder.UseContentRoot(AppContext.BaseDirectory);
                builder.UseWebRoot(webRoot);
                builder.UseSetting("hostBuilder:reloadConfigOnChange", "false");
                builder.UseSetting("Romd:DataDirectory", dataDirectory);
                builder.UseSetting("Romd:JwtSecret", TestJwtSecret);

                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IHostedService>();
                    services.AddHangfire((_, config) => config
                        .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
                        .UseSimpleAssemblyNameTypeSerializer()
                        .UseRecommendedSerializerSettings()
                        .UseInMemoryStorage());
                });
            });
}
