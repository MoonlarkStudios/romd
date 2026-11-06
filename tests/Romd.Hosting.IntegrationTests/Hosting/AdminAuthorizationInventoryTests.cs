using Microsoft.AspNetCore.Authorization;
using Romd.Host.Authorization;
using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Hosting;

/// <summary>
///     Enforces the protected-by-default decision (docs/decisions/api-contract-policy.md,
///     "Protected By Default"): every admin /api route carries an explicit
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
    ///     to stay explicit. Reference assets are public display facts. Export redemption requires
    ///     a short-lived opaque capability issued only after authenticated authorization.
    /// </summary>
    private static readonly string[] AllowedAnonymousApiRoutes =
        ["/api/auth/account-link", "/api/assets/{hash}", "/api/download-tickets/{token}"];

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

    [Theory]
    [InlineData("/api/dats/{datId}/replacements")]
    [InlineData("/api/dats/{datId}/replacements/preview")]
    [InlineData("/api/dats/{datId}/replacements/changes")]
    [InlineData("/api/dats/{datId}/replacements/changes/{entryId}")]
    public async Task ReplacementRoutes_RequireManager(string route)
    {
        await using var factory = CreateAdminHostFactory();
        var endpoint = AuthorizationInventory.GetRouteEndpoints(factory.Services)
            .Single(endpoint => endpoint.RoutePattern.RawText == route);
        endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().ShouldContain(policy => policy.Policy == AuthorizationPolicies.RequireManager);
    }

    [Fact]
    public async Task AdminHost_EveryApiRouteCarriesExplicitAuthorizationDecision()
    {
        await using var factory = CreateAdminHostFactory();

        AssertAuthorizationInventory(AuthorizationInventory.GetRouteEndpoints(factory.Services));
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
        AuthorizationInventory.AssertApiRoutes(
            endpoints,
            "admin",
            "MapRomdAdminHttp",
            AllowedAnonymousApiRoutes);

        AuthorizationInventory.AnonymousNonApiRoutes(endpoints).ShouldBe(ExpectedAnonymousNonApiRoutes);
    }

    private static WebApplicationFactory<Romd.Admin.Host.Program> CreateAdminHostFactory() =>
        RomdHostTestFactory.CreateAdmin(
            Path.Combine(Path.GetTempPath(), $"romd-admin-auth-inventory-{Guid.NewGuid():N}"),
            TestJwtSecret);
}
