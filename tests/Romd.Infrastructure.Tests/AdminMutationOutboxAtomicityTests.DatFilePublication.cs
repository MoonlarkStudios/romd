using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Libraries;
using Romd.Admin.Application.Source.Dat;
using Romd.Admin.Application.Source.Dat.Commands.IngestDat;
using Romd.Admin.Application.Source.Platform;
using Romd.Admin.Application.Storage.Files;
using Romd.Admin.Application.Taxonomy;
using Romd.Admin.Application.Titles.Matching;
using Romd.Application.Common.Configuration;
using Romd.Domain.Jobs;
using Romd.Domain.Source.Dat;
using Romd.Domain.Source.Dat.Parsing;
using Romd.Domain.Storage;
using Romd.Domain.Taxonomy;
using Romd.Infrastructure.Catalog;
using Romd.Infrastructure.Realtime;
using Romd.Infrastructure.Storage;
using Romd.Infrastructure.Tests.Helpers;
using Romd.Persistence;
using Romd.Persistence.Catalog;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.Storage;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests;

public sealed partial class AdminMutationOutboxAtomicityTests
{
    private static readonly DateTimeOffset DatPublicationTime = DateTimeOffset.UnixEpoch.AddTicks(1234567);
    private static readonly byte[] DatPublicationBytes = [68, 65, 84, 1, 2, 3];

