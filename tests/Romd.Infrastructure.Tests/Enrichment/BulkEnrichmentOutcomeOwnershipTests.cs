using System.Runtime.CompilerServices;
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
using Romd.Admin.Application.Source.Platform;
using Romd.Admin.Application.Storage.Files;
using Romd.Admin.Application.Titles;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Admin.Application.Titles.ReadModels;
using Romd.Application.Common.Configuration;
using Romd.Application.Common.Security;
using Romd.Domain.Catalog;
using Romd.Domain.Jobs;
using Romd.Domain.Source.Platform;
using Romd.Infrastructure.Jobs;
using Romd.Infrastructure.Jobs.Executors;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Enrichment;

public sealed class BulkEnrichmentOutcomeOwnershipTests
{
    [Theory]
    [InlineData("enriched", "rollback")]
    [InlineData("enriched", "lost-ack")]
    [InlineData("not-found", "rollback")]
    [InlineData("not-found", "lost-ack")]
    [InlineData("provider-failed", "rollback")]
    [InlineData("provider-failed", "lost-ack")]
    [InlineData("not-found", "rollback-cleanup")]
    [InlineData("enriched", "lost-ack-cleanup")]
    public async Task FinalMutation_FailureRecordsOneProcessingFailureWithoutInventingAResult(string result, string fault)
    {
        await using var rig = await Rig.CreateAsync(result, fault);

        await rig.RunAsync();

        rig.Probe.BeforeFinalCommit.ShouldBe(1);
        bool lostAcknowledgement = fault.StartsWith("lost-ack", StringComparison.Ordinal);
        rig.Probe.AfterFinalCommit.ShouldBe(lostAcknowledgement ? 1 : 0);
        rig.Probe.RollbackCleanupFailures.ShouldBe(fault.EndsWith("-cleanup", StringComparison.Ordinal) ? 1 : 0);
        rig.Probe.TransactionCleanupFailures.ShouldBe(fault.EndsWith("-cleanup", StringComparison.Ordinal) ? 1 : 0);
        await using var read = rig.Database.CreateContext();
        var job = (await new BulkEnrichmentJobRepository(read, TimeProvider.System).GetByIdAsync(rig.JobId)).ShouldNotBeNull();
        job.PhaseEnum.ShouldBe(BulkEnrichmentPhase.CompletedWithErrors);
        job.TotalTitles.ShouldBe(1);
        job.ProcessedCount.ShouldBe(1);
        job.FailedCount.ShouldBe(1);
        job.EnrichedCount.ShouldBe(0);
        job.NotFoundCount.ShouldBe(0);
        var error = job.Errors.ShouldHaveSingleItem();
        error.Reason.ShouldBe(JobErrorReason.ProcessingError);
        error.Message.ShouldBe(rig.Probe.Failure.Message);
        // A lost acknowledgement may have published the title; it never proves an acknowledged result.
        (await read.Titles.SingleAsync()).EnrichmentStatus.ShouldBe(lostAcknowledgement ? Status(result) : nameof(EnrichmentStatus.Pending));
    }

    [Theory]
    [InlineData("enriched", "none")]
    [InlineData("not-found", "none")]
    [InlineData("provider-failed", "none")]
    [InlineData("enriched", "scope-cleanup")]
    [InlineData("not-found", "scope-cleanup")]
    [InlineData("provider-failed", "scope-cleanup")]
    [InlineData("enriched", "notifier")]
    [InlineData("enriched", "ack-cleanup")]
    [InlineData("not-found", "ack-cleanup")]
    [InlineData("provider-failed", "ack-cleanup")]
    public async Task FinalMutation_AcknowledgedResultIsCountedOnceDespiteLaterOptionalFaults(string result, string fault)
    {
        await using var rig = await Rig.CreateAsync(result, fault);

        await rig.RunAsync();

        rig.Probe.BeforeFinalCommit.ShouldBe(1);
        rig.Probe.AfterFinalCommit.ShouldBe(1);
        rig.Probe.ScopeCleanupFailures.ShouldBe(fault == "scope-cleanup" ? 1 : 0);
        rig.Probe.RollbackCleanupFailures.ShouldBe(0);
        rig.Probe.TransactionCleanupFailures.ShouldBe(fault == "ack-cleanup" ? 1 : 0);
        await using var read = rig.Database.CreateContext();
        var job = (await new BulkEnrichmentJobRepository(read, TimeProvider.System).GetByIdAsync(rig.JobId)).ShouldNotBeNull();
        job.ProcessedCount.ShouldBe(1);
        job.EnrichedCount.ShouldBe(result == "enriched" ? 1 : 0);
        job.NotFoundCount.ShouldBe(result == "not-found" ? 1 : 0);
        job.FailedCount.ShouldBe(result == "provider-failed" ? 1 : 0);
        job.PhaseEnum.ShouldBe(result == "provider-failed" ? BulkEnrichmentPhase.CompletedWithErrors : BulkEnrichmentPhase.Completed);
        if (result == "provider-failed")
            job.Errors.ShouldHaveSingleItem().Reason.ShouldBe(JobErrorReason.ProviderError);
        else
            job.Errors.ShouldBeEmpty();
        (await read.Titles.SingleAsync()).EnrichmentStatus.ShouldBe(Status(result));
    }

