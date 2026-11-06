using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Source.Rom.Reconciliation;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Configuration;
using Romd.Application.Common.Content;
using Romd.Application.Common.Security;
using Romd.Domain.Jobs;
using Romd.Domain.Source.Rom;
using Romd.Infrastructure.Jobs;
using Romd.Infrastructure.Storage;
using Romd.Infrastructure.Tests.Helpers;
using Romd.Infrastructure.Tests.Persistence;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.Persistence.Repositories.Reconciliation;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Jobs;

/// <summary>Real claims and journal stores qualify terminal ownership without invoking native media work.</summary>
public sealed class JobRunnerJournalCapabilityTests
{
    [Fact]
    public async Task Run_CommittedJournal_CompletesWithoutDirectCheckpointAndTerminalRedeliveryDoesNothing()
    {
        await using var rig = await Rig.CreateAsync("complete");

        await rig.RunAsync();
        await rig.AssertCompletedAsync(MediaReconciliationOutcomeKind.Completed, "fixture-completed");
        await rig.RunAsync();

        rig.Probe.Executions.ShouldBe(1);
        rig.Probe.Checkpoints.ShouldBe(0);
        await rig.AssertCompletedAsync(MediaReconciliationOutcomeKind.Completed, "fixture-completed");
    }

    [Theory]
    [InlineData("missing-journal")]
    [InlineData("transient")]
    [InlineData("shutdown")]
    public async Task Run_WithoutCommittedOutcome_PreservesActiveClaimAndRedeliveryUsesJournal(string mode)
    {
        await using var rig = await Rig.CreateAsync(mode);

        var error = await Record.ExceptionAsync(() => rig.RunAsync());

        if (mode == "missing-journal")
            error.ShouldBeOfType<InvalidOperationException>().Message.ShouldContain("durable lifecycle outcome");
        else
            error.ShouldBeSameAs(rig.Probe.Failure);
        if (mode == "shutdown")
            error.ShouldBeAssignableTo<OperationCanceledException>().ShouldNotBeNull().CancellationToken.ShouldBe(rig.Probe.ExecutionToken);
        await using (var read = rig.Database.CreateContext())
        {
            var row = await read.Jobs.SingleAsync();
            row.Phase.ShouldBe("Planning");
            row.CompletedAt.ShouldBeNull();
            row.ExecutionFenceToken.ShouldBe(rig.Probe.Fences.ShouldHaveSingleItem());
            row.ExecutionLeaseExpiresAtUtc.ShouldNotBeNull();
            var persisted = (await new MediaReconciliationJobRepository(read, rig.Clock).GetByIdAsync(row.Id)).ShouldNotBeNull();
            persisted.Errors.Count.ShouldBe(mode == "shutdown" ? 0 : 1);
            (await read.Set<MediaReconciliationOutcomeEntity>().CountAsync()).ShouldBe(0);
        }
        rig.Probe.Checkpoints.ShouldBe(mode == "shutdown" ? 0 : 1);
        rig.Clock.Advance(TimeSpan.FromMinutes(6));

        await rig.RunAsync();

        rig.Probe.Executions.ShouldBe(2);
        rig.Probe.Fences[1].ShouldNotBe(rig.Probe.Fences[0]);
        rig.Probe.Checkpoints.ShouldBe(mode == "shutdown" ? 0 : 1);
        await rig.AssertCompletedAsync(MediaReconciliationOutcomeKind.Completed, "fixture-completed");
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("permanent")]
    public async Task Run_CancellationOrPermanentFailure_SettlesThroughTheRealFamilyJournal(string mode)
    {
        await using var rig = await Rig.CreateAsync(mode);
        if (mode == "cancel")
            await rig.RequestCancellationAsync();

        await rig.RunAsync();

        await rig.AssertCompletedAsync(mode == "cancel" ? MediaReconciliationOutcomeKind.Cancelled : MediaReconciliationOutcomeKind.Failed,
            mode == "cancel" ? "cancelled" : "retry-budget-exhausted");
        rig.Probe.Checkpoints.ShouldBe(0);
        rig.Probe.StopChecks.ShouldBeGreaterThan(0);
        await using var read = rig.Database.CreateContext();
        var row = await read.Jobs.SingleAsync();
        var persisted = (await new MediaReconciliationJobRepository(read, rig.Clock).GetByIdAsync(row.Id)).ShouldNotBeNull();
        persisted.Errors.ShouldBeEmpty("the journal owns the terminal reason");
    }

