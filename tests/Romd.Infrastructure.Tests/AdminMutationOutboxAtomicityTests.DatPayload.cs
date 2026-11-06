using System.Runtime.CompilerServices;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Source.Dat;
using Romd.Admin.Application.Source.Dat.Commands.IngestDat;
using Romd.Admin.Application.Source.Platform;
using Romd.Admin.Application.Storage.Files;
using Romd.Admin.Application.Taxonomy;
using Romd.Domain.Catalog;
using Romd.Domain.Jobs;
using Romd.Domain.Source.Dat;
using Romd.Domain.Source.Dat.Parsing;
using Romd.Domain.Storage;
using Romd.Domain.Taxonomy;
using Romd.Infrastructure.Catalog;
using Romd.Infrastructure.Tests.Helpers;
using Romd.Persistence;
using Romd.Persistence.Catalog;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests;

public sealed partial class AdminMutationOutboxAtomicityTests
{
    [Theory]
    [InlineData(PayloadFailure.None, false)]
    [InlineData(PayloadFailure.Games, false)]
    [InlineData(PayloadFailure.Games, true)]
    [InlineData(PayloadFailure.Links, false)]
    [InlineData(PayloadFailure.Links, true)]
    [InlineData(PayloadFailure.Regions, false)]
    [InlineData(PayloadFailure.Regions, true)]
    [InlineData(PayloadFailure.Languages, false)]
    [InlineData(PayloadFailure.Languages, true)]
    [InlineData(PayloadFailure.Commit, false)]
    [InlineData(PayloadFailure.Commit, true)]
    public async Task IngestDat_NonemptyPayloadOwnerPhases_KeepGraphFlagsEventsAndUploadOutcomeAtomic(PayloadFailure failure, bool cancel)
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedPayloadPrerequisitesAsync(database.Context);
        var claim = await ClaimOutcomeJobAsync(database.Context, JobItemKind.Dat);
        using var fence = JobExecutionFenceScope.Enter(claim.Job.Id, claim.FenceToken, TimeProvider.System);
        using var cancellation = new CancellationTokenSource();
        var work = new PayloadPhaseWork(database.UnitOfWork, database.Context, failure, cancel ? cancellation : null);
        var handler = PayloadHandler(database, work, 2);
        var command = IngestDatCommand.Create(new MemoryStream([1]), "payload.dat", 10).Value
            with
        { UploadItem = new UploadItemContext(claim.Job.Id, "payload.dat") };
        database.Context.Regions.Add(new RegionEntity { Name = "Unrelated pending", CanonicalKey = "dat-unrelated" });

        if (cancel)
            (await Should.ThrowAsync<OperationCanceledException>(() => handler.HandleAsync(command, cancellation.Token)))
                .CancellationToken.ShouldBe(cancellation.Token);
        else if (failure == PayloadFailure.Commit)
            await Should.ThrowAsync<UploadOutcomeUncertainException>(() => handler.HandleAsync(command));
        else if (failure != PayloadFailure.None)
            await Should.ThrowAsync<IOException>(() => handler.HandleAsync(command));
        else
        {
            var result = await handler.HandleAsync(command);
            result.IsError.ShouldBeFalse();
            result.Value.DatFile.GameCount.ShouldBe(2);
            result.Value.DatFile.RomCount.ShouldBe(2);
            result.Value.DatFile.DiskCount.ShouldBe(2);
        }

