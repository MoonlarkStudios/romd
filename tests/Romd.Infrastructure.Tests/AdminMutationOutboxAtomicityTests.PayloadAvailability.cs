using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Catalog.Commands.SetSourceStatus;
using Romd.Admin.Application.Configuration;
using Romd.Admin.Application.Libraries;
using Romd.Admin.Application.Source.Dat;
using Romd.Admin.Application.Source.Dat.Commands.ActivateDatVersion;
using Romd.Admin.Application.Source.Dat.Commands.DeleteDat;
using Romd.Admin.Application.Source.Rom.Commands.DeleteRom;
using Romd.Admin.Application.Source.Rom.Commands.BatchDelete;
using Romd.Admin.Application.Storage.Files;
using Romd.Admin.Application.Titles.Commands.MergeTitles;
using Romd.Admin.Application.Titles.Commands.MoveGame;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Domain.Catalog;
using Romd.Domain.Source.Dat;
using Romd.Infrastructure.Catalog;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.Infrastructure.Source;
using Romd.Infrastructure.Tests.Helpers;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests;

public sealed partial class AdminMutationOutboxAtomicityTests
{
    [Fact]
    public async Task DeleteRom_FirstThenLastPayloadThroughSetNull_ChangesFactsOnlyAtLastPayload()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedCatalogMutationGraphAsync(database.Context, routed: true);
        await AddLocalPayloadAsync(database.Context, datGameId: 40, romFileId: 70, datRomId: 80,
            fileId: 60, Sha1One, Md5One, CrcOne);
        await AddLocalPayloadAsync(database.Context, datGameId: 40, romFileId: 71, datRomId: 81,
            fileId: 61, Sha1Two, Md5Two, CrcTwo);
        await RefreshEntryAssertionsAsync(database.Context, 30);

        (await CreatePayloadDeleteHandler(database).HandleAsync(new DeleteRomCommand(70)))
            .IsError.ShouldBeFalse();

        await using (var firstRead = database.CreateReadContext())
        {
            (await firstRead.DatRoms.SingleAsync(rom => rom.Id == 80)).RomFileId.ShouldBeNull();
            (await firstRead.DatRoms.SingleAsync(rom => rom.Id == 81)).RomFileId.ShouldBe(71);
            (await firstRead.SourceEntries.SingleAsync(entry => entry.Id == 30))
                .HasLocalPayload.ShouldBeTrue();
            (await firstRead.Titles.SingleAsync(title => title.Id == 101))
                .HasLocalPayload.ShouldBeTrue();
        }

        (await CreatePayloadDeleteHandler(database).HandleAsync(new DeleteRomCommand(71)))
            .IsError.ShouldBeFalse();