    [Theory]
    [InlineData("io")]
    [InlineData("cancel")]
    [InlineData("ownership")]
    public async Task OwnedMutation_PrimaryFailureSurvivesRealRollbackAndTransactionCleanupFaults(string failureKind)
    {
        await using var rig = await Rig.CreateAsync("not-found", "guard-primary");
        JobExecutionClaim<BulkEnrichmentJob> claim;
        await using (var claimDb = rig.Database.CreateContext())
        {
            var claims = new ClaimedJobRepository<BulkEnrichmentJob, BulkEnrichmentJobRepository>(
                new BulkEnrichmentJobRepository(claimDb, TimeProvider.System), claimDb, TimeProvider.System, new());
            claim = (await claims.TryClaimExecutionAsync(rig.JobId, "guard-cleanup-owner")).ShouldNotBeNull();
        }

        using var cancellation = new CancellationTokenSource();
        Exception failure = failureKind switch
        {
            "cancel" => new OperationCanceledException(cancellation.Token),
            "ownership" => new JobExecutionOwnershipLostException(),
            _ => new IOException("owned mutation failed before commit")
        };
        await using var db = rig.Database.CreateContext();
        var work = new ObservedWork(new EfUnitOfWork(db), rig.Probe);
        var guard = new JobExecutionMutationGuard(db, work, TimeProvider.System, new());
        using var fence = JobExecutionFenceScope.Enter(claim.Job.Id, claim.FenceToken, TimeProvider.System);
        Exception? observed = null;
        try
        {
            await guard.ExecuteAsync(claim.Job.Id, claim.FenceToken, async token =>
            {
                var title = await db.Titles.AsTracking().SingleAsync(token);
                title.Name = "Uncommitted";
                title.NormalizedName = "uncommitted";
                await work.FlushAsync(token);
                await using var before = rig.Database.CreateContext();
                (await before.Titles.SingleAsync()).Name.ShouldBe("Chrono");
                if (failureKind == "cancel")
                    cancellation.Cancel();
                throw failure;
            }, cancellation.Token);
        }
        catch (Exception error)
        {
            observed = error;
        }

        var thrown = observed.ShouldNotBeNull();
        thrown.ShouldBeSameAs(failure);
        if (thrown is OperationCanceledException cancelled)
            cancelled.CancellationToken.ShouldBe(cancellation.Token);
        rig.Probe.RollbackCleanupFailures.ShouldBe(1);
        rig.Probe.TransactionCleanupFailures.ShouldBe(1);
        db.ChangeTracker.Entries().ShouldBeEmpty();
        await using var read = rig.Database.CreateContext();
        (await read.Titles.SingleAsync()).Name.ShouldBe("Chrono");
        var row = await read.Jobs.SingleAsync();
        row.ExecutionFenceToken.ShouldBe(claim.FenceToken);
        row.CompletedAt.ShouldBeNull();
        var job = (await new BulkEnrichmentJobRepository(read, TimeProvider.System).GetByIdAsync(rig.JobId)).ShouldNotBeNull();
        job.ProcessedCount.ShouldBe(0);
        job.EnrichedCount.ShouldBe(0);
        job.NotFoundCount.ShouldBe(0);
        job.FailedCount.ShouldBe(0);
        job.Errors.ShouldBeEmpty();
    }

    private static string Status(string result) => result switch
    {
        "enriched" => nameof(EnrichmentStatus.Completed),
        "not-found" => nameof(EnrichmentStatus.NotFound),
        _ => nameof(EnrichmentStatus.Failed)
    };