        work.Observed.ShouldBe(failure switch
        {
            PayloadFailure.Games => [PayloadFailure.Games],
            PayloadFailure.Links => [PayloadFailure.Games, PayloadFailure.Links],
            PayloadFailure.Regions => [PayloadFailure.Games, PayloadFailure.Links, PayloadFailure.Regions],
            _ => [PayloadFailure.Games, PayloadFailure.Links, PayloadFailure.Regions, PayloadFailure.Languages]
        });
        bool committed = failure == PayloadFailure.None;
        if (!committed)
            database.Context.ChangeTracker.Entries().ShouldBeEmpty();
        await database.Context.SaveChangesAsync();
        await using var verify = database.CreateReadContext();
        await AssertPayloadGraphAsync(verify, committed ? 2 : 0);
        (await verify.DatFiles.CountAsync()).ShouldBe(committed ? 1 : 0);
        (await verify.DatSources.CountAsync()).ShouldBe(committed ? 1 : 0);
        (await verify.SourceEntries.CountAsync()).ShouldBe(committed ? 2 : 0);
        (await verify.Titles.CountAsync()).ShouldBe(committed ? 2 : 0);
        (await verify.TitleSourceLinks.CountAsync()).ShouldBe(committed ? 2 : 0);
        (await verify.Regions.CountAsync()).ShouldBe(committed ? 2 : 1);
        (await verify.Platforms.SingleAsync()).CatalogRebuildState.ShouldBe(committed ? CatalogRebuildState.Dirty : CatalogRebuildState.Clean);
        (await verify.Libraries.SingleAsync()).NeedsMaterialization.ShouldBe(committed);
        (await GetEventTypesAsync(verify)).ShouldBe(committed ? StatsEventTypes() : []);
        (await verify.JobItems.CountAsync()).ShouldBe(committed ? 1 : 0);
        if (committed)
        {
            var outcome = await verify.JobItems.SingleAsync();
            outcome.GameCount.ShouldBe(2);
            outcome.DatFileId.ShouldBe((await verify.DatFiles.SingleAsync()).Id);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IngestDat_ThousandAndOneGames_FlushesCompleteProviderBatchBeforeNextPull(bool cancelAtNextPull)
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedPayloadPrerequisitesAsync(database.Context);
        using var cancellation = new CancellationTokenSource();
        bool checkedBoundary = false;
        async Task AtNextPullAsync()
        {
            await AssertPayloadGraphAsync(database.Context, 1000);
            await using var outside = database.CreateReadContext();
            await AssertPayloadGraphAsync(outside, 0);
            // No unflushed provider graph may be discarded by the next derivation batch's coordinated Clear.
            database.Context.ChangeTracker.Entries<DatGameEntity>().Count().ShouldBe(1000);
            database.Context.ChangeTracker.Entries<DatGameEntity>().ShouldAllBe(entry => entry.State == EntityState.Unchanged);
            database.Context.ChangeTracker.Entries<DatGameRegionEntity>().ShouldAllBe(entry => entry.State == EntityState.Unchanged);
            database.Context.ChangeTracker.Entries<DatGameLanguageEntity>().ShouldAllBe(entry => entry.State == EntityState.Unchanged);
            checkedBoundary = true;
            if (cancelAtNextPull)
            {
                await cancellation.CancelAsync();
                cancellation.Token.ThrowIfCancellationRequested();
            }
        }
        var handler = PayloadHandler(database, database.UnitOfWork, 1001, AtNextPullAsync);
        var command = IngestDatCommand.Create(new MemoryStream([1]), "large-payload.dat", 10).Value;

        if (cancelAtNextPull)
            (await Should.ThrowAsync<OperationCanceledException>(() => handler.HandleAsync(command, cancellation.Token)))
                .CancellationToken.ShouldBe(cancellation.Token);
        else
            (await handler.HandleAsync(command, cancellation.Token)).IsError.ShouldBeFalse();

        checkedBoundary.ShouldBeTrue();
        await using var verify = database.CreateReadContext();
        await AssertPayloadGraphAsync(verify, cancelAtNextPull ? 0 : 1001);
        (await verify.SourceEntries.CountAsync()).ShouldBe(cancelAtNextPull ? 0 : 1001);
        (await verify.TitleSourceLinks.CountAsync()).ShouldBe(cancelAtNextPull ? 0 : 1001);
    }

    [Fact]
    public async Task IngestDat_SecondBatchIdentityFlushFails_RollsBackEarlierCompleteBatchToo()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedPayloadPrerequisitesAsync(database.Context);
        var work = new PayloadPhaseWork(database.UnitOfWork, database.Context, PayloadFailure.SecondBatch);

        await Should.ThrowAsync<IOException>(() => PayloadHandler(database, work, 1001).HandleAsync(
            IngestDatCommand.Create(new MemoryStream([1]), "second-batch.dat", 10).Value));

