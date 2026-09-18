using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Npgsql;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Configuration;
using Romd.Admin.Application.Dashboard;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Libraries;
using Romd.Admin.Application.Source.Dat.Commands.ActivateDatVersion;
using Romd.Admin.Application.Source.Dat.Commands.IngestDat;
using Romd.Admin.Application.Storage.Files;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Domain.Catalog;
using Romd.Domain.Jobs;
using Romd.Domain.Source.Dat;
using Romd.Domain.Source.Platform;
using Romd.Infrastructure.Catalog;
using Romd.Infrastructure.Jobs.Executors;
using Romd.Infrastructure.Jobs;
using Romd.Infrastructure.Realtime;
using Romd.Infrastructure.Tests.Helpers;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.Persistence;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests;

public sealed partial class AdminMutationOutboxAtomicityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueueSafety_ReplaceJobsSharingSource_OverlapConflictsWhileSerialSucceeds(bool overlap)
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedPlatformFileAndDatAsync(database.Context, 7, 17, 10);
        await SeedGameWithRomAsync(database.Context, 70, 7, Sha1One);
        int sourceId = (await database.Context.DatSources.SingleAsync()).Id;
        await SeedLibraryAsync(database.Context, 3);
        await SeedFileAsync(database.Context, 51, DateTimeOffset.UtcNow);
        await SeedFileAsync(database.Context, 52, DateTimeOffset.UtcNow);
        // Explicit-id fixtures must advance the sequence before the real ingest allocates ids.
        await database.Context.Database.ExecuteSqlRawAsync("SELECT setval('romd.\"DatFiles_Id_seq\"', 7)");
        var jobs = new[] { ReplaceDatJob.Create(7, "a.dat", 10), ReplaceDatJob.Create(7, "b.dat", 10) };
        foreach (var job in jobs)
        {
            await new ReplaceDatJobRepository(database.Context, TimeProvider.System).AddAsync(job);
            database.Context.ChangeTracker.Clear();
        }
        var firstIngested = new QueueOperationGate();
        var ingestLog = new QueueExceptionLogger<IngestDatCommandHandler>();
        Task<ReplaceDatJob> Run(int index) => RunQueueOperationAsync(database, jobs[index].Id,
            db => new ReplaceDatJobRepository(db, TimeProvider.System),
            db => new QueueOperationExecutor<ReplaceDatJob>(async (job, context) =>
            {
                var unit = new EfUnitOfWork(db);
                var ingest = NewReplaceIngestHandler(db, unit, 51 + index, ingestLog);
                using var content = new MemoryStream([1]);
                var ingested = await ingest.HandleAsync(IngestDatCommand.Create(content, job.SourceFilename,
                    10, sourceId).Value, context.CancellationToken);
                if (ingested.IsError) throw new InvalidOperationException(ingested.FirstError.Description);
                job.SetNewDatId(ingested.Value.DatFile.Id);
                job.BeginReplacing();
                await context.CheckpointAsync();
                if (overlap && index == 0) await firstIngested.WaitAsync(context.CancellationToken);
                var handler = new ActivateDatVersionCommandHandler(
                    new DatRepository(db, TimeProvider.System), CatalogProjectionTestFactory.Create(db),
                    new LibraryRepository(db), new AdminRealtimeOutbox(db, TimeProvider.System), unit,
                    TimeProvider.System, NullLogger<ActivateDatVersionCommandHandler>.Instance,
                    new SourceLifecycleStore(db));
                var result = await handler.HandleAsync(ActivateDatVersionCommand.Create(job.NewDatId!.Value).Value,
                    context.CancellationToken);
                if (result.IsError) throw new InvalidOperationException(result.FirstError.Description);
            }));
        var first = Run(0);
        ReplaceDatJob second;
        if (overlap)
        {
            await firstIngested.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            second = await Run(1);
            firstIngested.Release.TrySetResult();
        }
        else
        {
            (await first).PhaseEnum.ShouldBe(ReplaceDatPhase.Completed);
            second = await Run(1);
        }
        (await first).PhaseEnum.ShouldBe(ReplaceDatPhase.Completed);
        second.PhaseEnum.ShouldBe(overlap ? ReplaceDatPhase.Ingesting : ReplaceDatPhase.Completed);
        second.Errors.Count.ShouldBe(overlap ? 1 : 0);
        AssertQueueUniqueConflict(ingestLog.Exceptions, overlap, "IX_DatFiles_Pending_SourceId");
        await using var read = database.CreateReadContext();
        var versions = await read.DatFiles.OrderBy(d => d.Id).ToListAsync();
        versions.Count(d => d.Lifecycle == nameof(DatFileLifecycle.Active)).ShouldBe(1);
        versions.Single(d => d.Lifecycle == nameof(DatFileLifecycle.Active)).FileId.ShouldBe(overlap ? 51 : 52);
        versions.ShouldNotContain(d => d.Lifecycle == nameof(DatFileLifecycle.PendingActivation));
        (await read.DatGames.AnyAsync(g => g.DatFileId == 7)).ShouldBeFalse();
        (await read.Platforms.SingleAsync(p => p.Id == 10)).CatalogRebuildState.ShouldBe(CatalogRebuildState.Dirty);
        (await read.Libraries.SingleAsync(l => l.Id == 3)).NeedsMaterialization.ShouldBeTrue();
        (await read.AdminRealtimeOutboxEvents.CountAsync()).ShouldBe(overlap ? 6 : 12);
        (await read.JobDispatches.CountAsync()).ShouldBe(2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueueSafety_SingleAndBulkEnrichmentShareTitle_RejectsStaleSnapshotAndPreservesWinningEvidence(bool overlap)
    {
        await using var database = await TestDatabase.CreateAsync();
        database.Context.Platforms.Add(new PlatformEntity
        {
            Id = 3, Name = "SNES", Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "SNES", BaseCompactLabel = "SNES", CanonicalKey = "snes", ShortName = "snes", CreatedAt = DateTimeOffset.UtcNow
        });
        database.Context.Titles.Add(new TitleEntity
        {
            Id = 7, PlatformId = 3, Name = "Chrono", NormalizedName = "chrono",
            EnrichmentStatus = nameof(EnrichmentStatus.Pending), CreatedAt = DateTimeOffset.UtcNow
        });
        await database.Context.SaveChangesAsync();
        database.Context.ChangeTracker.Clear();
        var single = EnrichmentJob.Create("Chrono", 7, 3, TimeProvider.System);
        var bulk = BulkEnrichmentJob.Create(3, "snes", EnrichmentScope.All);
        await new EnrichmentJobRepository(database.Context, TimeProvider.System).AddAsync(single);
        database.Context.ChangeTracker.Clear();
        await new BulkEnrichmentJobRepository(database.Context, TimeProvider.System).AddAsync(bulk);
        database.Context.ChangeTracker.Clear();
        var gates = new[] { new QueueOperationGate(), new QueueOperationGate() };
        var options = Options.Create(new EnrichmentOptions());
        var first = RunQueueOperationAsync(database, single.Id,
            db => new EnrichmentJobRepository(db, TimeProvider.System),
            db => new EnrichmentJobExecutor(new TitleRepository(db), new PlatformRepository(db),
                new QueueEvidenceOrchestrator(overlap ? gates[0] : null, "Single provider evidence"), Substitute.For<IFileStorageService>(),
                Substitute.For<IStatsNotifier>(), Substitute.For<ILibraryMaterializationService>(), options,
                NullLogger<EnrichmentJobExecutor>.Instance, Substitute.For<Romd.Admin.Application.Artwork.IAutomaticArtworkService>()));
        var bulkLog = new QueueExceptionLogger<BulkEnrichmentJobExecutor>();
        Task<BulkEnrichmentJob> RunBulk() => RunQueueOperationAsync(database, bulk.Id,
            db => new BulkEnrichmentJobRepository(db, TimeProvider.System),
            db => new BulkEnrichmentJobExecutor(new TitleRepository(db), new PlatformRepository(db),
                new QueueEvidenceOrchestrator(overlap ? gates[1] : null, "Bulk provider evidence"), Substitute.For<IFileStorageService>(),
                Substitute.For<IJobNotifier>(), Substitute.For<IStatsNotifier>(),
                Substitute.For<ILibraryMaterializationService>(), options,
                bulkLog, Substitute.For<Romd.Admin.Application.Artwork.IAutomaticArtworkService>()));
        Task<BulkEnrichmentJob> second;
        if (overlap)
        {
            second = RunBulk();
            await Task.WhenAll(gates.Select(g => g.Entered.Task)).WaitAsync(TimeSpan.FromSeconds(20));
            gates[0].Release.TrySetResult();
            (await first).PhaseEnum.ShouldBe(EnrichmentJobPhase.Completed);
            gates[1].Release.TrySetResult();
        }
        else
        {
            (await first).PhaseEnum.ShouldBe(EnrichmentJobPhase.Completed);
            second = RunBulk();
        }
        var result = await second;
        result.FailedCount.ShouldBe(overlap ? 1 : 0);
        result.PhaseEnum.ShouldBe(overlap ? BulkEnrichmentPhase.CompletedWithErrors : BulkEnrichmentPhase.Completed);
        if (overlap)
        {
            var conflict = bulkLog.Exceptions.ShouldHaveSingleItem().ShouldBeOfType<PersistenceConflictException>();
            conflict.InnerException.ShouldBeOfType<DbUpdateConcurrencyException>();
        }
        else
        {
            bulkLog.Exceptions.ShouldBeEmpty();
        }
        await using var read = database.CreateReadContext();
        var evidence = await read.TitleMetadataLayers.SingleAsync();
        evidence.SourceId.ShouldBe("igdb");
        evidence.ToDomain().GetPayload()!.Description.ShouldBe("Single provider evidence");
        (await read.Titles.SingleAsync()).EnrichmentStatus.ShouldBe(nameof(EnrichmentStatus.Completed));
        (await read.Jobs.CountAsync()).ShouldBe(2);
        (await read.JobDispatches.CountAsync()).ShouldBe(2);
    }

    // Run production executors/operations with separate persisted identities, claim/checkpoint
    // contexts and the production mutation fence. Transport scheduling is intentionally excluded.
    private static async Task<TJob> RunQueueOperationAsync<TJob>(TestDatabase database, Guid id,
        Func<RomdDbContext, IJobRepository<TJob>> repository,
        Func<RomdDbContext, IJobExecutor<TJob>> executor) where TJob : Job
    {
        await using var claimDb = database.CreateReadContext();
        await using var operationDb = database.CreateReadContext();
        var claims = new ClaimedJobRepository<TJob, IJobRepository<TJob>>(
            repository(claimDb), claimDb, TimeProvider.System, new JobExecutionClaimOptions());
        var claim = await claims.TryClaimExecutionAsync(id, $"queue-safety-{id}");
        claim.ShouldNotBeNull();
        var job = claim.Job;
        using var fence = JobExecutionFenceScope.Enter(id, claim.FenceToken, TimeProvider.System);
        var guard = new JobExecutionMutationGuard(operationDb, new EfUnitOfWork(operationDb),
            TimeProvider.System, new JobExecutionClaimOptions());
        var context = new JobContext(null, async ct =>
        {
            (await claims.TryUpdateClaimedAsync(job, claim.FenceToken, ct)).ShouldBeTrue();
        }, CancellationToken.None, ownedMutationDelegate: (mutation, ct) =>
            guard.ExecuteAsync(id, claim.FenceToken, mutation, ct));
        var operation = executor(operationDb);
        try
        {
            await operation.ExecuteAsync(job, context);
            job.Complete();
        }
        catch (JobExecutionOwnershipLostException) { throw; }
        catch (Exception exception) when (operation is IResumableJobExecutor<TJob>)
        {
            // Match the runner's persisted attempt outcome. Return it for assertions instead
            // of rethrowing to Hangfire; redelivery/resume is outside this operation test.
            job.RecordError("attempt", exception.Message);
        }
        catch (Exception exception) { job.Fail(exception.Message); }
        await context.CheckpointAsync();
        return job;
    }

    private sealed class QueueOperationExecutor<TJob>(Func<TJob, JobContext, Task> execute) : IResumableJobExecutor<TJob>
        where TJob : Job
    {
        public Task ExecuteAsync(TJob job, JobContext context) => execute(job, context);
    }

    private sealed class QueueOperationGate
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task WaitAsync(CancellationToken ct)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
        }
    }

    private static void AssertQueueUniqueConflict(IEnumerable<Exception> exceptions, bool overlap, string constraint)
    {
        var errors = exceptions.Select(e => e.GetBaseException()).ToArray();
        if (overlap)
            errors.OfType<PostgresException>().ShouldContain(e =>
                e.SqlState == PostgresErrorCodes.UniqueViolation && e.ConstraintName == constraint);
        else
            errors.ShouldBeEmpty();
    }

    private sealed class QueueExceptionLogger<T> : ILogger<T>
    {
        public ConcurrentBag<Exception> Exceptions { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (exception is not null) Exceptions.Add(exception);
        }
    }

    private sealed class QueueEvidenceOrchestrator(QueueOperationGate? gate, string description) : IEnrichmentOrchestrator
    {
        public async Task<OrchestrationResult> EnrichTitleAsync(Title title, Platform platform,
            CancellationToken cancellationToken = default)
        {
            if (gate is not null) await gate.WaitAsync(cancellationToken);
            title.StoreProviderLayer("igdb", MetadataSourceType.Provider,
                new TitleMetadataPayload { Description = description });
            return OrchestrationResult.Found(1);
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
