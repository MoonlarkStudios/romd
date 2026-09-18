using System.Net;
using System.Text.Json;
using Romd.Hosting.IntegrationTests.Infrastructure;
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
        var platforms = platformsDoc.RootElement;
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
}