        work.Observed.ShouldContain(PayloadFailure.Languages);
        work.Observed.ShouldContain(PayloadFailure.SecondBatch);
        database.Context.ChangeTracker.Entries().ShouldBeEmpty();
        await using var verify = database.CreateReadContext();
        await AssertPayloadGraphAsync(verify, 0);
        (await verify.DatFiles.CountAsync()).ShouldBe(0);
        (await verify.SourceEntries.CountAsync()).ShouldBe(0);
        (await verify.Titles.CountAsync()).ShouldBe(0);
        (await GetEventTypesAsync(verify)).ShouldBeEmpty();
    }

    private static async Task AssertPayloadGraphAsync(RomdDbContext db, int expected)
    {
        (await db.DatGames.CountAsync()).ShouldBe(expected);
        (await db.DatRoms.CountAsync()).ShouldBe(expected);
        (await db.DatDisks.CountAsync()).ShouldBe(expected);
        (await db.DatRoms.CountAsync(row => row.RomFileId == 9)).ShouldBe(expected);
        (await db.DatGameRegions.CountAsync(row => row.RegionId == 7)).ShouldBe(expected);
        (await db.DatGameLanguages.CountAsync(row => row.GameLanguageId == 8)).ShouldBe(expected);
    }

    private static async Task SeedPayloadPrerequisitesAsync(RomdDbContext db)
    {
        db.Platforms.Add(new PlatformEntity
        {
            Id = 10,
            Name = "Payload platform",
            ShortName = "payload",
            CanonicalKey = "payload",
            Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd,
            BuiltInVersion = 1,
            BaseName = "Payload platform",
            BaseCompactLabel = "PAY",
            CatalogRebuildState = CatalogRebuildState.Clean
        });
        db.Regions.Add(new RegionEntity { Id = 7, Name = "USA", CanonicalKey = "dat-usa" });
        db.GameLanguages.Add(new GameLanguageEntity { Id = 8, Name = "English", Code = "en", CanonicalKey = "dat-english" });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await SeedFileAsync(db, 51, DateTimeOffset.UtcNow);
        await SeedFileAndRomAsync(db, 9, 41, Sha1One, Md5One, CrcOne);
        await SeedLibraryAsync(db, 3);
    }

    private static IngestDatCommandHandler PayloadHandler(TestDatabase database, IUnitOfWork work, int count,
        Func<Task>? atNextPull = null)
    {
        var db = database.Context;
        var temp = new AtomicityTempFile([1], NewSha256(51));
        var tempFiles = Substitute.For<ITempFileFactory>();
        tempFiles.CreateAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>(), Arg.Any<Func<ReadOnlyMemory<byte>, bool>?>(),
            Arg.Any<Func<ReadOnlyMemory<byte>, CancellationToken, Task>?>(), Arg.Any<Func<ReadOnlyMemory<byte>, int>?>()).Returns(temp);
        var files = Substitute.For<IFileStorageService>();
        var staged = Substitute.For<IStagedFile>();
        files.StageAsync(Arg.Any<ITempFile>(), Arg.Any<CancellationToken>()).Returns(staged);
        var published = new FileStoreResult(FileEntity.Rehydrate(51, NewSha256(51), 1, 1, false, DateTimeOffset.UtcNow), false, true);
        var publication = Substitute.For<IStagedFilePublication>();
        publication.RequiresFlush.Returns(false);
        publication.ReadAfterFlush().Returns(published);
        files.PublishStagedAsync(Arg.Any<IStagedFile>(), Arg.Any<CancellationToken>()).Returns(publication);
        var reader = Substitute.For<IDatReader>();
        reader.ReadHeaderAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>()).Returns(new DatMetadata
        {
            Name = "Payload",
            Description = "Payload graph",
            DatType = DatType.NoIntro
        });
        reader.StreamGamesAsync(Arg.Any<Stream>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(call =>
            PayloadGames(call.Arg<int>(), count, atNextPull, call.Arg<CancellationToken>()));
        var regions = Substitute.For<ITaxonomyResolver<Region>>();
        regions.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(new[] { 7 });
        var languages = Substitute.For<ITaxonomyResolver<GameLanguage>>();
        languages.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(new[] { 8 });
        var actualProjection = CatalogProjectionTestFactory.Create(db);
        var projection = Substitute.For<ICatalogProjectionService>();
        projection.RefreshCatalogSourcePayloadAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(call =>
            actualProjection.RefreshCatalogSourcePayloadAsync(call.Arg<int>(), call.Arg<CancellationToken>()));
        projection.MarkPlatformDirtyAsync(Arg.Any<int>(), Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyCollection<int>?>()).Returns(call =>
            actualProjection.MarkPlatformDirtyAsync(call.Arg<int>(), call.Arg<CancellationToken>(), call.Arg<IReadOnlyCollection<int>?>()));
        return new IngestDatCommandHandler(reader, new DatRepository(db, TimeProvider.System),
            new TitleDerivationService(db, new TitleMatcher(new TitleRepository(db), TimeProvider.System), database.UnitOfWork), tempFiles, files,
            Substitute.For<IBiosGrouper>(), work, database.Outbox, new LibraryRepository(db), regions, languages, projection,
            Substitute.For<IPlatformHeaderResolver>(), NullLogger<IngestDatCommandHandler>.Instance,
            new SourceLifecycleStore(db), TimeProvider.System, new JobItemRepository(db));
    }

    private static async IAsyncEnumerable<ErrorOr<DatGame>> PayloadGames(int datId, int count, Func<Task>? atNextPull,
        [EnumeratorCancellation] CancellationToken ct)
    {
        for (int index = 0; index < count; index++)
        {
            if (index == 1000 && atNextPull is not null)
                await atNextPull();
            ct.ThrowIfCancellationRequested();
            var game = DatGame.CreateNew(datId, $"Payload {index:D4}", region: "USA", language: "en");
            game.AddRom(DatRom.CreateNew($"payload-{index}.rom", 1, sha1: Sha1One));
            game.AddDisk(DatDisk.CreateNew($"payload-{index}.disk", Sha1Two));
            yield return game;
        }
    }

    public enum PayloadFailure { None, Games, Links, Regions, Languages, Commit, SecondBatch }

    private sealed class PayloadPhaseWork(IUnitOfWork inner, RomdDbContext db, PayloadFailure failure,
        CancellationTokenSource? cancellation = null) : IUnitOfWork
    {
        public List<PayloadFailure> Observed { get; } = [];
        public TransactionConflict ConflictOf(Exception error) => inner.ConflictOf(error);
        public async Task<ITransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
            new Transaction(this, await inner.BeginTransactionAsync(cancellationToken));
        public async Task FlushAsync(CancellationToken cancellationToken = default)
        {
            await inner.FlushAsync(cancellationToken);
            int games = await db.DatGames.CountAsync(cancellationToken);
            if (games == 0)
                return;
            var phase = games > 1000 ? PayloadFailure.SecondBatch
                : await db.DatGameLanguages.AnyAsync(cancellationToken) ? PayloadFailure.Languages
                : await db.DatGameRegions.AnyAsync(cancellationToken) ? PayloadFailure.Regions
                : await db.DatRoms.AnyAsync(row => row.RomFileId != null, cancellationToken) ? PayloadFailure.Links
                : PayloadFailure.Games;
            if (!Observed.Contains(phase))
            {
                Observed.Add(phase);
                if (failure == phase)
                    await InterruptAsync(cancellationToken);
            }
        }
        private async Task InterruptAsync(CancellationToken ct)
        {
            if (cancellation is not null)
            {
                await cancellation.CancelAsync();
                ct.ThrowIfCancellationRequested();
            }
            throw new IOException("Injected failure after an actual DAT payload flush.");
        }
        private async Task BeforeCommitAsync(CancellationToken ct)
        {
            if (failure != PayloadFailure.Commit)
                return;
            await inner.FlushAsync(ct);
            (await db.AdminRealtimeOutboxEvents.CountAsync(ct)).ShouldBe(3);
            (await db.JobItems.CountAsync(ct)).ShouldBe(1);
            await InterruptAsync(ct);
        }
        private sealed class Transaction(PayloadPhaseWork owner, ITransaction transaction) : ITransaction
        {
            public async Task CommitAsync(CancellationToken cancellationToken = default)
            {
                await owner.BeforeCommitAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            public Task RollbackAsync(CancellationToken cancellationToken = default) => transaction.RollbackAsync(cancellationToken);
            public ValueTask DisposeAsync() => transaction.DisposeAsync();
        }
    }
}
