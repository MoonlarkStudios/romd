using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Romd.Application.Common.Ids;
using Romd.Contracts.Management.Libraries;
using Romd.Domain.Hashing;
using Romd.Domain.Identity;
using Romd.Domain.Libraries;
using Romd.Hosting.IntegrationTests.Infrastructure;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Shouldly;
using Xunit;

namespace Romd.Hosting.IntegrationTests.Endpoints;

[Collection(IntegrationTestCollection.Name)]
public sealed class LibraryReleaseDiagnosticsEndpointsTests(IntegrationTestFixture fixture)
{
    [Fact]
    public async Task GetLibraryTitleReleaseDiagnostics_Admin_ReturnsExposureDiagnostics()
    {
        var seed = await SeedDiagnosticsAsync(fixture);
        using var client = fixture.CreateAuthenticatedClient();

        var response = await client.GetAsync(
            $"/api/libraries/{IdCoder.Encode(seed.LibraryId)}/titles/{IdCoder.Encode(seed.TitleId)}/releases");
        var body = await response.Content.ReadFromJsonAsync<IReadOnlyList<LibraryTitleReleaseDiagnosticsDto>>();

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        body.ShouldNotBeNull();
        body.Count.ShouldBe(2);
        body[0].Id.ShouldBe(IdCoder.Encode(seed.ExposedReleaseId));
        body[0].DatId.ShouldBe(IdCoder.Encode(seed.DatFileId));
        body[0].Name.ShouldBe("Diagnostics Test (USA)");
        body[0].IsEligible.ShouldBeTrue();
        body[0].IsBlocked.ShouldBeFalse();
        body[0].BlockReason.ShouldBeNull();
        body[0].IsExposed.ShouldBeTrue();
        body[0].ExposureReason.ShouldBe("ExposedDefault");
        body[1].Id.ShouldBe(IdCoder.Encode(seed.BlockedReleaseId));
        body[1].IsEligible.ShouldBeFalse();
        body[1].IsBlocked.ShouldBeTrue();
        body[1].BlockReason.ShouldBe("ExcludedDat");
        body[1].IsExposed.ShouldBeFalse();
        body[1].ExposureReason.ShouldBe("ExcludedDat");
    }

    private static async Task<DiagnosticsSeed> SeedDiagnosticsAsync(IntegrationTestFixture fixture)
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        var now = DateTimeOffset.UtcNow;
        string unique = Guid.NewGuid().ToString("N");
        var library = LibraryEntity.FromDomain(Library.CreateNew(
            $"Diagnostics {unique}",
            new LibraryConfiguration()));
        var platform = new PlatformEntity
        {
            Name = $"Diagnostics Platform {unique}",
            Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Installation, BaseName = $"Diagnostics Platform {unique}", BaseCompactLabel = "Diagnostics", CanonicalKey = $"local-diag-{unique[..12]}",
            ShortName = $"diag-{unique[..12]}",
            Manufacturer = "Test",
            CreatedAt = now,
            CreatedByUserId = SystemActor.UserId
        };
        var file = new FileEntityPersistence
        {
            Sha256 = NewSha256(unique),
            Size = 1,
            SizeOnDisk = 1,
            IsCompressed = false,
            CreatedAt = now,
            CreatedByUserId = SystemActor.UserId
        };

        context.Libraries.Add(library);
        context.Platforms.Add(platform);
        context.Files.Add(file);
        await context.SaveChangesAsync();

        var datFile = new DatFileEntity
        {
            Source = new DatSourceEntity
            {
                CatalogSource = new CatalogSourceEntity { Kind = "Dat", Status = "Active" }
            },
            Name = $"Diagnostics DAT {unique}",
            Description = "Diagnostics DAT",
            Type = "NoIntro",
            PlatformId = platform.Id,
            OriginalFilename = $"diagnostics-{unique}.dat",
            FileId = file.Id,
            GameCount = 2,
            CreatedAt = now,
            CreatedByUserId = SystemActor.UserId
        };
        var title = new TitleEntity
        {
            PlatformId = platform.Id,
            Name = $"Diagnostics Test {unique}",
            NormalizedName = $"diagnostics test {unique}",
            Genre = "RPG",
            EnrichmentStatus = "None",
            CreatedAt = now,
            CreatedByUserId = SystemActor.UserId
        };
        context.DatFiles.Add(datFile);
        context.Titles.Add(title);
        await context.SaveChangesAsync();

