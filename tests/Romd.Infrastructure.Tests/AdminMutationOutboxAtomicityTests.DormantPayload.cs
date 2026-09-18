using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Hashing;
using Romd.Admin.Application.Libraries;
using Romd.Admin.Application.Source.Dat;
using Romd.Admin.Application.Source.Rom.Commands.DeleteRom;
using Romd.Admin.Application.Source.Rom.Commands.IngestRom;
using Romd.Admin.Application.Storage.Files;
using Romd.Admin.Application.Titles;
using Romd.Domain.Catalog;
using Romd.Domain.Libraries;
using Romd.Domain.Source.Dat;
using Romd.Infrastructure.Catalog;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.Infrastructure.Source;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests;

public sealed partial class AdminMutationOutboxAtomicityTests
{
    private const int DormantCatalogSourceId = 220;
    private const int DormantSourceEntryId = 230;
    private const int DormantTitleId = 250;
    private const int DormantRomFileId = 209;

    [Theory]
    [InlineData(nameof(CatalogSourceStatus.Disabled))]
    [InlineData(nameof(CatalogSourceStatus.Discontinued))]
    public async Task DormantSource_IngestRomThenReactivate_UsesTruthLevelPayloadAssertion(
        string dormantStatus)
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedDormantRomPayloadAsync(database.Context, dormantStatus, linked: false);
        var projection = CreatePayloadAvailabilityProjection(database.Context);

        var result = await CreateDormantIngestHandler(database, projection).HandleAsync(
            new IngestRomCommand(new MemoryStream([1]), "dormant.rom", AllowUnidentified: true));

        result.IsError.ShouldBeFalse();
        result.Value.MatchedTitleIds.ShouldBeEmpty();
        await using (var dormantRead = database.CreateReadContext())
        {
            (await dormantRead.SourceEntries.SingleAsync(entry => entry.Id == DormantSourceEntryId))
                .HasLocalPayload.ShouldBeTrue();
            (await dormantRead.Titles.SingleAsync(title => title.Id == DormantTitleId))
                .HasLocalPayload.ShouldBeFalse();
        }

        await ReactivateAndRollupAsync(database.Context, projection);

