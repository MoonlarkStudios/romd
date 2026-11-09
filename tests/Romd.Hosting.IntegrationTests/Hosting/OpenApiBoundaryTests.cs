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
using static Romd.Hosting.IntegrationTests.Infrastructure.OpenApiTestDocuments;
using static Romd.Hosting.IntegrationTests.Infrastructure.RepositoryPaths;

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
            ["/delivery/derived-descriptor/{token}"] = ["get"],
            ["/delivery/bios/{token}"] = ["get"],
            ["/delivery/original-layout/{token}"] = ["get"],
            ["/api/playback/config"] = ["get"],
            ["/api/systems"] = ["get"],
            ["/api/systems/{systemKey}"] = ["get"],
            ["/api/companies"] = ["get"],
            ["/api/companies/{companyKey}"] = ["get"],
            ["/api/regions"] = ["get"],
            ["/api/regions/{regionKey}"] = ["get"],
            ["/api/languages"] = ["get"],
            ["/api/languages/{languageKey}"] = ["get"],
            ["/api/rating-boards"] = ["get"],
            ["/api/rating-boards/{board}"] = ["get"],
            ["/api/rating-boards/{board}/ratings"] = ["get"],
            ["/api/rating-boards/{board}/ratings/{ratingCode}"] = ["get"],
            ["/api/catalog-snapshot"] = ["get"],
            ["/api/catalog-snapshot/systems"] = ["get"],
            ["/api/catalog-snapshot/companies"] = ["get"],
            ["/api/catalog-snapshot/regions"] = ["get"],
            ["/api/catalog-snapshot/languages"] = ["get"],
            ["/api/catalog-snapshot/rating-boards"] = ["get"],
            ["/api/catalog-snapshot/ratings"] = ["get"],

            ["/api/assets/{hash}"] = ["get"],
            ["/api/server/identity"] = ["get"],
            ["/api/me/connections"] = ["get", "post"],
            ["/api/me/connections/{connectionId}"] = ["delete", "patch"],
            ["/api/me/connections/{connectionId}/rotate"] = ["post"],
            ["/api/me/password"] = ["put"],
            ["/api/me/settings"] = ["get", "patch"],
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
            ["/api/titles/{titleId}/media"] = ["get"],
            ["/api/titles/{titleId}/releases"] = ["get"],
            ["/api/releases/{releaseId}/access"] = ["get"],
            ["/api/releases/{releaseId}/manifest"] = ["post"],
            ["/api/releases/{releaseId}/cd-archive"] = ["get"],
            ["/api/releases/{releaseId}/cd-archive/cue"] = ["get"],
            ["/api/disc-sets/{discSetId}/manifest"] = ["post"],
            ["/api/disc-sets/{discSetId}/status"] = ["get"],
            ["/api/disc-sets/{discSetId}/selection-manifest"] = ["post"],
            ["/api/disc-sets/{discSetId}/access/preview"] = ["post"],
            ["/api/disc-sets"] = ["get"],
            ["/api/collections"] = ["get"],
            ["/api/collections/{collectionId}"] = ["get"],
            ["/api/collections/{collectionId}/titles"] = ["get"]
        };

    private static readonly string[] ExpectedAdminPaths =
    [
        "/health",
        "/media/{mediaId}",
        "/artwork/{assetId}/{variantName}/{contentVersion}",
        "/api/me",
        "/api/me/password",
        "/api/libraries",
        "/api/libraries/{libraryId}",
        "/api/libraries/{libraryId}/disc-set-options",
        "/api/libraries/{libraryId}/titles/{titleId}/releases",
        "/api/roms",
        "/api/roms/{romId}/download",
        "/api/jobs",
        "/api/server/diagnostics",
        "/api/upload/rom",
        "/api/export/library",
        "/api/systems",
        "/api/systems/{systemKey}/bios",
        "/api/titles/{titleId}",
        "/api/collections",
        "/api/collections/{collectionId}/items",
        "/api/bios/backfill",
        "/api/bios/backfill-catalog"
    ];

    private static readonly string[] ExpectedAdminSchemas =
    [
        "Library",
        "LibraryConfiguration",
        "LibraryTitleReleaseDiagnostics",
        "Rom",
        "Dat",
        "Job",
        "JobReference",
        "OperationalDiagnostics",
        "ExportLibraryRequest",
        "CollectionDetail",
        "TitleDetail",
        "User"
    ];

    private static readonly string[] ForbiddenConsumerPathPrefixes =
    [
        "/api/admin",
        "/api/auth",
        "/api/dats",
        "/api/server/diagnostics",
        "/api/server/health",
        "/api/server/storage",
        "/api/artwork-providers",
        "/api/metadata-providers",
        "/api/bios",
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
        "Job",
        "JobReference",
        "LibraryConfiguration",
        "Library",
        "OperationalDiagnostics",
        "Rom",
        "RomMatch",
        "Sqid",
        "TitleDetail",
        "UpdateLibraryRequest",
        "UploadAccepted",
        "User"
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
        "404",
        "503"
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
        responses["503"].GetProperty("content").GetProperty("application/problem+json")
            .GetProperty("schema").GetProperty("$ref").GetString().ShouldBe("#/components/schemas/ProblemDetails");

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
        var itemSchema = schemas.GetProperty("ConsumerReleaseManifestItem");
        var itemProperties = itemSchema
            .GetProperty("properties")
            .EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal);
        var grantSchema = schemas.GetProperty("ContentGrant");
        var grantProperties = grantSchema
            .GetProperty("properties")
            .EnumerateObject()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        itemProperties.Keys
            .Order(StringComparer.Ordinal)
            .ShouldBe(["contentGrant", "formats", "isAvailable", "provenance", "relativePath", "role", "sha256", "sizeBytes"]);
        itemSchema
            .GetProperty("required")
            .EnumerateArray()
            .Select(property => property.GetString())
            .Order(StringComparer.Ordinal)
            .ShouldBe(["formats", "isAvailable", "provenance", "relativePath", "role", "sizeBytes"]);
        itemProperties["provenance"].GetProperty("$ref").GetString()
            .ShouldBe("#/components/schemas/ConsumerReleaseFileProvenance");
        schemas.GetProperty("ConsumerDiscSetFile").GetProperty("properties")
            .GetProperty("provenance").GetProperty("$ref").GetString()
            .ShouldBe("#/components/schemas/ConsumerReleaseFileProvenance");
        schemas.GetProperty("ConsumerDiscSetFile").GetProperty("required").EnumerateArray()
            .Select(property => property.GetString()).ShouldContain("provenance");
        schemas.GetProperty("ConsumerReleaseFileProvenance").GetProperty("enum").EnumerateArray()
            .Select(value => value.GetString()).ShouldBe(["Original", "Derived"]);
        schemas.GetProperty("ConsumerReleaseManifest").GetProperty("required").EnumerateArray()
            .Select(property => property.GetString()).ShouldContain("isDeliveryComplete");
        grantProperties.ShouldBe(["downloadUrl", "expiresAt"]);

        string json = itemSchema.GetRawText();
        json.ShouldNotContain("fileId", Case.Insensitive);
        json.ShouldNotContain("storageKey", Case.Insensitive);
        json.ShouldNotContain("casKey", Case.Insensitive);
        json.ShouldNotContain("blobId", Case.Insensitive);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeliveryFormats_LiveOpenApiHasClosedLowercaseMetadataAndAdminQueries(bool consumer)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var document = consumer
            ? await ReadLiveOpenApiAsync<Romd.Consumer.Host.Program>("consumer-v1", timeout.Token)
            : await ReadLiveOpenApiAsync<Romd.Admin.Host.Program>("admin-v1", timeout.Token);
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        var schema = schemas.GetProperty(consumer ? "ConsumerReleaseManifestFormat" : "RomDeliveryFormat");
        var properties = schema.GetProperty("properties");

        properties.GetProperty("format").GetProperty("type").GetString().ShouldBe("string");
        properties.GetProperty("format").GetProperty("enum").EnumerateArray()
            .Select(value => value.GetString()).ShouldBe(["original", "rvz", "chd"]);
        properties.GetProperty("state").GetProperty("type").GetString().ShouldBe("string");
        properties.GetProperty("state").GetProperty("enum").EnumerateArray()
            .Select(value => value.GetString()).ShouldBe(["available", "preparing", "unsupported"]);
        schema.GetProperty("required").EnumerateArray().Select(value => value.GetString())
            .Order(StringComparer.Ordinal).ShouldBe(consumer
                ? ["format", "label", "profileVersion", "relativePath", "runtimeApplicability", "state"]
                : ["format", "label", "profileVersion", "runtimeApplicability", "state"]);
        properties.GetProperty("label").GetProperty("type").GetString().ShouldBe("string");
        properties.GetProperty("label").TryGetProperty("enum", out _).ShouldBeFalse();
        properties.GetProperty("fileExtension").TryGetProperty("enum", out _).ShouldBeFalse();
        var sizeVariants = properties.GetProperty("sizeBytes").GetProperty("oneOf").EnumerateArray().ToArray();
        sizeVariants.Length.ShouldBe(2);
        sizeVariants.Single(value => value.TryGetProperty("type", out _))
            .GetProperty("type").GetString().ShouldBe("null");
        sizeVariants.Single(value => value.TryGetProperty("$ref", out _))
            .GetProperty("$ref").GetString().ShouldBe("#/components/schemas/ByteCount");
        schemas.GetProperty("ByteCount").GetProperty("type").GetString().ShouldBe("string");
        schemas.GetProperty("ByteCount").GetProperty("pattern").GetString()
            .ShouldBe("^(?:0|[1-9]\\d*)$");

        if (consumer)
        {
            schemas.GetProperty("ConsumerReleaseManifestItem").GetProperty("properties")
                .GetProperty("formats").GetProperty("items").GetProperty("$ref").GetString()
                .ShouldBe("#/components/schemas/ConsumerReleaseManifestFormat");
            properties.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal)
                .ShouldBe(["contentGrant", "fileExtension", "filename", "format", "label", "md5", "profileVersion",
                    "relativePath", "runtime", "runtimeApplicability", "sha1", "sha256", "sizeBytes", "state"]);
            var runtimeVariants = properties.GetProperty("runtime").GetProperty("oneOf").EnumerateArray().ToArray();
            runtimeVariants.Length.ShouldBe(2);
            runtimeVariants.Single(value => value.TryGetProperty("type", out _))
                .GetProperty("type").GetString().ShouldBe("null");
            runtimeVariants.Single(value => value.TryGetProperty("$ref", out _))
                .GetProperty("$ref").GetString().ShouldBe("#/components/schemas/ConsumerReleaseRuntime");
            return;
        }

        var parameter = document.RootElement.GetProperty("paths").GetProperty("/api/roms/{romId}/download")
            .GetProperty("get").GetProperty("parameters").EnumerateArray()
            .Single(value => value.GetProperty("name").GetString() == "format");
        parameter.GetProperty("in").GetString().ShouldBe("query");
        parameter.GetProperty("schema").GetProperty("type").GetString().ShouldBe("string");
        parameter.GetProperty("schema").GetProperty("enum").EnumerateArray()
            .Select(value => value.GetString()).ShouldBe(["original", "rvz", "chd"]);
        (!parameter.TryGetProperty("required", out var required) || !required.GetBoolean()).ShouldBeTrue();

        AssertEnumSchema(schemas, "LibraryTitleSelectionMode", ["Rules", "IncludeOnly"]);
    }

    [Fact]
    public async Task ArchiveFormats_LiveAdminOpenApiHasClosedLowercaseSelectionsAndTicketOperations()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var document = await ReadLiveOpenApiAsync<Romd.Admin.Host.Program>("admin-v1", timeout.Token);
        var paths = document.RootElement.GetProperty("paths");
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        // One ticket collection: the body names the target and its format, published by the request type.
        var create = paths.GetProperty("/api/download-tickets").GetProperty("post");
        create.GetProperty("operationId").GetString().ShouldBe("CreateDownloadTicket");
        (create.TryGetProperty("parameters", out var parameters) && parameters.EnumerateArray()
            .Any(value => value.GetProperty("name").GetString() == "format")).ShouldBeFalse();
        var ticket = schemas.GetProperty("CreateDownloadTicketRequest");
        ticket.GetProperty("required").EnumerateArray().Select(value => value.GetString()).Order().ShouldBe(["id", "target"]);
        // The target is a body enum, published as the other body enums are.
        ticket.GetProperty("properties").GetProperty("target").GetProperty("$ref").GetString()
            .ShouldBe("#/components/schemas/DownloadTicketTarget");
        AssertEnumSchema(schemas, "DownloadTicketTarget", ["Title", "Release", "Rom", "ExportJob"]);
        ticket.GetProperty("properties").GetProperty("format").GetProperty("enum").EnumerateArray()
            .Select(value => value.GetString()).ShouldBe(["original", "rvz", "chd"]);

        var selection = schemas.GetProperty("ExportLibraryRequest").GetProperty("properties").GetProperty("format");
        selection.GetProperty("enum").EnumerateArray().Select(value => value.GetString())
            .ShouldBe(["original", "rvz", "chd"]);
        string exportSchemaReference = schemas.GetProperty("Job").GetProperty("discriminator")
            .GetProperty("mapping").GetProperty("export").GetString()!;
        exportSchemaReference.ShouldStartWith("#/components/schemas/");
        schemas.GetProperty(exportSchemaReference["#/components/schemas/".Length..])
            .GetProperty("properties").GetProperty("format")
            .GetProperty("enum").EnumerateArray().Select(value => value.GetString())
            .ShouldBe(["original", "rvz", "chd"]);
        paths.TryGetProperty("/api/export/{jobId}/download-ticket", out _).ShouldBeFalse();
        paths.TryGetProperty("/api/export/{jobId}/download", out _).ShouldBeFalse();

        // Titles, releases and libraries list the formats they offer as the same wire values, labelled by the server.
        var offered = schemas.GetProperty("ArchiveFormat");
        offered.GetProperty("required").EnumerateArray().Select(value => value.GetString()).Order().ShouldBe(["format", "label"]);
        offered.GetProperty("properties").GetProperty("format").GetProperty("enum").EnumerateArray()
            .Select(value => value.GetString()).ShouldBe(["original", "rvz", "chd"]);
        offered.GetProperty("properties").GetProperty("label").GetProperty("type").GetString().ShouldBe("string");
        offered.GetProperty("properties").GetProperty("label").TryGetProperty("enum", out _).ShouldBeFalse();
        foreach (var (owner, property) in new[] { ("TitleDetail", "archiveFormats"), ("TitleRelease", "archiveFormats"), ("Library", "exportFormats") })
            schemas.GetProperty(owner).GetProperty("properties").GetProperty(property).GetProperty("items")
                .GetProperty("$ref").GetString().ShouldBe("#/components/schemas/ArchiveFormat", owner);
    }

    [Theory]
    [InlineData("/api/download-tickets")]
    [InlineData("/api/titles/{titleId}/artwork-candidates/{candidateId}/import")]
    [InlineData("/api/titles/{titleId}/media")]
    public async Task Create_LiveAdminOpenApiDocumentsTheCreatedMembersLocation(string path)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var document = await ReadLiveOpenApiAsync<Romd.Admin.Host.Program>("admin-v1", timeout.Token);

        var location = document.RootElement.GetProperty("paths").GetProperty(path).GetProperty("post")
            .GetProperty("responses").GetProperty("201").GetProperty("headers").GetProperty("Location");

        location.GetProperty("required").GetBoolean().ShouldBeTrue();
        location.GetProperty("schema").GetProperty("type").GetString().ShouldBe("string");
    }

    [Theory]
    [InlineData("/api/download-tickets", "201")]
    public async Task ArchiveFormats_LiveAdminOpenApiDocumentsBusinessFailureResponses(string path, string success)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var document = await ReadLiveOpenApiAsync<Romd.Admin.Host.Program>("admin-v1", timeout.Token);
        var responses = document.RootElement.GetProperty("paths").GetProperty(path).GetProperty("post")
            .GetProperty("responses");
        responses.EnumerateObject().Select(response => response.Name).Order()
            .ShouldBe(new[] { success, "400", "403", "404", "409" }.Order());
        foreach (string code in new[] { "400", "403", "404", "409" })
            responses.GetProperty(code).GetProperty("content").GetProperty("application/problem+json")
                .GetProperty("schema").GetProperty("$ref").GetString()
                .ShouldBe("#/components/schemas/ProblemDetails");
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
            .GetProperty("ConsumerPlaybackConfig");

        operation.GetProperty("operationId").GetString().ShouldBe("GetConsumerPlaybackConfig");
        operation.GetProperty("security").GetArrayLength().ShouldBe(0);
        operation.GetProperty("responses").EnumerateObject().Select(response => response.Name)
            .ShouldBe(["200", "400"]);
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
                .GetProperty("ConsumerServerIdentity");
            var accessOperation = document.RootElement
                .GetProperty("paths")
                .GetProperty("/api/releases/{releaseId}/access")
                .GetProperty("get");
            var accessSchema = document.RootElement
                .GetProperty("components")
                .GetProperty("schemas")
                .GetProperty("ConsumerReleaseAccess");
            var manifestSchema = document.RootElement
                .GetProperty("components")
                .GetProperty("schemas")
                .GetProperty("ConsumerReleaseManifest");

            operation.GetProperty("operationId").GetString().ShouldBe("GetConsumerServerIdentity");
            operation.GetProperty("security").GetArrayLength().ShouldBe(0);
            operation.GetProperty("responses").EnumerateObject().Select(response => response.Name)
                .ShouldBe(["200", "400"]);
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
                .GetProperty("ConsumerPlaybackConfig");
            var playerOrigin = schema
                .GetProperty("properties")
                .GetProperty("playerOrigin");

            operation.GetProperty("operationId").GetString().ShouldBe("GetConsumerPlaybackConfig");
            operation.GetProperty("security").GetArrayLength().ShouldBe(0);
            operation.GetProperty("responses").EnumerateObject().Select(response => response.Name)
                .ShouldBe(["200", "400"]);
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

        var operations = document.RootElement.GetProperty("paths");
        var selfOperations = new Dictionary<string, string[]>
        {
            ["/api/me"] = ["get"],
            ["/api/me/password"] = ["put"],
            ["/api/me/sessions"] = ["delete", "get"]
        };
        paths.Where(path => path == "/api/me" || path.StartsWith("/api/me/", StringComparison.Ordinal))
            .Order().ShouldBe(selfOperations.Keys.Order());
        foreach (var (path, methods) in selfOperations)
            operations.GetProperty(path).EnumerateObject().Select(operation => operation.Name).Order().ShouldBe(methods);
        operations.GetProperty("/api/me").GetProperty("get").GetProperty("responses").GetProperty("200")
            .GetProperty("content").GetProperty("application/json").GetProperty("schema")
            .GetProperty("$ref").GetString().ShouldBe("#/components/schemas/CurrentUserResponse");
        paths.ShouldNotContain("/api/playback/config");
        schemas.ShouldNotContain("ConsumerPlaybackConfig");
    }

    [Fact]
    public async Task ArtworkRoleAndMode_LiveAdminOpenApiPublishesTheirClosedValues()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var document = await ReadLiveOpenApiAsync<Romd.Admin.Host.Program>("admin-v1", timeout.Token);
        var paths = document.RootElement.GetProperty("paths");
        foreach ((string path, string method) in new[]
                 {
                     ("/api/titles/{titleId}/artwork/{role}", "get"),
                     ("/api/titles/{titleId}/artwork/{role}", "put"),
                     ("/api/titles/{titleId}/artwork/{role}/apply", "post")
                 })
        {
            var role = paths.GetProperty(path).GetProperty(method).GetProperty("parameters").EnumerateArray()
                .Single(value => value.GetProperty("name").GetString() == "role");
            role.GetProperty("in").GetString().ShouldBe("path");
            role.GetProperty("schema").GetProperty("enum").EnumerateArray().Select(value => value.GetString())
                .ShouldBe(["Poster", "Hero", "Logo", "Backdrop"], $"{method} {path}");
        }

        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        schemas.GetProperty("ArtworkSelectionState").GetProperty("properties").GetProperty("mode")
            .GetProperty("$ref").GetString().ShouldBe("#/components/schemas/ArtworkMode");
        AssertEnumSchema(schemas, "ArtworkMode", ["Automatic", "Empty", "Pinned"]);
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
            .GetProperty("LibraryConfiguration")
            .GetProperty("properties");
        configurationProperties.GetProperty("titleSelectionMode").GetProperty("$ref").GetString()
            .ShouldBe("#/components/schemas/LibraryTitleSelectionMode");
        configurationProperties.GetProperty("unknownGenrePolicy").GetProperty("$ref").GetString()
            .ShouldBe("#/components/schemas/UnknownMetadataPolicy");

        var ratingProperties = schemas
            .GetProperty("ContentRatingPolicy")
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
}
