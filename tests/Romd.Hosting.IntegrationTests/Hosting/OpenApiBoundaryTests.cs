using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Romd.Infrastructure.Identity;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Hosting;

public sealed class OpenApiBoundaryTests
{
    private static readonly IReadOnlyDictionary<string, string[]> ExpectedConsumerOperations =
        new Dictionary<string, string[]>
        {
            ["/health"] = ["get"],
            ["/media/{mediaId}"] = ["get"],
            ["/artwork/{assetId}/{variantName}/{contentVersion}"] = ["get"],
            ["/delivery/content/{token}"] = ["get"],
            ["/delivery/bios/{token}"] = ["get"],
            ["/api/playback/config"] = ["get"],
            ["/api/systems"] = ["get"],
            ["/api/systems/{key}"] = ["get"],
            ["/api/companies"] = ["get"],
            ["/api/companies/{key}"] = ["get"],
            ["/api/regions"] = ["get"],
            ["/api/regions/{key}"] = ["get"],
            ["/api/languages"] = ["get"],
            ["/api/languages/{key}"] = ["get"],
            ["/api/rating-boards"] = ["get"],
            ["/api/rating-boards/{key}"] = ["get"],
            ["/api/rating-boards/{board}/ratings"] = ["get"],
            ["/api/rating-boards/{board}/ratings/{code}"] = ["get"],
            ["/api/catalog-snapshot"] = ["get"],
            ["/api/assets/{hash}"] = ["get"],
            ["/api/server/identity"] = ["get"],
            ["/api/account/password"] = ["post"],
            ["/api/account/settings"] = ["get", "put"],
            ["/api/me"] = ["get"],
            ["/api/me/activity/play-sessions"] = ["delete", "get"],
            ["/api/me/activity/play-sessions/{sessionId}"] = ["delete", "get", "put"],
            ["/api/me/activity/recently-played"] = ["get"],
            ["/api/me/library"] = ["get"],
            ["/api/me/library/systems"] = ["get"],
            ["/api/me/library/systems/{systemKey}"] = ["get"],
            ["/api/systems/{systemKey}/bios"] = ["get"],
            ["/api/catalog"] = ["get"],
            ["/api/titles/{titleId}"] = ["get"],
            ["/api/releases/{releaseId}/access"] = ["post"],
            ["/api/releases/{releaseId}/manifest"] = ["post"],
            ["/api/collections"] = ["get"],
            ["/api/collections/{collectionId}"] = ["get"],
            ["/api/collections/{collectionId}/titles"] = ["get"]
        };

    private static readonly string[] ExpectedAdminPaths =
    [
        "/health",
        "/media/{mediaId}",
        "/artwork/{assetId}/{variantName}/{contentVersion}",
        "/api/auth/me",
        "/api/auth/password",
        "/api/libraries",
        "/api/libraries/{libraryId}",
        "/api/libraries/{libraryId}/titles/{titleId}/releases",
        "/api/roms",
        "/api/roms/{romId}/download",
        "/api/jobs",
        "/api/diagnostics/operational",
        "/api/upload/rom",
        "/api/export/library",
        "/api/systems",
        "/api/systems/{systemKey}/bios",
        "/api/titles/{titleId}/details",
        "/api/collections",
        "/api/collections/{collectionId}/items",
        "/api/admin/backfill-bios",
        "/api/admin/backfill-bios-catalog"
    ];

    private static readonly string[] ExpectedAdminSchemas =
    [
        "LibraryDto",
        "LibraryConfigurationDto",
        "LibraryTitleReleaseDiagnosticsDto",
        "Rom",
        "Dat",
        "JobDto",
        "JobReference",
        "OperationalDiagnosticsDto",
        "ExportLibraryRequest",
        "UploadAccepted",
        "CollectionDetail",
        "TitleDetail",
        "UserDto"
    ];

    private static readonly string[] ForbiddenConsumerPathPrefixes =
    [
        "/api/admin",
        "/api/auth",
        "/api/dats",
        "/api/diagnostics",
        "/api/enrichment",
        "/api/export",
        "/api/jobs",
        "/api/libraries",
        "/api/roms",
        "/api/taxonomy",
        "/api/upload",
        "/api/users"
    ];

