using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Romd.Application.Common.Ids;
using Romd.Contracts.Management.Artwork;
using Romd.Contracts.Management.Enrichment;
using Romd.Domain.Catalog;
using Romd.Domain.Jobs;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

/// <summary>
///     Integration tests for Catalog endpoints.
///     Uses a shared fixture to avoid Hangfire static state issues.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class CatalogEndpointsTests : IDisposable
{
    private readonly HttpClient _client;
    private readonly IntegrationTestFixture _fixture;

    public CatalogEndpointsTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.CreateAuthenticatedClient();
    }

    public void Dispose() => _client.Dispose();

    [Theory]
    [InlineData("missing")]
    [InlineData("upgrades")]
    public async Task CatalogStats_ReleaseReview_ReturnsExactTotalWithoutItems(string review)
    {
        string emptySystem = await SeedEmptySystemAsync();

        var response = await _client.GetAsync($"/api/catalog/stats?releaseReview={review}&systemKey={emptySystem}");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("totalCount").GetInt32().ShouldBe(0);
        body.RootElement.TryGetProperty("items", out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("/api/catalog?systemKey=nonexistent-system")]
    [InlineData("/api/catalog/stats?systemKey=nonexistent-system")]
    [InlineData("/api/search/games?systemKey=nonexistent-system")]
    public async Task SystemKeyFilter_UnknownKey_Returns400NamingTheFilter(string path)
    {
        var response = await _client.GetAsync(path);

        (await response.Content.ReadAsStringAsync()).ShouldNotContain("nonexistent-system");
        using var body = await ProblemEnvelope.ReadAsync(response, 400, "Request.InvalidParameter");
        body.RootElement.GetProperty("errors").EnumerateObject().Select(property => property.Name).ShouldBe(["systemKey"]);
    }

    // A request is validated in stages: binding, then declared parameter constraints and key
    // resolution, then the operation's own validation (docs/decisions/api-contract-policy.md,
    // "One Error Envelope"). Only the stage that rejects the request reports, with all its failures.
    [Theory]
    [InlineData("limit=abc&systemKey=nonexistent-system&sortBy=zzz", new[] { "limit" })]
    [InlineData("limit=500&systemKey=nonexistent-system&sortBy=zzz", new[] { "limit", "systemKey", "sortBy" })]
    [InlineData("limit=50&systemKey=nonexistent-system&sortBy=zzz", new[] { "systemKey", "sortBy" })]
    public async Task SearchCatalog_FailuresInSeveralStages_ReportsOnlyTheFirstRejectingStage(string query, string[] fields)
    {
        var response = await _client.GetAsync($"/api/catalog?{query}");

        using var body = await ProblemEnvelope.ReadAsync(response, 400, "Request.InvalidParameter");
        body.RootElement.GetProperty("errors").EnumerateObject().Select(property => property.Name)
            .ShouldBe(fields, ignoreOrder: true);
    }

    [Fact]
    public async Task UnknownRouteSystemKey_Anonymous_IsUnauthorizedBeforeResolution()
    {
        using var anonymous = _fixture.CreateClient();

        var response = await anonymous.GetAsync("/api/systems/nonexistent-system/titles");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    /// <summary>Seeds an installation system no title belongs to and returns its key.</summary>
    private async Task<string> SeedEmptySystemAsync()
    {
        string key = $"empty-{Guid.NewGuid():N}"[..20];
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        db.Platforms.Add(new PlatformEntity
        {
            Name = key,
            ShortName = key,
            CanonicalKey = key,
            Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Installation,
            BaseName = key,
            BaseCompactLabel = key,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        return key;
    }

    [Fact]
    public async Task SearchCatalog_WithQuery_ReturnsFtsResults()
    {
        // Arrange - Upload a DAT with a platform to create titles
        // NOTE: Titles are only created when a platformId is provided during DAT upload
        string platformId = await TestHelpers.GetFirstSystemKeyAsync(_client);

        string uniqueName = $"Catalog FTS Test DAT {Guid.NewGuid():N}";
        string datContent = $"""
                             <?xml version="1.0"?>
                             <datafile>
                               <header>
                                 <name>{uniqueName}</name>
                                 <description>Desc</description>
                               </header>
                               <game name="Super Mario World (USA)">
                                 <rom name="smw.sfc" size="524288" sha1="6B47BB75D16514B6A476AA0C73A683A2A4C18765"/>
                               </game>
                               <game name="Zelda - A Link to the Past (USA)">
                                 <rom name="zelda.sfc" size="1048576" sha1="AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"/>
                               </game>
                             </datafile>
                             """;

        await TestHelpers.UploadDatWithPlatformAsync(_client, datContent, "catalog-fts-test.dat", platformId);

        // Debug: Check if the DAT was imported successfully
        var datsResponse = await _client.GetAsync("/api/dats");
        datsResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        string datsBody = await datsResponse.Content.ReadAsStringAsync();

        // Debug: Get ALL titles (no filters) to see if any exist at all
        var allTitlesResponse = await _client.GetAsync("/api/catalog");
        allTitlesResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        string allTitlesBody = await allTitlesResponse.Content.ReadAsStringAsync();

        // Debug: First verify titles exist without FTS search
        var debugResponse = await _client.GetAsync($"/api/catalog?systemKey={platformId}");
        debugResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        string debugBody = await debugResponse.Content.ReadAsStringAsync();
        using var debugDoc = JsonDocument.Parse(debugBody);
        var debugItems = debugDoc.RootElement.GetProperty("items");

        // If this fails, titles aren't being created at all
        debugItems.GetArrayLength().ShouldBeGreaterThan(0,
            $"Titles should exist for platform {platformId}.\n" +
            $"DATs: {datsBody}\n" +
            $"All titles: {allTitlesBody}\n" +
            $"Platform filtered: {debugBody}");

        // Act - Search for "Mario"
        var response = await _client.GetAsync("/api/catalog?query=Mario");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        string responseBody = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(responseBody);
        var root = doc.RootElement;

        root.TryGetProperty("items", out var items).ShouldBeTrue();
        items.GetArrayLength().ShouldBeGreaterThanOrEqualTo(1,
            $"FTS search for 'Mario' should return results. Response: {responseBody}");

        // Verify the result contains Mario
        bool foundMario = false;
        foreach (var item in items.EnumerateArray())
        {
            string? name = item.GetProperty("name").GetString();
            if (name?.Contains("Mario", StringComparison.OrdinalIgnoreCase) == true)
            {
                foundMario = true;
                break;
            }
        }

        foundMario.ShouldBeTrue("Search should return results containing 'Mario'");
    }

    [Fact]
    public async Task SearchCatalog_ReleaseCompletenessAll_IncludesAllTitles()
    {
        // Arrange - Upload a DAT with a platform to create titles
        // NOTE: Titles are only created when a platformId is provided during DAT upload
        string platformId = await TestHelpers.GetFirstSystemKeyAsync(_client);

        string uniqueName = $"Catalog Owned Test DAT {Guid.NewGuid():N}";
        string datContent = $"""
                             <?xml version="1.0"?>
                             <datafile>
                               <header>
                                 <name>{uniqueName}</name>
                                 <description>Desc</description>
                               </header>
                               <game name="Owned Test Game Alpha">
                                 <rom name="alpha.rom" size="1024" sha1="1111111111111111111111111111111111111111"/>
                               </game>
                             </datafile>
                             """;

        await TestHelpers.UploadDatWithPlatformAsync(_client, datContent, "catalog-owned-test.dat", platformId);

        // Act - Search with releaseCompleteness=all (default, includes all titles)
        var response = await _client.GetAsync("/api/catalog?releaseCompleteness=all");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        string responseBody = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(responseBody);
        var root = doc.RootElement;

        root.TryGetProperty("items", out var items).ShouldBeTrue();
        items.GetArrayLength().ShouldBeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task SearchCatalog_ReleaseCompletenessNone_ShowsTitlesWithoutCompleteReleases()
    {
        // Arrange - Upload a DAT WITH a platform (so titles are created) but don't upload ROMs
        // NOTE: Titles are only created when a platformId is provided during DAT upload
        string platformId = await TestHelpers.GetFirstSystemKeyAsync(_client);

        string uniqueName = $"Catalog Local Payload Test DAT {Guid.NewGuid():N}";
        string datContent = $"""
                             <?xml version="1.0"?>
                             <datafile>
                               <header>
                                 <name>{uniqueName}</name>
                                 <description>Desc</description>
                               </header>
                               <game name="Unowned Test Game Unique12345">
                                 <rom name="unique.rom" size="1024" sha1="9999999999999999999999999999999999999999"/>
                               </game>
                             </datafile>
                             """;

        await TestHelpers.UploadDatWithPlatformAsync(_client, datContent, "catalog-ownership-test.dat", platformId);

        // Verify the title exists when searching with releaseCompleteness=all
        var verifyResponse = await _client.GetAsync("/api/catalog?releaseCompleteness=all&query=Unique12345");
        verifyResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var verifyDoc = JsonDocument.Parse(await verifyResponse.Content.ReadAsStringAsync());
        verifyDoc.RootElement.GetProperty("items").GetArrayLength().ShouldBeGreaterThan(0,
            "Title should exist when searching with releaseCompleteness=all");

        // Act - Search for titles with DAT releases but no locally available release payload.
        var response = await _client.GetAsync("/api/catalog?releaseCompleteness=none&query=Unique12345");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        string responseBody = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(responseBody);
        var root = doc.RootElement;

        root.TryGetProperty("items", out var items).ShouldBeTrue();

        // Should find our unique title without local payload.
        bool foundWithoutLocalPayload = false;
        foreach (var item in items.EnumerateArray())
        {
            string? name = item.GetProperty("name").GetString();
            if (name?.Contains("Unique12345", StringComparison.OrdinalIgnoreCase) == true)
            {
                foundWithoutLocalPayload = true;
                break;
            }
        }

        foundWithoutLocalPayload.ShouldBeTrue(
            "releaseCompleteness=none should include titles with no locally available DAT release");
    }

    [Fact]
    public async Task SearchCatalog_ReleaseCompletenessComplete_ExcludesIncompleteTitles()
    {
        // Arrange - Upload a DAT WITH a platform (so titles are created) but don't upload ROMs
        string platformId = await TestHelpers.GetFirstSystemKeyAsync(_client);

        string uniqueName = $"Catalog Complete Test DAT {Guid.NewGuid():N}";
        string datContent = $"""
                             <?xml version="1.0"?>
                             <datafile>
                               <header>
                                 <name>{uniqueName}</name>
                                 <description>Desc</description>
                               </header>
                               <game name="Unowned Test Game CompleteCheck789">
                                 <rom name="complete.rom" size="1024" sha1="8888888888888888888888888888888888888888"/>
                               </game>
                             </datafile>
                             """;

        await TestHelpers.UploadDatWithPlatformAsync(_client, datContent, "catalog-complete-test.dat", platformId);

        // Act - Search for titles whose every DAT release has local payload.
        var response = await _client.GetAsync("/api/catalog?releaseCompleteness=complete&query=CompleteCheck789");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        string responseBody = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(responseBody);
        var root = doc.RootElement;

        root.TryGetProperty("items", out var items).ShouldBeTrue();

        // Should not find our title without local payload.
        bool foundWithoutLocalPayload = false;
        foreach (var item in items.EnumerateArray())
        {
            string? name = item.GetProperty("name").GetString();
            if (name?.Contains("CompleteCheck789", StringComparison.OrdinalIgnoreCase) == true)
            {
                foundWithoutLocalPayload = true;
                break;
            }
        }

        foundWithoutLocalPayload.ShouldBeFalse(
            "releaseCompleteness=complete should exclude titles with an unavailable DAT release");
    }

    [Fact]
    public async Task SearchCatalog_WithPlatformFilter_ReturnsFilteredResults()
    {
        // Act - Get platforms first
        var platformsResponse = await _client.GetAsync("/api/systems");
        platformsResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var platformsDoc = JsonDocument.Parse(await platformsResponse.Content.ReadAsStringAsync());
        var platforms = platformsDoc.RootElement.GetProperty("items");
        platforms.GetArrayLength().ShouldBeGreaterThan(0);

        string? platformId = platforms[0].GetProperty("key").GetString();
        platformId.ShouldNotBeNullOrWhiteSpace();

        // Act - Search with platform filter
        var response = await _client.GetAsync($"/api/catalog?systemKey={platformId}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        string responseBody = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(responseBody);
        var root = doc.RootElement;

        root.TryGetProperty("items", out var items).ShouldBeTrue();

        // All returned items should have the requested platformId
        foreach (var item in items.EnumerateArray())
        {
            string? itemPlatformId = item.GetProperty("systemKey").GetString();
            itemPlatformId.ShouldBe(platformId);
        }
    }

    [Fact]
    public async Task SearchCatalog_Pagination_ReturnsCursorAndHasNextPage()
    {
        // Arrange - Upload a DAT with multiple games and a platform
        // NOTE: Titles are only created when a platformId is provided during DAT upload
        string platformId = await TestHelpers.GetFirstSystemKeyAsync(_client);

        string uniqueName = $"Catalog Pagination Test DAT {Guid.NewGuid():N}";
        string datContent = $"""
                             <?xml version="1.0"?>
                             <datafile>
                               <header>
                                 <name>{uniqueName}</name>
                                 <description>Desc</description>
                               </header>
                               <game name="Pagination Game A"><rom name="a.rom" size="100" sha1="AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"/></game>
                               <game name="Pagination Game B"><rom name="b.rom" size="100" sha1="BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB"/></game>
                               <game name="Pagination Game C"><rom name="c.rom" size="100" sha1="CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC"/></game>
                             </datafile>
                             """;

        await TestHelpers.UploadDatWithPlatformAsync(_client, datContent, "catalog-pagination-test.dat", platformId);

        // Act - Page 1 with limit=2
        var page1Response = await _client.GetAsync("/api/catalog?limit=2");
        page1Response.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var page1Doc = JsonDocument.Parse(await page1Response.Content.ReadAsStringAsync());
        var page1Root = page1Doc.RootElement;

        page1Root.TryGetProperty("items", out var page1Items).ShouldBeTrue();
        page1Root.TryGetProperty("hasNextPage", out var hasNextPage).ShouldBeTrue();

        // If we have more than 2 total items, there should be a next page
        if (page1Items.GetArrayLength() == 2 && hasNextPage.GetBoolean())
        {
            page1Root.TryGetProperty("nextCursor", out var nextCursor).ShouldBeTrue();
            string? cursor = nextCursor.GetString();
            cursor.ShouldNotBeNullOrWhiteSpace();

            // Act - Page 2
            var page2Response = await _client.GetAsync($"/api/catalog?limit=2&cursor={cursor}");
            page2Response.StatusCode.ShouldBe(HttpStatusCode.OK);

            using var page2Doc = JsonDocument.Parse(await page2Response.Content.ReadAsStringAsync());
            var page2Root = page2Doc.RootElement;

            page2Root.TryGetProperty("items", out var page2Items).ShouldBeTrue();
            page2Items.GetArrayLength().ShouldBeGreaterThan(0);
        }
    }

    [Fact]
    public async Task SearchCatalog_RelevanceSort_RequiresQuery()
    {
        // Act - Search with relevance sort but no query
        var response = await _client.GetAsync("/api/catalog?sortBy=relevance");

        // Assert - Should return 400 Bad Request
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SearchCatalog_ReturnsExpectedFields()
    {
        // Arrange - Upload a DAT with a platform to ensure we have data
        // NOTE: Titles are only created when a platformId is provided during DAT upload
        string platformId = await TestHelpers.GetFirstSystemKeyAsync(_client);

        string uniqueName = $"Catalog Fields Test DAT {Guid.NewGuid():N}";
        string datContent = $"""
                             <?xml version="1.0"?>
                             <datafile>
                               <header>
                                 <name>{uniqueName}</name>
                                 <description>Desc</description>
                               </header>
                               <game name="Fields Test Game">
                                 <rom name="fields.rom" size="1024" sha1="DDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDD"/>
                               </game>
                             </datafile>
                             """;

        await TestHelpers.UploadDatWithPlatformAsync(_client, datContent, "catalog-fields-test.dat", platformId);

        // Act
        var response = await _client.GetAsync("/api/catalog");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        string responseBody = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(responseBody);
        var root = doc.RootElement;

        root.TryGetProperty("items", out var items).ShouldBeTrue();
        items.GetArrayLength().ShouldBeGreaterThan(0);

        // Verify first item has expected fields
        var firstItem = items[0];
        firstItem.TryGetProperty("id", out _).ShouldBeTrue();
        firstItem.TryGetProperty("systemKey", out _).ShouldBeTrue();
        firstItem.TryGetProperty("name", out _).ShouldBeTrue();
        firstItem.TryGetProperty("hasLocalPayload", out _).ShouldBeTrue();
        firstItem.TryGetProperty("localPayloadVersionCount", out _).ShouldBeTrue();
        firstItem.TryGetProperty("totalVersionCount", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task SearchCatalog_RepeatedEnrichmentParameter_AndsFlagsAndMatchesCompletionPreview()
    {
        // Letters only, so search tokenization keeps the scope token intact.
        var token = "Enrich" + string.Concat(Guid.NewGuid().ToString("N").Select(c => (char)('a' + Convert.ToInt32(c.ToString(), 16))));
        var (settled, processing, importing, bare, imports) = await SeedEnrichmentTitlesAsync(token);
        try
        {
            // Provider configuration is environmental; the bare title's missing description decides the metadata flag.
            var detail = await _client.GetFromJsonAsync<EnrichmentAssessmentDto>($"/api/titles/{bare}/enrichment-assessment", WorkbenchJson);
            var metadataFlag = detail.ShouldNotBeNull().Requirements.Single(x => x.Key == "Metadata:Description").State.ToString();
            var unfiltered = await SearchEnrichmentAsync($"query={token}");
            unfiltered.Keys.Order().ShouldBe(new[] { settled, processing, importing, bare }.Order());
            unfiltered[settled].ShouldBe(new EnrichmentSummaryDto(false, false, false, false));
            unfiltered[processing].ShouldBe(new EnrichmentSummaryDto(false, false, true, false));
            unfiltered[importing].HasActiveWork.ShouldBeTrue();
            Carries(unfiltered[importing], metadataFlag).ShouldBeTrue(metadataFlag);
            unfiltered[bare].HasActiveWork.ShouldBeFalse();
            Carries(unfiltered[bare], metadataFlag).ShouldBeTrue(metadataFlag);

            // Both orders prove every repeated value binds: dropping either value would admit another title.
            foreach (var flags in new[] { $"enrichment={metadataFlag}&enrichment=Processing", $"enrichment=Processing&enrichment={metadataFlag}" })
                string.Join(",", (await SearchEnrichmentAsync($"query={token}&{flags}")).Keys).ShouldBe(importing, flags);
            var reviewAndProcessing = unfiltered.Where(x => x.Value.NeedsReview && x.Value.HasActiveWork).Select(x => x.Key).Order().ToArray();
            reviewAndProcessing.ShouldNotContain(processing);
            foreach (var flags in new[] { "enrichment=NeedsReview&enrichment=Processing", "enrichment=Processing&enrichment=NeedsReview" })
                string.Join(",", (await SearchEnrichmentAsync($"query={token}&{flags}")).Keys.Order()).ShouldBe(string.Join(",", reviewAndProcessing), flags);
            (await SearchEnrichmentAsync($"query={token}&enrichment=Processing&enrichment=Processing")).Keys.Order()
                .ShouldBe(new[] { processing, importing }.Order());
            (await SearchEnrichmentAsync($"query={token}&enrichment=Settled")).Keys.ShouldBe([settled]);

            foreach (var (flags, expected) in new (string[] Flags, int Titles)[] { ([metadataFlag, "Processing"], 1), (["Processing"], 2), (["Settled"], 1) })
            {
                using var preview = await _client.PostAsJsonAsync("/api/artwork/completion/preview", new ArtworkCompletionRequest(Guid.NewGuid(),
                    Scope: "Filtered", Filters: new(Query: token, Enrichment: flags), Mode: "Enrich"));
                preview.StatusCode.ShouldBe(HttpStatusCode.OK, await preview.Content.ReadAsStringAsync());
                (await preview.Content.ReadFromJsonAsync<ArtworkCompletionPreviewDto>())!.Titles.ShouldBe(expected, string.Join("+", flags));
            }
        }
        finally
        {
            using var scope = _fixture.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<RomdDbContext>().Set<ArtworkImportJobEntity>().Where(x => imports.Contains(x.Id))
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.Phase, "Completed"));
        }
    }

    [Fact]
    public async Task SearchCatalog_InvalidDeclaredEnrichmentFilters_ReportsOnlyDeclaredStageFailures()
    {
        var response = await _client.GetAsync(
            "/api/catalog?enrichment=Settled&enrichment=Blocked&enrichment=Bogus&enrichmentStatus=NeedsReview&tracked=nope");

        using var body = await ProblemEnvelope.ReadAsync(response, 400, "Request.InvalidParameter");
        var errors = body.RootElement.GetProperty("errors");
        errors.EnumerateObject().Select(property => property.Name)
            .ShouldBe(["enrichmentStatus", "tracked"], ignoreOrder: true);
        errors.GetProperty("enrichmentStatus")[0].GetString().ShouldBe("The value is not valid.");
        errors.GetProperty("tracked")[0].GetString().ShouldBe("The value is not valid.");
    }

    [Fact]
    public async Task SearchCatalog_InvalidEnrichmentFlags_PreservesEveryHandlerStageFailure()
    {
        var response = await _client.GetAsync(
            "/api/catalog?enrichment=Settled&enrichment=Blocked&enrichment=Bogus&enrichmentStatus=Completed&tracked=tracked");

        using var body = await ProblemEnvelope.ReadAsync(response, 400, "Catalog.InvalidQuery");
        var errors = body.RootElement.GetProperty("errors");
        errors.EnumerateObject().Select(property => property.Name).ShouldBe(["enrichment"]);
        var enrichment = errors.GetProperty("enrichment").EnumerateArray().Select(x => x.GetString()).ToArray();
        enrichment.Length.ShouldBe(2);
        enrichment[0].ShouldNotBeNull().ShouldContain("'Bogus'");
        enrichment[1].ShouldBe("Settled cannot be combined with other enrichment filters.");
    }

    [Theory]
    [InlineData("releaseReview=", "releaseReview")]
    [InlineData("releaseReview=bogus", "releaseReview")]
    [InlineData("enrichment=", "enrichment")]
    [InlineData("enrichment=Bogus", "enrichment")]
    [InlineData("enrichment=needsreview", "enrichment")]
    [InlineData("enrichment=NeedsReview,Blocked", "enrichment")]
    [InlineData("enrichment=Settled&enrichment=Processing", "enrichment")]
    [InlineData("enrichmentStatus=NeedsReview", "enrichmentStatus")]
    [InlineData("enrichmentStatus=Automatic", "enrichmentStatus")]
    [InlineData("enrichmentStatus=Processing", "enrichmentStatus")]
    [InlineData("enrichmentStatus=Blocked", "enrichmentStatus")]
    [InlineData("enrichmentStatus=failed", "enrichmentStatus")]
    public async Task SearchCatalog_UnsupportedEnrichmentValue_Returns400ForThatParameter(string query, string key)
    {
        var response = await _client.GetAsync($"/api/catalog?{query}");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("errors").EnumerateObject().Select(x => x.Name).ShouldBe([key]);
    }

    [Theory]
    [InlineData("enrichment=Settled")]
    [InlineData("enrichment=NeedsReview&enrichment=Blocked&enrichment=Processing&enrichment=Automatic")]
    [InlineData("enrichmentStatus=LowConfidence&enrichment=Automatic")]
    public async Task SearchCatalog_SupportedEnrichmentFilters_ReturnOk(string query) =>
        (await _client.GetAsync($"/api/catalog?{query}")).StatusCode.ShouldBe(HttpStatusCode.OK);

    private static readonly JsonSerializerOptions WorkbenchJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private static bool Carries(EnrichmentSummaryDto summary, string flag) => flag switch
    {
        "NeedsReview" => summary.NeedsReview,
        "Blocked" => summary.HasOperationalBlocker,
        "Automatic" => summary.CanEnrichAutomatically,
        _ => throw new ArgumentOutOfRangeException(nameof(flag), flag, "The bare title's description must be open.")
    };

    private async Task<Dictionary<string, EnrichmentSummaryDto>> SearchEnrichmentAsync(string query)
    {
        var items = await TestHelpers.ReadAllPageItemsAsync<JsonElement>(_client, $"/api/catalog?{query}");
        return items.ToDictionary(
            item => item.GetProperty("id").GetString()!,
            item => item.GetProperty("enrichmentAssessment").Deserialize<EnrichmentSummaryDto>(WorkbenchJson)!);
    }

    private async Task<(string Settled, string Processing, string Importing, string Bare, Guid[] Imports)> SeedEnrichmentTitlesAsync(string token)
    {
        using var scope = _fixture.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        var platformId = await db.Platforms.Select(x => x.Id).FirstAsync();
        TitleEntity Title(string suffix, bool complete) => new()
        {
            PlatformId = platformId,
            Name = $"{token} {suffix}",
            NormalizedName = $"{token} {suffix}".ToLowerInvariant(),
            EnrichmentStatus = "None",
            CreatedAt = DateTimeOffset.UtcNow,
            Description = complete ? "Curated" : null,
            Genre = complete ? "Adventure" : null,
            ReleaseDate = complete ? new DateOnly(1995, 3, 11) : null
        };
        TitleEntity[] titles = [Title("Settled", true), Title("Processing", true), Title("Importing", false), Title("Bare", false)];
        db.Titles.AddRange(titles);
        await db.SaveChangesAsync();
        // Empty roles settle artwork regardless of settings; a pending poster import is active work that
        // leaves the metadata requirements' states intact.
        var imports = new Dictionary<int, Guid> { [titles[1].Id] = Guid.NewGuid(), [titles[2].Id] = Guid.NewGuid() };
        foreach (var title in titles)
            foreach (var role in new[] { ArtworkRole.Poster, ArtworkRole.Hero, ArtworkRole.Logo, ArtworkRole.Backdrop })
                db.ArtworkSelections.Add(new()
                {
                    TitleId = title.Id,
                    Role = role,
                    Mode = ArtworkSelectionMode.Empty,
                    Revision = 1,
                    PendingRequestId = role == ArtworkRole.Poster && imports.TryGetValue(title.Id, out var pending) ? pending : null
                });
        foreach (var (titleId, requestId) in imports)
        {
            var job = ArtworkImportJobEntity.FromDomain(ArtworkImportJob.Create(requestId, $"{token} import", titleId, platformId,
                ArtworkRole.Poster, 1, "igdb", "42", "asset", "https://example.com/image", null, TimeProvider.System.GetUtcNow()));
            job.Phase = "Importing";
            db.Add(job);
        }
        await db.SaveChangesAsync();
        return (IdCoder.Encode(titles[0].Id), IdCoder.Encode(titles[1].Id), IdCoder.Encode(titles[2].Id), IdCoder.Encode(titles[3].Id),
            imports.Values.ToArray());
    }
}