    [Fact]
    public async Task Run_UnstoppedCustody_DefersWithPinsAndSettlesOnlyAfterStoppedRedelivery()
    {
        await using var rig = await Rig.CreateAsync("custody");

        await rig.RunAsync();

        await using (var read = rig.Database.CreateContext())
        {
            var row = await read.Jobs.SingleAsync();
            row.CompletedAt.ShouldBeNull();
            row.Phase.ShouldBe("Planning");
            var persisted = (await new MediaReconciliationJobRepository(read, rig.Clock).GetByIdAsync(row.Id)).ShouldNotBeNull();
            persisted.Errors.ShouldBeEmpty();
            row.ExecutionFenceToken.ShouldBeNull();
            row.DeferralCount.ShouldBe(1);
            var due = row.NextAttemptAtUtc.ShouldNotBeNull();
            rig.Clock.SetUtcNow(due);
            (await read.Set<MediaReconciliationPinEntity>().CountAsync()).ShouldBe(1);
            var attempt = await read.Database.SqlQuery<AttemptSnapshot>($"""
                SELECT "Outcome", "ExecutionFenceToken" FROM romd."MediaReconciliationAttempts" WHERE "JobId"={row.Id}
                """).SingleAsync();
            attempt.Outcome.ShouldBeNull();
            attempt.ExecutionFenceToken.ShouldBe(rig.Probe.Fences[0]);
            (await read.Set<MediaReconciliationOutcomeEntity>().CountAsync()).ShouldBe(0);
        }
        rig.Probe.Checkpoints.ShouldBe(0);
        rig.Probe.Stopped = true;

        await rig.RunAsync();

        await rig.AssertCompletedAsync(MediaReconciliationOutcomeKind.Completed, "fixture-completed");
        rig.Probe.Executions.ShouldBe(2);
        rig.Probe.Fences[1].ShouldNotBe(rig.Probe.Fences[0]);
        rig.Probe.Checkpoints.ShouldBe(0);
        await using var settled = rig.Database.CreateContext();
        (await settled.Set<MediaReconciliationPinEntity>().CountAsync()).ShouldBe(0);
        (await settled.Database.SqlQuery<AttemptSnapshot>($"""
            SELECT "Outcome", "ExecutionFenceToken" FROM romd."MediaReconciliationAttempts"
            """).SingleAsync()).Outcome.ShouldBe("Superseded");
        (await settled.Database.SqlQueryRaw<int>("SELECT COUNT(*)::int AS \"Value\" FROM romd.\"MediaReconciliationInputs\"").SingleAsync()).ShouldBe(1);
    }

