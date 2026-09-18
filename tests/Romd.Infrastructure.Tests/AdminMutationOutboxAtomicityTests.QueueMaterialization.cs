using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Libraries;
using Romd.Domain.Hashing;
using Romd.Domain.Jobs;
using Romd.Domain.Libraries;
using Romd.Infrastructure.Jobs.Executors;
using Romd.Infrastructure.Libraries;
using Romd.Infrastructure.Realtime;
using Romd.Infrastructure.Tests.Helpers;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace Romd.Infrastructure.Tests;

public sealed partial class AdminMutationOutboxAtomicityTests
{
    private readonly ITestOutputHelper _queueOutput;

    public AdminMutationOutboxAtomicityTests(ITestOutputHelper output) => _queueOutput = output;

    [Fact]
    public async Task QueueSafety_MaterializationDistinctLibraries_SharedCatalogPublishesIndependentProjections()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedQueueMaterializationAsync(database, 2, 3);
        await using (var configure = database.CreateReadContext())
        {
            var secondLibrary = (await new LibraryRepository(configure).GetByIdAsync(2))!;
            string json = JsonSerializer.Serialize(secondLibrary.Configuration with { ExcludeTitleIds = [3] });
            await configure.Libraries.Where(l => l.Id == 2)
                .ExecuteUpdateAsync(update => update.SetProperty(l => l.ConfigurationJson, json));
        }
        var gates = new[] { new QueueOperationGate(), new QueueOperationGate() };
        var first = await AddQueueMaterializationAsync(database, 1);
        var second = await AddQueueMaterializationAsync(database, 2);
        var runs = new[]
        {
            RunQueueMaterializationAsync(database, first.Id, gates[0]),
            RunQueueMaterializationAsync(database, second.Id, gates[1])
        };
        await Task.WhenAll(gates.Select(g => g.Entered.Task)).WaitAsync(TimeSpan.FromSeconds(20));
        await AssertQueueProjectionsAsync(database, 2, 0, 0);
        foreach (var gate in gates) gate.Release.TrySetResult();
        (await Task.WhenAll(runs)).ShouldAllBe(j => j.PhaseEnum == MaterializationPhase.Completed);
        await AssertQueueProjectionsAsync(database, 2, 3, 1, secondLibraryTitleCount: 2);
        await using var read = database.CreateReadContext();
        (await read.AdminRealtimeOutboxEvents.CountAsync()).ShouldBe(2);
        (await read.JobDispatches.CountAsync()).ShouldBe(2);
    }

    [Fact]
    public async Task QueueSafety_MaterializationCancelledJobOverlapsSuccessor_OnlySuccessorPublishesSameGeneration()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedQueueMaterializationAsync(database, 1, 3);
        var first = await AddQueueMaterializationAsync(database, 1);
        var gate = new QueueOperationGate();
        var oldRun = RunQueueMaterializationAsync(database, first.Id, gate);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));

        // Two active jobs for a library cannot be accepted, even with more queue workers.
        await using (var accept = database.CreateReadContext())
        {
            var rejected = MaterializationJob.Create(1, "same library");
            (await new MaterializationJobRepository(accept, TimeProvider.System)
                .TryAddIfNoActiveForLibraryAsync(rejected)).ShouldBeFalse();
        }
        // Cancellation permits a distinct successor while the old computation is still in flight.
        await using (var cancel = database.CreateReadContext())
        {
            var repository = new MaterializationJobRepository(cancel, TimeProvider.System);
            var job = await repository.GetByIdAsync(first.Id);
            job.ShouldNotBeNull();
            job.Cancel();
            await repository.UpdateAsync(job);
        }
        var successor = await AddQueueMaterializationAsync(database, 1);
        (await RunQueueMaterializationAsync(database, successor.Id)).PhaseEnum.ShouldBe(MaterializationPhase.Completed);
        gate.Release.TrySetResult();
        await Should.ThrowAsync<JobExecutionOwnershipLostException>(() => oldRun);
        await AssertQueueProjectionsAsync(database, 1, 3, 1);
        await using var read = database.CreateReadContext();
        (await read.AdminRealtimeOutboxEvents.CountAsync()).ShouldBe(1);
        (await read.Jobs.SingleAsync(j => j.Id == first.Id)).Phase.ShouldBe(nameof(MaterializationPhase.Cancelled));
        (await read.JobDispatches.CountAsync()).ShouldBe(2);
    }

    [Fact]
    public async Task QueueSafety_MaterializationReflagDuringBuild_RejectsOldProjectionAndSuccessorConverges()
    {
        await using var database = await TestDatabase.CreateAsync();
        await SeedQueueMaterializationAsync(database, 1, 3);
        var first = await AddQueueMaterializationAsync(database, 1);
        var gate = new QueueOperationGate();
        var oldRun = RunQueueMaterializationAsync(database, first.Id, gate);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await using (var reflag = database.CreateReadContext())
            await new LibraryRepository(reflag).FlagAllForRematerializationAsync();
        gate.Release.TrySetResult();
        (await oldRun).PhaseEnum.ShouldBe(MaterializationPhase.Completed);
        await AssertQueueProjectionsAsync(database, 1, 0, 0);
        var successor = await AddQueueMaterializationAsync(database, 1);
        (await RunQueueMaterializationAsync(database, successor.Id)).PhaseEnum.ShouldBe(MaterializationPhase.Completed);
        await AssertQueueProjectionsAsync(database, 1, 3, 1);
    }

    [Fact]
    public async Task QueueSafety_MaterializationWorkerComparison_ReportsThroughputWithEquivalentProjections()
    {
        // No timing threshold in CI. Report warm, alternating 1/2-worker trials, including
        // candidate queries, fenced publication, outbox and checkpoints; exclude seed/acceptance.
        const int libraryCount = 8;
        const int titleCount = 1000;
        await using var database = await TestDatabase.CreateAsync();
        await SeedQueueMaterializationAsync(database, libraryCount, titleCount);
        int generation = 0;
        foreach (int concurrency in new[] { 1, 1, 2, 2, 1, 1, 2 })
        {
            await using (var flag = database.CreateReadContext())
                await new LibraryRepository(flag).FlagAllForRematerializationAsync();
            var jobs = new List<MaterializationJob>();
            for (int libraryId = 1; libraryId <= libraryCount; libraryId++)
                jobs.Add(await AddQueueMaterializationAsync(database, libraryId));
            using var monitoring = new CancellationTokenSource();
            var locks = ObserveQueueLocksAsync(database, monitoring.Token);
            var elapsed = Stopwatch.StartNew();
            (int Samples, int BlockedSamples, int PeakBlocked) contention;
            try
            {
                await Parallel.ForEachAsync(jobs, new ParallelOptions { MaxDegreeOfParallelism = concurrency },
                    async (job, _) => (await RunQueueMaterializationAsync(database, job.Id))
                        .PhaseEnum.ShouldBe(MaterializationPhase.Completed));
            }
            finally
            {
                elapsed.Stop();
                await monitoring.CancelAsync();
                contention = await locks;
            }
            generation++;
            await AssertQueueProjectionsAsync(database, libraryCount, titleCount, generation);
            _queueOutput.WriteLine("workers={0}; libraries={1}; shared_titles={2}; elapsed_ms={3:F1}; jobs_per_second={4:F2}; failed_jobs=0; generation={5}; warmup={6}; lock_samples={7}; blocked_samples={8}; peak_blocked={9}",
                concurrency, libraryCount, titleCount, elapsed.Elapsed.TotalMilliseconds,
                libraryCount / elapsed.Elapsed.TotalSeconds, generation, generation == 1,
                contention.Samples, contention.BlockedSamples, contention.PeakBlocked);
        }
    }

    private static async Task<(int Samples, int BlockedSamples, int PeakBlocked)> ObserveQueueLocksAsync(
        TestDatabase database, CancellationToken ct)
    {
        await using var db = database.CreateReadContext();
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 5;
        command.CommandText = "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'";
        int samples = 0, blockedSamples = 0, peakBlocked = 0;
        while (!ct.IsCancellationRequested)
        {
            int blocked = Convert.ToInt32(await command.ExecuteScalarAsync(CancellationToken.None));
            samples++;
            if (blocked > 0) blockedSamples++;
            peakBlocked = Math.Max(peakBlocked, blocked);
            try { await Task.Delay(5, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        }
        return (samples, blockedSamples, peakBlocked);
    }

    private static async Task SeedQueueMaterializationAsync(TestDatabase database, int libraryCount, int titleCount)
    {
        var db = database.Context;
        await SeedPlatformFileAndDatAsync(db, 7, 17, 10);
        int catalogSourceId = (await db.DatSources.SingleAsync()).CatalogSourceId;
        var now = DateTimeOffset.UtcNow;
        for (int id = 1; id <= titleCount; id++)
        {
            db.Titles.Add(new TitleEntity
            {
                Id = id, PlatformId = 10, Name = $"Game {id}", NormalizedName = $"game {id}",
                EnrichmentStatus = "None", CreatedAt = now
            });
            db.SourceEntries.Add(new SourceEntryEntity
            {
                Id = id, CatalogSourceId = catalogSourceId, PlatformId = 10,
                EntryKey = $"Game {id}", Name = $"Game {id}", CreatedAt = now
            });
            db.TitleSourceLinks.Add(new TitleSourceLinkEntity { TitleId = id, SourceEntryId = id, CreatedAt = now });
            db.DatGames.Add(new DatGameEntity { Id = id, DatFileId = 7, SourceEntryId = id, Name = $"Game {id}", CreatedAt = now });
            db.DatRoms.Add(new DatRomEntity { Id = id, DatGameId = id, Name = $"game-{id}.rom", Size = 1, Sha1 = Sha1.Parse(id.ToString("x40")), CreatedAt = now });
        }
        for (int id = 1; id <= libraryCount; id++)
            db.Libraries.Add(new LibraryEntity
            {
                Id = id, Name = $"Library {id}", ConfigurationState = nameof(LibraryConfigurationState.Valid),
                ConfigurationJson = JsonSerializer.Serialize(new LibraryConfiguration
                {
                    ShowMissingGames = true,
                    ContentRatingPolicy = new ContentRatingPolicy { UnknownRatingPolicy = UnknownMetadataPolicy.Allow }
                }),
                NeedsMaterialization = true, CreatedAt = now, UpdatedAt = now
            });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        (await CatalogProjectionTestFactory.Create(db).RebuildPlatformAsync(10)).ShouldBeTrue();
        db.ChangeTracker.Clear();
    }

    private static async Task<MaterializationJob> AddQueueMaterializationAsync(TestDatabase database, int libraryId)
    {
        await using var db = database.CreateReadContext();
        var job = MaterializationJob.Create(libraryId, $"Library {libraryId}");
        (await new MaterializationJobRepository(db, TimeProvider.System).TryAddIfNoActiveForLibraryAsync(job)).ShouldBeTrue();
        return job;
    }

    private static Task<MaterializationJob> RunQueueMaterializationAsync(TestDatabase database, Guid id,
        QueueOperationGate? gate = null) => RunQueueOperationAsync(database, id,
        db => new MaterializationJobRepository(db, TimeProvider.System),
        db => new MaterializationJobExecutor(new LibraryMaterializationService(
                new LibraryRepository(db), new QueueMaterializationDataProvider(new MaterializationDataProvider(db), gate),
                CatalogProjectionTestFactory.Create(db), new AdminRealtimeOutbox(db, TimeProvider.System),
                new EfUnitOfWork(db), NullLogger<LibraryMaterializationService>.Instance),
            new LibraryRepository(db), NullLogger<MaterializationJobExecutor>.Instance));

    private static async Task AssertQueueProjectionsAsync(TestDatabase database, int libraryCount, int titleCount, int generation,
        int? secondLibraryTitleCount = null)
    {
        await using var read = database.CreateReadContext();
        var libraries = await read.Libraries.OrderBy(l => l.Id).ToListAsync();
        libraries.Count.ShouldBe(libraryCount);
        foreach (var library in libraries)
        {
            library.MaterializationGeneration.ShouldBe(generation);
            library.NeedsMaterialization.ShouldBe(generation == 0);
            library.ItemCount.ShouldBe(library.Id == 2 ? secondLibraryTitleCount ?? titleCount : titleCount);
        }
        int expectedCount = (libraryCount - 1) * titleCount + (secondLibraryTitleCount ?? titleCount);
        (await read.MaterializedLibraryTitles.CountAsync()).ShouldBe(expectedCount);
        var releases = await read.MaterializedLibraryReleases.ToListAsync();
        releases.Count.ShouldBe(expectedCount);
        releases.ShouldAllBe(r => r.IsExposed && r.IsEligible && !r.IsOwned && !r.IsPlayable && r.CatalogReleaseId != null);
        foreach (var library in libraries)
            releases.Where(r => r.LibraryId == library.Id).Select(r => r.TitleId).Order()
                .ShouldBe(Enumerable.Range(1, library.Id == 2 ? secondLibraryTitleCount ?? titleCount : titleCount));
    }

    private sealed class QueueMaterializationDataProvider(IMaterializationDataProvider inner, QueueOperationGate? gate)
        : IMaterializationDataProvider
    {
        public async Task<IReadOnlyList<TitleCandidates>> GetCandidatesAsync(LibraryConfiguration config,
            CancellationToken ct = default)
        {
            var result = await inner.GetCandidatesAsync(config, ct);
            if (gate is not null) await gate.WaitAsync(ct);
            return result;
        }
    }
}