    private sealed class Rig(PostgreSqlTestDatabase database, ServiceProvider services, RomdDbContext parent,
        Guid jobId, BoundaryProbe probe, BulkEnrichmentJobExecutor executor) : IAsyncDisposable
    {
        public PostgreSqlTestDatabase Database => database;
        public Guid JobId => jobId;
        public BoundaryProbe Probe => probe;

        public static async Task<Rig> CreateAsync(string result, string fault)
        {
            var database = PostgreSqlTestDatabase.Create();
            Guid jobId;
            await using (var seed = database.CreateContext())
            {
                seed.Platforms.Add(new PlatformEntity
                {
                    Id = 3,
                    Name = "SNES",
                    ShortName = "snes",
                    CanonicalKey = "snes",
                    Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd,
                    BuiltInVersion = 1,
                    BaseName = "SNES",
                    BaseCompactLabel = "SNES",
                    CreatedAt = DateTimeOffset.UtcNow
                });
                seed.Titles.Add(new TitleEntity
                {
                    Id = 7,
                    PlatformId = 3,
                    Name = "Chrono",
                    NormalizedName = "chrono",
                    EnrichmentStatus = nameof(EnrichmentStatus.Pending),
                    CreatedAt = DateTimeOffset.UtcNow
                });
                await seed.SaveChangesAsync();
                var job = BulkEnrichmentJob.Create(3, "snes", DateTimeOffset.UtcNow, EnrichmentScope.All);
                await using var acceptance = await new EfUnitOfWork(seed).BeginTransactionAsync();
                await new BulkEnrichmentJobRepository(seed, TimeProvider.System).AddStagedAsync(job);
                await acceptance.CommitAsync();
                jobId = job.Id;
            }
            var probe = new BoundaryProbe(database, result, fault);
            var notifier = Substitute.For<IJobNotifier>();
            if (fault == "notifier")
                notifier.NotifyTitleEnrichedAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromException(probe.Failure));
            var options = Options.Create(new EnrichmentOptions());
            var services = new ServiceCollection()
                .AddScoped(_ => database.CreateContext())
                .AddSingleton(TimeProvider.System)
                .AddSingleton(new JobExecutionClaimOptions())
                .AddScoped<BulkEnrichmentJobRepository>()
                .AddScoped<IJobRepository<BulkEnrichmentJob>, ClaimedJobRepository<BulkEnrichmentJob, BulkEnrichmentJobRepository>>()
                .AddScoped<IAuditContext, JobAuditContext>()
                .AddRomdJobLifecyclePolicies()
                .AddScoped<TitleRepository>()
                .AddScoped<IUnitOfWork>(provider => new ObservedWork(new EfUnitOfWork(provider.GetRequiredService<RomdDbContext>()), probe))
                .AddScoped<IJobExecutionMutationGuard, JobExecutionMutationGuard>()
                .AddScoped(_ => new ScopeCleanup(probe))
                .AddScoped(provider =>
                {
                    _ = provider.GetRequiredService<ScopeCleanup>();
                    var titles = provider.GetRequiredService<TitleRepository>();
                    return new BulkEnrichmentTitleProcessor(titles, titles, titles,
                        new PlatformRepository(provider.GetRequiredService<RomdDbContext>()), Substitute.For<IFileStorageService>(),
                        provider.GetRequiredService<IUnitOfWork>(), notifier, Substitute.For<ILibraryMaterializationService>(), options,
                        NullLogger<BulkEnrichmentTitleProcessor>.Instance, Substitute.For<IAutomaticArtworkService>(), TimeProvider.System);
                }).BuildServiceProvider();
            var parent = database.CreateContext();
            var selection = Substitute.For<ITitleEnrichmentStatusReader>();
            selection.GetCandidatesAsync(3, Arg.Any<EnrichmentScope>(), Arg.Any<CancellationToken>())
                .Returns(new[] { new EnrichmentCandidate(7, "Chrono", false) });
            var executor = new BulkEnrichmentJobExecutor(new TitleRepository(parent), selection, new PlatformRepository(parent),
                new SingleResult(result), Substitute.For<IStatsNotifier>(), options, NullLogger<BulkEnrichmentJobExecutor>.Instance,
                services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System);
            return new(database, services, parent, jobId, probe, executor);
        }

        public Task RunAsync()
        {
            var options = Substitute.For<IRomdOptions>();
            options.DataDirectory.Returns(Path.GetTempPath());
            var runner = new JobRunner<BulkEnrichmentJob>(services.GetRequiredService<IServiceScopeFactory>(),
                new ObserveExecutor(executor, Probe), Substitute.For<IJobNotifier>(), options,
                NullLogger<JobRunner<BulkEnrichmentJob>>.Instance);
            return runner.RunAsync(JobId, "outcome-owner", default);
        }