        var recommendedEntry = NewSourceEntry(
            datFile.Source!.CatalogSourceId, "Diagnostics Test (USA)", platform.Id, now);
        var hiddenEntry = NewSourceEntry(
            datFile.Source!.CatalogSourceId, "Diagnostics Test (Europe)", platform.Id, now);
        context.SourceEntries.AddRange(recommendedEntry, hiddenEntry);
        await context.SaveChangesAsync();

        var recommended = NewDatGame(datFile.Id, recommendedEntry.Id, "Diagnostics Test (USA)", now);
        var hidden = NewDatGame(datFile.Id, hiddenEntry.Id, "Diagnostics Test (Europe)", now);
        context.DatGames.AddRange(recommended, hidden);
        await context.SaveChangesAsync();

        context.MaterializedLibraryTitles.Add(new MaterializedLibraryTitleEntity
        {
            LibraryId = library.Id,
            TitleId = title.Id,
            PlatformId = platform.Id,
            Genre = "RPG",
            IsVisible = true,
            IsOwned = true,
            IsPlayable = true,
            EligibleReleaseCount = 1,
            PlayableReleaseCount = 1,
            ExposedReleaseCount = 1,
            Availability = LibraryTitleAvailability.Playable.ToString()
        });
        context.MaterializedLibraryReleases.AddRange(
            NewRelease(library.Id, title.Id, recommended.Id, datFile.Id, platform.Id, isBlocked: false),
            NewRelease(library.Id, title.Id, hidden.Id, datFile.Id, platform.Id, isBlocked: true));
        await context.SaveChangesAsync();

        return new DiagnosticsSeed(library.Id, title.Id, datFile.Id, recommended.Id, hidden.Id);
    }

    private static SourceEntryEntity NewSourceEntry(
        int catalogSourceId,
        string name,
        int platformId,
        DateTimeOffset now) =>
        new()
        {
            CatalogSourceId = catalogSourceId,
            EntryKey = name,
            Name = name,
            PlatformId = platformId,
            CreatedAt = now,
            CreatedByUserId = SystemActor.UserId
        };

    private static DatGameEntity NewDatGame(int datFileId, int sourceEntryId, string name, DateTimeOffset now) =>
        new()
        {
            DatFileId = datFileId,
            SourceEntryId = sourceEntryId,
            Name = name,
            CreatedAt = now,
            CreatedByUserId = SystemActor.UserId
        };

    private static MaterializedLibraryReleaseEntity NewRelease(
        int libraryId,
        int titleId,
        int datGameId,
        int datFileId,
        int platformId,
        bool isBlocked) =>
        new()
        {
            LibraryId = libraryId,
            TitleId = titleId,
            DatGameId = datGameId,
            DatFileId = datFileId,
            PlatformId = platformId,
            IsEligible = !isBlocked,
            IsComplete = true,
            IsOwned = true,
            IsPlayable = !isBlocked,
            IsBlocked = isBlocked,
            BlockReason = isBlocked ? "ExcludedDat" : null,
            IsExposed = !isBlocked,
            ExposureReason = isBlocked ? "ExcludedDat" : "ExposedDefault"
        };

    private static Sha256 NewSha256(string value)
    {
        var bytes = new byte[Sha256.ByteLength];
        Guid.Parse(value).ToByteArray().CopyTo(bytes, 0);
        return Sha256.FromBytes(bytes);
    }

    private sealed record DiagnosticsSeed(
        int LibraryId,
        int TitleId,
        int DatFileId,
        int ExposedReleaseId,
        int BlockedReleaseId);
}
