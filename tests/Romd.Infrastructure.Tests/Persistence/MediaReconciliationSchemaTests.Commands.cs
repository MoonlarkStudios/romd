using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Source.Rom.Commands.RetryMediaReconciliation;
using Romd.Admin.Application.Source.Rom.Commands.StartMediaReconciliationSelection;
using Romd.Admin.Application.Source.Rom.Commands.StartMediaReconciliationSubject;
using Romd.Application.Common.Security;
using Romd.Domain.Jobs;
using Romd.Infrastructure.Storage;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories.Reconciliation;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed partial class MediaReconciliationSchemaTests
{
    [Theory]
    [InlineData("subject", false)]
    [InlineData("selection", false)]
    [InlineData("retry", false)]
    [InlineData("subject", true)]
    [InlineData("selection", true)]
    [InlineData("retry", true)]
    public async Task Commands_AcceptanceAndDispatch_CommitOrRollbackTogether(string operation, bool failCommit)
    {
        var (db, graph) = await SetupAsync();
        await using var owned = db;
        Guid actor = await AddActorAsync(db), requestId = Guid.NewGuid(), previous = Guid.Empty;
        MediaReconciliationRequest.SelectionWork? originalSelection = null;
        if (operation == "retry")
        {
            var claim = await AcceptSelectionAsync(db, new MediaReconciliationFilter.AllSubjects(), 7, conversion: true);
            previous = claim.Job.Id;
            originalSelection = claim.Job.Request.ShouldBeOfType<MediaReconciliationRequest.SelectionWork>();
            using (JobExecutionFenceScope.Enter(previous, claim.FenceToken, TimeProvider.System))
            {
                var selections = Selections(db, graph);
                (await selections.StopOwnedAsync(previous)).IsError.ShouldBeFalse();
                (await selections.FinishAsync(previous, MediaReconciliationOutcomeKind.Cancelled, "cancelled")).IsError.ShouldBeFalse();
            }
            // Retrying preserves the original selection rather than recapturing a larger catalog.
            db.OriginalDiscLayouts.Add(new() { Id = 17, SourceJobId = Guid.NewGuid(), SourceRelativePath = "later.cue", EntryPoint = "later.cue", DescriptorFileId = 3 });
            await db.SaveChangesAsync();
        }
        int beforeJobs = await db.Jobs.CountAsync(), beforeDispatches = await db.JobDispatches.CountAsync();
        int beforeSubjects = await db.Set<MediaReconciliationSubjectEntity>().CountAsync();
        int beforeRequests = await db.Set<MediaReconciliationRequestEntity>().CountAsync();
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(actor);
        await using var requester = _database.CreateContext();
        var work = new AcceptanceCommitProbe(new EfUnitOfWork(requester), async () =>
        {
            await using var observer = _database.CreateContext();
            (await observer.Jobs.CountAsync()).ShouldBe(beforeJobs);
            (await observer.JobDispatches.CountAsync()).ShouldBe(beforeDispatches);
            (await observer.Set<MediaReconciliationRequestEntity>().AnyAsync(row => row.Id == requestId)).ShouldBeFalse();
        }, failCommit);
        var requests = new MediaReconciliationRequests(requester, TimeProvider.System);
        Task<ErrorOr<Guid>> AcceptAsync() => operation switch
        {
            "subject" => new StartMediaReconciliationSubjectCommandHandler(requests, work, user, NullLogger<StartMediaReconciliationSubjectCommandHandler>.Instance)
                .HandleAsync(new(requestId, "LogicalRom", graph.RomId, true)),
            "selection" => new StartMediaReconciliationSelectionCommandHandler(requests, work, user, RomMediaPolicyComposition.Create().Kinds,
                    Substitute.For<Romd.Application.Common.ReferenceCatalog.IReferenceCatalogService>(), NullLogger<StartMediaReconciliationSelectionCommandHandler>.Instance)
                .HandleAsync(new(requestId, "AllSubjects", null, null, 7, true)),
            "retry" => new RetryMediaReconciliationCommandHandler(requests, work, user, NullLogger<RetryMediaReconciliationCommandHandler>.Instance)
                .HandleAsync(new(requestId, previous)),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };

        ErrorOr<Guid> accepted = default;
        if (failCommit)
            (await Should.ThrowAsync<InvalidOperationException>(() => AcceptAsync())).Message.ShouldBe("Injected failure after acceptance flush.");
        else
        {
            accepted = await AcceptAsync();
            accepted.IsError.ShouldBeFalse();
        }

        work.CommitAttempts.ShouldBe(1);
        await using var read = _database.CreateContext();
        (await read.Jobs.CountAsync()).ShouldBe(beforeJobs + (failCommit ? 0 : 1));
        (await read.JobDispatches.CountAsync()).ShouldBe(beforeDispatches + (failCommit ? 0 : 1));
        (await read.Set<MediaReconciliationRequestEntity>().CountAsync()).ShouldBe(beforeRequests + (failCommit ? 0 : 1));
        (await read.Set<MediaReconciliationSubjectEntity>().CountAsync()).ShouldBe(beforeSubjects + (!failCommit && operation == "subject" ? 1 : 0));
        if (failCommit)
            requester.ChangeTracker.Entries().ShouldBeEmpty();
        else
        {
            var audit = await read.Set<MediaReconciliationRequestEntity>().SingleAsync(row => row.Id == requestId);
            audit.JobId.ShouldBe(accepted.Value);
            audit.ActorId.ShouldBe(actor);
            audit.ConversionRequested.ShouldBeTrue();
            var job = await new MediaReconciliationJobRepository(read, TimeProvider.System).GetByIdAsync(accepted.Value);
            job.ShouldNotBeNull();
            if (originalSelection is not null)
            {
                var selection = job.Request.ShouldBeOfType<MediaReconciliationRequest.SelectionWork>();
                selection.Bounds.ShouldBe(originalSelection.Bounds);
                selection.Limit.ShouldBe(originalSelection.Limit);
                audit.RetryOfJobId.ShouldBe(previous);
            }
        }
    }

    private sealed class AcceptanceCommitProbe(IUnitOfWork inner, Func<Task> assertUncommitted, bool failCommit) : IUnitOfWork
    {
        public int CommitAttempts { get; private set; }
        public Task FlushAsync(CancellationToken cancellationToken = default) => inner.FlushAsync(cancellationToken);
        public TransactionConflict ConflictOf(Exception error) => inner.ConflictOf(error);
        public async Task<ITransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
            new Transaction(await inner.BeginTransactionAsync(cancellationToken), this);

        private async Task PrepareCommitAsync(CancellationToken cancellationToken)
        {
            CommitAttempts++;
            await inner.FlushAsync(cancellationToken);
            await assertUncommitted();
            if (failCommit)
                throw new InvalidOperationException("Injected failure after acceptance flush.");
        }

        private sealed class Transaction(ITransaction innerTransaction, AcceptanceCommitProbe owner) : ITransaction
        {
            public async Task CommitAsync(CancellationToken cancellationToken = default)
            {
                await owner.PrepareCommitAsync(cancellationToken);
                await innerTransaction.CommitAsync(cancellationToken);
            }

            public Task RollbackAsync(CancellationToken cancellationToken = default) => innerTransaction.RollbackAsync(cancellationToken);
            public ValueTask DisposeAsync() => innerTransaction.DisposeAsync();
        }
    }
}
