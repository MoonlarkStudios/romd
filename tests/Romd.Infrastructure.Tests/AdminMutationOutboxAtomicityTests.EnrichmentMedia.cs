using Romd.Infrastructure.Tests.Helpers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Romd.Admin.Application.Artwork;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Configuration;
using Romd.Admin.Application.Dashboard;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Libraries;
using Romd.Admin.Application.Storage.Files;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Domain.Catalog;
using Romd.Domain.Hashing;
using Romd.Domain.Jobs;
using Romd.Domain.Source.Platform;
using Romd.Infrastructure.Jobs;
using Romd.Infrastructure.Jobs.Executors;
using Romd.Infrastructure.Realtime;
using Romd.Infrastructure.Storage;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.Storage;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests;

public sealed partial class AdminMutationOutboxAtomicityTests
{
    [Theory]
    [InlineData("single", false)]
    [InlineData("single", true)]
    [InlineData("bulk-enriched", false)]
    [InlineData("bulk-enriched", true)]
    [InlineData("bulk-not-found", false)]
    [InlineData("bulk-not-found", true)]
    public async Task EnrichmentMedia_OwnedMutation_CommitsFileRowBlobAndMediaReferenceTogether(string path, bool existingFile)
    {
        await using var database = await TestDatabase.CreateAsync();
        database.Context.Platforms.Add(new PlatformEntity
        {
            Id = 3,
            Name = "SNES",
            Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd,
            BuiltInVersion = 1,
            BaseName = "SNES",
            BaseCompactLabel = "SNES",
            CanonicalKey = "snes",
            ShortName = "snes",
            CreatedAt = DateTimeOffset.UtcNow
        });
        database.Context.Titles.Add(new TitleEntity
        {
            Id = 7,
            PlatformId = 3,
            Name = "Chrono",
            NormalizedName = "chrono",
            EnrichmentStatus = nameof(EnrichmentStatus.Pending),
            CreatedAt = DateTimeOffset.UtcNow
        });
        await database.Context.SaveChangesAsync();
        database.Context.ChangeTracker.Clear();
        string root = Path.Combine(Path.GetTempPath(), $"romd-enrichment-media-{Guid.NewGuid():N}");
        byte[] image = Enumerable.Range(0, 70_000).Select(i => (byte)(i % 13)).ToArray();
        var options = Options.Create(new EnrichmentOptions());
        try
        {
            await using var services = new ServiceCollection()
                .AddContentAddressableStorage(settings => settings.RootPath = root).BuildServiceProvider();
            var cas = services.GetRequiredService<IContentAddressableStore>();
            FileStorageService Storage(RomdDbContext db) => new(cas, new FileRepository(db), new EfUnitOfWork(db),
                new FileMutationLock(db), new AdminRealtimeOutbox(db, TimeProvider.System),
                NullLogger<FileStorageService>.Instance, TimeProvider.System);
            int? existingFileId = null;
            if (existingFile)
            {
                var owner = new EfUnitOfWork(database.Context);
                await using var content = new MemoryStream(image, writable: false);
                await using var staged = await Storage(database.Context).StageAsync(content);
                await using (var transaction = await owner.BeginTransactionAsync())
                {
                    var publication = await Storage(database.Context).PublishStagedAsync(staged);
                    publication.RequiresFlush.ShouldBeTrue();
                    await owner.FlushAsync();
                    existingFileId = publication.ReadAfterFlush().File.Id;
                    await transaction.CommitAsync();
                }
                database.Context.ChangeTracker.Clear();
            }
            if (path != "single")
            {
                var job = BulkEnrichmentJob.Create(3, "snes", createdAt: DateTimeOffset.UtcNow, EnrichmentScope.All);
                await new BulkEnrichmentJobRepository(database.Context, TimeProvider.System).CommitCreationAsync(database.Context, job);
                database.Context.ChangeTracker.Clear();
                await using var bulkServices = CreateBulkTitleServices(database, options, Storage);
                var completed = await RunQueueOperationAsync(database, job.Id,
                    db => new BulkEnrichmentJobRepository(db, TimeProvider.System),
                    db =>
                    {
                        var storage = Storage(db);
                        return new BulkEnrichmentJobExecutor(new TitleRepository(db), new TitleReadRepository(db), new PlatformRepository(db),
                            new StagedMediaOrchestrator(storage, image, path == "bulk-enriched"), Substitute.For<IStatsNotifier>(), options,
                            NullLogger<BulkEnrichmentJobExecutor>.Instance,
                            bulkServices.GetRequiredService<IServiceScopeFactory>(), clock: TimeProvider.System);
                    });
                completed.PhaseEnum.ShouldBe(BulkEnrichmentPhase.Completed);
            }
            else
            {
                var job = EnrichmentJob.Create("Chrono", 7, 3, TimeProvider.System.GetUtcNow());
                await new EnrichmentJobRepository(database.Context, TimeProvider.System).CommitCreationAsync(database.Context, job);
                database.Context.ChangeTracker.Clear();
                var completed = await RunQueueOperationAsync(database, job.Id,
                    db => new EnrichmentJobRepository(db, TimeProvider.System),
                    db =>
                    {
                        var storage = Storage(db);
                        return new EnrichmentJobExecutor(new TitleEnrichmentPipeline(new TitleRepository(db), new TitleRepository(db), new TitleRepository(db),
                            new PlatformRepository(db), new StagedMediaOrchestrator(storage, image), storage, new EfUnitOfWork(db),
                            Substitute.For<IStatsNotifier>(), Substitute.For<ILibraryMaterializationService>(), options,
                            NullLogger<TitleEnrichmentPipeline>.Instance,
                            Substitute.For<Romd.Admin.Application.Artwork.IAutomaticArtworkService>(),
                            Substitute.For<Romd.Admin.Application.Artwork.IArtworkCompletionStore>(), clock: TimeProvider.System));
                    });
                completed.PhaseEnum.ShouldBe(EnrichmentJobPhase.Completed);
            }

            await using var read = database.CreateReadContext();
            var media = await read.TitleMedia.SingleAsync(row => row.TitleId == 7);
            media.SourceId.ShouldBe("igdb");
            (await read.Files.CountAsync()).ShouldBe(1);
            if (existingFileId is { } identity)
                media.FileId.ShouldBe(identity);
            var file = await read.Files.SingleAsync(row => row.Id == media.FileId);
            file.Sha256.ShouldBe(Sha256.FromSpan(SHA256.HashData(image)));
            (await cas.VerifyAsync(StorageKey.FromHash(file.Sha256))).ShouldBeTrue();
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData("single", false)]
    [InlineData("single", true)]
    [InlineData("bulk-enriched", false)]
    [InlineData("bulk-enriched", true)]
    [InlineData("bulk-not-found", false)]
    [InlineData("bulk-not-found", true)]
    public async Task EnrichmentMedia_InterruptAfterActualFileIdentityFlush_RollsBackLedgerAndReferenceUnderWorkerFence(string path, bool cancel)
    {
        await using var database = await TestDatabase.CreateAsync();
        var db = database.Context;
        db.Platforms.Add(new PlatformEntity
        {
            Id = 3,
            Name = "SNES",
            ShortName = "snes",
            CanonicalKey = "snes",
            BaseName = "SNES",
            BaseCompactLabel = "SNES",
            Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd,
            BuiltInVersion = 1,
            CreatedAt = DateTimeOffset.UtcNow
        });
        db.Titles.Add(new TitleEntity
        {
            Id = 7,
            PlatformId = 3,
            Name = "Chrono",
            NormalizedName = "chrono",
            EnrichmentStatus = nameof(EnrichmentStatus.Pending),
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var claims = new JobExecutionClaimOptions();
        Guid jobId;
        Guid fenceToken;
        BulkEnrichmentJob? bulkJob = null;
        if (path == "single")
        {
            var job = EnrichmentJob.Create("Chrono", 7, 3, TimeProvider.System.GetUtcNow());
            var repository = new EnrichmentJobRepository(db, TimeProvider.System, claims);
            await repository.CommitCreationAsync(db, job);
            var claimed = (await repository.TryClaimExecutionAsync(job.Id, "identity-flush")).ShouldNotBeNull();
            (jobId, fenceToken) = (job.Id, claimed.FenceToken);
        }
        else
        {
            var job = BulkEnrichmentJob.Create(3, "snes", DateTimeOffset.UtcNow, EnrichmentScope.All);
            var repository = new BulkEnrichmentJobRepository(db, TimeProvider.System, claims);
            await repository.CommitCreationAsync(db, job);
            var claimed = (await repository.TryClaimExecutionAsync(job.Id, "identity-flush")).ShouldNotBeNull();
            (jobId, fenceToken, bulkJob) = (job.Id, claimed.FenceToken, claimed.Job);
        }
        string root = Path.Combine(Path.GetTempPath(), $"romd-media-identity-{Guid.NewGuid():N}");
        using var cancellation = new CancellationTokenSource();
        byte[] image = Enumerable.Range(0, 70_000).Select(index => (byte)(index % 13)).ToArray();
        var primary = new IOException("after actual worker file identity SQL");
        try
        {
            await using var services = new ServiceCollection().AddContentAddressableStorage(options => options.RootPath = root).BuildServiceProvider();
            var cas = services.GetRequiredService<IContentAddressableStore>();
            var owner = new EfUnitOfWork(db);
            var phase = new MediaIdentityFlushWork(owner, db, database, cancellation, cancel, primary);
            var storage = new FileStorageService(cas, new FileRepository(db), owner, new FileMutationLock(db),
                database.Outbox, NullLogger<FileStorageService>.Instance, TimeProvider.System);
            var titles = new TitleRepository(db);
            var orchestrator = new StagedMediaOrchestrator(storage, image, path != "bulk-not-found");
            var guard = new JobExecutionMutationGuard(db, owner, TimeProvider.System, claims);
            var context = new JobContext(null, _ => Task.CompletedTask, cancellation.Token,
                ownedMutationDelegate: (mutation, token) => guard.ExecuteAsync(jobId, fenceToken, mutation, token), jobId: jobId);
            var options = Options.Create(new EnrichmentOptions());
            async Task PublishAsync()
            {
                using var fence = JobExecutionFenceScope.Enter(jobId, fenceToken, TimeProvider.System);
                if (path == "single")
                {
                    var pipeline = new TitleEnrichmentPipeline(titles, titles, titles, new PlatformRepository(db), orchestrator,
                        storage, phase, Substitute.For<IStatsNotifier>(), Substitute.For<ILibraryMaterializationService>(), options,
                        NullLogger<TitleEnrichmentPipeline>.Instance, Substitute.For<IAutomaticArtworkService>(),
                        Substitute.For<IArtworkCompletionStore>(), TimeProvider.System);
                    await pipeline.EnrichAsync(7, 3, context);
                }
                else
                {
                    var title = (await titles.GetAggregateAsync(7)).ShouldNotBeNull();
                    var platform = (await new PlatformRepository(db).GetByIdAsync(3)).ShouldNotBeNull();
                    await using var prepared = await orchestrator.EnrichTitleAsync(title, platform, cancellation.Token);
                    var processor = new BulkEnrichmentTitleProcessor(titles, titles, titles, new PlatformRepository(db), storage, phase,
                        Substitute.For<IJobNotifier>(), Substitute.For<ILibraryMaterializationService>(), options,
                        NullLogger<BulkEnrichmentTitleProcessor>.Instance, Substitute.For<IAutomaticArtworkService>(), TimeProvider.System);
                    await processor.ProcessResultAsync(bulkJob!, context, title, platform, prepared, new HashSet<int>());
                }
            }

            if (cancel)
                (await Should.ThrowAsync<OperationCanceledException>(PublishAsync)).CancellationToken.ShouldBe(cancellation.Token);
            else
                (await Should.ThrowAsync<IOException>(PublishAsync)).ShouldBeSameAs(primary);

            phase.FlushCount.ShouldBe(1);
            db.ChangeTracker.Entries().ShouldBeEmpty();
            await using var read = database.CreateReadContext();
            (await read.Files.CountAsync()).ShouldBe(0);
            (await read.TitleMedia.CountAsync()).ShouldBe(0);
            (await read.Titles.SingleAsync()).EnrichmentStatus.ShouldBe(nameof(EnrichmentStatus.Pending));
            // CAS publication is not rolled back; its unreferenced bytes remain for the locked collector.
            (await cas.VerifyAsync(StorageKey.FromHash(Sha256.FromSpan(SHA256.HashData(image))))).ShouldBeTrue();
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task BulkEnrichmentMedia_ArtworkFailurePreservesFirstCommittedPublication()
    {
        await using var database = await TestDatabase.CreateAsync();
        var db = database.Context;
        db.Platforms.Add(new PlatformEntity
        {
            Id = 3,
            Name = "SNES",
            ShortName = "snes",
            CanonicalKey = "snes",
            BaseName = "SNES",
            BaseCompactLabel = "SNES",
            Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd,
            BuiltInVersion = 1,
            CreatedAt = DateTimeOffset.UtcNow
        });
        db.Titles.Add(new TitleEntity
        {
            Id = 7,
            PlatformId = 3,
            Name = "Chrono",
            NormalizedName = "chrono",
            EnrichmentStatus = nameof(EnrichmentStatus.Pending),
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var claims = new JobExecutionClaimOptions();
        var job = BulkEnrichmentJob.Create(3, "snes", DateTimeOffset.UtcNow, EnrichmentScope.All);
        var jobs = new BulkEnrichmentJobRepository(db, TimeProvider.System, claims);
        await jobs.CommitCreationAsync(db, job);
        var claimed = (await jobs.TryClaimExecutionAsync(job.Id, "after-media-commit")).ShouldNotBeNull();
        string root = Path.Combine(Path.GetTempPath(), $"romd-media-partial-{Guid.NewGuid():N}");
        byte[] image = Enumerable.Range(0, 70_000).Select(index => (byte)(index % 13)).ToArray();
        var primary = new IOException("artwork failed after media commit");
        try
        {
            await using var services = new ServiceCollection().AddContentAddressableStorage(options => options.RootPath = root).BuildServiceProvider();
            var cas = services.GetRequiredService<IContentAddressableStore>();
            var owner = new EfUnitOfWork(db);
            var storage = new FileStorageService(cas, new FileRepository(db), owner, new FileMutationLock(db),
                database.Outbox, NullLogger<FileStorageService>.Instance, TimeProvider.System);
            var titles = new TitleRepository(db);
            var title = (await titles.GetAggregateAsync(7)).ShouldNotBeNull();
            var platform = (await new PlatformRepository(db).GetByIdAsync(3)).ShouldNotBeNull();
            var artwork = Substitute.For<IAutomaticArtworkService>();
            artwork.FillAsync(Arg.Any<Title>(), Arg.Any<Platform>(), Arg.Any<JobContext>(), Arg.Any<CancellationToken>(),
                Arg.Any<IReadOnlyList<ProviderArtworkResult>?>())
                .Returns(_ => Task.FromException(primary));
            var processor = new BulkEnrichmentTitleProcessor(titles, titles, titles, new PlatformRepository(db), storage, owner,
                Substitute.For<IJobNotifier>(), Substitute.For<ILibraryMaterializationService>(), Options.Create(new EnrichmentOptions()),
                NullLogger<BulkEnrichmentTitleProcessor>.Instance, artwork, TimeProvider.System);
            var guard = new JobExecutionMutationGuard(db, owner, TimeProvider.System, claims);
            var context = new JobContext(null, _ => Task.CompletedTask, CancellationToken.None,
                ownedMutationDelegate: (mutation, token) => guard.ExecuteAsync(job.Id, claimed.FenceToken, mutation, token), jobId: job.Id);
            using (JobExecutionFenceScope.Enter(job.Id, claimed.FenceToken, TimeProvider.System))
            {
                await using var prepared = await new StagedMediaOrchestrator(storage, image).EnrichTitleAsync(title, platform);
                (await Should.ThrowAsync<IOException>(() => processor.ProcessResultAsync(claimed.Job, context, title, platform,
                    prepared, new HashSet<int>()))).ShouldBeSameAs(primary);
            }

            await using var read = database.CreateReadContext();
            var media = await read.TitleMedia.SingleAsync();
            var file = await read.Files.SingleAsync();
            media.FileId.ShouldBe(file.Id);
            (await read.Titles.SingleAsync()).EnrichmentStatus.ShouldBe(nameof(EnrichmentStatus.Pending));
            (await cas.VerifyAsync(StorageKey.FromHash(file.Sha256))).ShouldBeTrue();
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    private sealed class MediaIdentityFlushWork(IUnitOfWork inner, RomdDbContext db, TestDatabase database,
        CancellationTokenSource cancellation, bool cancel, IOException primary) : IUnitOfWork
    {
        public int FlushCount { get; private set; }
        public Task<ITransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The existing fenced JobContext owns the transaction.");
        public TransactionConflict ConflictOf(Exception error) => inner.ConflictOf(error);
        public async Task FlushAsync(CancellationToken cancellationToken = default)
        {
            await inner.FlushAsync(cancellationToken);
            FlushCount++;
            (await db.Files.CountAsync(cancellationToken)).ShouldBe(1);
            (await db.TitleMedia.CountAsync(cancellationToken)).ShouldBe(0);
            await using (var outside = database.CreateReadContext())
            {
                (await outside.Files.CountAsync(cancellationToken)).ShouldBe(0);
                (await outside.TitleMedia.CountAsync(cancellationToken)).ShouldBe(0);
            }
            if (cancel)
            {
                await cancellation.CancelAsync();
                cancellationToken.ThrowIfCancellationRequested();
            }
            throw primary;
        }
    }

    /// <summary>Finds metadata and one screenshot, staged through the executor's own file ledger.</summary>
    private sealed class StagedMediaOrchestrator(IFileStorageService storage, byte[] image, bool enriched = true) : IEnrichmentOrchestrator
    {
        public async Task<OrchestrationResult> EnrichTitleAsync(Title title, Platform platform,
            CancellationToken cancellationToken = default)
        {
            title.StoreProviderLayer("igdb", MetadataSourceType.Provider,
                new TitleMetadataPayload { Description = "Provider evidence" }, updatedAt: DateTimeOffset.UnixEpoch);
            var download = new MemoryStream(image, writable: false);
            var staged = await storage.StageAsync(download, cancellationToken);
            return (enriched ? OrchestrationResult.Found(1) : OrchestrationResult.NotFound) with
            {
                StagedMedia =
                [
                    new StagedTitleMedia(MediaType.Screenshot, "igdb", "image/png",
                        "https://images.igdb.com/igdb/image/upload/shot.png", staged, download)
                ]
            };
        }

        public async IAsyncEnumerable<(int TitleId, OrchestrationResult Result)> EnrichStreamAsync(
            IAsyncEnumerable<(Title Title, Platform Platform)> titles,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var (title, platform) in titles.WithCancellation(cancellationToken))
                yield return (title.Id, await EnrichTitleAsync(title, platform, cancellationToken));
        }
    }
}
