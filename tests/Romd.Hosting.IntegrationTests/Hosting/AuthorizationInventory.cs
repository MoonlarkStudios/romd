using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Romd.Hosting.IntegrationTests.Hosting;

/// <summary>
///     Route-metadata checks shared by the admin and consumer authorization inventories
///     (docs/decisions/api-contract-policy.md, "Protected By Default").
/// </summary>
internal static class AuthorizationInventory
{
    public static RouteEndpoint[] GetRouteEndpoints(IServiceProvider services) =>
        services.GetRequiredService<EndpointDataSource>()
            .Endpoints
            .OfType<RouteEndpoint>()
            .ToArray();

    public static string RoutePatternOf(RouteEndpoint endpoint) =>
        endpoint.RoutePattern.RawText ?? string.Empty;

    public static bool HasNamedPolicy(RouteEndpoint endpoint) =>
        endpoint.Metadata
            .GetOrderedMetadata<IAuthorizeData>()
            .Any(data => !string.IsNullOrEmpty(data.Policy));

    public static bool AllowsAnonymous(RouteEndpoint endpoint) =>
        endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null;

    /// <summary>
    ///     Asserts the /api half of the inventory: every route declares a named policy or an
    ///     explicit AllowAnonymous, sits inside the group-level default-deny fallback, and the
    ///     anonymous /api surface is exactly <paramref name="allowedAnonymousApiRoutes" />.
    /// </summary>
    public static void AssertApiRoutes(
        IReadOnlyList<RouteEndpoint> endpoints,
        string surface,
        string groupMappingMethod,
        IReadOnlyCollection<string> allowedAnonymousApiRoutes)
    {
        var apiEndpoints = endpoints
            .Where(endpoint => RoutePatternOf(endpoint).StartsWith("/api", StringComparison.Ordinal))
            .ToArray();

        apiEndpoints.ShouldNotBeEmpty();

        string[] undecidedRoutes = DistinctRoutes(apiEndpoints
            .Where(endpoint => !HasNamedPolicy(endpoint) && !AllowsAnonymous(endpoint)));
        undecidedRoutes.ShouldBeEmpty(
            $"Every {surface} /api route must declare a named authorization policy or an explicit " +
            $"AllowAnonymous. Undecided routes: {string.Join(", ", undecidedRoutes)}");

        string[] routesWithoutGroupFallback = DistinctRoutes(apiEndpoints
            .Where(endpoint => !HasGroupDefaultAuthorization(endpoint)));
        routesWithoutGroupFallback.ShouldBeEmpty(
            $"Every {surface} /api route must sit inside the default-deny authorization group " +
            $"({groupMappingMethod}). Missing fallback: {string.Join(", ", routesWithoutGroupFallback)}");

        DistinctRoutes(apiEndpoints.Where(AllowsAnonymous))
            .ShouldBe(allowedAnonymousApiRoutes.Order(StringComparer.Ordinal).ToArray());
    }

    public static string[] AnonymousNonApiRoutes(IEnumerable<RouteEndpoint> endpoints) =>
        DistinctRoutes(endpoints
            .Where(endpoint => !RoutePatternOf(endpoint).StartsWith("/api", StringComparison.Ordinal))
            .Where(AllowsAnonymous));

    /// <summary>The /api group-level default-deny fallback: no policy, roles or schemes of its own.</summary>
    public static bool IsGroupDefaultAuthorization(IAuthorizeData data) =>
        string.IsNullOrEmpty(data.Policy)
        && string.IsNullOrEmpty(data.Roles)
        && string.IsNullOrEmpty(data.AuthenticationSchemes);

    private static bool HasGroupDefaultAuthorization(RouteEndpoint endpoint) =>
        endpoint.Metadata
            .GetOrderedMetadata<IAuthorizeData>()
            .Any(IsGroupDefaultAuthorization);

    private static string[] DistinctRoutes(IEnumerable<RouteEndpoint> endpoints) =>
        endpoints
            .Select(RoutePatternOf)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
}
