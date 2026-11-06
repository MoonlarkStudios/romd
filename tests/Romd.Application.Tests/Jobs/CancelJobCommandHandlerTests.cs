using ErrorOr;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Ingestion.Jobs.Commands.CancelJob;
using Romd.Admin.Application.Source.Rom.Reconciliation;
using Romd.Application.Common.Security;
using Romd.Domain.Identity;
using Romd.Domain.Jobs;
using Romd.Domain.Source.Rom;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Jobs;

public sealed class CancelJobCommandHandlerTests
{
    private static readonly DateTimeOffset Now = new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero).AddTicks(1234560);

    [Fact]
    public async Task HandleAsync_AuthorizedJob_CommitsAndDisposesBeforeStoppingCurrentDelivery()
    {
        var fixture = new Fixture();
        fixture.Job.SetHangfireJobId("current-delivery");

        var result = await fixture.Handler.HandleAsync(new(fixture.Job.Id));

        result.IsError.ShouldBeFalse();
        fixture.Job.Phase.ShouldBe("Cancelled");
        fixture.Job.CompletedAt.ShouldBe(Now);
        Received.InOrder(() =>
        {
            fixture.Store.LockVisibleForCancellationStagedAsync(fixture.Job.Id,
                Arg.Is<JobVisibilityScope>(scope => scope.OwnerId == fixture.Actor && !scope.IncludeAdminOnly && !scope.Empty), CancellationToken.None);
            fixture.Store.ApplyCancellationStagedAsync(fixture.Job, CancellationToken.None);
            fixture.Transaction.CommitAsync(CancellationToken.None);
            fixture.Transaction.DisposeAsync();
            fixture.Delivery.RequestStopAsync("current-delivery", CancellationToken.None);
        });
        await fixture.Jobs.DidNotReceiveWithAnyArgs().UpdateStagedAsync(default!, default);
    }

    [Fact]
    public async Task HandleAsync_RequestDisconnectedAfterAuthorizedRead_UsesIndependentTokenThroughCommitAndDelivery()
    {
        var fixture = new Fixture();
        using var request = new CancellationTokenSource();
        fixture.Jobs.GetByIdAsync(fixture.Job.Id, request.Token).Returns(_ =>
        {
            request.Cancel();
            return fixture.Job;
        });
        fixture.Job.SetHangfireJobId("delivery");

        (await fixture.Handler.HandleAsync(new(fixture.Job.Id), request.Token)).IsError.ShouldBeFalse();

        await fixture.UnitOfWork.Received(1).BeginTransactionAsync(CancellationToken.None);
        await fixture.Store.Received(1).ApplyCancellationStagedAsync(fixture.Job, CancellationToken.None);
        await fixture.Transaction.Received(1).CommitAsync(CancellationToken.None);
        await fixture.Delivery.Received(1).RequestStopAsync("delivery", CancellationToken.None);
    }

    [Fact]
    public async Task HandleAsync_RequestReadCancelled_DoesNotStartDurableMutation()
    {
        var fixture = new Fixture();
        using var request = new CancellationTokenSource();
        request.Cancel();
        fixture.Jobs.GetByIdAsync(fixture.Job.Id, request.Token)
            .Returns(Task.FromCanceled<Job?>(request.Token));

        await Should.ThrowAsync<OperationCanceledException>(() => fixture.Handler.HandleAsync(new(fixture.Job.Id), request.Token));

        fixture.UnitOfWork.ReceivedCalls().ShouldBeEmpty();
        fixture.Store.ReceivedCalls().ShouldBeEmpty();
        fixture.Delivery.ReceivedCalls().ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HandleAsync_HiddenJobOrMissingActor_DoesNotRevealOrLock(bool missingActor)
    {
        var fixture = new Fixture();
        fixture.User.UserId.Returns(missingActor ? null : Guid.NewGuid());

        var result = await fixture.Handler.HandleAsync(new(fixture.Job.Id));

        result.FirstError.Code.ShouldBe("Jobs.NotFound");
        fixture.UnitOfWork.ReceivedCalls().ShouldBeEmpty();
        fixture.Store.ReceivedCalls().ShouldBeEmpty();
        fixture.Delivery.ReceivedCalls().ShouldBeEmpty();
        fixture.Job.IsTerminal.ShouldBeFalse();
        if (missingActor)
            fixture.Jobs.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task HandleAsync_MetadataAlreadyTerminal_ReportsRequiredWorkBeforeTerminalConflict()
    {
        var fixture = new Fixture();
        var job = MetadataRematerializationJob.Create(1, "system", fixture.Actor, Now);
        job.Complete(Now);
        fixture.Jobs.GetByIdAsync(job.Id, Arg.Any<CancellationToken>()).Returns(job);

        var result = await fixture.Handler.HandleAsync(new(job.Id));

        result.FirstError.Code.ShouldBe("Jobs.NotCancellable");
        fixture.UnitOfWork.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task HandleAsync_InitiallyTerminal_ReturnsConflictWithoutTransaction()
    {
        var fixture = new Fixture();
        CompleteExport(fixture.Job);

        var result = await fixture.Handler.HandleAsync(new(fixture.Job.Id));

        result.FirstError.Code.ShouldBe("Jobs.AlreadyComplete");
        fixture.UnitOfWork.ReceivedCalls().ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HandleAsync_CurrentRowMissingOrTerminal_RollsBackWithoutApplyingOrSignalling(bool terminal)
    {
        var fixture = new Fixture();
        fixture.Store.LockVisibleForCancellationStagedAsync(fixture.Job.Id, Arg.Any<JobVisibilityScope>(), CancellationToken.None)
            .Returns(_ =>
            {
                if (terminal)
                {
                    CompleteExport(fixture.Job);
                    return fixture.Job;
                }
                return null;
            });

        var result = await fixture.Handler.HandleAsync(new(fixture.Job.Id));

        result.FirstError.Code.ShouldBe(terminal ? "Jobs.AlreadyComplete" : "Jobs.NotFound");
        await fixture.Store.DidNotReceiveWithAnyArgs().ApplyCancellationStagedAsync(default!, default);
        await fixture.Transaction.DidNotReceiveWithAnyArgs().CommitAsync(default);
        await fixture.Transaction.Received(1).DisposeAsync();
        fixture.Delivery.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task HandleAsync_LockReadWaits_CapturesTimeAndDeliveryFromCurrentStateAfterWait()
    {
        var fixture = new Fixture();
        fixture.Job.SetHangfireJobId("old-delivery");
        var locked = new TaskCompletionSource<Job?>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Store.LockVisibleForCancellationStagedAsync(fixture.Job.Id, Arg.Any<JobVisibilityScope>(), CancellationToken.None)
            .Returns(locked.Task);
        var operation = fixture.Handler.HandleAsync(new(fixture.Job.Id));
        fixture.Clock.ReceivedCalls().ShouldBeEmpty();
        var afterWait = Now.AddMinutes(1);
        fixture.Clock.GetUtcNow().Returns(afterWait);
        fixture.Job.SetHangfireJobId("winning-delivery");
        locked.SetResult(fixture.Job);

        (await operation).IsError.ShouldBeFalse();

        fixture.Job.CompletedAt.ShouldBe(afterWait);
        await fixture.Delivery.Received(1).RequestStopAsync("winning-delivery", CancellationToken.None);
        await fixture.Delivery.DidNotReceive().RequestStopAsync("old-delivery", Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("stage")]
    [InlineData("commit")]
    public async Task HandleAsync_DurableMutationFails_DoesNotStopDeliveryAndPreservesFailure(string point)
    {
        var fixture = new Fixture();
        fixture.Job.SetHangfireJobId("delivery");
        var failure = new InvalidOperationException("Injected " + point + " failure.");
        if (point == "stage")
            fixture.Store.ApplyCancellationStagedAsync(fixture.Job, CancellationToken.None).Returns(Task.FromException(failure));
        else
            fixture.Transaction.CommitAsync(CancellationToken.None).Returns(Task.FromException(failure));

        (await Should.ThrowAsync<InvalidOperationException>(() => fixture.Handler.HandleAsync(new(fixture.Job.Id))))
            .ShouldBeSameAs(failure);

        fixture.Delivery.ReceivedCalls().ShouldBeEmpty();
        await fixture.Transaction.Received(1).DisposeAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HandleAsync_AfterCommitDeliveryOrDisposalFails_PreservesDurableSuccess(bool disposal)
    {
        var fixture = new Fixture();
        fixture.Job.SetHangfireJobId("delivery");
        var failure = new IOException("Injected postcommit failure.");
        if (disposal)
            fixture.Transaction.DisposeAsync().Returns(ValueTask.FromException(failure));
        else
            fixture.Delivery.RequestStopAsync("delivery", CancellationToken.None).Returns(Task.FromException(failure));

        (await fixture.Handler.HandleAsync(new(fixture.Job.Id))).IsError.ShouldBeFalse();

        await fixture.Transaction.Received(1).CommitAsync(CancellationToken.None);
        await fixture.Delivery.Received(1).RequestStopAsync("delivery", CancellationToken.None);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HandleAsync_MediaAdmin_PreservesJournalOutcomeAndRaceErrorWithoutDeletingDelivery(bool settled)
    {
        var fixture = new Fixture();
        fixture.User.HasRole(Arg.Any<RomdRoleType>()).Returns(true);
        var job = MediaReconciliationJob.Create(Guid.NewGuid(),
            new MediaReconciliationRequest.SubjectWork(new MediaReconciliationSubject.LogicalRom(1)), "media", null, fixture.Actor, Now);
        job.SetHangfireJobId("media-delivery");
        fixture.Jobs.GetByIdAsync(job.Id, Arg.Any<CancellationToken>()).Returns(job);
        fixture.Media.StageCancellationAsync(job.Id, CancellationToken.None).Returns(settled
            ? Error.Conflict("MediaReconciliation.AlreadyComplete", "The job settled.")
            : (ErrorOr<Success>)Result.Success);

        var result = await fixture.Handler.HandleAsync(new(job.Id));

        result.IsError.ShouldBe(settled);
        if (settled)
            result.FirstError.Code.ShouldBe("MediaReconciliation.AlreadyComplete");
        else
            await fixture.Transaction.Received(1).CommitAsync(CancellationToken.None);
        job.IsTerminal.ShouldBeFalse();
        fixture.Store.ReceivedCalls().ShouldBeEmpty();
        fixture.Delivery.ReceivedCalls().ShouldBeEmpty();
    }

    private static void CompleteExport(ExportJob job)
    {
        job.Start("completed-delivery", Now.AddSeconds(-1));
        job.Complete(Now);
    }

    private sealed class Fixture
    {
        public Guid Actor { get; } = Guid.NewGuid();
        public ICurrentUser User { get; } = Substitute.For<ICurrentUser>();
        public IJobRepository Jobs { get; } = Substitute.For<IJobRepository>();
        public IJobCancellationStore Store { get; } = Substitute.For<IJobCancellationStore>();
        public IMediaReconciliationRequests Media { get; } = Substitute.For<IMediaReconciliationRequests>();
        public IUnitOfWork UnitOfWork { get; } = Substitute.For<IUnitOfWork>();
        public ITransaction Transaction { get; } = Substitute.For<ITransaction>();
        public IJobDeliveryCancellation Delivery { get; } = Substitute.For<IJobDeliveryCancellation>();
        public TimeProvider Clock { get; } = Substitute.For<TimeProvider>();
        public ExportJob Job { get; }
        public CancelJobCommandHandler Handler { get; }
        public Fixture()
        {
            User.UserId.Returns(Actor);
            Clock.GetUtcNow().Returns(Now);
            Job = ExportJob.CreateLibrary(7, 1, Now, Actor);
            Jobs.GetByIdAsync(Job.Id, Arg.Any<CancellationToken>()).Returns(Job);
            Store.LockVisibleForCancellationStagedAsync(Job.Id, Arg.Any<JobVisibilityScope>(), CancellationToken.None).Returns(Job);
            UnitOfWork.BeginTransactionAsync(CancellationToken.None).Returns(Transaction);
            Handler = new(Jobs, Store, Media, UnitOfWork, User, Clock, Delivery, NullLogger<CancelJobCommandHandler>.Instance);
        }
    }
}