    private sealed class Rig(PostgreSqlTestDatabase database, ServiceProvider provider, ManualTimeProvider clock,
        Guid jobId, Probe probe, ScriptedExecutor executor) : IAsyncDisposable
    {
        public PostgreSqlTestDatabase Database => database;
        public ManualTimeProvider Clock => clock;
        public Probe Probe => probe;
        public static async Task<Rig> CreateAsync(string mode)
        {
            var database = PostgreSqlTestDatabase.Create();
            var clock = new ManualTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
            Guid jobId;
            MediaFactGraphFixture graph;
            await using (var seed = database.CreateContext())
            {
                graph = await MediaFactGraphFixture.CreateAsync(seed);
                await using var acceptance = await new EfUnitOfWork(seed).BeginTransactionAsync();
                var accepted = await new MediaReconciliationRequests(seed, clock).StageSubjectAsync(new(
                    Guid.NewGuid(), new MediaReconciliationSubject.Representation(graph.Cd.Id), false,
                    MediaReconciliationCause.Readiness, null, null));
                accepted.IsError.ShouldBeFalse();
                jobId = accepted.Value.Job.Id;
                await acceptance.CommitAsync();
            }
            var probe = new Probe();
            var custody = Substitute.For<IMediaRunCustody>();
            custody.EnsureJobStoppedAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(_ =>
            {
                probe.StopChecks++;
                return Task.FromResult<ErrorOr<Success>>(probe.Stopped ? Result.Success : MediaReconciliationErrors.Custody);
            });
            var media = RomMediaPolicyComposition.Create();
            var decision = new MediaReconciliationDecision(media.Kinds, media.Reconciliation,
                Substitute.For<IMediaHostCapabilities>(), Substitute.For<IMediaReconciliationMediaProfile>());
            var services = new ServiceCollection()
                .AddScoped(_ => database.CreateContext())
                .AddSingleton<TimeProvider>(clock)
                .AddSingleton(new JobExecutionClaimOptions())
                .AddScoped<IAuditContext, JobAuditContext>()
                .AddScoped<MediaReconciliationJobRepository>()
                .AddScoped<IJobRepository<MediaReconciliationJob>>(p => new ObservedClaims(
                    new ClaimedJobRepository<MediaReconciliationJob, MediaReconciliationJobRepository>(
                        p.GetRequiredService<MediaReconciliationJobRepository>(), p.GetRequiredService<RomdDbContext>(), clock, new()), probe))
                .AddScoped<IMediaReconciliationRequests>(p => new MediaReconciliationRequests(p.GetRequiredService<RomdDbContext>(), clock))
                .AddScoped<IMediaReconciliationExecution>(p => new MediaReconciliationExecution(p.GetRequiredService<RomdDbContext>(),
                    database.CreateOptions(), new EfUnitOfWork(p.GetRequiredService<RomdDbContext>()), custody, clock, media.Kinds, decision))
                .AddScoped<IMediaReconciliationSelections>(p => new MediaReconciliationSelections(p.GetRequiredService<RomdDbContext>(),
                    database.CreateOptions(), new EfUnitOfWork(p.GetRequiredService<RomdDbContext>()), p.GetRequiredService<IMediaReconciliationRequests>(),
                    Substitute.For<IMediaReconciliationSnapshotReader>(), media.Kinds, custody, clock))
                .AddRomdJobLifecyclePolicies();
            var provider = services.BuildServiceProvider();
            return new(database, provider, clock, jobId, probe, new(mode, graph, provider.GetRequiredService<IServiceScopeFactory>(), probe));
        }

        public async Task RunAsync()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            probe.Shutdown = deadline;
            probe.ExecutionToken = deadline.Token;
            var options = Substitute.For<IRomdOptions>();
            options.DataDirectory.Returns(Path.GetTempPath());
            await new JobRunner<MediaReconciliationJob>(provider.GetRequiredService<IServiceScopeFactory>(), executor,
                Substitute.For<IJobNotifier>(), options, clock, new JobExecutionClaimOptions { HeartbeatInterval = TimeSpan.FromMilliseconds(20) },
                NullLogger<JobRunner<MediaReconciliationJob>>.Instance).RunAsync(jobId, $"delivery-{probe.Executions + 1}", deadline.Token);
        }

        public async Task RequestCancellationAsync()
        {
            await using var scope = provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
            await using var transaction = await new EfUnitOfWork(db).BeginTransactionAsync();
            (await scope.ServiceProvider.GetRequiredService<IMediaReconciliationRequests>().StageCancellationAsync(jobId)).IsError.ShouldBeFalse();
            await transaction.CommitAsync();
        }

        public async Task AssertCompletedAsync(MediaReconciliationOutcomeKind kind, string code)
        {
            await using var read = database.CreateContext();
            var journal = (await read.Set<MediaReconciliationOutcomeEntity>().ToListAsync()).ShouldHaveSingleItem();
            journal.Kind.ShouldBe(kind.ToString());
            journal.Code.ShouldBe(code);
            journal.AcknowledgedVersion.ShouldBe(1);
            var row = await read.Jobs.SingleAsync();
            row.Phase.ShouldBe(kind.ToString());
            row.CompletedAt.ShouldBe(DateTimeOffset.FromUnixTimeMilliseconds(new DateTimeOffset(journal.CompletedAtUtcTicks, TimeSpan.Zero).ToUnixTimeMilliseconds()));
            row.ExecutionFenceToken.ShouldBeNull();
            row.ExecutionLeaseExpiresAtUtc.ShouldBeNull();
            var loaded = (await new MediaReconciliationJobRepository(read, clock).GetByIdAsync(jobId)).ShouldNotBeNull();
            loaded.Outcome.ShouldBe(kind);
            loaded.OutcomeCode.ShouldBe(code);
            loaded.CompletedAt.ShouldBe(new DateTimeOffset(journal.CompletedAtUtcTicks, TimeSpan.Zero));
        }

