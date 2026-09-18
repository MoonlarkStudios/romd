using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Romd.Application.Common.Ids;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Dat;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

/// <summary>
///     Integration tests for DAT endpoints.
///     Uses a shared fixture to avoid Hangfire static state issues.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class DatEndpointsTests : IDisposable
{
    private readonly HttpClient _client;
    private readonly IntegrationTestFixture _fixture;

    public DatEndpointsTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
        // Use authenticated client since endpoints require authorization
        _client = fixture.CreateAuthenticatedClient();
    }

    public void Dispose() => _client.Dispose();

    [Fact]
    public async Task UploadDat_ValidDatFile_IngestsSuccessfully()
    {
        // Arrange
        const string datContent = """
                                  <?xml version="1.0"?>
                                  <datafile>
                                    <header>
                                      <name>Test DAT</name>
                                      <description>Test DAT Description</description>
                                      <version>1.0</version>
                                      <author>Test Author</author>
                                    </header>
                                    <game name="Super Mario World (USA)">
                                      <description>Super Mario World (USA)</description>
                                      <rom name="Super Mario World (USA).sfc" size="524288" crc="B19ED489" sha1="6B47BB75D16514B6A476AA0C73A683A2A4C18765"/>
                                    </game>
                                  </datafile>
                                  """;

        using var content = TestHelpers.CreateMultipartContent(datContent, "test.dat");

        // Act
        var response = await _client.PostAsync("/api/upload/dat", content);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        string responseBody = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(responseBody);
        string? jobId = doc.RootElement.GetProperty("jobId").GetString();
        jobId.ShouldNotBeNull();

        await TestHelpers.WaitForJobCompletionAsync(_client, jobId!);

        // Verify ingest by fetching the DAT via name lookup
        string datId = await TestHelpers.GetDatIdByNameAsync(_client, "Test DAT");

        var datResponse = await _client.GetAsync($"/api/dats/{datId}");
        datResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        string datBody = await datResponse.Content.ReadAsStringAsync();
        using var datDoc = JsonDocument.Parse(datBody);

        var dat = datDoc.RootElement;
        dat.GetProperty("name").GetString().ShouldBe("Test DAT");
        dat.GetProperty("description").GetString().ShouldBe("Test DAT Description");
        dat.GetProperty("version").GetString().ShouldBe("1.0");
        dat.GetProperty("author").GetString().ShouldBe("Test Author");
        dat.GetProperty("gameCount").GetInt32().ShouldBe(1);
        dat.GetProperty("romCount").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task DownloadDat_IngestedDat_ReturnsOriginalSourceFile()
    {
        // Arrange
        string uniqueName = $"Download Source DAT {Guid.NewGuid():N}";
        const string fileName = "download-source-test.dat";
        string datContent = $"""
                             <?xml version="1.0"?>
                             <datafile>
                               <header>
                                 <name>{uniqueName}</name>
                                 <description>Download Source DAT Description</description>
                               </header>
                               <game name="Downloadable Test Game">
                                 <rom name="downloadable-test-game.rom" size="1024" sha1="4444444444444444444444444444444444444444"/>
                               </game>
                             </datafile>
                             """;

        using var content = TestHelpers.CreateMultipartContent(datContent, fileName);
        var uploadResponse = await _client.PostAsync("/api/upload/dat", content);
        uploadResponse.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        string uploadBody = await uploadResponse.Content.ReadAsStringAsync();
        using var uploadDoc = JsonDocument.Parse(uploadBody);
        string? jobId = uploadDoc.RootElement.GetProperty("jobId").GetString();
        jobId.ShouldNotBeNull();

        await TestHelpers.WaitForJobCompletionAsync(_client, jobId!);

        string datId = await TestHelpers.GetDatIdByNameAsync(_client, uniqueName);

        // Act
        var response = await _client.GetAsync($"/api/dats/{datId}/download");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/octet-stream");
        response.Content.Headers.ContentDisposition?.FileName?.Trim('"').ShouldBe(fileName);

        string downloadedContent = await response.Content.ReadAsStringAsync();
        downloadedContent.ShouldBe(datContent);
    }

    [Fact]
    public async Task DownloadDat_NonExistentDat_Returns404()
    {
        // Arrange
        string nonExistentId = IdCoder.Encode(999999);

        // Act
        var response = await _client.GetAsync($"/api/dats/{nonExistentId}/download");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ReplaceDat_ExistingDat_ReplacesSourceInsteadOfAdding()
    {
        // Arrange
        string platformId = await TestHelpers.GetFirstSystemKeyAsync(_client);
        string suffix = Guid.NewGuid().ToString("N");
        string oldName = $"Replace Old DAT {suffix}";
        string newName = $"Replace New DAT {suffix}";
        string oldContent = $"""
                             <?xml version="1.0"?>
                             <datafile>
                               <header>
                                 <name>{oldName}</name>
                                 <description>Old replacement source</description>
                               </header>
                               <game name="Replacement Test Game">
                                 <rom name="replacement-old.rom" size="1024" sha1="5555555555555555555555555555555555555555"/>
                               </game>
                             </datafile>
                             """;
        string newContent = $"""
                             <?xml version="1.0"?>
                             <datafile>
                               <header>
                                 <name>{newName}</name>
                                 <description>New replacement source</description>
                               </header>
                               <game name="Replacement Test Game">
                                 <rom name="replacement-new.rom" size="1024" sha1="6666666666666666666666666666666666666666"/>
                               </game>
                             </datafile>
                             """;

        using var uploadContent = TestHelpers.CreateMultipartContent(oldContent, "replace-old.dat");
        var uploadResponse = await _client.PostAsync($"/api/upload/dat?systemKey={platformId}", uploadContent);
        uploadResponse.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        string uploadBody = await uploadResponse.Content.ReadAsStringAsync();
        using var uploadDoc = JsonDocument.Parse(uploadBody);
        string? uploadJobId = uploadDoc.RootElement.GetProperty("jobId").GetString();
        uploadJobId.ShouldNotBeNull();

        await TestHelpers.WaitForJobCompletionAsync(_client, uploadJobId!);

        string oldDatId = await TestHelpers.GetDatIdByNameAsync(_client, oldName);

        using var replacementContent = TestHelpers.CreateMultipartContent(newContent, "replace-new.dat");

        // Act
        var replaceResponse = await _client.PutAsync(
            $"/api/dats/{oldDatId}/replace?systemKey={platformId}",
            replacementContent);

        // Assert
        replaceResponse.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        string replaceBody = await replaceResponse.Content.ReadAsStringAsync();
        using var replaceDoc = JsonDocument.Parse(replaceBody);
        string? replaceJobId = replaceDoc.RootElement.GetProperty("jobId").GetString();
        replaceJobId.ShouldNotBeNull();

        await TestHelpers.WaitForJobCompletionAsync(_client, replaceJobId!);

        // The replaced version is retained as queryable provenance, not deleted:
        // direct GET returns it as Superseded while listings exclude it.
        var oldDatResponse = await _client.GetAsync($"/api/dats/{oldDatId}");
        oldDatResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        string oldDatBody = await oldDatResponse.Content.ReadAsStringAsync();
        using var oldDatDoc = JsonDocument.Parse(oldDatBody);
        oldDatDoc.RootElement.GetProperty("lifecycle").GetString().ShouldBe("Superseded");

        var datsResponse = await _client.GetAsync("/api/dats");
        datsResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        string datsBody = await datsResponse.Content.ReadAsStringAsync();
        using var datsDoc = JsonDocument.Parse(datsBody);
        var matchingNames = datsDoc.RootElement
            .EnumerateArray()
            .Select(dat => dat.GetProperty("name").GetString())
            .Where(name => name == oldName || name == newName)
            .ToList();

        matchingNames.ShouldBe(new[] { newName });
    }

    [Fact]
    public async Task GetDats_ReturnsListOfDats()
    {
        // Act
        var response = await _client.GetAsync("/api/dats");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        string responseBody = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(responseBody);
        doc.RootElement.ValueKind.ShouldBe(JsonValueKind.Array);
    }

    [Fact]
    public async Task GetDats_SupersededVersion_ExcludedFromListButAddressableByIdWithLifecycle()
    {
        // Arrange - seed a source with an Active and a Superseded version directly; nothing in
        // this slice produces Superseded rows yet, but the contract must already carry them.
        string uniquePrefix = $"Lifecycle Contract DAT {Guid.NewGuid():N}";
        int sourceId;
        int activeDatId;
        int supersededDatId;
        using (var scope = _fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
            var storedFile = new FileEntityPersistence
            {
                Sha256 = NewRandomSha256(),
                Size = 1,
                SizeOnDisk = 1
            };
            db.Files.Add(storedFile);
            await db.SaveChangesAsync();

            var source = new DatSourceEntity
            {
                CatalogSource = new CatalogSourceEntity { Kind = "Dat", Status = "Active" }
            };
            var activeDat = NewSeededDatFile(
                source, storedFile.Id, $"{uniquePrefix} Active", nameof(DatFileLifecycle.Active));
            var supersededDat = NewSeededDatFile(
                source, storedFile.Id, $"{uniquePrefix} Superseded", nameof(DatFileLifecycle.Superseded));
            db.DatFiles.AddRange(activeDat, supersededDat);
            await db.SaveChangesAsync();
            sourceId = source.Id;
            activeDatId = activeDat.Id;
            supersededDatId = supersededDat.Id;
        }

        // Act
        var byIdResponse = await _client.GetAsync($"/api/dats/{IdCoder.Encode(supersededDatId)}");
        var listResponse = await _client.GetAsync("/api/dats");

        // Assert - superseded provenance stays addressable by id, with its lifecycle and source.
        byIdResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var byIdDoc = JsonDocument.Parse(await byIdResponse.Content.ReadAsStringAsync());
        byIdDoc.RootElement.GetProperty("lifecycle").GetString().ShouldBe("Superseded");
        byIdDoc.RootElement.GetProperty("sourceId").GetString().ShouldBe(IdCoder.Encode(sourceId));

        // Assert - listings return Active versions only.
        listResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var listDoc = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync());
        var listed = listDoc.RootElement
            .EnumerateArray()
            .Where(dat => dat.GetProperty("name").GetString()!.StartsWith(uniquePrefix))
            .ToList();
        listed.Select(dat => dat.GetProperty("id").GetString())
            .ShouldBe([IdCoder.Encode(activeDatId)]);
        listed.Single().GetProperty("lifecycle").GetString().ShouldBe("Active");
        listed.Single().GetProperty("sourceId").GetString().ShouldBe(IdCoder.Encode(sourceId));
    }

    [Fact]
    public async Task GetDats_SourceStatus_CarriedOnListAndById()
    {
        // Arrange - seed one Active-source DAT and one Disabled-source DAT directly; the
        // contract carries the source's lifecycle status on every read.
        string uniquePrefix = $"Source Status DAT {Guid.NewGuid():N}";
        int disabledDatId;
        int activeCatalogSourceId;
        int disabledCatalogSourceId;
        using (var scope = _fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
            var storedFile = new FileEntityPersistence
            {
                Sha256 = NewRandomSha256(),
                Size = 1,
                SizeOnDisk = 1
            };
            db.Files.Add(storedFile);
            await db.SaveChangesAsync();

            var activeSource = new DatSourceEntity
            {
                CatalogSource = new CatalogSourceEntity { Kind = "Dat", Status = "Active" }
            };
            var disabledSource = new DatSourceEntity
            {
                CatalogSource = new CatalogSourceEntity { Kind = "Dat", Status = "Disabled" }
            };
            var activeDat = NewSeededDatFile(
                activeSource, storedFile.Id, $"{uniquePrefix} Active", nameof(DatFileLifecycle.Active));
            var disabledDat = NewSeededDatFile(
                disabledSource, storedFile.Id, $"{uniquePrefix} Disabled", nameof(DatFileLifecycle.Active));
            db.DatFiles.AddRange(activeDat, disabledDat);
            await db.SaveChangesAsync();
            disabledDatId = disabledDat.Id;
            activeCatalogSourceId = activeSource.CatalogSourceId;
            disabledCatalogSourceId = disabledSource.CatalogSourceId;
        }

        // Act
        var byIdResponse = await _client.GetAsync($"/api/dats/{IdCoder.Encode(disabledDatId)}");
        var listResponse = await _client.GetAsync("/api/dats");

        // Assert - by-id carries the source's status and neutral catalog source id.
        byIdResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var byIdDoc = JsonDocument.Parse(await byIdResponse.Content.ReadAsStringAsync());
        byIdDoc.RootElement.GetProperty("sourceStatus").GetString().ShouldBe("Disabled");
        byIdDoc.RootElement.GetProperty("catalogSourceId").GetString()
            .ShouldBe(IdCoder.Encode(disabledCatalogSourceId));

        // Assert - the listing carries each DAT's own source status and catalog source id.
        listResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var listDoc = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync());
        var listedByName = listDoc.RootElement
            .EnumerateArray()
            .Where(dat => dat.GetProperty("name").GetString()!.StartsWith(uniquePrefix))
            .ToDictionary(dat => dat.GetProperty("name").GetString()!);
        listedByName.Count.ShouldBe(2);
        listedByName[$"{uniquePrefix} Active"].GetProperty("sourceStatus").GetString().ShouldBe("Active");
        listedByName[$"{uniquePrefix} Active"].GetProperty("catalogSourceId").GetString()
            .ShouldBe(IdCoder.Encode(activeCatalogSourceId));
        listedByName[$"{uniquePrefix} Disabled"].GetProperty("sourceStatus").GetString().ShouldBe("Disabled");
        listedByName[$"{uniquePrefix} Disabled"].GetProperty("catalogSourceId").GetString()
            .ShouldBe(IdCoder.Encode(disabledCatalogSourceId));
    }

    [Fact]
    public async Task GetDats_DatDeleted_ListingsReturnRemainingDatsCleanly()
    {
        // Arrange - two routed and two unrouted DATs; remove one of each the way DAT
        // deletion does (version row, then source anchor, then catalog source). The
        // platform and unrouted listings read DAT and source rows in one snapshot, so
        // they must keep serving the survivors instead of erroring on the vanished rows.
        string suffix = Guid.NewGuid().ToString("N");
        string uniquePrefix = $"Deletion Sanity DAT {suffix}";
        string systemKey;
        int removedRoutedDatId;
        int removedRoutedSourceId;
        int removedRoutedCatalogSourceId;
        int removedUnroutedDatId;
        int removedUnroutedSourceId;
        int removedUnroutedCatalogSourceId;
        using (var scope = _fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
            var platform = new PlatformEntity
            {
                Name = $"Deletion Sanity Platform {suffix}",
                Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Installation,
                BaseName = $"Deletion Sanity Platform {suffix}",
                BaseCompactLabel = "Deletion Sanity",
                CanonicalKey = $"local-del-{suffix}",
                ShortName = $"local-del-{suffix}"
            };
            var storedFile = new FileEntityPersistence
            {
                Sha256 = NewRandomSha256(),
                Size = 1,
                SizeOnDisk = 1
            };
            db.Platforms.Add(platform);
            db.Files.Add(storedFile);
            await db.SaveChangesAsync();

            var keptRoutedSource = NewActiveSource();
            var removedRoutedSource = NewActiveSource();
            var keptUnroutedSource = NewActiveSource();
            var removedUnroutedSource = NewActiveSource();
            var keptRoutedDat = NewSeededDatFile(
                keptRoutedSource, storedFile.Id, $"{uniquePrefix} Routed Kept", nameof(DatFileLifecycle.Active));
            keptRoutedDat.PlatformId = platform.Id;
            var removedRoutedDat = NewSeededDatFile(
                removedRoutedSource, storedFile.Id, $"{uniquePrefix} Routed Removed",
                nameof(DatFileLifecycle.Active));
            removedRoutedDat.PlatformId = platform.Id;
            var keptUnroutedDat = NewSeededDatFile(
                keptUnroutedSource, storedFile.Id, $"{uniquePrefix} Unrouted Kept",
                nameof(DatFileLifecycle.Active));
            var removedUnroutedDat = NewSeededDatFile(
                removedUnroutedSource, storedFile.Id, $"{uniquePrefix} Unrouted Removed",
                nameof(DatFileLifecycle.Active));
            db.DatFiles.AddRange(keptRoutedDat, removedRoutedDat, keptUnroutedDat, removedUnroutedDat);
            await db.SaveChangesAsync();

            systemKey = platform.CanonicalKey!;
            removedRoutedDatId = removedRoutedDat.Id;
            removedRoutedSourceId = removedRoutedSource.Id;
            removedRoutedCatalogSourceId = removedRoutedSource.CatalogSourceId;
            removedUnroutedDatId = removedUnroutedDat.Id;
            removedUnroutedSourceId = removedUnroutedSource.Id;
            removedUnroutedCatalogSourceId = removedUnroutedSource.CatalogSourceId;
        }

        using (var scope = _fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
            await db.DatFiles
                .Where(d => d.Id == removedRoutedDatId || d.Id == removedUnroutedDatId)
                .ExecuteDeleteAsync();
            await db.DatSources
                .Where(s => s.Id == removedRoutedSourceId || s.Id == removedUnroutedSourceId)
                .ExecuteDeleteAsync();
            await db.CatalogSources
                .Where(c => c.Id == removedRoutedCatalogSourceId || c.Id == removedUnroutedCatalogSourceId)
                .ExecuteDeleteAsync();
        }

        // Act
        var platformDatsResponse = await _client.GetAsync($"/api/systems/{systemKey}/dats");
        var unroutedResponse = await _client.GetAsync("/api/dats/unrouted");

        // Assert - the platform listing returns only the surviving routed DAT.
        platformDatsResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var platformDatsDoc = JsonDocument.Parse(await platformDatsResponse.Content.ReadAsStringAsync());
        platformDatsDoc.RootElement
            .EnumerateArray()
            .Select(dat => dat.GetProperty("name").GetString())
            .ShouldBe([$"{uniquePrefix} Routed Kept"]);

        // Assert - the unrouted listing returns only the surviving unrouted DAT.
        unroutedResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var unroutedDoc = JsonDocument.Parse(await unroutedResponse.Content.ReadAsStringAsync());
        unroutedDoc.RootElement
            .EnumerateArray()
            .Select(item => item.GetProperty("dat").GetProperty("name").GetString())
            .Where(name => name!.StartsWith(uniquePrefix))
            .ShouldBe([$"{uniquePrefix} Unrouted Kept"]);
    }

    private static DatSourceEntity NewActiveSource() =>
        new()
        {
            CatalogSource = new CatalogSourceEntity { Kind = "Dat", Status = "Active" }
        };

    private static DatFileEntity NewSeededDatFile(
        DatSourceEntity source,
        int fileId,
        string name,
        string lifecycle) =>
        new()
        {
            Source = source,
            Name = name,
            Description = name,
            Type = nameof(DatType.NoIntro),
            OriginalFilename = $"{lifecycle.ToLowerInvariant()}.dat",
            FileId = fileId,
            Lifecycle = lifecycle
        };

    private static Sha256 NewRandomSha256()
    {
        var bytes = new byte[Sha256.ByteLength];
        RandomNumberGenerator.Fill(bytes);
        return Sha256.FromBytes(bytes);
    }

    [Fact]
    public async Task GetGamesByDatId_ReturnsPagedGames()
    {
        // Arrange
        string uniqueName = $"Games Test DAT {Guid.NewGuid():N}";
        string datContent = $"""
                             <?xml version="1.0"?>
                             <datafile>
                               <header>
                                 <name>{uniqueName}</name>
                                 <description>Desc</description>
                               </header>
                               <game name="Alpha Game">
                                 <rom name="alpha.rom" size="1024" sha1="0000000000000000000000000000000000000001"/>
                               </game>
                               <game name="Beta Game">
                                 <rom name="beta.rom" size="2048" sha1="0000000000000000000000000000000000000002"/>
                               </game>
                               <game name="Gamma Game">
                                 <rom name="gamma.rom" size="4096" sha1="0000000000000000000000000000000000000003"/>
                               </game>
                             </datafile>
                             """;

        using var content = TestHelpers.CreateMultipartContent(datContent, "games-test.dat");
        var uploadResponse = await _client.PostAsync("/api/upload/dat", content);
        uploadResponse.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        string uploadBody = await uploadResponse.Content.ReadAsStringAsync();
        string? jobId = JsonDocument.Parse(uploadBody).RootElement.GetProperty("jobId").GetString();
        await TestHelpers.WaitForJobCompletionAsync(_client, jobId!);

        string datId = await TestHelpers.GetDatIdByNameAsync(_client, uniqueName);

        // Act
        var response = await _client.GetAsync($"/api/dats/{datId}/games");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        string responseBody = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(responseBody);
        var root = doc.RootElement;

        root.TryGetProperty("items", out var items).ShouldBeTrue();
        items.GetArrayLength().ShouldBe(3);

        // Items are sorted by Name by default
        items[0].GetProperty("name").GetString().ShouldBe("Alpha Game");
        items[0].GetProperty("roms").GetArrayLength().ShouldBe(1);

        root.TryGetProperty("hasNextPage", out var hasNextPage).ShouldBeTrue();
        hasNextPage.GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task GetGamesByDatId_PaginationIsStable()
    {
        // Arrange
        string uniqueName = $"Pagination Test DAT {Guid.NewGuid():N}";
        string datContent = $"""
                             <?xml version="1.0"?>
                             <datafile>
                               <header>
                                 <name>{uniqueName}</name>
                                 <description>Desc</description>
                               </header>
                               <game name="Game A"><rom name="a.rom" size="100" sha1="aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"/></game>
                               <game name="Game B"><rom name="b.rom" size="100" sha1="bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"/></game>
                               <game name="Game C"><rom name="c.rom" size="100" sha1="cccccccccccccccccccccccccccccccccccccccc"/></game>
                             </datafile>
                             """;

        using var content = TestHelpers.CreateMultipartContent(datContent, "pagination-test.dat");
        var uploadResponse = await _client.PostAsync("/api/upload/dat", content);
        string? jobId = JsonDocument.Parse(await uploadResponse.Content.ReadAsStringAsync())
            .RootElement.GetProperty("jobId").GetString();
        await TestHelpers.WaitForJobCompletionAsync(_client, jobId!);

        string datId = await TestHelpers.GetDatIdByNameAsync(_client, uniqueName);

        // Act - Page 1
        var page1Response = await _client.GetAsync($"/api/dats/{datId}/games?limit=2");
        page1Response.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var page1Doc = JsonDocument.Parse(await page1Response.Content.ReadAsStringAsync());
        var page1Root = page1Doc.RootElement;
        page1Root.GetProperty("items").GetArrayLength().ShouldBe(2);
        page1Root.GetProperty("hasNextPage").GetBoolean().ShouldBeTrue();
        string? nextCursor = page1Root.GetProperty("nextCursor").GetString();
        nextCursor.ShouldNotBeNullOrWhiteSpace();

        // Act - Page 2
        var page2Response = await _client.GetAsync($"/api/dats/{datId}/games?limit=2&cursor={nextCursor}");
        page2Response.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var page2Doc = JsonDocument.Parse(await page2Response.Content.ReadAsStringAsync());
        var page2Root = page2Doc.RootElement;
        page2Root.GetProperty("items").GetArrayLength().ShouldBe(1);
        page2Root.GetProperty("hasNextPage").GetBoolean().ShouldBeFalse();
        page2Root.GetProperty("items")[0].GetProperty("name").GetString().ShouldBe("Game C");
    }

    [Fact]
    public async Task GetGamesByDatId_NonExistentDat_Returns404()
    {
        // Arrange
        string nonExistentId = IdCoder.Encode(999999);

        // Act
        var response = await _client.GetAsync($"/api/dats/{nonExistentId}/games");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetGameById_ReturnsGameWithRoms()
    {
        // Arrange
        string uniqueName = $"Single Game Test DAT {Guid.NewGuid():N}";
        string datContent = $"""
                             <?xml version="1.0"?>
                             <datafile>
                               <header>
                                 <name>{uniqueName}</name>
                                 <description>Desc</description>
                               </header>
                               <game name="Test Game With Multiple Roms">
                                 <description>A test game</description>
                                 <year>1994</year>
                                 <manufacturer>Test Corp</manufacturer>
                                 <rom name="game.bin" size="1024" crc="DEADBEEF" sha1="0123456789abcdef0123456789abcdef01234567"/>
                                 <rom name="game.cue" size="128" sha1="fedcba9876543210fedcba9876543210fedcba98"/>
                               </game>
                             </datafile>
                             """;

        using var content = TestHelpers.CreateMultipartContent(datContent, "single-game-test.dat");
        var uploadResponse = await _client.PostAsync("/api/upload/dat", content);
        string? jobId = JsonDocument.Parse(await uploadResponse.Content.ReadAsStringAsync())
            .RootElement.GetProperty("jobId").GetString();
        await TestHelpers.WaitForJobCompletionAsync(_client, jobId!);

        string datId = await TestHelpers.GetDatIdByNameAsync(_client, uniqueName);

        // Find the game ID by listing games
        var gamesResponse = await _client.GetAsync($"/api/dats/{datId}/games");
        using var gamesDoc = JsonDocument.Parse(await gamesResponse.Content.ReadAsStringAsync());
        string? gameId = gamesDoc.RootElement.GetProperty("items")[0].GetProperty("id").GetString();

        // Act
        var response = await _client.GetAsync($"/api/dats/{datId}/games/{gameId}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        root.GetProperty("id").GetString().ShouldBe(gameId);
        root.GetProperty("name").GetString().ShouldBe("Test Game With Multiple Roms");
        root.GetProperty("description").GetString().ShouldBe("A test game");
        root.GetProperty("year").GetString().ShouldBe("1994");
        root.GetProperty("manufacturer").GetString().ShouldBe("Test Corp");
        root.GetProperty("roms").GetArrayLength().ShouldBe(2);
        root.TryGetProperty("disks", out var disks).ShouldBeTrue();
        disks.ValueKind.ShouldBe(JsonValueKind.Array);
    }

    [Fact]
    public async Task GetGameById_NonExistentDat_Returns404()
    {
        // Arrange
        string nonExistentDatId = IdCoder.Encode(999999);
        string nonExistentGameId = IdCoder.Encode(999999);

        // Act
        var response = await _client.GetAsync($"/api/dats/{nonExistentDatId}/games/{nonExistentGameId}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetGameById_NonExistentGame_Returns404()
    {
        // Arrange
        string uniqueName = $"NonExistent Game Test DAT {Guid.NewGuid():N}";
        string datContent = $"""
                             <?xml version="1.0"?>
                             <datafile>
                               <header>
                                 <name>{uniqueName}</name>
                                 <description>Desc</description>
                               </header>
                               <game name="Some Game">
                                 <rom name="test.rom" size="1024" sha1="1111111111111111111111111111111111111111"/>
                               </game>
                             </datafile>
                             """;

        using var content = TestHelpers.CreateMultipartContent(datContent, "nonexistent-game-test.dat");
        var uploadResponse = await _client.PostAsync("/api/upload/dat", content);
        string? jobId = JsonDocument.Parse(await uploadResponse.Content.ReadAsStringAsync())
            .RootElement.GetProperty("jobId").GetString();
        await TestHelpers.WaitForJobCompletionAsync(_client, jobId!);

        string datId = await TestHelpers.GetDatIdByNameAsync(_client, uniqueName);
        string nonExistentGameId = IdCoder.Encode(999999);

        // Act
        var response = await _client.GetAsync($"/api/dats/{datId}/games/{nonExistentGameId}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task
        GetGameById_GameInWrongDat_Returns404()
    {
        // Arrange
        string dat1Name = $"DAT 1 {Guid.NewGuid():N}";
        string dat2Name = $"DAT 2 {Guid.NewGuid():N}";

        string dat1Content = $"""
                              <?xml version="1.0"?>
                              <datafile>
                                <header><name>{dat1Name}</name><description>Desc</description></header>
                                <game name="Game in DAT 1">
                                  <rom name="dat1.rom" size="1024" sha1="2222222222222222222222222222222222222222"/>
                                </game>
                              </datafile>
                              """;
        string dat2Content = $"""
                              <?xml version="1.0"?>
                              <datafile>
                                <header><name>{dat2Name}</name><description>Desc</description></header>
                                <game name="Game in DAT 2">
                                  <rom name="dat2.rom" size="1024" sha1="3333333333333333333333333333333333333333"/>
                                </game>
                              </datafile>
                              """;

        // Upload DAT 1
        using var content1 = TestHelpers.CreateMultipartContent(dat1Content, "dat1.dat");
        var response1 = await _client.PostAsync("/api/upload/dat", content1);
        string? jobId1 = JsonDocument.Parse(await response1.Content.ReadAsStringAsync())
            .RootElement.GetProperty("jobId").GetString();
        await TestHelpers.WaitForJobCompletionAsync(_client, jobId1!);
        string dat1Id = await TestHelpers.GetDatIdByNameAsync(_client, dat1Name);

        // Upload DAT 2
        using var content2 = TestHelpers.CreateMultipartContent(dat2Content, "dat2.dat");
        var response2 = await _client.PostAsync("/api/upload/dat", content2);
        string? jobId2 = JsonDocument.Parse(await response2.Content.ReadAsStringAsync())
            .RootElement.GetProperty("jobId").GetString();
        await TestHelpers.WaitForJobCompletionAsync(_client, jobId2!);
        string dat2Id = await TestHelpers.GetDatIdByNameAsync(_client, dat2Name);

        // Get Game ID from DAT 1
        var games1Response = await _client.GetAsync($"/api/dats/{dat1Id}/games");
        string? game1Id = JsonDocument.Parse(await games1Response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("items")[0].GetProperty("id").GetString();

        // Act - Try to access Game 1 via DAT 2
        var response = await _client.GetAsync($"/api/dats/{dat2Id}/games/{game1Id}");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
