using System.Text.Json;
using System.Text.RegularExpressions;
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

namespace Romd.Hosting.IntegrationTests.Hosting;

/// <summary>
///     Enforces the published-security half of the protected-by-default decision in
///     docs/decisions/admin-api-contract-policy.md: the admin OpenAPI document carries the single
///     OAuth2 (authorization-code + PKCE) security scheme with a document-level default, anonymous
///     operations override it with an explicit empty requirement, and the live document always
///     agrees with the runtime authorization metadata on every operation.
/// </summary>
public sealed class AdminOpenApiSecurityTests
{
    private static readonly string[] KnownHttpMethods =
        ["delete", "get", "head", "options", "patch", "post", "put", "trace"];

    /// <summary>
    ///     The intersection of the scopes OpenIddict registers and the scopes the admin SPA
    ///     requests (web/packages/romd-admin-app/src/auth/userManager.ts).
    /// </summary>
    private static readonly string[] ExpectedOAuth2Scopes = ["email", "offline_access", "profile", "roles"];

    private static readonly (string Path, string Method)[] ExpectedAnonymousOperations =
    [
        ("/api/assets/{hash}", "get"),
        ("/api/auth/account-link", "post"),
        ("/artwork/{assetId}/{variantName}/{contentVersion}", "get"),
        ("/health", "get"),
        ("/media/{mediaId}", "get")
    ];

    [Fact]
    public void AdminOpenApi_CommittedSnapshotPublishesSecuritySchemeAndAnonymousOverrides()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(FindRepositoryFile("web/schemas/admin-v1.json")));

        AssertSecurityContract(document.RootElement);
    }

    [Fact]
    public async Task AdminOpenApi_LiveDocumentSecurityAgreesWithEndpointAuthorization()
    {
        string dataDirectory = Path.Combine(
            Path.GetTempPath(),
            $"romd-admin-openapi-security-{Guid.NewGuid():N}");

        try
        {
            await using var factory = CreateAdminHostFactory(dataDirectory);
            using var client = factory.CreateClient();
            using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/admin-v1.json"));

            AssertSecurityContract(document.RootElement);

            var endpoints = factory.Services
                .GetRequiredService<EndpointDataSource>()
                .Endpoints
                .OfType<RouteEndpoint>()
                .ToArray();

            foreach (var path in document.RootElement.GetProperty("paths").EnumerateObject())
            {
                foreach (var operation in path.Value.EnumerateObject()
                             .Where(candidate => KnownHttpMethods.Contains(candidate.Name, StringComparer.Ordinal)))
                {
                    var matches = endpoints
                        .Where(endpoint =>
                            NormalizeRouteTemplate(endpoint.RoutePattern.RawText ?? string.Empty) == path.Name
                            && (endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods
                                .Contains(operation.Name, StringComparer.OrdinalIgnoreCase) ?? false))
                        .ToArray();
                    matches.Length.ShouldBe(
                        1,
                        $"Expected exactly one endpoint for documented operation {operation.Name} {path.Name}.");

                    var endpoint = matches[0];
                    bool allowsAnonymous = endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null;
                    bool hasOperationSecurity = operation.Value.TryGetProperty("security", out var security);

                    if (allowsAnonymous)
                    {
                        hasOperationSecurity.ShouldBeTrue(
                            $"Anonymous operation {operation.Name} {path.Name} must publish an explicit empty security override.");
                        security.GetArrayLength().ShouldBe(
                            0,
                            $"Anonymous operation {operation.Name} {path.Name} must publish `security: []`.");
                    }
                    else
                    {
                        endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().ShouldNotBeEmpty(
                            $"Documented operation {operation.Name} {path.Name} carries neither authorization metadata nor AllowAnonymous.");
                        hasOperationSecurity.ShouldBeFalse(
                            $"Protected operation {operation.Name} {path.Name} must inherit the document-level security default.");
                    }
                }
            }
        }
        finally
        {
            if (Directory.Exists(dataDirectory))
            {
                Directory.Delete(dataDirectory, recursive: true);
            }
        }
    }

    private static void AssertSecurityContract(JsonElement root)
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
        foreach (var path in root.GetProperty("paths").EnumerateObject())
        {
            foreach (var operation in path.Value.EnumerateObject()
                         .Where(candidate => KnownHttpMethods.Contains(candidate.Name, StringComparer.Ordinal)))
            {
                if (!operation.Value.TryGetProperty("security", out var security))
                {
                    continue;
                }

                security.GetArrayLength().ShouldBe(
                    0,
                    $"Operation-level security on {operation.Name} {path.Name} may only be the anonymous `security: []` override.");
                operationsWithSecurity.Add((path.Name, operation.Name));
            }
        }

        operationsWithSecurity
            .OrderBy(operation => operation.Path, StringComparer.Ordinal)
            .ThenBy(operation => operation.Method, StringComparer.Ordinal)
            .ShouldBe(ExpectedAnonymousOperations);
    }

    /// <summary>Reduces a route pattern to its OpenAPI template: strips parameter constraints,
    /// default values, optional markers, catch-all prefixes, and the trailing slash a group
    /// prefix leaves on empty-pattern routes.</summary>
    private static string NormalizeRouteTemplate(string pattern)
    {
        string template = Regex.Replace(pattern, @"\{\*{0,2}([^}:=?]+)[^}]*\}", "{$1}");

        return template.Length > 1 ? template.TrimEnd('/') : template;
    }

    private static WebApplicationFactory<Romd.Admin.Host.Program> CreateAdminHostFactory(string dataDirectory)
    {
        string webRoot = Path.Combine(dataDirectory, "wwwroot");
        Directory.CreateDirectory(webRoot);
        File.WriteAllText(Path.Combine(webRoot, "index.html"), "<!doctype html><html></html>");
        OpenIddictSigningKey.EnsureCreated(dataDirectory);

        return new WebApplicationFactory<Romd.Admin.Host.Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.UseContentRoot(AppContext.BaseDirectory);
                builder.UseWebRoot(webRoot);
                builder.UseSetting("hostBuilder:reloadConfigOnChange", "false");
                builder.UseSetting("Romd:DataDirectory", dataDirectory);
                builder.UseSetting(
                    "Romd:JwtSecret",
                    "admin-openapi-security-test-secret-at-least-32-characters");
                builder.ConfigureTestServices(services => services.RemoveAll<IHostedService>());
            });
    }

    private static string FindRepositoryFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            string path = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(path))
            {
                return path;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not find repository file '{relativePath}'.");
    }
}
