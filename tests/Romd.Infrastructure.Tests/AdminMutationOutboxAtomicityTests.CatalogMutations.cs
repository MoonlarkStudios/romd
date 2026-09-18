using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Configuration;
using Romd.Admin.Application.Libraries;
using Romd.Admin.Application.Source.Dat;
using Romd.Admin.Application.Source.Dat.Commands.AssignPlatform;
using Romd.Admin.Application.Source.Dat.Commands.IngestDat;
using Romd.Admin.Application.Source.Platform;
using Romd.Admin.Application.Storage.Files;
using Romd.Admin.Application.Taxonomy;
using Romd.Admin.Application.Titles.Commands.MergeTitles;
using Romd.Admin.Application.Titles.Commands.MoveGame;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Admin.Application.Titles.Matching;
using Romd.Domain.Catalog;
using Romd.Domain.Libraries;
using Romd.Domain.Source.Dat;
using Romd.Domain.Storage;
using Romd.Domain.Taxonomy;
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
    public async Task IngestDat_TransactionDisposalFailsAfterCommit_StillReportsDurableSuccess()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedIngestCatalogEffectsAsync(database.Context);
        var handler = NewCatalogMutationIngestHandler(
            database,
            new RebuildSuppressingCatalogProjection(RealProjection(database)),
            new ThrowOnDisposeAfterCommitUnitOfWork(database.UnitOfWork));

        var result = await handler.HandleAsync(
            IngestDatCommand.Create(new MemoryStream([1]), "catalog-effects.dat", platformId: 10).Value);

        result.IsError.ShouldBeFalse();
        await using var read = database.CreateReadContext();
        (await read.DatFiles.AnyAsync()).ShouldBeTrue();
        await AssertCatalogEffectsAsync(read, rolledBack: false);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IngestDat_CommitOutcome_KeepsSourceDirtyStateAndLibraryFlagAtomic(bool failCommit)
    {
        await using var database = await TestDatabase.CreateAsync(failCommit ? 1 : null);
        await SeedIngestCatalogEffectsAsync(database.Context);
        var handler = NewCatalogMutationIngestHandler(
            database,
            new RebuildSuppressingCatalogProjection(RealProjection(database)));

        if (failCommit)
        {
            await Should.ThrowAsync<InvalidOperationException>(() => handler.HandleAsync(
                IngestDatCommand.Create(new MemoryStream([1]), "catalog-effects.dat", platformId: 10).Value));
        }
        else
        {
            var result = await handler.HandleAsync(
                IngestDatCommand.Create(new MemoryStream([1]), "catalog-effects.dat", platformId: 10).Value);
            result.IsError.ShouldBeFalse();
        }

        await using var read = database.CreateReadContext();
        (await read.DatFiles.AnyAsync()).ShouldBe(!failCommit);
        (await read.TitleSourceLinks.AnyAsync()).ShouldBe(!failCommit);
        if (!failCommit)
        {
            (await read.DatRoms.SingleAsync()).RomFileId.ShouldBe(61);
            (await read.SourceEntries.SingleAsync()).HasLocalPayload.ShouldBeTrue();
            (await read.Titles.SingleAsync()).HasLocalPayload.ShouldBeTrue();
        }
        (await read.RomFiles.SingleAsync(rom => rom.Id == 61)).FileId.ShouldBe(52);
        await AssertCatalogEffectsAsync(read, failCommit);

        if (!failCommit)
        {
            await AssertRecoveryConvergesAsync(database);
        }
    }

    [Fact]
    public async Task IngestDat_RebuildThrowsAfterCommit_ReportsDurableSuccess()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedIngestCatalogEffectsAsync(database.Context);
        var handler = NewCatalogMutationIngestHandler(
            database,
            new ThrowingRebuildCatalogProjection(RealProjection(database)));

        var result = await handler.HandleAsync(
            IngestDatCommand.Create(new MemoryStream([1]), "catalog-effects.dat", platformId: 10).Value);

        result.IsError.ShouldBeFalse();
        await using var read = database.CreateReadContext();
        (await read.DatFiles.AnyAsync()).ShouldBeTrue();
        await AssertCatalogEffectsAsync(read, rolledBack: false);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AssignPlatform_CommitOutcome_KeepsRoutingDirtyStateAndLibraryFlagAtomic(bool failCommit)
    {
        await using var database = await TestDatabase.CreateAsync(failCommit ? 1 : null);
        await SeedCatalogMutationGraphAsync(database.Context, routed: false, withLocalPayload: true);

        var repository = new DatRepository(database.Context, TimeProvider.System);
        var projection = new RebuildSuppressingCatalogProjection(RealProjection(database));
        var handler = new AssignPlatformCommandHandler(
            repository,
            new TitleDerivationService(database.Context, new TitleMatcher(new TitleRepository(database.Context))),
            new PlatformRepository(database.Context),
            Substitute.For<IBiosGrouper>(),
            database.UnitOfWork,
            new LibraryRepository(database.Context),
            projection,
            NullLogger<AssignPlatformCommandHandler>.Instance,
            new SourceLifecycleStore(database.Context));

        var result = await handler.HandleAsync(AssignPlatformCommand.Create(7, 10).Value);

        result.IsError.ShouldBe(failCommit);
        await using var read = database.CreateReadContext();
        (await read.DatFiles.SingleAsync(row => row.Id == 7)).PlatformId.ShouldBe(failCommit ? null : 10);
        (await read.SourceEntries.SingleAsync(row => row.Id == 30)).PlatformId.ShouldBe(failCommit ? null : 10);
        (await read.DatRoms.SingleAsync(row => row.Id == 50)).RomFileId.ShouldBe(19);
        (await read.SourceEntries.SingleAsync(row => row.Id == 30)).HasLocalPayload
            .ShouldBe(!failCommit);
        (await read.Titles.SingleAsync(row => row.Id == 101)).HasLocalPayload
            .ShouldBe(!failCommit);
        (await read.TitleSourceLinks.SingleAsync(row => row.SourceEntryId == 30)).TitleId.ShouldBe(101);
        await AssertCatalogEffectsAsync(read, failCommit);

        if (!failCommit)
        {
            await AssertRecoveryConvergesAsync(database);
        }
    }

    [Fact]
    public async Task AssignPlatform_TransactionDisposalFailsAfterCommit_StillReportsDurableSuccess()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedCatalogMutationGraphAsync(database.Context, routed: false);
        var repository = new DatRepository(database.Context, TimeProvider.System);
        var handler = new AssignPlatformCommandHandler(
            repository,
            new TitleDerivationService(database.Context, new TitleMatcher(new TitleRepository(database.Context))),
            new PlatformRepository(database.Context),
            Substitute.For<IBiosGrouper>(),
            new ThrowOnDisposeAfterCommitUnitOfWork(database.UnitOfWork),
            new LibraryRepository(database.Context),
            new RebuildSuppressingCatalogProjection(RealProjection(database)),
            NullLogger<AssignPlatformCommandHandler>.Instance,
            new SourceLifecycleStore(database.Context));

        var result = await handler.HandleAsync(AssignPlatformCommand.Create(7, 10).Value);

        result.IsError.ShouldBeFalse();
        await using var read = database.CreateReadContext();
        (await read.DatFiles.SingleAsync(row => row.Id == 7)).PlatformId.ShouldBe(10);
        await AssertCatalogEffectsAsync(read, rolledBack: false);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MoveGame_CommitOutcome_KeepsAssignmentDirtyStateAndLibraryFlagAtomic(bool failCommit)
    {
        await using var database = await TestDatabase.CreateAsync(failCommit ? 1 : null);
        await SeedCatalogMutationGraphAsync(database.Context, routed: true);

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
            var result = await handler.HandleAsync(new MoveGameCommand(40, 202, null));
            result.IsError.ShouldBeFalse();
        }

        await using var read = database.CreateReadContext();
        (await read.TitleSourceLinks.SingleAsync(row => row.SourceEntryId == 30)).TitleId
            .ShouldBe(failCommit ? 101 : 202);
        (await read.Titles.AnyAsync(row => row.Id == 101)).ShouldBe(failCommit);
        await AssertCatalogEffectsAsync(read, failCommit);

        if (!failCommit)
        {
            await AssertRecoveryConvergesAsync(database);
        }
    }

    [Fact]
    public async Task MoveGame_TransactionDisposalFailsAfterCommit_StillReportsDurableSuccess()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedCatalogMutationGraphAsync(database.Context, routed: true);
        var repository = new DatRepository(database.Context, TimeProvider.System);
        var handler = new MoveGameCommandHandler(new Romd.Persistence.ReferenceData.ReferenceCatalogService(database.Context, Romd.PostgreSql.TestSupport.TestReferenceBlobs.Instance),
            new TitleRepository(database.Context),
            repository,
            new ThrowOnDisposeAfterCommitUnitOfWork(database.UnitOfWork),
            new LibraryRepository(database.Context),
            new RebuildSuppressingCatalogProjection(RealProjection(database)),
            NullLogger<MoveGameCommandHandler>.Instance);

        var result = await handler.HandleAsync(new MoveGameCommand(40, 202, null));

        result.IsError.ShouldBeFalse();
        await using var read = database.CreateReadContext();
        (await read.TitleSourceLinks.SingleAsync(row => row.SourceEntryId == 30)).TitleId.ShouldBe(202);
        await AssertCatalogEffectsAsync(read, rolledBack: false);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MergeTitles_CommitOutcome_KeepsAssignmentsDirtyStateAndLibraryFlagAtomic(bool failCommit)
    {
        await using var database = await TestDatabase.CreateAsync(failCommit ? 1 : null);
        await SeedCatalogMutationGraphAsync(database.Context, routed: true, secondSourceEntry: true);

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

        if (failCommit)
        {
            await Should.ThrowAsync<InvalidOperationException>(() =>
                handler.HandleAsync(new MergeTitlesCommand(101, 202)));
        }
        else
        {
            var result = await handler.HandleAsync(new MergeTitlesCommand(101, 202));
            result.IsError.ShouldBeFalse();
        }

        await using var read = database.CreateReadContext();
        var titleIds = await read.TitleSourceLinks
            .OrderBy(row => row.SourceEntryId)
            .Select(row => row.TitleId)
            .ToArrayAsync();
        titleIds.ShouldBe(failCommit ? [101, 101] : [202, 202]);
        (await read.Titles.AnyAsync(row => row.Id == 101)).ShouldBe(failCommit);
        await AssertCatalogEffectsAsync(read, failCommit);

        if (!failCommit)
        {
            await AssertRecoveryConvergesAsync(database);
        }
    }

    [Fact]
    public async Task MergeTitles_TransactionDisposalFailsAfterCommit_StillReportsDurableSuccess()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedCatalogMutationGraphAsync(database.Context, routed: true, secondSourceEntry: true);
        var handler = NewMergeHandler(
            database,
            new ThrowOnDisposeAfterCommitUnitOfWork(database.UnitOfWork));

        var result = await handler.HandleAsync(new MergeTitlesCommand(101, 202));

        result.IsError.ShouldBeFalse();
        await using var read = database.CreateReadContext();
        (await read.Titles.AnyAsync(row => row.Id == 101)).ShouldBeFalse();
        (await read.TitleSourceLinks.AllAsync(row => row.TitleId == 202)).ShouldBeTrue();
        await AssertCatalogEffectsAsync(read, rolledBack: false);
    }

    [Fact]
    public async Task MergeTitles_CancelledAfterCommit_ReloadsDurableResultWithoutCallerToken()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedCatalogMutationGraphAsync(database.Context, routed: true, secondSourceEntry: true);
        using var cancellation = new CancellationTokenSource();
        var handler = NewMergeHandler(
            database,
            new CancelAfterSuccessfulCommitUnitOfWork(database.UnitOfWork, cancellation));

        var result = await handler.HandleAsync(new MergeTitlesCommand(101, 202), cancellation.Token);

        cancellation.IsCancellationRequested.ShouldBeTrue();
        result.IsError.ShouldBeFalse();
        result.Value.Name.ShouldBe("Target Title");
        await using var read = database.CreateReadContext();
        (await read.Titles.AnyAsync(row => row.Id == 101)).ShouldBeFalse();
        await AssertCatalogEffectsAsync(read, rolledBack: false);
    }

    private static CatalogProjectionService RealProjection(TestDatabase database) =>
        CatalogProjectionTestFactory.Create(database.Context);

    private static IngestDatCommandHandler NewCatalogMutationIngestHandler(
        TestDatabase database,
        ICatalogProjectionService projection,
        IUnitOfWork? unitOfWork = null)
    {
        var tempFile = new AtomicityTempFile([1], NewSha256(151));
        var tempFiles = Substitute.For<ITempFileFactory>();
        tempFiles.CreateAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>()).Returns(tempFile);
        var fileStorage = Substitute.For<IFileStorageService>();
        fileStorage.StoreFromTempFileAsync(tempFile, Arg.Any<CancellationToken>())
            .Returns(new FileStoreResult(
                FileEntity.Rehydrate(51, NewSha256(51), 1, 1, false, DateTimeOffset.UtcNow),
                false,
                true));
        var datReader = Substitute.For<IDatReader>();
        datReader.ReadHeaderAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(new DatMetadata
            {
                Name = "Catalog Effects DAT",
                Description = "Catalog Effects DAT",
                DatType = DatType.NoIntro
            });
        datReader.StreamGamesAsync(Arg.Any<Stream>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call => OneDatGameStream(call.ArgAt<int>(1)));
        var regionResolver = Substitute.For<ITaxonomyResolver<Region>>();
        regionResolver.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());
        var languageResolver = Substitute.For<ITaxonomyResolver<GameLanguage>>();
        languageResolver.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());

        var repository = new DatRepository(database.Context, TimeProvider.System);
        return new IngestDatCommandHandler(
            datReader,
            repository,
            new TitleDerivationService(database.Context, new TitleMatcher(new TitleRepository(database.Context))),
            tempFiles,
            fileStorage,
            Substitute.For<IBiosGrouper>(),
            unitOfWork ?? database.UnitOfWork,
            database.Outbox,
            new LibraryRepository(database.Context),
            regionResolver,
            languageResolver,
            projection,
            Substitute.For<IPlatformHeaderResolver>(),
            NullLogger<IngestDatCommandHandler>.Instance,
            new SourceLifecycleStore(database.Context));
    }

    private static MergeTitlesCommandHandler NewMergeHandler(TestDatabase database, IUnitOfWork unitOfWork)
    {
        var repository = new DatRepository(database.Context, TimeProvider.System);
        return new MergeTitlesCommandHandler(new Romd.Persistence.ReferenceData.ReferenceCatalogService(database.Context, Romd.PostgreSql.TestSupport.TestReferenceBlobs.Instance),
            new TitleRepository(database.Context),
            new TrackedTitleRepository(database.Context, TimeProvider.System),
            repository,
            unitOfWork,
            Substitute.For<IRematerializationScheduler>(),
            new LibraryRepository(database.Context),
            new RebuildSuppressingCatalogProjection(RealProjection(database)),
            Options.Create(new EnrichmentOptions()),
            NullLogger<MergeTitlesCommandHandler>.Instance);
    }

    private static async Task AssertCatalogEffectsAsync(RomdDbContext read, bool rolledBack)
    {
        (await read.Platforms.SingleAsync(row => row.Id == 10)).CatalogRebuildState
            .ShouldBe(rolledBack ? CatalogRebuildState.Clean : CatalogRebuildState.Dirty);
        (await read.Libraries.SingleAsync(row => row.Id == 3)).NeedsMaterialization.ShouldBe(!rolledBack);
    }

    private static async Task AssertRecoveryConvergesAsync(TestDatabase database)
    {
        (await RealProjection(database).RebuildPlatformAsync(10)).ShouldBeTrue();
        await using var recovered = database.CreateReadContext();
        (await recovered.Platforms.SingleAsync(row => row.Id == 10)).CatalogRebuildState
            .ShouldBe(CatalogRebuildState.Clean);
    }

    private static async Task SeedCatalogMutationGraphAsync(
        RomdDbContext context,
        bool routed,
        bool secondSourceEntry = false,
        bool withLocalPayload = false)
    {
        var now = DateTimeOffset.UtcNow;
        context.Platforms.Add(new PlatformEntity
        {
            Id = 10,
            Name = "Platform 10",
            Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "Platform 10", BaseCompactLabel = "Platform 10", CanonicalKey = "p10", ShortName = "p10",
            CatalogRebuildState = CatalogRebuildState.Clean,
            CreatedAt = now
        });
        context.Files.Add(NewFile(17, now));
        context.CatalogSources.Add(new CatalogSourceEntity
        {
            Id = 20,
            Kind = "Dat",
            Status = "Active",
            CreatedAt = now
        });
        context.DatSources.Add(new DatSourceEntity
        {
            Id = 21,
            CatalogSourceId = 20,
            CreatedAt = now
        });
        context.DatFiles.Add(new DatFileEntity
        {
            Id = 7,
            DatSourceId = 21,
            Name = "Mutation DAT",
            Description = "Mutation DAT",
            Type = DatType.NoIntro.ToString(),
            PlatformId = routed ? 10 : null,
            OriginalFilename = "mutation.dat",
            FileId = 17,
            GameCount = secondSourceEntry ? 2 : 1,
            CreatedAt = now
        });
        context.SourceEntries.Add(new SourceEntryEntity
        {
            Id = 30,
            CatalogSourceId = 20,
            EntryKey = "Source Game",
            Name = "Source Game",
            PlatformId = routed ? 10 : null,
            CreatedAt = now
        });
        context.DatGames.Add(new DatGameEntity
        {
            Id = 40,
            DatFileId = 7,
            SourceEntryId = 30,
            Name = "Source Game",
            CreatedAt = now
        });
        if (withLocalPayload)
        {
            context.Files.Add(NewFile(18, now));
            context.RomFiles.Add(new RomFileEntity
            {
                Id = 19,
                OriginalFilename = "assignment.rom",
                FileId = 18,
                Sha1 = Sha1One,
                Md5 = Md5One,
                Crc32 = CrcOne,
                CreatedAt = now
            });
            context.DatRoms.Add(new DatRomEntity
            {
                Id = 50,
                DatGameId = 40,
                RomFileId = 19,
                Name = "assignment.rom",
                Size = 1,
                Sha1 = Sha1One,
                Md5 = Md5One,
                Crc = CrcOne,
                CreatedAt = now
            });
        }
        context.Titles.AddRange(
            NewTitleEntity(101, "Source Title", now),
            NewTitleEntity(202, "Target Title", now));
        context.TitleSourceLinks.Add(new TitleSourceLinkEntity
        {
            SourceEntryId = 30,
            TitleId = 101,
            CreatedAt = now
        });

        if (secondSourceEntry)
        {
            context.SourceEntries.Add(new SourceEntryEntity
            {
                Id = 31,
                CatalogSourceId = 20,
                EntryKey = "Second Source Game",
                Name = "Second Source Game",
                PlatformId = 10,
                CreatedAt = now
            });
            context.DatGames.Add(new DatGameEntity
            {
                Id = 41,
                DatFileId = 7,
                SourceEntryId = 31,
                Name = "Second Source Game",
                CreatedAt = now
            });
            context.TitleSourceLinks.Add(new TitleSourceLinkEntity
            {
                SourceEntryId = 31,
                TitleId = 101,
                CreatedAt = now
            });
        }

        context.Libraries.Add(new LibraryEntity
        {
            Id = 3,
            Name = "Library 3",
            ConfigurationJson = "{}",
            ConfigurationState = LibraryConfigurationState.Valid.ToString(),
            NeedsMaterialization = false,
            CreatedAt = now
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
    }

    private static async Task SeedIngestCatalogEffectsAsync(RomdDbContext context)
    {
        var now = DateTimeOffset.UtcNow;
        context.Platforms.Add(new PlatformEntity
        {
            Id = 10,
            Name = "Platform 10",
            Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "Platform 10", BaseCompactLabel = "Platform 10", CanonicalKey = "p10", ShortName = "p10",
            CatalogRebuildState = CatalogRebuildState.Clean,
            CreatedAt = now
        });
        context.Files.Add(NewFile(51, now));
        context.Files.Add(NewFile(52, now));
        context.RomFiles.Add(new RomFileEntity
        {
            Id = 61,
            OriginalFilename = "catalog-game.rom",
            FileId = 52,
            Sha1 = Sha1One,
            Md5 = Md5One,
            Crc32 = CrcOne,
            CreatedAt = now
        });
        context.Libraries.Add(new LibraryEntity
        {
            Id = 3,
            Name = "Library 3",
            ConfigurationJson = "{}",
            ConfigurationState = LibraryConfigurationState.Valid.ToString(),
            NeedsMaterialization = false,
            CreatedAt = now
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
    }

    private static async IAsyncEnumerable<ErrorOr<DatGame>> OneDatGameStream(int datFileId)
    {
        await Task.CompletedTask;
        var game = DatGame.CreateNew(datFileId, "Catalog Game");
        game.AddRom(DatRom.CreateNew(
            "catalog-game.rom",
            1,
            CrcOne,
            Md5One,
            Sha1One));
        yield return game;
    }

    private sealed class ThrowingRebuildCatalogProjection(ICatalogProjectionService inner)
        : ICatalogProjectionService
    {
        public Task<bool> RebuildPlatformAsync(int platformId, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("rebuild interrupted");

        public Task MarkPlatformDirtyAsync(
            int platformId,
            CancellationToken cancellationToken = default,
            IReadOnlyCollection<int>? affectedTitleIds = null) =>
            inner.MarkPlatformDirtyAsync(platformId, cancellationToken, affectedTitleIds);

        public Task MarkCatalogSourceDirtyAsync(
            int platformId,
            int catalogSourceId,
            CancellationToken cancellationToken = default) =>
            inner.MarkCatalogSourceDirtyAsync(platformId, catalogSourceId, cancellationToken);

        public Task RefreshCatalogSourcePayloadAsync(
            int catalogSourceId,
            CancellationToken cancellationToken = default) =>
            inner.RefreshCatalogSourcePayloadAsync(catalogSourceId, cancellationToken);

        public Task RefreshPlatformPayloadAsync(
            int platformId,
            CancellationToken cancellationToken = default) =>
            inner.RefreshPlatformPayloadAsync(platformId, cancellationToken);

        public Task<IReadOnlyList<int>> GetPlatformIdsNeedingRebuildAsync(
            CancellationToken cancellationToken = default) =>
            inner.GetPlatformIdsNeedingRebuildAsync(cancellationToken);
    }

    private sealed class CancelAfterSuccessfulCommitUnitOfWork(
        IUnitOfWork inner,
        CancellationTokenSource cancellation) : IUnitOfWork
    {
        public Task FlushAsync(CancellationToken cancellationToken = default) =>
            inner.FlushAsync(cancellationToken);

        public async Task<ITransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
            new CancelAfterSuccessfulCommitTransaction(
                await inner.BeginTransactionAsync(cancellationToken),
                cancellation);

        private sealed class CancelAfterSuccessfulCommitTransaction(
            ITransaction transaction,
            CancellationTokenSource cancellation) : ITransaction
        {
            public async Task CommitAsync(CancellationToken cancellationToken = default)
            {
                await transaction.CommitAsync(cancellationToken);
                await cancellation.CancelAsync();
            }

            public Task RollbackAsync(CancellationToken cancellationToken = default) =>
                transaction.RollbackAsync(cancellationToken);

            public ValueTask DisposeAsync() => transaction.DisposeAsync();
        }
    }

    private static TitleEntity NewTitleEntity(int id, string name, DateTimeOffset createdAt) => new()
    {
        Id = id,
        PlatformId = 10,
        Name = name,
        NormalizedName = TitleNormalizer.Normalize(name),
        EnrichmentStatus = EnrichmentStatus.None.ToString(),
        CreatedAt = createdAt
    };
}
