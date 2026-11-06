using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Source.Dat;
using Romd.Admin.Application.Source.Dat.Commands.IngestDat;
using Romd.Domain.Jobs;
using Romd.Infrastructure.Import;
using Romd.Infrastructure.Jobs.Lifecycle;
using Romd.Infrastructure.Tests.Helpers;
using Romd.Persistence;
using Romd.Persistence.Repositories;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Jobs;

public sealed partial class UploadCapacityRecoveryTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task JournalBatch_WorkAndFinalFlushFail_PreservesOriginalAndRetainsInputsForRealRedelivery(
        bool shutdownCancellation, bool lostJournalAcknowledgement)
    {
        var job = await SeedAsync();
        foreach (string path in Directory.GetFiles(Workspace(job)))
            File.Delete(path);
        string source = Directory.CreateDirectory(Path.Combine(_root, "settlement-sources")).FullName;
        _options.AllowedImportPaths.Returns([source]);
        var entries = new List<ImportManifestEntry>();
        foreach (string name in new[] { "a.dat", "z.dat" })
        {
            string path = Path.Combine(source, name);
            await File.WriteAllBytesAsync(path, [1]);
            File.Copy(path, Path.Combine(Workspace(job), name));
            var info = new FileInfo(path);
            entries.Add(new(name, name, info.Length, info.LastWriteTimeUtc));
        }
        job.SetImportSource(source, move: true);
        for (int count = 0; count < 3; count++)
            job.RecordDeferral("Earlier pressure", _clock.GetUtcNow());
        await using (var db = _database.CreateContext())
            await new UploadJobRepository(db, _clock).CommitCheckpointAsync(db, job);
        string manifest = ImportManifest.PathFor(_root, job.Id);
        await ImportManifest.WriteAsync(manifest, new(ImportManifest.CurrentVersion, source, ".", true, entries), default);
        byte[] manifestBytes = await File.ReadAllBytesAsync(manifest);
        using var shutdown = new CancellationTokenSource();
        Exception primary = shutdownCancellation
            ? new OperationCanceledException("Original DAT interruption", shutdown.Token)
            : new IOException("Original DAT I/O failure");
        var secondary = new IOException("Secondary final journal failure");
        bool interrupt = true;
        _datHandler.HandleAsync(Arg.Any<IngestDatCommand>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            if (interrupt && call.Arg<IngestDatCommand>().OriginalFilename == "z.dat")
            {
                if (shutdownCancellation)
                    shutdown.Cancel();
                return Task.FromException<ErrorOr<DatIngestResult>>(primary);
            }
            return Task.FromResult<ErrorOr<DatIngestResult>>(Error.Validation("Dat.InvalidFile", "Fixture DAT rejected"));
        });
        bool completionCleanupRan = false;
        var cleanup = Substitute.For<IJobCompletionCleanup<UploadJob>>();
        cleanup.RunAfterCompletionPersistedAsync(Arg.Any<UploadJob>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            await using var read = _database.CreateContext();
            (await read.Jobs.SingleAsync()).Phase.ShouldBe("CompletedWithErrors");
            (await read.JobItems.CountAsync()).ShouldBe(2);
            call.Arg<UploadJob>().PhaseEnum.ShouldBe(UploadPhase.CompletedWithErrors);
            await new MoveImportCompletionCleanup(new ImportSourceCleanup(_options, new PathValidator(_options), new JobItemRepository(read),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<ImportSourceCleanup>.Instance))
                .RunAfterCompletionPersistedAsync(call.Arg<UploadJob>(), call.Arg<CancellationToken>());
            completionCleanupRan = true;
        });
        int flushes = 0;
        async Task InterruptJournalAsync(bool committed)
        {
            flushes++;
            await using var observer = _database.CreateContext();
            (await observer.JobItems.CountAsync()).ShouldBe(committed ? 1 : 0);
            throw secondary;
        }
        Exception? executorFailure = null;
        Exception? runnerFailure = null;
        await using (var host = Host(configure: services =>
        {
            services.AddSingleton(cleanup);
            services.AddScoped<IUnitOfWork>(provider => new JournalBoundaryProbe(
                new EfUnitOfWork(provider.GetRequiredService<RomdDbContext>()),
                lostJournalAcknowledgement ? null : async () =>
                {
                    (await provider.GetRequiredService<RomdDbContext>().JobItems.CountAsync()).ShouldBe(1);
                    await InterruptJournalAsync(committed: false);
                },
                lostJournalAcknowledgement ? () => InterruptJournalAsync(committed: true) : null));
        }))
        {
            try
            {
                await Runner(host, onExecutorFailure: error => executorFailure = error)
                    .RunAsync(job.Id, "dual-failure", shutdown.Token);
            }
            catch (Exception error)
            {
                runnerFailure = error;
            }
        }
        executorFailure.ShouldBeSameAs(primary);
        runnerFailure.ShouldBeSameAs(primary);
        if (shutdownCancellation)
            runnerFailure.ShouldBeOfType<OperationCanceledException>().CancellationToken.ShouldBe(shutdown.Token);
        flushes.ShouldBe(1);
        cleanup.ReceivedCalls().ShouldBeEmpty();
        File.Exists(manifest).ShouldBeTrue();
        Directory.Exists(Workspace(job)).ShouldBeTrue();
        entries.ShouldAllBe(entry => File.Exists(Path.Combine(source, entry.SourceRelativePath)));
        Guid[] durableIds;
        await using (var read = _database.CreateContext())
        {
            var saved = await read.Jobs.SingleAsync();
            saved.Phase.ShouldBe("IngestingDats");
            saved.CompletedAt.ShouldBeNull();
            saved.DeferralCount.ShouldBe(3);
            saved.NextAttemptAtUtc.ShouldBeNull();
            durableIds = await read.JobItems.Select(item => item.Id).ToArrayAsync();
            durableIds.Length.ShouldBe(lostJournalAcknowledgement ? 1 : 0);
        }
        interrupt = false;
        _clock.Advance(new JobExecutionClaimOptions().LeaseDuration + TimeSpan.FromSeconds(1));
        await using (var retry = Host(configure: services => services.AddSingleton(cleanup)))
            await Runner(retry).RunAsync(job.Id, "dual-failure-recovery", default);
        await cleanup.Received(1).RunAfterCompletionPersistedAsync(Arg.Any<UploadJob>(), CancellationToken.None);
        completionCleanupRan.ShouldBeTrue();
        await using (var read = _database.CreateContext())
        {
            (await read.Jobs.SingleAsync()).Phase.ShouldBe("CompletedWithErrors");
            var ids = await read.JobItems.Select(item => item.Id).ToArrayAsync();
            ids.Length.ShouldBe(2);
            durableIds.ShouldAllBe(id => ids.Contains(id));
            (await read.JobItems.Select(item => item.FileName).Distinct().CountAsync()).ShouldBe(2);
        }
        // Failed DAT outcomes finish with errors: the real completion policy retains the
        // manifest and both originals. Only a persisted Completed phase admits move cleanup.
        File.Exists(manifest).ShouldBeTrue();
        (await File.ReadAllBytesAsync(manifest)).ShouldBe(manifestBytes);
        entries.ShouldAllBe(entry => File.Exists(Path.Combine(source, entry.SourceRelativePath)));
    }

    [Fact]
    public async Task JournalBatch_RealFenceLossThenFinalFlushAlsoLosesFence_PreservesOriginalOwnershipFailure()
    {
        var job = await SeedAsync();
        foreach (string path in Directory.GetFiles(Workspace(job)))
            File.Delete(path);
        foreach (string name in new[] { "a.dat", "z.dat" })
            await File.WriteAllBytesAsync(Path.Combine(Workspace(job), name), [1]);
        JobExecutionOwnershipLostException? primary = null;
        Guid successor = default;
        _datHandler.HandleAsync(Arg.Any<IngestDatCommand>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            if (call.Arg<IngestDatCommand>().OriginalFilename == "a.dat")
                return (ErrorOr<DatIngestResult>)Error.Validation("Dat.InvalidFile", "Fixture DAT rejected");
            _clock.Advance(new JobExecutionClaimOptions().LeaseDuration + TimeSpan.FromSeconds(1));
            await using (var competing = _database.CreateContext())
            {
                var claims = new ClaimedJobRepository<UploadJob, UploadJobRepository>(new(competing, _clock), competing, _clock, new());
                successor = (await claims.TryClaimExecutionAsync(job.Id, "successor")).ShouldNotBeNull().FenceToken;
            }
            await using var stale = _database.CreateContext();
            try
            {
                await using var transaction = await new EfUnitOfWork(stale).BeginTransactionAsync(CancellationToken.None);
            }
            catch (JobExecutionOwnershipLostException error)
            {
                primary = error;
                throw;
            }
            throw new ShouldAssertException("The real stale mutation must lose its execution fence.");
        });
        var final = new FinalJournalBeginProbe();
        Exception? observed = null;
        await using (var host = Host(configure: services => services.AddScoped<IUnitOfWork>(provider =>
            new ObservedJournalBegin(new EfUnitOfWork(provider.GetRequiredService<RomdDbContext>()), final))))
            // Lease time is controlled here; prevent an unrelated real-time heartbeat from
            // extending it between the explicit clock advance and the competing claim.
            await Runner(host, onExecutorFailure: error => observed = error,
                claimOptions: new() { HeartbeatInterval = TimeSpan.FromHours(1) }).RunAsync(job.Id, "stale-attempt", default);

        observed.ShouldBeSameAs(primary.ShouldNotBeNull());
        final.Attempts.ShouldBe(1);
        final.Failure.ShouldBeOfType<JobExecutionOwnershipLostException>().ShouldNotBeSameAs(primary);
        await using var read = _database.CreateContext();
        var saved = await read.Jobs.SingleAsync();
        saved.ExecutionFenceToken.ShouldBe(successor);
        saved.Phase.ShouldBe("IngestingDats");
        saved.CompletedAt.ShouldBeNull();
        (await read.JobItems.CountAsync()).ShouldBe(0);
        Directory.Exists(Workspace(job)).ShouldBeTrue();
    }

    private sealed class FailureCapturingUploadExecutor(IJobExecutor<UploadJob> inner, Action<Exception> failure) : IJobExecutor<UploadJob>
    {
        public async Task ExecuteAsync(UploadJob job, JobContext context)
        {
            try
            { await inner.ExecuteAsync(job, context); }
            catch (Exception error) { failure(error); throw; }
        }
    }

    private sealed class FinalJournalBeginProbe
    {
        public int Attempts { get; set; }
        public Exception? Failure { get; set; }
    }

    private sealed class ObservedJournalBegin(IUnitOfWork inner, FinalJournalBeginProbe probe) : IUnitOfWork
    {
        public Task FlushAsync(CancellationToken ct = default) => inner.FlushAsync(ct);
        public TransactionConflict ConflictOf(Exception error) => inner.ConflictOf(error);
        public async Task<ITransaction> BeginTransactionAsync(CancellationToken ct = default)
        {
            probe.Attempts++;
            try
            { return await inner.BeginTransactionAsync(ct); }
            catch (Exception error) { probe.Failure = error; throw; }
        }
    }
}