        await using var lastRead = database.CreateReadContext();
        (await lastRead.DatRoms.SingleAsync(rom => rom.Id == 81)).RomFileId.ShouldBeNull();
        (await lastRead.SourceEntries.SingleAsync(entry => entry.Id == 30))
            .HasLocalPayload.ShouldBeFalse();
        (await lastRead.Titles.SingleAsync(title => title.Id == 101))
            .HasLocalPayload.ShouldBeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BatchDelete_CommitOutcome_ChangesRomLinkAndPayloadFactsAtomically(bool failCommit)
    {
        await using var database = await TestDatabase.CreateAsync(failCommit ? 1 : null);
        await SeedCatalogMutationGraphAsync(database.Context, routed: true);
        await AddLocalPayloadAsync(database.Context, datGameId: 40, romFileId: 70, datRomId: 80,
            fileId: 60, Sha1One, Md5One, CrcOne);
        await RefreshEntryAssertionsAsync(database.Context, 30);
        var catalogReader = new RomCatalogOwnershipReader(database.Context);
        var fileStorage = Substitute.For<IFileStorageService>();
        fileStorage.DeleteIfUnreferencedAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(true);
        var handler = new BatchDeleteRomsCommandHandler(
            new RomRepository(database.Context),
            catalogReader,
            catalogReader,
            fileStorage,
            database.Outbox,
            database.UnitOfWork,
            new LibraryRepository(database.Context),
            CreateMatrixPayloadProjection(database.Context),
            NullLogger<BatchDeleteRomsCommandHandler>.Instance);

        var result = await handler.HandleAsync(new BatchDeleteRomsCommand { RomIds = [70] });

        result.IsError.ShouldBeFalse();
        result.Value.DeletedCount.ShouldBe(failCommit ? 0 : 1);
        result.Value.FailedCount.ShouldBe(failCommit ? 1 : 0);
        await using var read = database.CreateReadContext();
        (await read.RomFiles.AnyAsync(rom => rom.Id == 70)).ShouldBe(failCommit);
        (await read.DatRoms.SingleAsync(rom => rom.Id == 80)).RomFileId
            .ShouldBe(failCommit ? 70 : null);
        (await read.SourceEntries.SingleAsync(entry => entry.Id == 30)).HasLocalPayload
            .ShouldBe(failCommit);
        (await read.Titles.SingleAsync(title => title.Id == 101)).HasLocalPayload
            .ShouldBe(failCommit);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SetSourceStatus_CommitOutcome_ChangesStatusAndTitleFactAtomically(bool failCommit)
    {
        await using var database = await TestDatabase.CreateAsync(failCommit ? 1 : null);
        await SeedCatalogMutationGraphAsync(database.Context, routed: true);
        await AddLocalPayloadAsync(database.Context, datGameId: 40, romFileId: 70, datRomId: 80,
            fileId: 60, Sha1One, Md5One, CrcOne);
        await RefreshEntryAssertionsAsync(database.Context, 30);
        var handler = new SetSourceStatusCommandHandler(
            new DatRepository(database.Context, TimeProvider.System),
            new SourceLifecycleStore(database.Context),
            CatalogProjectionTestFactory.Create(database.Context),
            new LibraryRepository(database.Context),
            database.Outbox,
            database.UnitOfWork,
            NullLogger<SetSourceStatusCommandHandler>.Instance);

        var result = await handler.HandleAsync(
            SetSourceStatusCommand.Create(7, nameof(CatalogSourceStatus.Disabled)).Value);

        result.IsError.ShouldBe(failCommit);
        await using var read = database.CreateReadContext();
        (await read.CatalogSources.SingleAsync(source => source.Id == 20)).Status.ShouldBe(
            failCommit ? nameof(CatalogSourceStatus.Active) : nameof(CatalogSourceStatus.Disabled));
        (await read.SourceEntries.SingleAsync(entry => entry.Id == 30)).HasLocalPayload.ShouldBeTrue();
        (await read.Titles.SingleAsync(title => title.Id == 101)).HasLocalPayload
            .ShouldBe(failCommit);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MoveGame_PayloadEntryMovesBetweenTitles_FactsCommitOrRollbackTogether(
        bool failCommit)
    {
        await using var database = await TestDatabase.CreateAsync(failCommit ? 1 : null);
        await SeedCatalogMutationGraphAsync(database.Context, routed: true, secondSourceEntry: true);
        await AddLocalPayloadAsync(database.Context, datGameId: 40, romFileId: 70, datRomId: 80,
            fileId: 60, Sha1One, Md5One, CrcOne);
        await RefreshEntryAssertionsAsync(database.Context, 30);

        var repository = new DatRepository(database.Context, TimeProvider.System);
        var handler = new MoveGameCommandHandler(new Romd.Persistence.ReferenceData.ReferenceCatalogService(database.Context, Romd.PostgreSql.TestSupport.TestReferenceBlobs.Instance),
            new TitleRepository(database.Context),
            repository,
            database.UnitOfWork,
            new LibraryRepository(database.Context),
            new RebuildSuppressingCatalogProjection(RealProjection(database)),
            NullLogger<MoveGameCommandHandler>.Instance);

        if (failCommit)
        {
            await Should.ThrowAsync<InvalidOperationException>(() =>
                handler.HandleAsync(new MoveGameCommand(40, 202, null)));
        }
        else
        {
            (await handler.HandleAsync(new MoveGameCommand(40, 202, null)))
                .IsError.ShouldBeFalse();
        }

        await using var read = database.CreateReadContext();
        (await read.SourceEntries.SingleAsync(entry => entry.Id == 30))
            .HasLocalPayload.ShouldBeTrue();
        (await read.TitleSourceLinks.SingleAsync(link => link.SourceEntryId == 30)).TitleId
            .ShouldBe(failCommit ? 101 : 202);
        (await read.Titles.SingleAsync(title => title.Id == 101)).HasLocalPayload
            .ShouldBe(failCommit);
        (await read.Titles.SingleAsync(title => title.Id == 202)).HasLocalPayload
            .ShouldBe(!failCommit);
    }

    [Fact]
    public async Task MergeTitles_PayloadAssignmentsMoveToTarget_TargetBecomesAvailable()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedCatalogMutationGraphAsync(database.Context, routed: true, secondSourceEntry: true);
        await AddLocalPayloadAsync(database.Context, datGameId: 40, romFileId: 70, datRomId: 80,
            fileId: 60, Sha1One, Md5One, CrcOne);
        await RefreshEntryAssertionsAsync(database.Context, 30);

        var repository = new DatRepository(database.Context, TimeProvider.System);
        var handler = new MergeTitlesCommandHandler(new Romd.Persistence.ReferenceData.ReferenceCatalogService(database.Context, Romd.PostgreSql.TestSupport.TestReferenceBlobs.Instance),
            new TitleRepository(database.Context),
            new TrackedTitleRepository(database.Context, TimeProvider.System),
            repository,
            database.UnitOfWork,
            Substitute.For<IRematerializationScheduler>(),
            new LibraryRepository(database.Context),
            new RebuildSuppressingCatalogProjection(RealProjection(database)),
            Options.Create(new EnrichmentOptions()),
            NullLogger<MergeTitlesCommandHandler>.Instance);

        (await handler.HandleAsync(new MergeTitlesCommand(101, 202))).IsError.ShouldBeFalse();

        await using var read = database.CreateReadContext();
        (await read.Titles.AnyAsync(title => title.Id == 101)).ShouldBeFalse();
        (await read.Titles.SingleAsync(title => title.Id == 202)).HasLocalPayload.ShouldBeTrue();
        (await read.TitleSourceLinks.AllAsync(link => link.TitleId == 202)).ShouldBeTrue();
        (await read.SourceEntries.SingleAsync(entry => entry.Id == 30))
            .HasLocalPayload.ShouldBeTrue();
    }

    [Fact]
    public async Task RefreshSourceEntryPayloadAssertions_BiosEntryWithoutTitleLink_DoesNotMintTitleAvailability()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedCatalogMutationGraphAsync(database.Context, routed: true);
        await database.Context.TitleSourceLinks
            .Where(link => link.SourceEntryId == 30)
            .ExecuteDeleteAsync();
        await AddLocalPayloadAsync(database.Context, datGameId: 40, romFileId: 70, datRomId: 80,
            fileId: 60, Sha1One, Md5One, CrcOne);
        database.Context.Bios.Add(new BiosEntity
        {
            Id = 90,
            PlatformId = 10,
            Name = "Firmware",
            NormalizedName = "firmware",
            CreatedAt = DateTimeOffset.UtcNow
        });
        database.Context.BiosGameMappings.Add(new BiosGameMappingEntity
        {
            BiosId = 90,
            DatGameId = 40,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await database.Context.SaveChangesAsync();
        database.Context.ChangeTracker.Clear();

        await RefreshEntryAssertionsAsync(database.Context, 30);

        await using var read = database.CreateReadContext();
        (await read.SourceEntries.SingleAsync(entry => entry.Id == 30))
            .HasLocalPayload.ShouldBeTrue();
        (await read.Titles.AnyAsync(title => title.HasLocalPayload)).ShouldBeFalse();
        (await read.TitleSourceLinks.AnyAsync()).ShouldBeFalse();
    }

    [Fact]
    public async Task DeleteDat_LastSourceVersionPrunesPayloadEntry_SurvivingTitleBecomesUnavailable()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedPlatformFileAndDatAsync(database.Context, datId: 7, fileId: 17, platformId: 10);
        await SeedGameWithRomAsync(database.Context, gameId: 70, datFileId: 7, Sha1One);
        await AddLocalPayloadAsync(database.Context, datGameId: 70, romFileId: 71, datRomId: 701,
            fileId: 61, Sha1Two, Md5Two, CrcTwo);
        var now = DateTimeOffset.UtcNow;
        database.Context.CatalogSources.Add(new CatalogSourceEntity
        {
            Id = 220,
            Kind = nameof(CatalogSourceKind.Import),
            Status = nameof(CatalogSourceStatus.Active),
            CreatedAt = now
        });
        database.Context.SourceEntries.Add(new SourceEntryEntity
        {
            Id = 230,
            CatalogSourceId = 220,
            EntryKey = "surviving-import",
            Name = "Surviving import",
            PlatformId = 10,
            CreatedAt = now
        });
        database.Context.Titles.Add(NewTitleEntity(101, "Surviving title", now));
        database.Context.TitleSourceLinks.AddRange(
            new TitleSourceLinkEntity { SourceEntryId = 70, TitleId = 101, CreatedAt = now },
            new TitleSourceLinkEntity { SourceEntryId = 230, TitleId = 101, CreatedAt = now });
        await database.Context.SaveChangesAsync();
        database.Context.ChangeTracker.Clear();
        await RefreshEntryAssertionsAsync(database.Context, 70);

        (await NewDeleteDatHandler(database, database.UnitOfWork)
            .HandleAsync(DeleteDatCommand.Create(7).Value)).IsError.ShouldBeFalse();

        await using var read = database.CreateReadContext();
        (await read.DatSources.AnyAsync()).ShouldBeFalse();
        (await read.SourceEntries.AnyAsync(entry => entry.Id == 70)).ShouldBeFalse();
        (await read.SourceEntries.AnyAsync(entry => entry.Id == 230)).ShouldBeTrue();
        (await read.Titles.SingleAsync(title => title.Id == 101)).HasLocalPayload.ShouldBeFalse();
    }

    [Fact]
    public async Task ActivateDatVersion_PrunesOldPayloadEntry_SurvivingVersionRecomputesTitleFalse()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedSourceWithActiveAndPendingAsync(database.Context);
        await AddLocalPayloadAsync(database.Context, datGameId: 70, romFileId: 71, datRomId: 701,
            fileId: 61, Sha1Three, Md5Three, CrcThree);
        var now = DateTimeOffset.UtcNow;
        database.Context.Titles.Add(NewTitleEntity(101, "Versioned title", now));
        database.Context.TitleSourceLinks.AddRange(
            new TitleSourceLinkEntity { SourceEntryId = 70, TitleId = 101, CreatedAt = now },
            new TitleSourceLinkEntity { SourceEntryId = 80, TitleId = 101, CreatedAt = now });
        await database.Context.SaveChangesAsync();
        database.Context.ChangeTracker.Clear();
        await RefreshEntriesAssertionsAsync(database.Context, 70, 80);

        (await NewActivateHandler(database, database.UnitOfWork)
            .HandleAsync(ActivateDatVersionCommand.Create(8).Value)).IsError.ShouldBeFalse();

        await using var read = database.CreateReadContext();
        (await read.SourceEntries.AnyAsync(entry => entry.Id == 70)).ShouldBeFalse();
        (await read.SourceEntries.SingleAsync(entry => entry.Id == 80))
            .HasLocalPayload.ShouldBeFalse();
        (await read.Titles.SingleAsync(title => title.Id == 101)).HasLocalPayload.ShouldBeFalse();
    }

