using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Shouldly;

namespace Romd.Hosting.IntegrationTests.Hosting;

/// <summary>
///     Published-security checks shared by the admin and consumer OpenAPI documents
///     (docs/decisions/api-contract-policy.md, "Protected By Default"): one OAuth2
///     authorization-code scheme as the document default, anonymous operations overriding it with
///     `security: []`, and runtime authorization agreeing with the document on every operation.
/// </summary>
internal static class OpenApiSecurityContract
{
    private static readonly string[] KnownHttpMethods =
        ["delete", "get", "head", "options", "patch", "post", "put", "trace"];

    /// <summary>
    ///     The intersection of the scopes OpenIddict registers and the scopes the first-party clients
    ///     request (both SPAs' userManager.ts and the console's consumer_api_client.dart).
    /// </summary>
    private static readonly string[] ExpectedOAuth2Scopes = ["email", "offline_access", "profile", "roles"];

    public static void AssertDocument(
        JsonElement root,
        IReadOnlyCollection<(string Path, string Method)> expectedAnonymousOperations)
    {
        var securitySchemes = root
            .GetProperty("components")
            .GetProperty("securitySchemes");
        securitySchemes.EnumerateObject().Select(scheme => scheme.Name).ShouldBe(["oauth2"]);

        var oauth2 = securitySchemes.GetProperty("oauth2");
        oauth2.GetProperty("type").GetString().ShouldBe("oauth2");

        var flows = oauth2.GetProperty("flows");
        flows.EnumerateObject().Select(flow => flow.Name).ShouldBe(["authorizationCode"]);

        var authorizationCode = flows.GetProperty("authorizationCode");
        authorizationCode.GetProperty("authorizationUrl").GetString().ShouldBe("/connect/authorize");
        authorizationCode.GetProperty("tokenUrl").GetString().ShouldBe("/connect/token");
        authorizationCode.GetProperty("scopes").EnumerateObject()
            .Select(scope => scope.Name)
            .Order(StringComparer.Ordinal)
            .ShouldBe(ExpectedOAuth2Scopes);

        var documentSecurity = root.GetProperty("security");
        documentSecurity.GetArrayLength().ShouldBe(1);
        var requirement = documentSecurity[0];
        requirement.EnumerateObject().Select(entry => entry.Name).ShouldBe(["oauth2"]);
        requirement.GetProperty("oauth2").GetArrayLength().ShouldBe(0);

        var operationsWithSecurity = new List<(string Path, string Method)>();
        foreach (var (path, method, operation) in Operations(root))
        {
            if (!operation.TryGetProperty("security", out var security))
            {
                continue;
            }

            security.GetArrayLength().ShouldBe(
                0,
                $"Operation-level security on {method} {path} may only be the anonymous `security: []` override.");
            operationsWithSecurity.Add((path, method));
        }

        operationsWithSecurity
            .OrderBy(operation => operation.Path, StringComparer.Ordinal)
            .ThenBy(operation => operation.Method, StringComparer.Ordinal)
            .ShouldBe(expectedAnonymousOperations
                .OrderBy(operation => operation.Path, StringComparer.Ordinal)
                .ThenBy(operation => operation.Method, StringComparer.Ordinal));
    }

    public static void AssertAgreesWithEndpoints(JsonElement root, IReadOnlyList<RouteEndpoint> endpoints)
    {
        foreach (var (path, method, operation) in Operations(root))
        {
            var matches = endpoints
                .Where(endpoint =>
                    NormalizeRouteTemplate(endpoint.RoutePattern.RawText ?? string.Empty) == path
                    && (endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods
                        .Contains(method, StringComparer.OrdinalIgnoreCase) ?? false))
                .ToArray();
            matches.Length.ShouldBe(
                1,
                $"Expected exactly one endpoint for documented operation {method} {path}.");

            var endpoint = matches[0];
            bool allowsAnonymous = endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null;
            bool hasOperationSecurity = operation.TryGetProperty("security", out var security);

            if (allowsAnonymous)
            {
                hasOperationSecurity.ShouldBeTrue(
                    $"Anonymous operation {method} {path} must publish an explicit empty security override.");
                security.GetArrayLength().ShouldBe(
                    0,
                    $"Anonymous operation {method} {path} must publish `security: []`.");
            }
            else
            {
                endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().ShouldNotBeEmpty(
                    $"Documented operation {method} {path} carries neither authorization metadata nor AllowAnonymous.");
                hasOperationSecurity.ShouldBeFalse(
                    $"Protected operation {method} {path} must inherit the document-level security default.");
            }
        }
    }

    private static IEnumerable<(string Path, string Method, JsonElement Operation)> Operations(JsonElement root) =>
        root.GetProperty("paths")
            .EnumerateObject()
            .SelectMany(path => path.Value.EnumerateObject()
                .Where(candidate => KnownHttpMethods.Contains(candidate.Name, StringComparer.Ordinal))
                .Select(operation => (path.Name, operation.Name, operation.Value)));

    /// <summary>Reduces a route pattern to its OpenAPI template: strips parameter constraints,
    /// default values, optional markers, catch-all prefixes, and the trailing slash a group
    /// prefix leaves on empty-pattern routes.</summary>
    private static string NormalizeRouteTemplate(string pattern)
    {
        string template = Regex.Replace(pattern, @"\{\*{0,2}([^}:=?]+)[^}]*\}", "{$1}");

        return template.Length > 1 ? template.TrimEnd('/') : template;
    }
}