        await using var activeRead = database.CreateReadContext();
        (await activeRead.Titles.SingleAsync(title => title.Id == DormantTitleId))
            .HasLocalPayload.ShouldBeTrue();
    }

    [Theory]
    [InlineData(nameof(CatalogSourceStatus.Disabled))]
    [InlineData(nameof(CatalogSourceStatus.Discontinued))]
    public async Task DormantSource_DeleteRomThenReactivate_DoesNotRestoreStalePayload(
        string dormantStatus)
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedDormantRomPayloadAsync(database.Context, dormantStatus, linked: true);
        var projection = CreatePayloadAvailabilityProjection(database.Context);

        var result = await CreateDormantDeleteHandler(database, projection)
            .HandleAsync(new DeleteRomCommand(DormantRomFileId));

        result.IsError.ShouldBeFalse();
        await using (var dormantRead = database.CreateReadContext())
        {
            (await dormantRead.SourceEntries.SingleAsync(entry => entry.Id == DormantSourceEntryId))
                .HasLocalPayload.ShouldBeFalse();
            (await dormantRead.Titles.SingleAsync(title => title.Id == DormantTitleId))
                .HasLocalPayload.ShouldBeFalse();
        }

        await ReactivateAndRollupAsync(database.Context, projection);

        await using var activeRead = database.CreateReadContext();
        (await activeRead.Titles.SingleAsync(title => title.Id == DormantTitleId))
            .HasLocalPayload.ShouldBeFalse();
    }

    [Fact]
    public async Task DormantSource_DeleteRomCommitFailure_RollsBackPayloadAssertionWithRomLink()
    {
        await using var database = await TestDatabase.CreateAsync(failCommitNumber: 1);
        await SeedDormantRomPayloadAsync(
            database.Context,
            nameof(CatalogSourceStatus.Disabled),
            linked: true);
        var projection = CreatePayloadAvailabilityProjection(database.Context);

        var result = await CreateDormantDeleteHandler(database, projection)
            .HandleAsync(new DeleteRomCommand(DormantRomFileId));

        result.IsError.ShouldBeTrue();
        database.UnitOfWork.RollbackCleanupCount.ShouldBe(1);
        await using (var dormantRead = database.CreateReadContext())
        {
            (await dormantRead.DatRoms.SingleAsync()).RomFileId.ShouldBe(DormantRomFileId);
            (await dormantRead.SourceEntries.SingleAsync(entry => entry.Id == DormantSourceEntryId))
                .HasLocalPayload.ShouldBeTrue();
        }

        await ReactivateAndRollupAsync(database.Context, projection);

        await using var activeRead = database.CreateReadContext();
        (await activeRead.Titles.SingleAsync(title => title.Id == DormantTitleId))
            .HasLocalPayload.ShouldBeTrue();
    }

    private static IngestRomCommandHandler CreateDormantIngestHandler(
        TestDatabase database,
        ITitlePayloadAvailabilityProjection projection)
    {
        var tempFile = new AtomicityTempFile([1], NewSha256(219));
        var tempFiles = Substitute.For<ITempFileFactory>();
        tempFiles.CreateAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>()).Returns(tempFile);
        var hashing = Substitute.For<IHashingService>();
        hashing.ComputeDatHashesAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(new DatHashResult(Sha1One, Md5One, CrcOne, 1));
        var catalogReader = new RomCatalogOwnershipReader(database.Context);

        return new IngestRomCommandHandler(
            new RomRepository(database.Context),
            new DatRepository(database.Context, TimeProvider.System),
            catalogReader,
            Substitute.For<IFileStorageService>(),
            tempFiles,
            hashing,
            database.UnitOfWork,
            database.Outbox,
            new LibraryRepository(database.Context),
            new TitleRepository(database.Context),
            catalogReader,
            projection,
            NullLogger<IngestRomCommandHandler>.Instance);
    }

    private static DeleteRomCommandHandler CreateDormantDeleteHandler(
        TestDatabase database,
        ITitlePayloadAvailabilityProjection projection)
    {
        var fileStorage = Substitute.For<IFileStorageService>();
        fileStorage.DeleteIfUnreferencedAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(true);
        var catalogReader = new RomCatalogOwnershipReader(database.Context);
        return new DeleteRomCommandHandler(
            new RomRepository(database.Context),
            catalogReader,
            catalogReader,
            fileStorage,
            database.Outbox,
            database.UnitOfWork,
            new LibraryRepository(database.Context),
            projection,
            NullLogger<DeleteRomCommandHandler>.Instance);
    }

    private static TitlePayloadAvailabilityProjection CreatePayloadAvailabilityProjection(
        RomdDbContext context) =>
        new(
            context,
            new CatalogPayloadAssertionReader(
                context,
                [new DatCatalogPayloadAssertionProvider(context)]));

    private static async Task ReactivateAndRollupAsync(
        RomdDbContext context,
        ITitlePayloadAvailabilityProjection projection)
    {
        await using var transaction = await context.Database.BeginTransactionAsync();
        await context.CatalogSources
            .Where(source => source.Id == DormantCatalogSourceId)
            .ExecuteUpdateAsync(update =>
                update.SetProperty(source => source.Status, nameof(CatalogSourceStatus.Active)));
        await projection.RollupCatalogSourceAsync(DormantCatalogSourceId);
        await transaction.CommitAsync();
    }

    private static async Task SeedDormantRomPayloadAsync(
        RomdDbContext context,
        string sourceStatus,
        bool linked)
    {
        context.Platforms.Add(new PlatformEntity
        {
            Id = 210,
            Name = "Dormant platform",
            Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "Dormant platform", BaseCompactLabel = "Dormant platform", CanonicalKey = "dormant", ShortName = "dormant",
            CreatedAt = DateTimeOffset.UtcNow
        });
        context.Files.AddRange(
            NewFile(211, DateTimeOffset.UtcNow),
            NewFile(212, DateTimeOffset.UtcNow));
        context.RomFiles.Add(new RomFileEntity
        {
            Id = DormantRomFileId,
            OriginalFilename = "dormant.rom",
            FileId = 211,
            Sha1 = Sha1One,
            Md5 = Md5One,
            Crc32 = CrcOne,
            CreatedAt = DateTimeOffset.UtcNow
        });
        context.DatFiles.Add(new DatFileEntity
        {
            Source = new DatSourceEntity
            {
                CatalogSource = new CatalogSourceEntity
                {
                    Id = DormantCatalogSourceId,
                    Kind = nameof(CatalogSourceKind.Dat),
                    Status = sourceStatus
                }
            },
            Id = 221,
            Name = "Dormant DAT",
            Description = "Dormant DAT",
            Type = DatType.NoIntro.ToString(),
            PlatformId = 210,
            OriginalFilename = "dormant.dat",
            FileId = 212,
            CreatedAt = DateTimeOffset.UtcNow
        });
        context.SourceEntries.Add(new SourceEntryEntity
        {
            Id = DormantSourceEntryId,
            CatalogSourceId = DormantCatalogSourceId,
            EntryKey = "dormant-game",
            Name = "Dormant game",
            PlatformId = 210,
            HasLocalPayload = linked,
            CreatedAt = DateTimeOffset.UtcNow
        });
        context.DatGames.Add(new DatGameEntity
        {
            Id = 240,
            DatFileId = 221,
            SourceEntryId = DormantSourceEntryId,
            Name = "Dormant game",
            CreatedAt = DateTimeOffset.UtcNow
        });
        context.DatRoms.Add(new DatRomEntity
        {
            Id = 241,
            DatGameId = 240,
            Name = "dormant.rom",
            Size = 1,
            Sha1 = Sha1One,
            RomFileId = linked ? DormantRomFileId : null,
            CreatedAt = DateTimeOffset.UtcNow
        });
        context.Titles.Add(new TitleEntity
        {
            Id = DormantTitleId,
            PlatformId = 210,
            Name = "Dormant title",
            NormalizedName = "dormanttitle",
            EnrichmentStatus = "None",
            HasLocalPayload = false,
            CreatedAt = DateTimeOffset.UtcNow
        });
        context.TitleSourceLinks.Add(new TitleSourceLinkEntity
        {
            SourceEntryId = DormantSourceEntryId,
            TitleId = DormantTitleId
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
    }
}