        public async ValueTask DisposeAsync()
        {
            await provider.DisposeAsync();
            await database.DisposeAsync();
        }
    }

    private sealed class Probe
    {
        public int Executions { get; set; }
        public int Checkpoints { get; set; }
        public int StopChecks { get; set; }
        public bool Stopped { get; set; } = true;
        public List<Guid> Fences { get; } = [];
        public Exception? Failure { get; set; }
        public CancellationTokenSource? Shutdown { get; set; }
        public CancellationToken ExecutionToken { get; set; }
    }

    private sealed class AttemptSnapshot
    {
        public string? Outcome { get; set; }
        public Guid ExecutionFenceToken { get; set; }
    }

    private sealed class ScriptedExecutor(string mode, MediaFactGraphFixture graph, IServiceScopeFactory scopes, Probe probe)
        : IResumableJobExecutor<MediaReconciliationJob>
    {
        public async Task ExecuteAsync(MediaReconciliationJob job, JobContext context)
        {
            int attempt = ++probe.Executions;
            var fence = JobExecutionFenceScope.Current ?? throw new InvalidOperationException("The real runner must own its execution fence.");
            probe.Fences.Add(fence.Token);
            await using var scope = scopes.CreateAsyncScope();
            var execution = scope.ServiceProvider.GetRequiredService<IMediaReconciliationExecution>();
            if (attempt == 1)
            {
                if (mode == "missing-journal")
                    return;
                if (mode == "transient")
                {
                    var failure = new IOException("An unfinished media operation failed.");
                    probe.Failure = failure;
                    throw failure;
                }
                if (mode == "shutdown")
                {
                    probe.Shutdown.ShouldNotBeNull().Cancel();
                    var failure = new OperationCanceledException("Host shutdown.", probe.ExecutionToken);
                    probe.Failure = failure;
                    throw failure;
                }
                if (mode == "cancel")
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken);
                    throw new InvalidOperationException("Cancellation did not interrupt media work.");
                }
                if (mode == "custody")
                {
                    var capture = new MediaReconciliationCapture(job.Id, 1,
                        new([new MediaReconciliationInput.Artifact(graph.Cd, null, null)], null, [], null),
                        MediaReconciliationOperation.Verify, null, MediaFactGraphFixture.Hash('a'), MediaFactGraphFixture.Hash('b'),
                        new RomDecoderIdentity.Native(new("linux-arm64", MediaFactGraphFixture.Short('e'), 3)), null, null, null);
                    (await execution.CaptureAsync(capture)).IsError.ShouldBeFalse();
                    probe.Stopped = false;
                }
                if (mode is "custody" or "permanent")
                    throw new JobPermanentFailureException("The operation cannot continue.");
            }
            if (mode == "custody")
                (await execution.SupersedeAttemptAsync(job.Id, "stopped-redelivery", true)).IsError.ShouldBeFalse();
            var demand = (await execution.ReadDemandAsync(job.Id)).ShouldNotBeNull();
            (await execution.FinishAsync(job.Id, demand.Version, MediaReconciliationOutcomeKind.Completed,
                "fixture-completed", true)).IsError.ShouldBeFalse();
        }
    }

    private sealed class ObservedClaims(IJobExecutionClaimRepository<MediaReconciliationJob> inner, Probe probe)
        : IJobExecutionClaimRepository<MediaReconciliationJob>
    {
        public Task<MediaReconciliationJob?> GetByIdAsync(Guid id, CancellationToken ct = default) => inner.GetByIdAsync(id, ct);
        public Task<IReadOnlyList<MediaReconciliationJob>> GetActiveAsync(CancellationToken ct = default) => inner.GetActiveAsync(ct);
        public Task UpdateStagedAsync(MediaReconciliationJob job, CancellationToken ct = default) => inner.UpdateStagedAsync(job, ct);
        public Task<JobExecutionClaim<MediaReconciliationJob>?> TryClaimExecutionAsync(Guid id, string delivery, CancellationToken ct = default) => inner.TryClaimExecutionAsync(id, delivery, ct);
        public Task<bool> RenewExecutionLeaseAsync(Guid id, Guid token, CancellationToken ct = default) => inner.RenewExecutionLeaseAsync(id, token, ct);
        public Task<bool> HasExecutionOwnershipAsync(Guid id, Guid token, CancellationToken ct = default) => inner.HasExecutionOwnershipAsync(id, token, ct);
        public Task<bool> TryDeferClaimedAsync(MediaReconciliationJob job, Guid token, DateTimeOffset until, CancellationToken ct = default) => inner.TryDeferClaimedAsync(job, token, until, ct);
        public Task<bool> TryUpdateClaimedAsync(MediaReconciliationJob job, Guid token, CancellationToken ct = default)
        {
            probe.Checkpoints++;
            return inner.TryUpdateClaimedAsync(job, token, ct);
        }
    }
}