    private static DeleteRomCommandHandler CreatePayloadDeleteHandler(TestDatabase database)
    {
        var catalogReader = new RomCatalogOwnershipReader(database.Context);
        var fileStorage = Substitute.For<IFileStorageService>();
        fileStorage.DeleteIfUnreferencedAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(true);
        return new DeleteRomCommandHandler(
            new RomRepository(database.Context),
            catalogReader,
            catalogReader,
            fileStorage,
            database.Outbox,
            database.UnitOfWork,
            new LibraryRepository(database.Context),
            CreateMatrixPayloadProjection(database.Context),
            NullLogger<DeleteRomCommandHandler>.Instance);
    }

    private static TitlePayloadAvailabilityProjection CreateMatrixPayloadProjection(
        RomdDbContext context) =>
        new(
            context,
            new CatalogPayloadAssertionReader(
                context,
                [new DatCatalogPayloadAssertionProvider(context)]));

    private static async Task RefreshEntryAssertionsAsync(RomdDbContext context, int sourceEntryId)
    {
        await RefreshEntriesAssertionsAsync(context, sourceEntryId);
    }

    private static async Task RefreshEntriesAssertionsAsync(
        RomdDbContext context,
        params int[] sourceEntryIds)
    {
        await using var transaction = await context.Database.BeginTransactionAsync();
        await CreateMatrixPayloadProjection(context)
            .RefreshSourceEntryPayloadAssertionsAsync(sourceEntryIds);
        await transaction.CommitAsync();
        context.ChangeTracker.Clear();
    }

    private static async Task AddLocalPayloadAsync(
        RomdDbContext context,
        int datGameId,
        int romFileId,
        int datRomId,
        int fileId,
        Romd.Domain.Hashing.Sha1 sha1,
        Romd.Domain.Hashing.Md5 md5,
        Romd.Domain.Hashing.Crc32 crc32)
    {
        context.Files.Add(NewFile(fileId, DateTimeOffset.UtcNow));
        context.RomFiles.Add(new RomFileEntity
        {
            Id = romFileId,
            FileId = fileId,
            OriginalFilename = $"payload-{romFileId}.rom",
            Sha1 = sha1,
            Md5 = md5,
            Crc32 = crc32,
            CreatedAt = DateTimeOffset.UtcNow
        });
        context.DatRoms.Add(new DatRomEntity
        {
            Id = datRomId,
            DatGameId = datGameId,
            RomFileId = romFileId,
            Name = $"payload-{romFileId}.rom",
            Size = 1,
            Sha1 = sha1,
            Md5 = md5,
            Crc = crc32,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
    }
}
