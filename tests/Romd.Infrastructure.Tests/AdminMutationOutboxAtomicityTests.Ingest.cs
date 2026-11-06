using Romd.Persistence.Catalog;
using Romd.PostgreSql.TestSupport;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Domain.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Polly.Registry;
using Romd.Infrastructure.Jobs.Processors;
using Romd.Infrastructure.Resilience;
using System.Text.Json;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Hashing;
using Romd.Admin.Application.Libraries;
using Romd.Admin.Application.Source.Dat;
using Romd.Admin.Application.Source.Dat.Commands.IngestDat;
using Romd.Admin.Application.Source.Platform;
using Romd.Admin.Application.Source.Rom.Commands.IngestRom;
using Romd.Admin.Application.Storage.Files;
using Romd.Admin.Application.Taxonomy;
using Romd.Admin.Application.Titles;
using Romd.Domain.Catalog;
using Romd.Domain.Hashing;
using Romd.Domain.Libraries;
using Romd.Domain.Source.Dat;
using Romd.Domain.Storage;
using Romd.Domain.Taxonomy;
using Romd.Infrastructure.Catalog;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests;

public sealed partial class AdminMutationOutboxAtomicityTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task IngestRomNewBranch_CommitOutcome_KeepsRomAndChildEventsAtomic(bool failCommit, bool withUpload)
    {
        await using var database = await TestDatabase.CreateAsync(failCommit ? 1 : null);
        await SeedFileAsync(database.Context, fileId: 41, createdAt: DateTimeOffset.UtcNow);

        var uploadClaim = withUpload ? await ClaimOutcomeJobAsync(database.Context,
            JobItemKind.Rom) : null;
        using var fence = uploadClaim is null ? null :
            JobExecutionFenceScope.Enter(uploadClaim.Job.Id, uploadClaim.FenceToken, TimeProvider.System);
        var uploadItem = uploadClaim is null ? null : new UploadItemContext(uploadClaim.Job.Id, "folder/game");

        var tempFile = new AtomicityTempFile([1], NewSha256(41));
        var tempFiles = Substitute.For<ITempFileFactory>();
        tempFiles.CreateAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>(), Arg.Any<Func<ReadOnlyMemory<byte>, bool>?>(), Arg.Any<Func<ReadOnlyMemory<byte>, CancellationToken, Task>?>(), Arg.Is<Func<ReadOnlyMemory<byte>, int>?>(selector => selector != null)).Returns(tempFile);
        var hashing = Substitute.For<IHashingService>();
        hashing.ComputeDatHashesAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(new DatHashResult(Sha1One, Md5One, CrcOne, 1));
        var fileStorage = Substitute.For<IFileStorageService>();
        var publication = Substitute.For<IStagedFilePublication>();
        publication.ReadAfterFlush().Returns(new FileStoreResult(
            FileEntity.Rehydrate(41, NewSha256(41), 1, 1, false, DateTimeOffset.UtcNow), false, true));
        fileStorage.PublishStagedAsync(Arg.Any<IStagedFile>(), Arg.Any<CancellationToken>()).Returns(publication);
        var dats = Substitute.For<IDatRepository>();
        var catalogMatches = Substitute.For<IRomCatalogMatchReader>();
        catalogMatches.ReadAsync(Sha1One, Arg.Any<CancellationToken>())
            .Returns(new RomCatalogMatch([], [], []));
        dats.LinkDatRomsToRomFileAsync(Sha1One, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(0);

        var handler = new IngestRomCommandHandler(
            Romd.PostgreSql.TestSupport.MediaTestServices.CreateRomRepository(database.Context),
            dats,
            catalogMatches,
            tempFiles,
            Romd.Infrastructure.Tests.Storage.MediaInfrastructureTestServices.ManagedPreparation(hashing, () => new Romd.Infrastructure.Storage.RvzVerifier(new()), new Romd.Infrastructure.Tests.Storage.MediaInfrastructureTestServices.EmptyReferences()), Substitute.For<Romd.Admin.Application.Source.Rom.Compressed.IRomCdIngestor>(),
            MediaTestServices.CreatePublisher(database.Context, fileStorage, database.UnitOfWork, TestRomMediaPolicies.Create(),
                Romd.Infrastructure.Storage.RomEncodingComposition.CreateRegistry(), Substitute.For<IFileMutationLock>(), new JobItemRepository(database.Context),
                new Romd.Infrastructure.Jobs.JobAuditContext(), TimeProvider.System),
            database.Outbox,
            Substitute.For<ILibraryRepository>(),
            Substitute.For<ITrackedTitleReader>(), Substitute.For<ITrackedTitleRepository>(),
            Substitute.For<IRomPayloadAssertionImpactReader>(),
            Substitute.For<ITitlePayloadAvailabilityProjection>(),
            NullLogger<IngestRomCommandHandler>.Instance, clock: TimeProvider.System);

        var command = new IngestRomCommand(new MemoryStream([1]), "new.rom", AllowUnidentified: true,
            UploadItem: uploadItem);
        if (failCommit && withUpload)
            await Should.ThrowAsync<UploadOutcomeUncertainException>(() => handler.HandleAsync(command));
        else
            (await handler.HandleAsync(command)).IsError.ShouldBe(failCommit);
        database.UnitOfWork.RollbackCleanupCount.ShouldBe(failCommit ? 1 : 0);
        await using var readContext = database.CreateReadContext();
        (await readContext.JobItems.CountAsync()).ShouldBe(withUpload && !failCommit ? 1 : 0);
        if (withUpload && !failCommit)
            (await readContext.JobItems.SingleAsync()).FileName.ShouldBe("folder/game");
        var persisted = await readContext.RomFiles.AsNoTracking().SingleOrDefaultAsync();
        (persisted is not null).ShouldBe(!failCommit);
        (await readContext.Verifications.CountAsync()).ShouldBe(failCommit ? 0 : 1);
        if (persisted is not null)
        {
            persisted.Id.ShouldBeGreaterThan(0);
            persisted.Sha1.ShouldBe(Sha1One);
        }

        (await GetEventTypesAsync(readContext)).ShouldBe(
            failCommit ? [] : StatsEventTypes());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task IngestRomExistingLinkBranch_CommitOutcome_KeepsLinkOwnershipLibraryAndEventsAtomic(
        bool failCommit, bool withUpload)
    {
        await using var database = await TestDatabase.CreateAsync(failCommit ? 1 : null);
        await SeedExistingRomCatalogMatchAsync(database.Context);

        var uploadClaim = withUpload ? await ClaimOutcomeJobAsync(database.Context,
            JobItemKind.Rom) : null;
        using var fence = uploadClaim is null ? null :
            JobExecutionFenceScope.Enter(uploadClaim.Job.Id, uploadClaim.FenceToken, TimeProvider.System);
        var uploadItem = uploadClaim is null ? null : new UploadItemContext(uploadClaim.Job.Id, "folder/game");

        var tempFile = new AtomicityTempFile([1], NewSha256(41));
        var tempFiles = Substitute.For<ITempFileFactory>();
        tempFiles.CreateAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>(), Arg.Any<Func<ReadOnlyMemory<byte>, bool>?>(), Arg.Any<Func<ReadOnlyMemory<byte>, CancellationToken, Task>?>(), Arg.Is<Func<ReadOnlyMemory<byte>, int>?>(selector => selector != null)).Returns(tempFile);
        var hashing = Substitute.For<IHashingService>();
        hashing.ComputeDatHashesAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(new DatHashResult(Sha1One, Md5One, CrcOne, 1));
        var fileStorage = Substitute.For<IFileStorageService>();

        var catalogReader = new RomCatalogOwnershipReader(database.Context);
        var handler = new IngestRomCommandHandler(
            Romd.PostgreSql.TestSupport.MediaTestServices.CreateRomRepository(database.Context),
            new DatRepository(database.Context, TimeProvider.System),
            catalogReader,
            tempFiles,
            Romd.Infrastructure.Tests.Storage.MediaInfrastructureTestServices.ManagedPreparation(hashing, () => new Romd.Infrastructure.Storage.RvzVerifier(new()), new Romd.Infrastructure.Tests.Storage.MediaInfrastructureTestServices.EmptyReferences()), Substitute.For<Romd.Admin.Application.Source.Rom.Compressed.IRomCdIngestor>(),
            MediaTestServices.CreatePublisher(database.Context, fileStorage, database.UnitOfWork, TestRomMediaPolicies.Create(),
                Romd.Infrastructure.Storage.RomEncodingComposition.CreateRegistry(), Substitute.For<IFileMutationLock>(), new JobItemRepository(database.Context),
                new Romd.Infrastructure.Jobs.JobAuditContext(), TimeProvider.System),
            database.Outbox,
            new LibraryRepository(database.Context),
            new TrackedTitleRepository(database.Context, TimeProvider.System), new TrackedTitleRepository(database.Context, TimeProvider.System),
            catalogReader,
            Substitute.For<ITitlePayloadAvailabilityProjection>(),
            NullLogger<IngestRomCommandHandler>.Instance, clock: TimeProvider.System);

        var command = new IngestRomCommand(new MemoryStream([1]), "existing.rom", AllowUnidentified: true,
            UploadItem: uploadItem);
        if (failCommit && withUpload)
            await Should.ThrowAsync<UploadOutcomeUncertainException>(() => handler.HandleAsync(command));
        else
            (await handler.HandleAsync(command)).IsError.ShouldBe(failCommit);
        database.UnitOfWork.RollbackCleanupCount.ShouldBe(failCommit ? 1 : 0);
        await fileStorage.DidNotReceive()
            .StageAsync(Arg.Any<ITempFile>(), Arg.Any<CancellationToken>());

        await using var readContext = database.CreateReadContext();
        (await readContext.JobItems.CountAsync()).ShouldBe(withUpload && !failCommit ? 1 : 0);
        if (withUpload && !failCommit)
            (await readContext.JobItems.SingleAsync()).FileName.ShouldBe("folder/game");
        (await readContext.DatRoms.AsNoTracking().SingleAsync()).RomFileId.ShouldBe(failCommit ? null : 9);
        (await readContext.TrackedTitles.AsNoTracking().AnyAsync()).ShouldBe(!failCommit);
        (await readContext.Libraries.AsNoTracking().SingleAsync()).NeedsMaterialization.ShouldBeFalse();
        (await readContext.Jobs.OfType<MaterializationJobEntity>().AnyAsync()).ShouldBeFalse();
        (await GetEventTypesAsync(readContext)).ShouldBe(
            failCommit ? [] : StatsEventTypes());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task IngestDat_CommitOutcome_KeepsStagedDatAndExactlyThreeChildEventsAtomic(bool failCommit, bool withUpload)
    {
        await using var database = await TestDatabase.CreateAsync(failCommit ? 1 : null);
        await SeedFileAsync(database.Context, fileId: 51, createdAt: DateTimeOffset.UtcNow);

        var uploadClaim = withUpload ? await ClaimOutcomeJobAsync(database.Context,
            JobItemKind.Dat) : null;
        using var fence = uploadClaim is null ? null :
            JobExecutionFenceScope.Enter(uploadClaim.Job.Id, uploadClaim.FenceToken, TimeProvider.System);
        var uploadItem = uploadClaim is null ? null : new UploadItemContext(uploadClaim.Job.Id, "folder/game");

        var tempFile = new AtomicityTempFile([1], NewSha256(151));
        var tempFiles = Substitute.For<ITempFileFactory>();
        tempFiles.CreateAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>(), Arg.Any<Func<ReadOnlyMemory<byte>, bool>?>(), Arg.Any<Func<ReadOnlyMemory<byte>, CancellationToken, Task>?>(), Arg.Is<Func<ReadOnlyMemory<byte>, int>?>(selector => selector != null)).Returns(tempFile);
        var fileStorage = Substitute.For<IFileStorageService>();
        var publication = Substitute.For<IStagedFilePublication>();
        publication.ReadAfterFlush().Returns(new FileStoreResult(
            FileEntity.Rehydrate(51, NewSha256(51), 1, 1, false, DateTimeOffset.UtcNow), false, true));
        fileStorage.PublishStagedAsync(Arg.Any<IStagedFile>(), Arg.Any<CancellationToken>()).Returns(publication);
        var datReader = Substitute.For<IDatReader>();
        datReader.ReadHeaderAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(new DatMetadata
            {
                Name = "Atomic DAT",
                Description = "Atomic DAT",
                DatType = DatType.NoIntro
            });
        datReader.StreamGamesAsync(Arg.Any<Stream>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ => EmptyDatGameStream());
        var regionResolver = Substitute.For<ITaxonomyResolver<Region>>();
        regionResolver.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());
        var languageResolver = Substitute.For<ITaxonomyResolver<GameLanguage>>();
        languageResolver.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());
        var catalogProjection = Substitute.For<ICatalogProjectionService>();
        var headerResolver = Substitute.For<IPlatformHeaderResolver>();
        headerResolver.ResolvePlatformIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((int?)null);

        var datRepository = new DatRepository(database.Context, TimeProvider.System);
        var handler = new IngestDatCommandHandler(
            datReader,
            datRepository,
            new TitleDerivationService(database.Context, new TitleMatcher(new TitleRepository(database.Context), clock: TimeProvider.System), new EfUnitOfWork(database.Context)),
            tempFiles,
            fileStorage,
            Substitute.For<IBiosGrouper>(),
            database.UnitOfWork,
            database.Outbox,
            Substitute.For<ILibraryRepository>(),
            regionResolver,
            languageResolver,
            catalogProjection,
            headerResolver,
            NullLogger<IngestDatCommandHandler>.Instance,
            new SourceLifecycleStore(database.Context), clock: TimeProvider.System, new JobItemRepository(database.Context));

        if (failCommit)
        {
            var command = IngestDatCommand.Create(new MemoryStream([1]), "atomic.dat").Value with { UploadItem = uploadItem };
            if (withUpload)
                await Should.ThrowAsync<UploadOutcomeUncertainException>(() => handler.HandleAsync(command));
            else
                await Should.ThrowAsync<InvalidOperationException>(() => handler.HandleAsync(command));
        }
        else
        {
            var result = await handler.HandleAsync(
                IngestDatCommand.Create(new MemoryStream([1]), "atomic.dat").Value with { UploadItem = uploadItem });
            result.IsError.ShouldBeFalse();
            result.Value.DatFile.Id.ShouldBeGreaterThan(0);
            result.Value.DatFile.DatSourceId.ShouldBeGreaterThan(0);
            result.Value.DatFile.Lifecycle.ShouldBe(DatFileLifecycle.Active);
        }

        database.UnitOfWork.RollbackCleanupCount.ShouldBe(failCommit ? 1 : 0);
        await using var readContext = database.CreateReadContext();
        (await readContext.JobItems.CountAsync()).ShouldBe(withUpload && !failCommit ? 1 : 0);
        if (withUpload && !failCommit)
            (await readContext.JobItems.SingleAsync()).FileName.ShouldBe("folder/game");
        (await readContext.DatFiles.AsNoTracking().CountAsync()).ShouldBe(failCommit ? 0 : 1);
        // The source anchor commits and rolls back atomically with its first version.
        (await readContext.DatSources.AsNoTracking().CountAsync()).ShouldBe(failCommit ? 0 : 1);
        if (!failCommit)
        {
            var persistedDat = await readContext.DatFiles.AsNoTracking().SingleAsync();
            var persistedSource = await readContext.DatSources.AsNoTracking().SingleAsync();
            persistedDat.DatSourceId.ShouldBe(persistedSource.Id);
            persistedDat.Lifecycle.ShouldBe(nameof(DatFileLifecycle.Active));
            if (withUpload)
            {
                // Simulate redelivery after publication but before the executor's counters/sink.
                // No handler is registered and no input file exists: only the durable outcome
                // can satisfy this call without importing the DAT again as a duplicate.
                var services = new ServiceCollection().AddResiliencePolicies();
                services.AddScoped(_ => database.CreateReadContext());
                services.AddScoped<IJobItemRepository, JobItemRepository>();
                await using var provider = services.BuildServiceProvider();
                var processor = new DatProcessor(provider, provider.GetRequiredService<ResiliencePipelineProvider<string>>(),
                    NullLogger<DatProcessor>.Instance);
                var replay = await processor.ProcessAsync("/missing/atomic.dat", null, default, uploadItem);
                replay.Success.ShouldBeTrue();
                replay.DatFileId.ShouldBe(persistedDat.Id);
                replay.JournaledOutcome!.Id.ShouldBe((await readContext.JobItems.SingleAsync()).Id);
                (await readContext.DatFiles.CountAsync()).ShouldBe(1);
            }
        }

        (await GetEventTypesAsync(readContext)).ShouldBe(
            failCommit ? [] : StatsEventTypes());

        // A rolled-back publication reclaims its blob unless a file row claims it.
        if (failCommit)
        {
            await fileStorage.Received(1)
                .DeleteOrphanAsync(NewSha256(51), CancellationToken.None);
        }
        else
        {
            await fileStorage.DidNotReceive()
                .DeleteOrphanAsync(Arg.Any<Sha256>(), Arg.Any<CancellationToken>());
        }
    }

    private static async Task<JobExecutionClaim<UploadJob>> ClaimOutcomeJobAsync(RomdDbContext db, JobItemKind kind)
    {
        var job = UploadJob.Create("fixture", createdAt: DateTimeOffset.UtcNow);
        job.Start("fixture", startedAt: DateTimeOffset.UtcNow);
        job.BeginClassification();
        job.SetDiscoveredFiles(kind == JobItemKind.Dat ? 1 : 0, kind == JobItemKind.Rom ? 1 : 0);
        job.BeginDatIngestion();
        if (kind == JobItemKind.Rom)
            job.BeginRomIngestion();
        var inner = new UploadJobRepository(db, TimeProvider.System);
        await using (var fixtureAcceptance = await new EfUnitOfWork(db).BeginTransactionAsync())
        {
            await inner.AddStagedAsync(job);
            await fixtureAcceptance.CommitAsync();
        }
        db.ChangeTracker.Clear();
        var claims = new ClaimedJobRepository<UploadJob, UploadJobRepository>(inner, db, TimeProvider.System, new());
        return (await claims.TryClaimExecutionAsync(job.Id, "fixture"))!;
    }

    private static async Task SeedExistingRomCatalogMatchAsync(RomdDbContext context)
    {
        context.Platforms.Add(new PlatformEntity
        {
            Id = 10,
            Name = "Platform 10",
            Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd,
            BuiltInVersion = 1,
            BaseName = "Platform 10",
            BaseCompactLabel = "Platform 10",
            CanonicalKey = "p10",
            ShortName = "p10",
            CreatedAt = DateTimeOffset.UtcNow
        });
        context.Files.AddRange(
            NewFile(41, DateTimeOffset.UtcNow),
            NewFile(42, DateTimeOffset.UtcNow));
        context.StageOriginalRom(new OriginalRomFixture
        {
            Id = 9,
            OriginalFilename = "existing.rom",
            FileId = 41,
            Sha1 = Sha1One,
            Md5 = Md5One,
            Crc32 = CrcOne,
            CreatedAt = DateTimeOffset.UtcNow
        });
        context.DatFiles.Add(new DatFileEntity
        {
            Source = new DatSourceEntity
            {
                CatalogSource = new CatalogSourceEntity { Id = 20, Kind = "Dat", Status = "Active" }
            },
            Id = 20,
            Name = "Catalog DAT",
            Description = "Catalog DAT",
            Type = DatType.NoIntro.ToString(),
            PlatformId = 10,
            OriginalFilename = "catalog.dat",
            FileId = 42,
            CreatedAt = DateTimeOffset.UtcNow
        });
        context.SourceEntries.Add(new SourceEntryEntity
        {
            Id = 30,
            CatalogSourceId = 20,
            EntryKey = "Matched Game",
            Name = "Matched Game",
            PlatformId = 10,
            CreatedAt = DateTimeOffset.UtcNow
        });
        context.DatGames.Add(new DatGameEntity
        {
            Id = 30,
            DatFileId = 20,
            SourceEntryId = 30,
            Name = "Matched Game",
            CreatedAt = DateTimeOffset.UtcNow
        });
        context.DatRoms.Add(new DatRomEntity
        {
            Id = 40,
            DatGameId = 30,
            Name = "existing.rom",
            Size = 1,
            Sha1 = Sha1One,
            RomFileId = null,
            CreatedAt = DateTimeOffset.UtcNow
        });
        context.Titles.Add(new TitleEntity
        {
            Id = 50,
            PlatformId = 10,
            Name = "Matched Title",
            NormalizedName = "matched title",
            EnrichmentStatus = EnrichmentStatus.None.ToString(),
            CreatedAt = DateTimeOffset.UtcNow
        });
        context.TitleSourceLinks.Add(new TitleSourceLinkEntity { SourceEntryId = 30, TitleId = 50 });
        context.Libraries.Add(new LibraryEntity
        {
            Id = 60,
            Name = "Matched Library",
            ConfigurationJson = JsonSerializer.Serialize(new LibraryConfiguration
            {
                AllowedPlatformIds = [10]
            }),
            ConfigurationState = LibraryConfigurationState.Valid.ToString(),
            NeedsMaterialization = false,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await context.PublishOriginalRomsAsync();
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
    }

    private static Sha256 NewSha256(int value) =>
        Sha256.Parse(string.Concat(Enumerable.Repeat($"{value:x2}", 32)));

    private static async IAsyncEnumerable<ErrorOr<DatGame>> EmptyDatGameStream()
    {
        await Task.CompletedTask;
        yield break;
    }

    private sealed class AtomicityTempFile(byte[] content, Sha256 sha256) : ITempFile
    {
        public bool TryBeginChildUse() => true;
        public bool TryEndChildUse() => true;
        public bool Quarantine() => true;
        public string Path => "memory.tmp";
        public Sha256 FileSha256 { get; } = sha256;
        public long FileSize => content.LongLength;
        public Stream OpenRead() => new MemoryStream(content, writable: false);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
