using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Ingestion.Jobs.Commands.CancelJob;
using Romd.Application.Common.Security;
using Romd.Domain.Identity;
using Romd.Domain.Jobs;
using Romd.Domain.Source.Rom;
using Romd.Infrastructure.Storage;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.Persistence.Repositories.Reconciliation;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed partial class MediaReconciliationSchemaTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationCommand_AfterAuthorizedDisconnect_CommitsOnlyStopIntentOrRollsBackAfterFlush(bool failCommit)
    {
        var (db, graph) = await SetupAsync();
        await using var owned = db;
        var claim = await AcceptExecutionAsync(db, new MediaReconciliationSubject.Representation(graph.Cd.Id), false);
        var execution = Execution(db, StoppedCustody(), PolicyDecision(TargetDecoder, true));
        using (JobExecutionFenceScope.Enter(claim.Job.Id, claim.FenceToken, TimeProvider.System))
            (await execution.CaptureAsync(VerifyCapture(claim.Job.Id, 1, graph.Cd, TargetDecoder, 'b'))).IsError.ShouldBeFalse();
        await using var requester = _database.CreateContext();
        using var request = new CancellationTokenSource();
        var reader = Substitute.For<IJobRepository>();
        reader.GetByIdAsync(claim.Job.Id, request.Token).Returns(async _ =>
        {
            var current = await new JobRepository(requester, TimeProvider.System).GetByIdAsync(claim.Job.Id, request.Token);
            request.Cancel();
            return current;
        });
        var work = new AcceptanceCommitProbe(new EfUnitOfWork(requester), async () =>
        {
            await using var observer = _database.CreateContext();
            (await observer.Set<MediaReconciliationDefinitionEntity>().SingleAsync()).CancelRequested.ShouldBeFalse();
            (await observer.Set<MediaReconciliationPinEntity>().CountAsync()).ShouldBe(1);
            (await observer.Jobs.SingleAsync()).Phase.ShouldBe(claim.Job.Phase);
        }, failCommit);
        var delivery = Substitute.For<IJobDeliveryCancellation>();
        var ordinary = Substitute.For<IJobCancellationStore>();
        var handler = new CancelJobCommandHandler(reader, ordinary, new MediaReconciliationRequests(requester, TimeProvider.System),
            work, CancellationAdmin(), TimeProvider.System, delivery, NullLogger<CancelJobCommandHandler>.Instance);

        if (failCommit)
            await Should.ThrowAsync<InvalidOperationException>(() => handler.HandleAsync(new(claim.Job.Id), request.Token));
        else
            (await handler.HandleAsync(new(claim.Job.Id), request.Token)).IsError.ShouldBeFalse();

        ordinary.ReceivedCalls().ShouldBeEmpty();
        delivery.ReceivedCalls().ShouldBeEmpty();
        await using var verify = _database.CreateContext();
        (await verify.Set<MediaReconciliationDefinitionEntity>().SingleAsync()).CancelRequested.ShouldBe(!failCommit);
        (await verify.Set<MediaReconciliationPinEntity>().CountAsync()).ShouldBe(1);
        (await execution.ReadOutcomeAsync(claim.Job.Id)).ShouldBeNull();
        (await verify.Jobs.SingleAsync()).ExecutionFenceToken.ShouldBe(claim.FenceToken);
        if (failCommit)
            requester.ChangeTracker.Entries().ShouldBeEmpty();
    }

    [Fact]
    public async Task CancellationCommand_WorkerSettlementWinsLock_PreservesJournalOutcomeAndReleasedCustody()
    {
        var (db, graph) = await SetupAsync();
        await using var owned = db;
        var claim = await AcceptExecutionAsync(db, new MediaReconciliationSubject.Representation(graph.Cd.Id), false);
        using (JobExecutionFenceScope.Enter(claim.Job.Id, claim.FenceToken, TimeProvider.System))
            (await Execution(db, StoppedCustody(), PolicyDecision(TargetDecoder, true))
                .CaptureAsync(VerifyCapture(claim.Job.Id, 1, graph.Cd, TargetDecoder, 'b'))).IsError.ShouldBeFalse();
        await using var worker = _database.CreateContext();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var reachedCommit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCommit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = new AcceptanceCommitProbe(new EfUnitOfWork(worker), async () =>
        {
            reachedCommit.TrySetResult();
            await releaseCommit.Task.WaitAsync(timeout.Token);
        }, false);
        var execution = new MediaReconciliationExecution(worker, _database.CreateOptions(), work, StoppedCustody(),
            TimeProvider.System, RomMediaPolicyComposition.Create().Kinds, PolicyDecision(TargetDecoder, true));
        var finishing = Task.Run(async () =>
        {
            using var fence = JobExecutionFenceScope.Enter(claim.Job.Id, claim.FenceToken, TimeProvider.System);
            return await execution.FinishAsync(claim.Job.Id, 1, MediaReconciliationOutcomeKind.Failed, "failed-first", true, timeout.Token);
        });
        await using var requester = _database.CreateContext();
        Task<ErrorOr<Success>>? cancelling = null;
        Exception? primary = null;
        try
        {
            var first = await Task.WhenAny(reachedCommit.Task, finishing).WaitAsync(timeout.Token);
            if (first == finishing)
                await finishing;
            reachedCommit.Task.IsCompleted.ShouldBeTrue();
            int pid = await requester.OpenBackendProcessIdAsync(timeout.Token);
            var delivery = Substitute.For<IJobDeliveryCancellation>();
            var handler = new CancelJobCommandHandler(new JobRepository(requester, TimeProvider.System), Substitute.For<IJobCancellationStore>(),
                new MediaReconciliationRequests(requester, TimeProvider.System), new EfUnitOfWork(requester), CancellationAdmin(),
                TimeProvider.System, delivery, NullLogger<CancelJobCommandHandler>.Instance);
            cancelling = handler.HandleAsync(new(claim.Job.Id), timeout.Token);
            (await _database.WaitsForALockAsync(pid, cancelling, timeout.Token)).ShouldBeTrue();
            releaseCommit.TrySetResult();
            (await finishing.WaitAsync(timeout.Token)).IsError.ShouldBeFalse();
            (await cancelling.WaitAsync(timeout.Token)).FirstError.Code.ShouldBe("MediaReconciliation.AlreadyComplete");
            delivery.ReceivedCalls().ShouldBeEmpty();
            var outcome = (await execution.ReadOutcomeAsync(claim.Job.Id)).ShouldNotBeNull();
            outcome.Kind.ShouldBe(MediaReconciliationOutcomeKind.Failed);
            (await requester.Set<MediaReconciliationDefinitionEntity>().SingleAsync()).CancelRequested.ShouldBeFalse();
            (await requester.Set<MediaReconciliationPinEntity>().CountAsync()).ShouldBe(0);
            (await requester.Jobs.SingleAsync()).Phase.ShouldBe("Failed");
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            releaseCommit.TrySetResult();
            await PostgreSqlLockQueue.CancelAndJoinAsync(timeout, primary,
                cancelling is null ? [finishing] : [finishing, cancelling]);
        }
    }

    private static ICurrentUser CancellationAdmin()
    {
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(Guid.NewGuid());
        user.HasRole(Arg.Any<RomdRoleType>()).Returns(true);
        return user;
    }
}