    private static readonly string[] ForbiddenConsumerRawStorageRouteTokens =
    [
        "{fileId}",
        "{storageId}",
        "{storageKey}",
        "{sha256}",
        "{hash}",
        "{casKey}",
        "{blobId}"
    ];

    private static readonly string[] ForbiddenConsumerSchemas =
    [
        "AddAliasRequest",
        "CollectionDetail",
        "CollectionSummary",
        "CreateLibraryRequest",
        "Dat",
        "DatGame",
        "DatRom",
        "ExportLibraryRequest",
        "JobDto",
        "JobReference",
        "LibraryConfigurationDto",
        "LibraryDto",
        "OperationalDiagnosticsDto",
        "Rom",
        "RomMatch",
        "Sqid",
        "TitleDetail",
        "UpdateLibraryRequest",
        "UploadAccepted",
        "UserDto"
    ];

    private static readonly string[] ForbiddenConsumerVocabulary =
    [
        "LaunchOption",
        "launch-option",
        "launch_option",
        "ImmediateDownload",
        "InstallRequired",
        "SupportsImmediateDownload",
        "SupportsRangeRequests"
    ];

    private static readonly string[] ExpectedContentGrantRedemptionResponses =
    [
        "200",
        "401",
        "404"
    ];

    [Fact]
    public void ConsumerOpenApi_ExcludesManagementPathsAndSchemas()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(FindRepositoryFile("web/schemas/consumer-v1.json")));
        var schemas = document.RootElement
            .GetProperty("components")
            .GetProperty("schemas")
            .EnumerateObject()
            .Select(schema => schema.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var operations = document.RootElement
            .GetProperty("paths")
            .EnumerateObject()
            .ToDictionary(
                path => path.Name,
                path => path.Value.EnumerateObject().Select(operation => operation.Name).Order(StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal);
        var paths = operations.Keys.Order(StringComparer.Ordinal).ToArray();

        paths.ShouldBe(ExpectedConsumerOperations.Keys.Order(StringComparer.Ordinal).ToArray());
        foreach ((string path, string[] expectedMethods) in ExpectedConsumerOperations)
        {
            operations[path].ShouldBe(expectedMethods);
        }

        paths.ShouldNotContain(path =>
            ForbiddenConsumerPathPrefixes.Any(prefix => path.StartsWith(prefix, StringComparison.Ordinal)));
        // Public reference artwork addresses a dedicated display-asset table, never content storage.
        paths.Where(path => path != "/api/assets/{hash}").ShouldNotContain(path =>
            ForbiddenConsumerRawStorageRouteTokens.Any(token =>
                path.Contains(token, StringComparison.OrdinalIgnoreCase)));
        schemas.ShouldNotContain(schema => ForbiddenConsumerSchemas.Contains(schema, StringComparer.Ordinal));

        string json = document.RootElement.GetRawText();
        foreach (string term in ForbiddenConsumerVocabulary)
        {
            json.ShouldNotContain(term, Case.Insensitive);
        }
    }

    [Fact]
    public void ConsumerOpenApi_ContentGrantRedemptionDocumentsFullObjectOnly()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(FindRepositoryFile("web/schemas/consumer-v1.json")));
        var operation = document.RootElement
            .GetProperty("paths")
            .GetProperty("/delivery/content/{token}")
            .GetProperty("get");
        var responses = operation
            .GetProperty("responses")
            .EnumerateObject()
            .ToDictionary(response => response.Name, response => response.Value, StringComparer.Ordinal);

        responses.Keys.Order(StringComparer.Ordinal).ShouldBe(ExpectedContentGrantRedemptionResponses);
        responses.ShouldNotContainKey("206");
        responses.ShouldNotContainKey("501");

        string json = operation.GetRawText();
        json.ShouldNotContain("Accept-Ranges", Case.Insensitive);
        json.ShouldNotContain("Content-Range", Case.Insensitive);
        json.ShouldNotContain("Range", Case.Insensitive);
    }

    [Fact]
    public void ConsumerOpenApi_ReleaseManifestDocumentsFullDownloadMaterializationInputs()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(FindRepositoryFile("web/schemas/consumer-v1.json")));
        var schemas = document.RootElement
            .GetProperty("components")
            .GetProperty("schemas");
        var itemSchema = schemas.GetProperty("ConsumerReleaseManifestItemDto");
        var itemProperties = itemSchema
            .GetProperty("properties")
            .EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal);
        var grantSchema = schemas.GetProperty("ContentGrantDto");
        var grantProperties = grantSchema
            .GetProperty("properties")
            .EnumerateObject()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        itemProperties.Keys
            .Order(StringComparer.Ordinal)
            .ShouldBe(["contentGrant", "isAvailable", "relativePath", "role", "sha256", "sizeBytes"]);
        itemSchema
            .GetProperty("required")
            .EnumerateArray()
            .Select(property => property.GetString())
            .Order(StringComparer.Ordinal)
            .ShouldBe(["isAvailable", "relativePath", "role", "sizeBytes"]);
        grantProperties.ShouldBe(["downloadUrl", "expiresAt"]);

        string json = itemSchema.GetRawText();
        json.ShouldNotContain("fileId", Case.Insensitive);
        json.ShouldNotContain("storageKey", Case.Insensitive);
        json.ShouldNotContain("casKey", Case.Insensitive);
        json.ShouldNotContain("blobId", Case.Insensitive);
    }

    [Fact]
    public void ConsumerOpenApi_PlaybackConfigSnapshotHasExactRequiredNullableContract()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(FindRepositoryFile("web/schemas/consumer-v1.json")));
        var operation = document.RootElement
            .GetProperty("paths")
            .GetProperty("/api/playback/config")
            .GetProperty("get");
        var schema = document.RootElement
            .GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("ConsumerPlaybackConfigDto");

        operation.GetProperty("operationId").GetString().ShouldBe("GetConsumerPlaybackConfig");
        operation.TryGetProperty("security", out _).ShouldBeFalse();
        operation.GetProperty("responses").EnumerateObject().Select(response => response.Name)
            .ShouldBe(["200"]);
        schema.GetProperty("properties").EnumerateObject().Select(property => property.Name)
            .ShouldBe(["playerOrigin"]);
        schema.GetProperty("required").EnumerateArray().Select(property => property.GetString())
            .ShouldBe(["playerOrigin"]);
        schema.GetProperty("properties").GetProperty("playerOrigin")
            .GetProperty("type").EnumerateArray().Select(type => type.GetString())
            .ShouldBe(["null", "string"]);
    }

    [Fact]
    public async Task ConsumerServerIdentity_LiveOpenApiDocumentsAnonymousExactContract()
    {
        string dataDirectory = Path.Combine(
            Path.GetTempPath(),
            $"romd-consumer-identity-openapi-{Guid.NewGuid():N}");
        string webRoot = Path.Combine(dataDirectory, "wwwroot");

        try
        {
            Directory.CreateDirectory(webRoot);
            File.WriteAllText(Path.Combine(webRoot, "index.html"), "<!doctype html><html></html>");
            OpenIddictSigningKey.EnsureCreated(dataDirectory);

            await using var factory = new WebApplicationFactory<Romd.Consumer.Host.Program>()
                .WithWebHostBuilder(builder =>
                {
                    builder.UseEnvironment("Development");
                    builder.UseContentRoot(AppContext.BaseDirectory);
                    builder.UseWebRoot(webRoot);
                    builder.UseSetting("hostBuilder:reloadConfigOnChange", "false");
                    builder.UseSetting("Romd:DataDirectory", dataDirectory);
                    builder.UseSetting(
                        "Romd:JwtSecret",
                        "consumer-identity-openapi-test-secret-at-least-32-characters");
                    builder.ConfigureTestServices(services =>
                    {
                        services.RemoveAll<IHostedService>();
                        services.AddHostedService<ServerInstanceIdentityInitializer>();
                    });
                });
            using var client = factory.CreateClient();
            using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/consumer-v1.json"));

            var operation = document.RootElement
                .GetProperty("paths")
                .GetProperty("/api/server/identity")
                .GetProperty("get");
            var schema = document.RootElement
                .GetProperty("components")
                .GetProperty("schemas")
                .GetProperty("ConsumerServerIdentityDto");
            var accessOperation = document.RootElement
                .GetProperty("paths")
                .GetProperty("/api/releases/{releaseId}/access")
                .GetProperty("post");
            var accessSchema = document.RootElement
                .GetProperty("components")
                .GetProperty("schemas")
                .GetProperty("ConsumerReleaseAccessDto");
            var manifestSchema = document.RootElement
                .GetProperty("components")
                .GetProperty("schemas")
                .GetProperty("ConsumerReleaseManifestDto");

            operation.GetProperty("operationId").GetString().ShouldBe("GetConsumerServerIdentity");
            operation.TryGetProperty("security", out _).ShouldBeFalse();
            operation.GetProperty("responses").EnumerateObject().Select(response => response.Name)
                .ShouldBe(["200"]);
            schema.GetProperty("properties").EnumerateObject().Select(property => property.Name)
                .ShouldBe(["instanceId"]);
            schema.GetProperty("required").EnumerateArray().Select(property => property.GetString())
                .ShouldBe(["instanceId"]);

            accessOperation.GetProperty("operationId").GetString().ShouldBe("GetConsumerReleaseAccess");
            accessOperation.TryGetProperty("requestBody", out _).ShouldBeFalse();
            accessOperation.GetProperty("responses").EnumerateObject().Select(response => response.Name)
                .Order(StringComparer.Ordinal)
                .ShouldBe(["200", "400", "401", "409"]);
            accessSchema.GetProperty("properties").EnumerateObject().Select(property => property.Name)
                .Order(StringComparer.Ordinal)
                .ShouldBe(["allowed", "releaseId", "serverInstanceId", "titleId"]);
            accessSchema.GetProperty("required").EnumerateArray().Select(property => property.GetString())
                .Order(StringComparer.Ordinal)
                .ShouldBe(["allowed", "releaseId", "serverInstanceId"]);
            manifestSchema.GetProperty("required").EnumerateArray().Select(property => property.GetString())
                .ShouldContain("serverInstanceId");

            string accessJson = accessOperation.GetRawText() + accessSchema.GetRawText();
            accessJson.ShouldNotContain("requestBody", Case.Insensitive);
            accessJson.ShouldNotContain("oneOf", Case.Insensitive);
            accessJson.ShouldNotContain("batch", Case.Insensitive);
            accessJson.ShouldNotContain("AccessRevision", Case.Insensitive);
            accessJson.ShouldNotContain("SecurityRevision", Case.Insensitive);
            accessJson.ShouldNotContain("authority", Case.Insensitive);
        }
        finally
        {
            if (Directory.Exists(dataDirectory))
            {
                Directory.Delete(dataDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ConsumerPlaybackConfig_LiveOpenApiDocumentsAnonymousRequiredNullableContract()
    {
        string dataDirectory = Path.Combine(
            Path.GetTempPath(),
            $"romd-consumer-playback-openapi-{Guid.NewGuid():N}");
        string webRoot = Path.Combine(dataDirectory, "wwwroot");

        try
        {
            Directory.CreateDirectory(webRoot);
            File.WriteAllText(Path.Combine(webRoot, "index.html"), "<!doctype html><html></html>");
            OpenIddictSigningKey.EnsureCreated(dataDirectory);

            await using var factory = new WebApplicationFactory<Romd.Consumer.Host.Program>()
                .WithWebHostBuilder(builder =>
                {
                    builder.UseEnvironment("Development");
                    builder.UseContentRoot(AppContext.BaseDirectory);
                    builder.UseWebRoot(webRoot);
                    builder.UseSetting("hostBuilder:reloadConfigOnChange", "false");
                    builder.UseSetting("Romd:DataDirectory", dataDirectory);
                    builder.UseSetting(
                        "Romd:JwtSecret",
                        "consumer-playback-openapi-test-secret-at-least-32-characters");
                    builder.ConfigureTestServices(services =>
                    {
                        services.RemoveAll<IHostedService>();
                        services.AddHostedService<ServerInstanceIdentityInitializer>();
                    });
                });
            using var client = factory.CreateClient();
            using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/consumer-v1.json"));

            var operation = document.RootElement
                .GetProperty("paths")
                .GetProperty("/api/playback/config")
                .GetProperty("get");
            var schema = document.RootElement
                .GetProperty("components")
                .GetProperty("schemas")
                .GetProperty("ConsumerPlaybackConfigDto");
            var playerOrigin = schema
                .GetProperty("properties")
                .GetProperty("playerOrigin");

            operation.GetProperty("operationId").GetString().ShouldBe("GetConsumerPlaybackConfig");
            operation.TryGetProperty("security", out _).ShouldBeFalse();
            operation.GetProperty("responses").EnumerateObject().Select(response => response.Name)
                .ShouldBe(["200"]);
            schema.GetProperty("properties").EnumerateObject().Select(property => property.Name)
                .ShouldBe(["playerOrigin"]);
            schema.GetProperty("required").EnumerateArray().Select(property => property.GetString())
                .ShouldBe(["playerOrigin"]);
            playerOrigin.GetProperty("type").EnumerateArray().Select(type => type.GetString())
                .ShouldBe(["null", "string"]);
        }
        finally
        {
            if (Directory.Exists(dataDirectory))
            {
                Directory.Delete(dataDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void AdminOpenApi_IncludesManagementPathsAndSchemas()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(FindRepositoryFile("web/schemas/admin-v1.json")));
        var paths = document.RootElement
            .GetProperty("paths")
            .EnumerateObject()
            .Select(path => path.Name)
            .ToArray();
        var schemas = document.RootElement
            .GetProperty("components")
            .GetProperty("schemas")
            .EnumerateObject()
            .Select(schema => schema.Name)
            .ToArray();

        foreach (string expectedPath in ExpectedAdminPaths)
        {
            paths.ShouldContain(expectedPath);
        }

        foreach (string expectedSchema in ExpectedAdminSchemas)
        {
            schemas.ShouldContain(expectedSchema);
        }

        paths.ShouldNotContain("/api/me");
        paths.ShouldNotContain(path => path.StartsWith("/api/me/", StringComparison.Ordinal));
        paths.ShouldNotContain("/api/playback/config");
        schemas.ShouldNotContain("ConsumerPlaybackConfigDto");
    }

    [Fact]
    public void AdminOpenApi_LibraryPolicyEnums_AreClosedNamedStringSchemas()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(FindRepositoryFile("web/schemas/admin-v1.json")));
        var schemas = document.RootElement
            .GetProperty("components")
            .GetProperty("schemas");

        AssertEnumSchema(schemas, "LibraryTitleSelectionMode", ["Rules", "IncludeOnly"]);
        AssertEnumSchema(schemas, "RatingBasisSelection", ["Strictest", "Preferred"]);
        AssertEnumSchema(schemas, "UnknownMetadataPolicy", ["Allow", "Hide", "NeedsReview"]);
        AssertEnumSchema(
            schemas,
            "RatingBoard",
            ["Esrb", "Pegi", "Cero", "Usk", "Grac", "ClassInd", "Acb"]);

        var configurationProperties = schemas
            .GetProperty("LibraryConfigurationDto")
            .GetProperty("properties");
        configurationProperties.GetProperty("titleSelectionMode").GetProperty("$ref").GetString()
            .ShouldBe("#/components/schemas/LibraryTitleSelectionMode");
        configurationProperties.GetProperty("unknownGenrePolicy").GetProperty("$ref").GetString()
            .ShouldBe("#/components/schemas/UnknownMetadataPolicy");

        var ratingProperties = schemas
            .GetProperty("ContentRatingPolicyDto")
            .GetProperty("properties");
        ratingProperties.GetProperty("basisSelection").GetProperty("$ref").GetString()
            .ShouldBe("#/components/schemas/RatingBasisSelection");
        ratingProperties.GetProperty("boardPreference").GetProperty("items")
            .GetProperty("$ref").GetString().ShouldBe("#/components/schemas/RatingBoard");
        ratingProperties.GetProperty("unknownRatingPolicy").GetProperty("$ref").GetString()
            .ShouldBe("#/components/schemas/UnknownMetadataPolicy");
    }

    private static void AssertEnumSchema(
        JsonElement schemas,
        string schemaName,
        string[] expectedNames)
    {
        var schema = schemas.GetProperty(schemaName);
        schema.GetProperty("enum")
            .EnumerateArray()
            .Select(value => value.GetString())
            .ShouldBe(expectedNames);
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
