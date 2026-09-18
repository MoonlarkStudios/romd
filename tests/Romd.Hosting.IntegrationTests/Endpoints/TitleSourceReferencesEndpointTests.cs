using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Romd.Application.Common.Ids;
using Romd.Domain.Catalog;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Dat;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

/// <summary>
///     Integration tests for the title source-references endpoint.
///     Uses a shared fixture to avoid Hangfire static state issues.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class TitleSourceReferencesEndpointTests : IDisposable
{
    private readonly HttpClient _client;
    private readonly IntegrationTestFixture _fixture;

    public TitleSourceReferencesEndpointTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.CreateAuthenticatedClient();
    }

    public void Dispose() => _client.Dispose();

    [Fact]
    public async Task GetTitleSourceReferences_TitleBackedByTwoSources_ReturnsTruthLevelReferences()
    {
        // Arrange - a title backed by a Dat-kind source (two entries, Active) and an
        // Import-kind source (one entry, Disabled). Truth-level reads return both.
        string suffix = Guid.NewGuid().ToString("N");
        string datName = $"Reference DAT {suffix}";
        string importName = $"Bulk Import {suffix}";
        int titleId;
        int datCatalogSourceId;
        int importCatalogSourceId;
        using (var scope = _fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();

            var platform = new PlatformEntity
            {
                Name = $"Reference Platform {suffix}",
                Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Installation, BaseName = $"Reference Platform {suffix}", BaseCompactLabel = "Reference", CanonicalKey = $"local-ref-{suffix}",
                ShortName = $"ref-{suffix}"
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

            var title = new TitleEntity
            {
                PlatformId = platform.Id,
                Name = $"Reference Title {suffix}",
                NormalizedName = $"reference title {suffix}",
                EnrichmentStatus = "None"
            };
            db.Titles.Add(title);

            var datCatalogSource = new CatalogSourceEntity
            {
                Kind = nameof(CatalogSourceKind.Dat),
                Status = nameof(CatalogSourceStatus.Active)
            };
            db.DatFiles.Add(new DatFileEntity
            {
                Source = new DatSourceEntity { CatalogSource = datCatalogSource },
                Name = datName,
                Description = datName,
                Type = nameof(DatType.NoIntro),
                PlatformId = platform.Id,
                OriginalFilename = "reference.dat",
                FileId = storedFile.Id,
                Lifecycle = nameof(DatFileLifecycle.Active)
            });

            var importCatalogSource = new CatalogSourceEntity
            {
                Kind = nameof(CatalogSourceKind.Import),
                Status = nameof(CatalogSourceStatus.Disabled),
                Name = importName
            };
            db.CatalogSources.Add(importCatalogSource);
            await db.SaveChangesAsync();

            var entries = new[]
            {
                NewEntry(datCatalogSource.Id, $"Game A {suffix}", platform.Id),
                NewEntry(datCatalogSource.Id, $"Game B {suffix}", platform.Id),
                NewEntry(importCatalogSource.Id, $"Game C {suffix}", platform.Id)
            };
            db.SourceEntries.AddRange(entries);
            await db.SaveChangesAsync();

            db.TitleSourceLinks.AddRange(entries.Select(entry => new TitleSourceLinkEntity
            {
                SourceEntryId = entry.Id,
                TitleId = title.Id
            }));
            await db.SaveChangesAsync();

            titleId = title.Id;
            datCatalogSourceId = datCatalogSource.Id;
            importCatalogSourceId = importCatalogSource.Id;
        }

        // Act
        var response = await _client.GetAsync($"/api/titles/{IdCoder.Encode(titleId)}/source-references");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.ValueKind.ShouldBe(JsonValueKind.Array);
        var references = doc.RootElement.EnumerateArray().ToList();
        references.Count.ShouldBe(2);

        var datReference = references.Single(r => r.GetProperty("kind").GetString() == "Dat");
        datReference.GetProperty("catalogSourceId").GetString().ShouldBe(IdCoder.Encode(datCatalogSourceId));
        datReference.GetProperty("name").GetString().ShouldBe(datName);
        datReference.GetProperty("status").GetString().ShouldBe("Active");
        datReference.GetProperty("entryCount").GetInt32().ShouldBe(2);

        var importReference = references.Single(r => r.GetProperty("kind").GetString() == "Import");
        importReference.GetProperty("catalogSourceId").GetString().ShouldBe(IdCoder.Encode(importCatalogSourceId));
        importReference.GetProperty("name").GetString().ShouldBe(importName);
        importReference.GetProperty("status").GetString().ShouldBe("Disabled");
        importReference.GetProperty("entryCount").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task GetTitleSourceReferences_NonExistentTitle_Returns404()
    {
        // Arrange
        string nonExistentId = IdCoder.Encode(999999);

        // Act
        var response = await _client.GetAsync($"/api/titles/{nonExistentId}/source-references");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private static SourceEntryEntity NewEntry(int catalogSourceId, string entryKey, int platformId) =>
        new()
        {
            CatalogSourceId = catalogSourceId,
            EntryKey = entryKey,
            Name = entryKey,
            PlatformId = platformId
        };

    private static Sha256 NewRandomSha256()
    {
        var bytes = new byte[Sha256.ByteLength];
        RandomNumberGenerator.Fill(bytes);
        return Sha256.FromBytes(bytes);
    }
}
