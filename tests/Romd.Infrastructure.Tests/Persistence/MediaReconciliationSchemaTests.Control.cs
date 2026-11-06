using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Source.Rom.Reconciliation;
using Romd.Domain.Jobs;
using Romd.Domain.Source.Rom;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories.Reconciliation;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed partial class MediaReconciliationSchemaTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_CallerTransaction_RecordsOnlyIntentUntilStoppedWorkerSettlement(bool commit)
    {
        var (db, graph) = await SetupAsync();
        await using var owned = db;
        var claim = await AcceptExecutionAsync(db, new MediaReconciliationSubject.Representation(graph.Cd.Id), false);
        using var fence = JobExecutionFenceScope.Enter(claim.Job.Id, claim.FenceToken, TimeProvider.System);
        var execution = Execution(db, StoppedCustody(), PolicyDecision(TargetDecoder, true));
        var captured = await execution.CaptureAsync(VerifyCapture(claim.Job.Id, 1, graph.Cd, TargetDecoder, 'b'));
        captured.IsError.ShouldBeFalse();
        await using (var requester = _database.CreateContext())
        await using (var transaction = await requester.Database.BeginTransactionAsync())
        {
            (await new MediaReconciliationRequests(requester, TimeProvider.System).StageCancellationAsync(claim.Job.Id)).IsError.ShouldBeFalse();
            await requester.SaveChangesAsync();
            await using var observer = _database.CreateContext();
            (await observer.Set<MediaReconciliationDefinitionEntity>().SingleAsync()).CancelRequested.ShouldBeFalse();
            (await observer.Set<MediaReconciliationPinEntity>().CountAsync()).ShouldBe(1);
            if (commit)
                await transaction.CommitAsync();
            else
                await transaction.RollbackAsync();
        }
        (await execution.IsCancellationRequestedAsync(claim.Job.Id)).ShouldBe(commit);
        (await execution.ReadOutcomeAsync(claim.Job.Id)).ShouldBeNull();
        (await db.Set<MediaReconciliationPinEntity>().CountAsync()).ShouldBe(1);
        if (!commit)
            return;
        (await execution.FinishAsync(claim.Job.Id, 1, MediaReconciliationOutcomeKind.Cancelled, "cancelled", processTreeStopped: false)).FirstError.ShouldBe(MediaReconciliationErrors.Custody);
        (await db.Set<MediaReconciliationPinEntity>().CountAsync()).ShouldBe(1);
        (await execution.FinishAsync(claim.Job.Id, 1, MediaReconciliationOutcomeKind.Cancelled, "cancelled", processTreeStopped: true)).IsError.ShouldBeFalse();
        (await db.Set<MediaReconciliationPinEntity>().CountAsync()).ShouldBe(0);
        (await execution.ReadAttemptOutcomeAsync(claim.Job.Id, captured.Value.Number)).ShouldNotBeNull().Disposition.ShouldBe(MediaReconciliationAttemptDisposition.Cancelled);
        await using var after = _database.CreateContext();
        await using var afterTransaction = await after.Database.BeginTransactionAsync();
        (await new MediaReconciliationRequests(after, TimeProvider.System).StageCancellationAsync(claim.Job.Id)).FirstError.Code.ShouldBe("MediaReconciliation.AlreadyComplete");
    }

    [Fact]
    public async Task Retry_ConcurrentExactRequest_PreservesConversionAndAttributionWithoutSelectionOwnership()
    {
        var (db, graph) = await SetupAsync();
        await using var owned = db;
        Guid actor = await AddActorAsync(db), requestId = Guid.NewGuid();
        var claim = await AcceptExecutionAsync(db, new MediaReconciliationSubject.LogicalRom(graph.RomId), conversion: true);
        using (JobExecutionFenceScope.Enter(claim.Job.Id, claim.FenceToken, TimeProvider.System))
            (await Execution(db, StoppedCustody(), PolicyDecision(TargetDecoder, true)).FinishAsync(claim.Job.Id, 1, MediaReconciliationOutcomeKind.Failed, "fixture-failure", true)).IsError.ShouldBeFalse();
        async Task<MediaReconciliationAcceptance> RetryAsync()
        {
            await using var requester = _database.CreateContext();
            await using var transaction = await requester.Database.BeginTransactionAsync();
            var result = await new MediaReconciliationRequests(requester, TimeProvider.System).StageRetryAsync(requestId, claim.Job.Id, actor);
            result.IsError.ShouldBeFalse();
            await requester.SaveChangesAsync();
            await transaction.CommitAsync();
            return result.Value;
        }
        var retries = await Task.WhenAll(RetryAsync(), RetryAsync());
        retries.Count(retry => retry.Created).ShouldBe(1);
        retries.Select(retry => retry.Job.Id).Distinct().ShouldHaveSingleItem();
        retries[0].Job.OriginSelectionJobId.ShouldBeNull();
        await using var read = _database.CreateContext();
        var state = await read.Set<MediaReconciliationSubjectEntity>().SingleAsync();
        state.RequestedVersion.ShouldBe(2);
        state.AcknowledgedVersion.ShouldBe(1);
        state.ConversionRequested.ShouldBeTrue();
        var audit = await read.Set<MediaReconciliationRequestEntity>().SingleAsync(row => row.Id == requestId);
        audit.RetryOfJobId.ShouldBe(claim.Job.Id);
        audit.ActorId.ShouldBe(actor);
        audit.ConversionRequested.ShouldBeTrue();
        (await read.JobDispatches.CountAsync()).ShouldBe(2);
        await using var transaction2 = await read.Database.BeginTransactionAsync();
        var requests = new MediaReconciliationRequests(read, TimeProvider.System);
        (await requests.StageRetryAsync(requestId, Guid.NewGuid(), actor)).FirstError.Code.ShouldBe("MediaReconciliation.RequestConflict");
        (await requests.StageSubjectAsync(new(requestId, new MediaReconciliationSubject.LogicalRom(graph.RomId), true, MediaReconciliationCause.Admin, actor, null))).FirstError.Code.ShouldBe("MediaReconciliation.RequestConflict");
    }

    [Fact]
    public async Task Retry_SelectionAfterCatalogGrowth_KeepsOriginalFiniteBoundsAndIndependentOwner()
    {
        var (db, graph) = await SetupAsync();
        await using var owned = db;
        Guid actor = await AddActorAsync(db);
        var claim = await AcceptSelectionAsync(db, new MediaReconciliationFilter.AllSubjects(), 10, conversion: true);
        var original = (MediaReconciliationRequest.SelectionWork)claim.Job.Request;
        using (JobExecutionFenceScope.Enter(claim.Job.Id, claim.FenceToken, TimeProvider.System))
        {
            var selections = Selections(db, graph);
            (await selections.StopOwnedAsync(claim.Job.Id)).IsError.ShouldBeFalse();
            (await selections.FinishAsync(claim.Job.Id, MediaReconciliationOutcomeKind.Cancelled, "cancelled")).IsError.ShouldBeFalse();
        }
        db.OriginalDiscLayouts.Add(new() { Id = 17, SourceJobId = Guid.NewGuid(), SourceRelativePath = "later.cue", EntryPoint = "later.cue", DescriptorFileId = 3 });
        await db.SaveChangesAsync();
        await using var requester = _database.CreateContext();
        await using var transaction = await requester.Database.BeginTransactionAsync();
        var requests = new MediaReconciliationRequests(requester, TimeProvider.System);
        var retry = await requests.StageRetryAsync(Guid.NewGuid(), claim.Job.Id, actor);
        retry.IsError.ShouldBeFalse();
        var selection = retry.Value.Job.Request.ShouldBeOfType<MediaReconciliationRequest.SelectionWork>();
        selection.Bounds.ShouldBe(original.Bounds);
        selection.Limit.ShouldBe(10);
        selection.ConversionRequested.ShouldBeTrue();
        retry.Value.Job.OriginSelectionJobId.ShouldBeNull();
        var fresh = await requests.StageSelectionAsync(Guid.NewGuid(), new MediaReconciliationFilter.AllSubjects(), 10, true, MediaReconciliationCause.Admin, actor);
        fresh.IsError.ShouldBeFalse();
        fresh.Value.Job.Request.ShouldBeOfType<MediaReconciliationRequest.SelectionWork>().Bounds.OriginalLayout.ShouldBe(17);
        await requester.SaveChangesAsync();
        await transaction.CommitAsync();
        await using var read = _database.CreateContext();
        (await read.Set<MediaReconciliationRequestEntity>().SingleAsync(row => row.JobId == retry.Value.Job.Id)).RetryOfJobId.ShouldBe(claim.Job.Id);
    }

    [Fact]
    public async Task Retry_ActiveWork_RefusesBeforeAddingDemandOrDispatch()
    {
        var (db, graph) = await SetupAsync();
        await using var owned = db;
        Guid actor = await AddActorAsync(db);
        var claim = await AcceptExecutionAsync(db, new MediaReconciliationSubject.LogicalRom(graph.RomId), false);
        await using var requester = _database.CreateContext();
        await using var transaction = await requester.Database.BeginTransactionAsync();
        (await new MediaReconciliationRequests(requester, TimeProvider.System).StageRetryAsync(Guid.NewGuid(), claim.Job.Id, actor)).FirstError.Code.ShouldBe("MediaReconciliation.NotComplete");
        (await requester.Set<MediaReconciliationRequestEntity>().CountAsync()).ShouldBe(1);
        (await requester.JobDispatches.CountAsync()).ShouldBe(1);
    }
}
