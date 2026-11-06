using System.Text.Json;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Hosting;

/// <summary>
///     Enforces the published-security half of the protected-by-default decision
///     (docs/decisions/api-contract-policy.md, "Protected By Default"): the admin OpenAPI
///     document carries the single OAuth2 (authorization-code + PKCE) security scheme with a
///     document-level default, anonymous operations override it with an explicit empty
///     requirement, and the live document always agrees with the runtime authorization metadata
///     on every operation.
/// </summary>
public sealed class AdminOpenApiSecurityTests
{
    private static readonly (string Path, string Method)[] ExpectedAnonymousOperations =
    [
        ("/api/assets/{hash}", "get"),
        ("/api/auth/account-link", "post"),
        ("/api/download-tickets/{token}", "get"),
        ("/artwork/{assetId}/{variantName}/{contentVersion}", "get"),
        ("/health", "get"),
        ("/media/{mediaId}", "get")
    ];

    [Fact]
    public void AdminOpenApi_CommittedSnapshotPublishesSecuritySchemeAndAnonymousOverrides()
    {
        using var document = JsonDocument.Parse(
            File.ReadAllText(RepositoryPaths.FindRepositoryFile("web/schemas/admin-v1.json")));

        OpenApiSecurityContract.AssertDocument(document.RootElement, ExpectedAnonymousOperations);
    }

    [Fact]
    public async Task AdminOpenApi_LiveDocumentSecurityAgreesWithEndpointAuthorization()
    {
        string dataDirectory = Path.Combine(
            Path.GetTempPath(),
            $"romd-admin-openapi-security-{Guid.NewGuid():N}");

        try
        {
            await using var factory = RomdHostTestFactory.Create<Romd.Admin.Host.Program>(
                dataDirectory,
                "Development",
                "admin-openapi-security-test-secret-at-least-32-characters");
            using var client = factory.CreateClient();
            using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/admin-v1.json"));

            OpenApiSecurityContract.AssertDocument(document.RootElement, ExpectedAnonymousOperations);
            OpenApiSecurityContract.AssertAgreesWithEndpoints(
                document.RootElement,
                AuthorizationInventory.GetRouteEndpoints(factory.Services));
        }
        finally
        {
            if (Directory.Exists(dataDirectory))
            {
                Directory.Delete(dataDirectory, recursive: true);
            }
        }
    }
}
