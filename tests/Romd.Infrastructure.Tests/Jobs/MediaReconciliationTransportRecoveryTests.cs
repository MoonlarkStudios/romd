using System.Text.Json;
using Hangfire;
using Hangfire.States;
using Hangfire.Storage;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Storage.Files;
using Romd.Domain.Hashing;
using Romd.Domain.Jobs;
using Romd.Domain.Source.Rom;
using Romd.Infrastructure.Jobs;
using Romd.Infrastructure.Tests.Helpers;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Identity;
using Romd.Persistence.Repositories;
using Romd.Persistence.Repositories.Reconciliation;
using Romd.Admin.Application.Source.Rom.Reconciliation;
using Romd.Application.Common.Content;
using Romd.Infrastructure.Storage;
using Romd.Infrastructure.Tests.Persistence;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Jobs;

/// <summary>Real database/filter tests; synthetic media identities qualify transport custody, not decoding.</summary>
public sealed class MediaReconciliationTransportRecoveryTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnStateApplied_ExpiredConvergenceTransport_RearmsDispatchAndPreservesPinsOutcomesAndProgress(bool bulk)
    {
        await VerifyTransportAsync(bulk, activeLease: false);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnStateApplied_TransportExhaustedWithActiveLease_DoesNotStealExecutionOrMutateDependencies(bool bulk)
    {
        await VerifyTransportAsync(bulk, activeLease: true);
    }

    private static async Task VerifyTransportAsync(bool bulk, bool activeLease)
    {
        await using var database = PostgreSqlTestDatabase.Create();
        var clock = new ManualTimeProvider(Now);
        var seeded = await SeedAsync(database, clock, bulk, activeLease);
        string beforeJob;
        string beforeDispatch;
        string beforeSources;
        string beforeOutcomes;
        string beforeItems;
        await using (var db = database.CreateContext())
        {
            beforeJob = JobFacts(await db.Jobs.SingleAsync(row => row.Id == seeded.JobId), activeLease);
            beforeDispatch = DispatchFacts(await db.JobDispatches.SingleAsync(row => row.JobId == seeded.JobId), activeLease);
            beforeSources = JsonSerializer.Serialize(await db.Set<MediaReconciliationPinEntity>().OrderBy(row => row.JobId).ThenBy(row => row.Ordinal).ToArrayAsync());
            beforeOutcomes = JsonSerializer.Serialize(await db.Set<MediaReconciliationOutcomeEntity>().OrderBy(row => row.JobId).ToArrayAsync());
            beforeItems = await ProtocolFactsAsync(db);
            if (activeLease)
                (await TryClaimAsync(db, clock, seeded.JobId, "late-delivery")).ShouldBeNull();
        }
        var services = new ServiceCollection();
        services.AddDbContext<RomdDbContext>(options => options.UseNpgsql(database.ConnectionString));
        await using var provider = services.BuildServiceProvider();
        var filter = new HangfireJobStateSyncFilter(provider.GetRequiredService<IServiceScopeFactory>(), clock,
            NullLogger<HangfireJobStateSyncFilter>.Instance);

        filter.OnStateApplied(CreateFailedContext(seeded.JobId), Substitute.For<IWriteOnlyTransaction>());

        await using var read = database.CreateContext();
        var job = await read.Jobs.SingleAsync(row => row.Id == seeded.JobId);
        var dispatch = await read.JobDispatches.SingleAsync(row => row.JobId == seeded.JobId);
        JobFacts(job, activeLease).ShouldBe(beforeJob);
        DispatchFacts(dispatch, activeLease).ShouldBe(beforeDispatch);
        JsonSerializer.Serialize(await read.Set<MediaReconciliationPinEntity>().OrderBy(row => row.JobId).ThenBy(row => row.Ordinal).ToArrayAsync()).ShouldBe(beforeSources);
        JsonSerializer.Serialize(await read.Set<MediaReconciliationOutcomeEntity>().OrderBy(row => row.JobId).ToArrayAsync()).ShouldBe(beforeOutcomes);
        (await ProtocolFactsAsync(read)).ShouldBe(beforeItems);
        (await read.AdminRealtimeOutboxEvents.CountAsync()).ShouldBe(0);
        job.CompletedAt.ShouldBeNull();
        if (activeLease)
        {
            job.ExecutionFenceToken.ShouldBe(seeded.Fence);
            job.HangfireJobId.ShouldBe("delivery");
            dispatch.DeliveredAtUtc.ShouldNotBeNull();
            (await TryClaimAsync(read, clock, seeded.JobId, "late-delivery")).ShouldBeNull();
        }
        else
        {
            var due = Now.AddSeconds(30);
            job.ExecutionFenceToken.ShouldBeNull();
            job.ExecutionLeaseExpiresAtUtc.ShouldBeNull();
            job.HangfireJobId.ShouldBeNull();
            job.NextAttemptAtUtc.ShouldBe(due);
            job.LastDeferralError.ShouldBe("Media transport failed; awaiting durable reconciliation.");
            dispatch.DeliveredAtUtc.ShouldBeNull();
            dispatch.ClaimToken.ShouldBeNull();
            dispatch.LeaseExpiresAtUtc.ShouldBeNull();
            dispatch.HangfireJobId.ShouldBeNull();
            dispatch.AvailableAtUtc.ShouldBe(due);
            (await TryClaimAsync(read, clock, seeded.JobId, "next-delivery")).ShouldBeNull();
            clock.Advance(TimeSpan.FromSeconds(30));
            var recovered = await TryClaimAsync(read, clock, seeded.JobId, "next-delivery");
            recovered.ShouldNotBeNull();
            recovered.ShouldNotBe(seeded.Fence);
            (await read.Set<MediaReconciliationPinEntity>().CountAsync()).ShouldBe(1);
            (await read.Set<MediaReconciliationOutcomeEntity>().CountAsync()).ShouldBe(1);
        }
    }

    private static Task<string> ProtocolFactsAsync(RomdDbContext db) => db.Database.SqlQueryRaw<string>("""
        SELECT jsonb_build_object(
            'definitions',(SELECT jsonb_agg(to_jsonb(d) ORDER BY d."JobId") FROM romd."MediaReconciliationJobs" d),
            'requests',(SELECT jsonb_agg(to_jsonb(r) ORDER BY r."Id") FROM romd."MediaReconciliationRequests" r),
            'attempts',(SELECT jsonb_agg(to_jsonb(a) ORDER BY a."JobId",a."Number") FROM romd."MediaReconciliationAttempts" a),
            'inputs',(SELECT jsonb_agg(to_jsonb(i) ORDER BY i."JobId",i."AttemptNumber",i."Ordinal") FROM romd."MediaReconciliationInputs" i),
            'selectionItems',(SELECT jsonb_agg(to_jsonb(i) ORDER BY i."SelectionJobId",i."Ordinal") FROM romd."MediaReconciliationSelectionItems" i)
        )::text AS "Value"
        """).SingleAsync();

    private static string JobFacts(JobEntity job, bool includeTransport)
    {
        var facts = JsonSerializer.SerializeToNode(job, job.GetType())!.AsObject();
        if (!includeTransport)
        {
            foreach (string name in new[] { nameof(JobEntity.ExecutionFenceToken), nameof(JobEntity.ExecutionLeaseExpiresAtUtc),
                nameof(JobEntity.HangfireJobId), nameof(JobEntity.NextAttemptAtUtc), nameof(JobEntity.LastDeferralError) })
                facts.Remove(name);
        }
        return facts.ToJsonString();
    }

    private static string DispatchFacts(JobDispatchEntity dispatch, bool includeTransport)
    {
        var facts = JsonSerializer.SerializeToNode(dispatch)!.AsObject();
        if (!includeTransport)
        {
            foreach (string name in new[] { nameof(JobDispatchEntity.DeliveredAtUtc), nameof(JobDispatchEntity.ClaimToken),
                nameof(JobDispatchEntity.LeaseExpiresAtUtc), nameof(JobDispatchEntity.HangfireJobId), nameof(JobDispatchEntity.AvailableAtUtc) })
                facts.Remove(name);
        }
        return facts.ToJsonString();
    }

    private static async Task<Seeded> SeedAsync(PostgreSqlTestDatabase database, ManualTimeProvider clock, bool selection, bool activeLease)
    {
        await using var db = database.CreateContext();
        var graph = await MediaFactGraphFixture.CreateAsync(db);
        var requests = new MediaReconciliationRequests(db, clock);
        var media = RomMediaPolicyComposition.Create();
        var custody = Substitute.For<IMediaRunCustody>();
        custody.EnsureJobStoppedAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(Result.Success);
        var selections = new MediaReconciliationSelections(db, database.CreateOptions(), new EfUnitOfWork(db), requests,
            Substitute.For<IMediaReconciliationSnapshotReader>(), media.Kinds, custody, clock);
        Guid? parent = null;
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            if (selection)
            {
                var accepted = await requests.StageSelectionAsync(Guid.NewGuid(), new MediaReconciliationFilter.RetainedRepresentations(), 2, false, MediaReconciliationCause.Readiness, null);
                accepted.IsError.ShouldBeFalse();
                parent = accepted.Value.Job.Id;
            }
            else
            {
                foreach (int id in new[] { graph.Source.Id, graph.Cd.Id }.Order())
                    (await requests.StageSubjectAsync(new(Guid.NewGuid(), new MediaReconciliationSubject.Representation(id), false, MediaReconciliationCause.Readiness, null, null))).IsError.ShouldBeFalse();
            }
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        JobExecutionClaim<MediaReconciliationJob>? parentClaim = null;
        if (parent is { } parentId)
        {
            parentClaim = (await Claims(db, clock).TryClaimExecutionAsync(parentId, "parent-fixture")).ShouldNotBeNull();
            using var parentFence = JobExecutionFenceScope.Enter(parentId, parentClaim.FenceToken, clock);
            var advanced = await selections.AdvanceAsync(parentId, 10);
            advanced.IsError.ShouldBeFalse();
            advanced.Value.Progress.Selected.ShouldBe(2);
        }
        Guid Child(int id) => db.Set<MediaReconciliationDefinitionEntity>().Single(row => row.RequestMode == "Subject" && row.SubjectId == id && row.SubjectKind == "Representation").JobId;
        Guid activeId = Child(graph.Cd.Id), finishedId = Child(graph.Source.Id);
        var activeClaim = (await Claims(db, clock).TryClaimExecutionAsync(activeId, "active-fixture")).ShouldNotBeNull();
        var decision = new MediaReconciliationDecision(media.Kinds, media.Reconciliation, Substitute.For<IMediaHostCapabilities>(), Substitute.For<IMediaReconciliationMediaProfile>());
        var execution = new MediaReconciliationExecution(db, database.CreateOptions(), new EfUnitOfWork(db), custody, clock, media.Kinds, decision);
        using (JobExecutionFenceScope.Enter(activeId, activeClaim.FenceToken, clock))
        {
            var capture = new MediaReconciliationCapture(activeId, 1, new([new MediaReconciliationInput.Artifact(graph.Cd, null, null)], null, [], null),
                MediaReconciliationOperation.Verify, null, MediaFactGraphFixture.Hash('a'), MediaFactGraphFixture.Hash('b'),
                new RomDecoderIdentity.Native(new("linux-arm64", MediaFactGraphFixture.Short('e'), 3)), null, null, null);
            (await execution.CaptureAsync(capture)).IsError.ShouldBeFalse();
        }
        var completedClaim = (await Claims(db, clock).TryClaimExecutionAsync(finishedId, "completed-fixture")).ShouldNotBeNull();
        using (JobExecutionFenceScope.Enter(finishedId, completedClaim.FenceToken, clock))
            (await execution.FinishAsync(finishedId, 1, MediaReconciliationOutcomeKind.Failed, "fixture-failure", processTreeStopped: true)).IsError.ShouldBeFalse();
        if (parentClaim is not null)
        {
            using var parentFence = JobExecutionFenceScope.Enter(parentClaim.Job.Id, parentClaim.FenceToken, clock);
            var refreshed = await selections.RefreshAsync(parentClaim.Job.Id);
            refreshed.IsError.ShouldBeFalse();
            refreshed.Value.Progress.Completed.ShouldBe(1);
            refreshed.Value.Progress.Failed.ShouldBe(1);
        }
        Guid targetId = parent ?? activeId;
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            (await requests.StageCancellationAsync(targetId)).IsError.ShouldBeFalse();
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        Guid fence = parentClaim?.FenceToken ?? activeClaim.FenceToken;
        await db.Jobs.Where(row => row.Id == targetId).ExecuteUpdateAsync(set =>
            set.SetProperty(row => row.HangfireJobId, "delivery").SetProperty(row => row.ExecutionFenceToken, fence)
                .SetProperty(row => row.ExecutionLeaseExpiresAtUtc, activeLease ? Now.AddMinutes(5) : Now.AddSeconds(-1))
                .SetProperty(row => row.CurrentItem, "Preserve this durable work item").SetProperty(row => row.DeferralCount, 3)
                .SetProperty(row => row.FirstDeferralAt, Now.AddHours(-25)).SetProperty(row => row.LastDeferralAt, Now.AddMinutes(-1))
                .SetProperty(row => row.LastDeferralError, "Existing capacity shortage"));
        await db.JobDispatches.Where(row => row.JobId == targetId).ExecuteUpdateAsync(set =>
            set.SetProperty(row => row.HangfireJobId, "delivery").SetProperty(row => row.DeliveredAtUtc, Now.AddMinutes(-2))
                .SetProperty(row => row.ClaimToken, Guid.NewGuid()).SetProperty(row => row.LeaseExpiresAtUtc, Now.AddMinutes(-1))
                .SetProperty(row => row.AttemptCount, 9).SetProperty(row => row.LastError, "Previous dispatch observation"));
        db.ChangeTracker.Clear();
        return new(targetId, fence);
    }
    private static ClaimedJobRepository<MediaReconciliationJob, MediaReconciliationJobRepository> Claims(RomdDbContext db, TimeProvider clock) =>
        new(new(db, clock), db, clock, new());
    private static async Task<Guid?> TryClaimAsync(RomdDbContext db, TimeProvider clock, Guid id, string delivery) =>
        (await Claims(db, clock).TryClaimExecutionAsync(id, delivery))?.FenceToken;

    private static ApplyStateContext CreateFailedContext(Guid jobId)
    {
        var method = typeof(EntryPoint).GetMethod(nameof(EntryPoint.Execute))!;
        var background = new BackgroundJob("delivery", new Hangfire.Common.Job(typeof(EntryPoint), method, [jobId]), DateTime.UtcNow);
        return new ApplyStateContext(Substitute.For<JobStorage>(), Substitute.For<IStorageConnection>(),
            Substitute.For<IWriteOnlyTransaction>(), background, new FailedState(new IOException("Scheduler retries exhausted.")),
            ProcessingState.StateName);
    }

    private sealed record Seeded(Guid JobId, Guid Fence);
    private sealed class EntryPoint { public void Execute(Guid jobId) { } }
}
