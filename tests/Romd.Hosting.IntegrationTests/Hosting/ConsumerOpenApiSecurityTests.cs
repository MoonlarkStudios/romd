using System.Text.Json;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Hosting;

/// <summary>
///     Enforces the published-security half of the protected-by-default decision
///     (docs/decisions/api-contract-policy.md, "Protected By Default") for the consumer
///     document: the same OAuth2 scheme as admin is the document default, anonymous operations
///     publish `security: []`, and the live document agrees with the consumer host's runtime
///     authorization on every operation.
/// </summary>
public sealed class ConsumerOpenApiSecurityTests
{
    private static readonly (string Path, string Method)[] ExpectedAnonymousOperations =
    [
        ("/api/assets/{hash}", "get"),
        ("/api/playback/config", "get"),
        ("/api/server/identity", "get"),
        ("/artwork/{assetId}/{variantName}/{contentVersion}", "get"),
        ("/delivery/bios/{token}", "get"),
        ("/delivery/content/{token}", "get"),
        ("/delivery/derived-descriptor/{token}", "get"),
        ("/delivery/original-layout/{token}", "get"),
        ("/delivery/disc-set/{token}", "get"),
        ("/health", "get"),
        ("/media/{mediaId}", "get")
    ];

    [Fact]
    public void ConsumerOpenApi_CommittedSnapshotPublishesSecuritySchemeAndAnonymousOverrides()
    {
        using var document = JsonDocument.Parse(
            File.ReadAllText(RepositoryPaths.FindRepositoryFile("web/schemas/consumer-v1.json")));

        OpenApiSecurityContract.AssertDocument(document.RootElement, ExpectedAnonymousOperations);
    }

    [Fact]
    public async Task ConsumerOpenApi_LiveDocumentSecurityAgreesWithEndpointAuthorization()
    {
        string dataDirectory = Path.Combine(
            Path.GetTempPath(),
            $"romd-consumer-openapi-security-{Guid.NewGuid():N}");

        try
        {
            await using var factory = RomdHostTestFactory.Create<Romd.Consumer.Host.Program>(
                dataDirectory,
                "Development",
                "consumer-openapi-security-test-secret-at-least-32-characters");
            using var client = factory.CreateClient();
            using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/consumer-v1.json"));

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