    [Theory]
    [InlineData(false, false, DatFilePublicationFailure.None)]
    [InlineData(false, true, DatFilePublicationFailure.None)]
    [InlineData(true, false, DatFilePublicationFailure.None)]
    [InlineData(false, false, DatFilePublicationFailure.AfterIdentityFlush)]
    [InlineData(false, false, DatFilePublicationFailure.CancelAfterIdentityFlush)]
    [InlineData(false, false, DatFilePublicationFailure.Commit)]
    [InlineData(false, true, DatFilePublicationFailure.Commit)]
    [InlineData(true, false, DatFilePublicationFailure.Commit)]
    public async Task IngestDat_RealFilePublication_KeepsIdentityDatSourceOutcomeAndEventsAtomic(
        bool existingFile, bool withUpload, DatFilePublicationFailure failure)
    {
        string root = Path.Combine(Path.GetTempPath(), "romd-dat-file-publication", Guid.NewGuid().ToString("N"));
        try
        {
            await using var database = await TestDatabase.CreateAsync(failure == DatFilePublicationFailure.Commit ? 1 : null);
            await using var provider = new ServiceCollection().AddContentAddressableStorage(options => options.RootPath = Path.Combine(root, "cas"))
                .BuildServiceProvider();
            var cas = provider.GetRequiredService<IContentAddressableStore>();
            var db = database.Context;
            var storage = new FileStorageService(cas, new FileRepository(db), new EfUnitOfWork(db), new FileMutationLock(db),
                database.Outbox, NullLogger<FileStorageService>.Instance, new ManualTimeProvider(DatPublicationTime));
            int? existingId = null;
            if (existingFile)
            {
                await using var bytes = new MemoryStream(DatPublicationBytes);
                await using var staged = await storage.StageAsync(bytes);
                await using var transaction = await new EfUnitOfWork(db).BeginTransactionAsync();
                var publication = await storage.PublishStagedAsync(staged);
                await db.SaveChangesAsync();
                existingId = publication.ReadAfterFlush().File.Id;
                await transaction.CommitAsync();
            }
            var claim = withUpload ? await ClaimOutcomeJobAsync(db, JobItemKind.Dat) : null;
            using var fence = claim is null ? null : JobExecutionFenceScope.Enter(claim.Job.Id, claim.FenceToken, TimeProvider.System);
            using var cancellation = new CancellationTokenSource();
            var work = new ObserveDatFilePublication(database.UnitOfWork, db, failure, cancellation);
            var options = Substitute.For<IRomdOptions>();
            options.DataDirectory.Returns(root);
            options.MaxUploadBytes.Returns(1L << 20);
            var reader = Substitute.For<IDatReader>();
            reader.ReadHeaderAsync(Arg.Any<Stream>(), Arg.Any<CancellationToken>()).Returns(new DatMetadata
            {
                Name = "File publication DAT",
                Description = "File publication DAT",
                DatType = DatType.NoIntro
            });
            reader.StreamGamesAsync(Arg.Any<Stream>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(_ => EmptyDatGameStream());
            var repository = new DatRepository(db, new ManualTimeProvider(DatPublicationTime));
            var handler = new IngestDatCommandHandler(reader, repository,
                new TitleDerivationService(db, new TitleMatcher(new TitleRepository(db), clock: TimeProvider.System), database.UnitOfWork),
                new LocalTempFileFactory(options, new System.IO.Abstractions.FileSystem()), storage, Substitute.For<IBiosGrouper>(), work,
                new AdminRealtimeOutbox(db, TimeProvider.System), Substitute.For<ILibraryRepository>(),
                Substitute.For<ITaxonomyResolver<Region>>(), Substitute.For<ITaxonomyResolver<GameLanguage>>(),
                Substitute.For<ICatalogProjectionService>(), Substitute.For<IPlatformHeaderResolver>(),
                NullLogger<IngestDatCommandHandler>.Instance, new SourceLifecycleStore(db), new ManualTimeProvider(DatPublicationTime),
                new JobItemRepository(db));
            var command = IngestDatCommand.Create(new MemoryStream(DatPublicationBytes), "publication.dat").Value with
            {
                UploadItem = claim is null ? null : new UploadItemContext(claim.Job.Id, "folder/publication.dat")
            };

            if (failure == DatFilePublicationFailure.CancelAfterIdentityFlush)
                (await Should.ThrowAsync<OperationCanceledException>(() => handler.HandleAsync(command, cancellation.Token)))
                    .CancellationToken.ShouldBe(cancellation.Token);
            else if (failure == DatFilePublicationFailure.AfterIdentityFlush)
                await Should.ThrowAsync<IOException>(() => handler.HandleAsync(command, cancellation.Token));
            else if (failure == DatFilePublicationFailure.Commit && withUpload)
                await Should.ThrowAsync<UploadOutcomeUncertainException>(() => handler.HandleAsync(command, cancellation.Token));
            else if (failure == DatFilePublicationFailure.Commit)
                await Should.ThrowAsync<InvalidOperationException>(() => handler.HandleAsync(command, cancellation.Token));
            else
            {
                var result = await handler.HandleAsync(command, cancellation.Token);
                result.IsError.ShouldBeFalse();
                result.Value.DatFile.FileId.ShouldBe(existingId ?? work.GeneratedFileId);
            }

            work.FirstFlushHadPendingFile.ShouldBe(!existingFile);
            work.FirstFlushHadPendingDat.ShouldBe(existingFile);
            if (!existingFile)
            {
                work.GeneratedFileId.ShouldBeGreaterThan(0);
                work.SawFileBeforeDat.ShouldBeTrue();
            }
            if (failure != DatFilePublicationFailure.None)
                db.ChangeTracker.Entries().ShouldBeEmpty();
            await db.SaveChangesAsync();
            bool committed = failure == DatFilePublicationFailure.None;
            await using var verification = database.CreateReadContext();
            (await verification.Files.CountAsync()).ShouldBe(committed || existingFile ? 1 : 0);
            (await verification.DatFiles.CountAsync()).ShouldBe(committed ? 1 : 0);
            (await verification.DatSources.CountAsync()).ShouldBe(committed ? 1 : 0);
            (await verification.CatalogSources.CountAsync()).ShouldBe(committed ? 1 : 0);
            (await verification.JobItems.CountAsync()).ShouldBe(committed && withUpload ? 1 : 0);
            (await GetEventTypesAsync(verification)).ShouldBe(committed ? StatsEventTypes() : []);
            var hash = Romd.Domain.Hashing.Sha256.FromSpan(System.Security.Cryptography.SHA256.HashData(DatPublicationBytes));
            bool orphan = failure is DatFilePublicationFailure.AfterIdentityFlush or DatFilePublicationFailure.CancelAfterIdentityFlush;
            (await cas.ExistsAsync(StorageKey.FromHash(hash))).ShouldBe(committed || existingFile || orphan);
            if (orphan)
            {
                (await storage.DeleteOrphanAsync(hash, CancellationToken.None)).ShouldBeTrue();
                (await cas.ExistsAsync(StorageKey.FromHash(hash))).ShouldBeFalse();
            }
            if (committed)
            {
                var file = await verification.Files.SingleAsync();
                var dat = await verification.DatFiles.SingleAsync();
                dat.FileId.ShouldBe(file.Id);
                dat.DatSourceId.ShouldBe((await verification.DatSources.SingleAsync()).Id);
                dat.Lifecycle.ShouldBe(nameof(DatFileLifecycle.Active));
                file.CreatedAt.ShouldBe(DateTimeOffset.FromUnixTimeMilliseconds(DatPublicationTime.ToUnixTimeMilliseconds()));
            }
            if (failure == DatFilePublicationFailure.Commit)
                database.UnitOfWork.RollbackCleanupCount.ShouldBe(1);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    public enum DatFilePublicationFailure { None, AfterIdentityFlush, CancelAfterIdentityFlush, Commit }

    private sealed class ObserveDatFilePublication(IUnitOfWork inner, RomdDbContext db, DatFilePublicationFailure failure,
        CancellationTokenSource cancellation) : IUnitOfWork
    {
        private bool _flushed;
        public bool FirstFlushHadPendingFile { get; private set; }
        public bool FirstFlushHadPendingDat { get; private set; }
        public bool SawFileBeforeDat { get; private set; }
        public int GeneratedFileId { get; private set; }
        public TransactionConflict ConflictOf(Exception error) => inner.ConflictOf(error);
        public Task<ITransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) => inner.BeginTransactionAsync(cancellationToken);
        public async Task FlushAsync(CancellationToken cancellationToken = default)
        {
            bool first = !_flushed;
            if (first)
            {
                FirstFlushHadPendingFile = db.ChangeTracker.Entries<FileEntityPersistence>().Any(entry => entry.State == EntityState.Added);
                FirstFlushHadPendingDat = db.ChangeTracker.Entries<DatFileEntity>().Any(entry => entry.State == EntityState.Added);
            }
            await inner.FlushAsync(cancellationToken);
            _flushed = true;
            if (first && FirstFlushHadPendingFile)
            {
                GeneratedFileId = (await db.Files.SingleAsync(cancellationToken)).Id;
                SawFileBeforeDat = !await db.DatFiles.AnyAsync(cancellationToken);
                if (failure == DatFilePublicationFailure.AfterIdentityFlush)
                    throw new IOException("Injected failure after DAT file identity flush.");
                if (failure == DatFilePublicationFailure.CancelAfterIdentityFlush)
                {
                    await cancellation.CancelAsync();
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
        }
    }
}