        public async ValueTask DisposeAsync()
        {
            await services.DisposeAsync();
            await parent.DisposeAsync();
            database.Dispose();
        }
    }

    private sealed class ObserveExecutor(BulkEnrichmentJobExecutor inner, BoundaryProbe probe) : IJobExecutor<BulkEnrichmentJob>
    {
        public Task ExecuteAsync(BulkEnrichmentJob job, JobContext context)
        {
            probe.Job = job;
            return inner.ExecuteAsync(job, context);
        }
    }

    private sealed class BoundaryProbe(PostgreSqlTestDatabase database, string result, string fault)
    {
        public BulkEnrichmentJob? Job { get; set; }
        public IOException Failure { get; } = new("final title boundary failed");
        public IOException CleanupFailure { get; } = new("secondary transaction cleanup failed");
        public int BeforeFinalCommit { get; private set; }
        public int AfterFinalCommit { get; private set; }
        public int ScopeCleanupFailures { get; private set; }
        public int RollbackCleanupFailures { get; private set; }
        public int TransactionCleanupFailures { get; private set; }
        public int FinalCommit => result == "enriched" ? 2 : 1;
        public async Task ObserveAsync(bool committed)
        {
            var job = Job.ShouldNotBeNull();
            job.ProcessedCount.ShouldBe(0);
            job.EnrichedCount.ShouldBe(0);
            job.NotFoundCount.ShouldBe(0);
            job.FailedCount.ShouldBe(0);
            await using var read = database.CreateContext();
            (await read.Titles.SingleAsync()).EnrichmentStatus.ShouldBe(committed ? Status(result) : nameof(EnrichmentStatus.Pending));
            if (committed)
                AfterFinalCommit++;
            else
                BeforeFinalCommit++;
            if (committed ? fault is "lost-ack" or "lost-ack-cleanup" : fault is "rollback" or "rollback-cleanup")
                throw Failure;
        }
        public void AfterRollback(bool final)
        {
            if (fault == "guard-primary" || (final && fault is "rollback-cleanup" or "lost-ack-cleanup"))
            {
                RollbackCleanupFailures++;
                throw CleanupFailure;
            }
        }
        public void AfterTransactionDispose(bool final)
        {
            if (fault == "guard-primary" || (final && fault is "rollback-cleanup" or "lost-ack-cleanup" or "ack-cleanup"))
            {
                TransactionCleanupFailures++;
                throw CleanupFailure;
            }
        }
        public void Cleanup()
        {
            if (fault == "scope-cleanup" && AfterFinalCommit == 1)
            {
                ScopeCleanupFailures++;
                throw Failure;
            }
        }
    }

    private sealed class ObservedWork(IUnitOfWork inner, BoundaryProbe probe) : IUnitOfWork
    {
        private readonly IUnitOfWork _inner = inner;
        private readonly BoundaryProbe _probe = probe;
        private int _commits;
        public Task FlushAsync(CancellationToken ct = default) => _inner.FlushAsync(ct);
        public TransactionConflict ConflictOf(Exception error) => _inner.ConflictOf(error);
        public async Task<ITransaction> BeginTransactionAsync(CancellationToken ct = default) =>
            new ObservedTransaction(await _inner.BeginTransactionAsync(ct), this);
        private sealed class ObservedTransaction(ITransaction inner, ObservedWork owner) : ITransaction
        {
            private bool _final;
            public async Task CommitAsync(CancellationToken ct = default)
            {
                // Enriched results first persist provider identities while their title remains pending.
                _final = ++owner._commits == owner._probe.FinalCommit;
                await owner._inner.FlushAsync(ct);
                if (_final)
                    await owner._probe.ObserveAsync(false);
                await inner.CommitAsync(ct);
                if (_final)
                    await owner._probe.ObserveAsync(true);
            }
            public async Task RollbackAsync(CancellationToken ct = default)
            {
                await inner.RollbackAsync(ct);
                owner._probe.AfterRollback(_final);
            }
            public async ValueTask DisposeAsync()
            {
                await inner.DisposeAsync();
                owner._probe.AfterTransactionDispose(_final);
            }
        }
    }

    private sealed class ScopeCleanup(BoundaryProbe probe) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            probe.Cleanup();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class SingleResult(string result) : IEnrichmentOrchestrator
    {
        public Task<OrchestrationResult> EnrichTitleAsync(Title title, Platform platform, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public async IAsyncEnumerable<(int TitleId, OrchestrationResult Result)> EnrichStreamAsync(
            IAsyncEnumerable<(Title Title, Platform Platform)> titles, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var (title, _) in titles.WithCancellation(ct))
                yield return (title.Id, result == "enriched" ? OrchestrationResult.Found(1) :
                    result == "not-found" ? OrchestrationResult.NotFound : OrchestrationResult.Failed);
        }
    }
}
