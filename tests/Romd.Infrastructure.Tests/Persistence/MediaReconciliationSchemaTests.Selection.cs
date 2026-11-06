using Microsoft.EntityFrameworkCore;
using Npgsql;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Source.Rom.Reconciliation;
using Romd.Domain.Jobs;
using Romd.Domain.Source.Rom;
using Romd.Infrastructure.Storage;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.Persistence.Repositories.Reconciliation;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed partial class MediaReconciliationSchemaTests
{
    private async Task<JobExecutionClaim<MediaReconciliationJob>> AcceptSelectionAsync(RomdDbContext db, MediaReconciliationFilter filter, long limit, bool conversion)
    {
        Guid id;
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var accepted = await new MediaReconciliationRequests(db, TimeProvider.System).StageSelectionAsync(Guid.NewGuid(), filter, limit, conversion, MediaReconciliationCause.Readiness, null);
            accepted.IsError.ShouldBeFalse();
            id = accepted.Value.Job.Id;
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        return (await new ClaimedJobRepository<MediaReconciliationJob, MediaReconciliationJobRepository>(new(db, TimeProvider.System), db, TimeProvider.System, new())
            .TryClaimExecutionAsync(id, "selection-fixture")).ShouldNotBeNull();
    }
    private MediaReconciliationSelections Selections(RomdDbContext db, MediaFactGraphFixture graph) => new(db, _database.CreateOptions(), new EfUnitOfWork(db),
        new MediaReconciliationRequests(db, TimeProvider.System), SnapshotReader(graph), RomMediaPolicyComposition.Create().Kinds, StoppedCustody(), TimeProvider.System);
    private sealed class SelectionItem
    {
        public string SubjectKind { get; set; } = null!;
        public int SubjectId { get; set; }
        public Guid? ChildJobId { get; set; }
        public bool IsOwned { get; set; }
        public string? SkippedCode { get; set; }
    }
    private static Task<SelectionItem[]> SelectionItemsAsync(RomdDbContext db, Guid id) => db.Database.SqlQuery<SelectionItem>($"""
        SELECT "SubjectKind","SubjectId","ChildJobId","IsOwned","SkippedCode" FROM romd."MediaReconciliationSelectionItems" WHERE "SelectionJobId"={id} ORDER BY "Ordinal"
        """).ToArrayAsync();

    [Fact]
    public async Task Selection_BoundedPages_KeepExactOwnershipDispatchAndVerifyOnlyRepresentationDemand()
    {
        var (db, graph) = await SetupAsync();
        await using var owned = db;
        var claim = await AcceptSelectionAsync(db, new MediaReconciliationFilter.AllSubjects(), 2, conversion: true);
        using var fence = JobExecutionFenceScope.Enter(claim.Job.Id, claim.FenceToken, TimeProvider.System);
        var selections = Selections(db, graph);
        var first = await selections.AdvanceAsync(claim.Job.Id, 1);
        first.IsError.ShouldBeFalse();
        first.Value.Progress.ShouldBe(new(new MediaReconciliationSubject.LogicalRom(graph.RomId), 1, 1, 0, 0, 0, 0, false));
        var second = await selections.AdvanceAsync(claim.Job.Id, 64);
        second.IsError.ShouldBeFalse();
        second.Value.Progress.Selected.ShouldBe(2);
        second.Value.Progress.Created.ShouldBe(2);
        second.Value.Progress.EnumerationComplete.ShouldBeTrue();
        var items = await SelectionItemsAsync(db, claim.Job.Id);
        items.Select(item => item.SubjectKind).ShouldBe(["LogicalRom", "Representation"]);
        items.All(item => item.IsOwned).ShouldBeTrue();
        foreach (var item in items)
        {
            (await db.Set<MediaReconciliationDefinitionEntity>().SingleAsync(row => row.JobId == item.ChildJobId)).OriginSelectionJobId.ShouldBe(claim.Job.Id);
            (await db.Set<MediaReconciliationSubjectEntity>().SingleAsync(row => row.LatestJobId == item.ChildJobId)).ConversionRequested.ShouldBe(item.SubjectKind == "LogicalRom");
        }
        (await db.JobDispatches.CountAsync()).ShouldBe(3);
        (await db.Set<MediaReconciliationRequestEntity>().CountAsync()).ShouldBe(3);
        var replay = await selections.AdvanceAsync(claim.Job.Id, 64);
        replay.IsError.ShouldBeFalse();
        replay.Value.ShouldBe(second.Value);
        (await db.JobDispatches.CountAsync()).ShouldBe(3);
    }

    [Fact]
    public async Task Selection_SkippedPrefix_DoesNotConsumeTheAcceptedLimitOrWidenKindScope()
    {
        var (db, graph) = await SetupAsync();
        await using var owned = db;
        await LayoutSourcesAsync(db, graph);
        await db.RepresentationOutputs.Where(row => row.RepresentationId == graph.Cd.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.RomId, (int?)null));
        var claim = await AcceptSelectionAsync(db, new MediaReconciliationFilter.KindSubjects("ps1-cd-set", 1), 1, conversion: true);
        using var fence = JobExecutionFenceScope.Enter(claim.Job.Id, claim.FenceToken, TimeProvider.System);
        var result = await Selections(db, graph).AdvanceAsync(claim.Job.Id, 64);
        result.IsError.ShouldBeFalse();
        result.Value.Progress.Selected.ShouldBe(2);
        result.Value.Progress.Skipped.ShouldBe(1);
        result.Value.Progress.Created.ShouldBe(1);
        result.Value.Progress.Cursor.ShouldBe(new MediaReconciliationSubject.OriginalLayout(1));
        result.Value.Progress.EnumerationComplete.ShouldBeTrue();
        var items = await SelectionItemsAsync(db, claim.Job.Id);
        items[0].SkippedCode.ShouldBe("kind-not-selected");
        items[0].ChildJobId.ShouldBeNull();
        items[1].SubjectKind.ShouldBe("OriginalLayout");
        items[1].ChildJobId.ShouldNotBeNull();
    }

    [Fact]
    public async Task Selection_InjectedItemFailure_RollsBackEveryChildDemandDispatchCounterAndCursor()
    {
        var (db, graph) = await SetupAsync();
        await using var owned = db;
        var claim = await AcceptSelectionAsync(db, new MediaReconciliationFilter.AllSubjects(), 10, conversion: false);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION romd.fail_selection_fixture() RETURNS trigger LANGUAGE plpgsql AS $fixture$
            BEGIN IF NEW."Ordinal"=2 THEN RAISE EXCEPTION 'injected second item failure' USING ERRCODE='P0001'; END IF; RETURN NEW; END; $fixture$;
            CREATE TRIGGER "TR_FailSelectionFixture" BEFORE INSERT ON romd."MediaReconciliationSelectionItems" FOR EACH ROW EXECUTE FUNCTION romd.fail_selection_fixture();
            """);
        using var fence = JobExecutionFenceScope.Enter(claim.Job.Id, claim.FenceToken, TimeProvider.System);
        var error = await Should.ThrowAsync<PostgresException>(() => Selections(db, graph).AdvanceAsync(claim.Job.Id, 64));
        error.SqlState.ShouldBe("P0001");
        (await db.Jobs.CountAsync()).ShouldBe(1);
        (await db.JobDispatches.CountAsync()).ShouldBe(1);
        (await db.Set<MediaReconciliationRequestEntity>().CountAsync()).ShouldBe(1);
        (await db.Set<MediaReconciliationSubjectEntity>().CountAsync()).ShouldBe(0);
        (await SelectionItemsAsync(db, claim.Job.Id)).ShouldBeEmpty();
        (await Selections(db, graph).ReadAsync(claim.Job.Id)).ShouldNotBeNull().Progress.ShouldBe(new(null, 0, 0, 0, 0, 0, 0, false));
    }

    [Fact]
    public async Task Selection_Cancellation_StopsOnlyOriginalChildrenAndWaitsForTheirDurableCustodyRelease()
    {
        var (db, graph) = await SetupAsync();
        await using var owned = db;
        var independent = await AcceptExecutionAsync(db, new MediaReconciliationSubject.LogicalRom(graph.RomId), false);
        var claim = await AcceptSelectionAsync(db, new MediaReconciliationFilter.AllSubjects(), 10, conversion: false);
        var selections = Selections(db, graph);
        using var fence = JobExecutionFenceScope.Enter(claim.Job.Id, claim.FenceToken, TimeProvider.System);
        var page = await selections.AdvanceAsync(claim.Job.Id, 64);
        page.IsError.ShouldBeFalse();
        page.Value.Progress.Adopted.ShouldBe(1);
        page.Value.Progress.Created.ShouldBe(2);
        var items = await SelectionItemsAsync(db, claim.Job.Id);
        items.Single(item => item.ChildJobId == independent.Job.Id).IsOwned.ShouldBeFalse();
        (await selections.StopOwnedAsync(claim.Job.Id)).IsError.ShouldBeFalse();
        (await db.Set<MediaReconciliationDefinitionEntity>().SingleAsync(row => row.JobId == independent.Job.Id)).CancelRequested.ShouldBeFalse();
        foreach (var item in items.Where(item => item.IsOwned))
            (await db.Set<MediaReconciliationDefinitionEntity>().SingleAsync(row => row.JobId == item.ChildJobId)).CancelRequested.ShouldBeTrue();
        (await selections.FinishAsync(claim.Job.Id, MediaReconciliationOutcomeKind.Cancelled, "cancelled")).FirstError.ShouldBe(MediaReconciliationErrors.Changed);
        foreach (var item in items.Where(item => item.IsOwned))
        {
            var child = (await new ClaimedJobRepository<MediaReconciliationJob, MediaReconciliationJobRepository>(new(db, TimeProvider.System), db, TimeProvider.System, new())
                .TryClaimExecutionAsync(item.ChildJobId!.Value, "owned-child")).ShouldNotBeNull();
            using var childFence = JobExecutionFenceScope.Enter(child.Job.Id, child.FenceToken, TimeProvider.System);
            (await Execution(db, StoppedCustody(), PolicyDecision(TargetDecoder, true)).FinishAsync(child.Job.Id, 1, MediaReconciliationOutcomeKind.Cancelled, "cancelled", true)).IsError.ShouldBeFalse();
        }
        var finished = await selections.FinishAsync(claim.Job.Id, MediaReconciliationOutcomeKind.Cancelled, "cancelled");
        finished.IsError.ShouldBeFalse();
        finished.Value.AcknowledgedVersion.ShouldBe(0);
        (await db.Jobs.SingleAsync(row => row.Id == independent.Job.Id)).CompletedAt.ShouldBeNull();
        (await selections.ReadAsync(claim.Job.Id)).ShouldNotBeNull().Progress.Completed.ShouldBe(2);
        // Journal replay precedes a stale fence or invalid terminal payload after an ambiguous commit.
        using var stale = JobExecutionFenceScope.Enter(claim.Job.Id, Guid.NewGuid(), TimeProvider.System);
        (await selections.FinishAsync(claim.Job.Id, (MediaReconciliationOutcomeKind)99, "\n")).Value.ShouldBe(finished.Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Selection_Finish_UsesActualChildOutcomesAndNeverCompletesWhileSubjectsAreActive(bool failedChild)
    {
        var (db, graph) = await SetupAsync();
        await using var owned = db;
        var claim = await AcceptSelectionAsync(db, new MediaReconciliationFilter.RetainedRepresentations(), 1, conversion: false);
        var selections = Selections(db, graph);
        using var fence = JobExecutionFenceScope.Enter(claim.Job.Id, claim.FenceToken, TimeProvider.System);
        (await selections.AdvanceAsync(claim.Job.Id, 64)).IsError.ShouldBeFalse();
        (await selections.FinishAsync(claim.Job.Id, MediaReconciliationOutcomeKind.Completed, "complete")).FirstError.ShouldBe(MediaReconciliationErrors.Changed);
        var item = (await SelectionItemsAsync(db, claim.Job.Id)).ShouldHaveSingleItem();
        var child = (await new ClaimedJobRepository<MediaReconciliationJob, MediaReconciliationJobRepository>(new(db, TimeProvider.System), db, TimeProvider.System, new())
            .TryClaimExecutionAsync(item.ChildJobId!.Value, "selected-child")).ShouldNotBeNull();
        using (JobExecutionFenceScope.Enter(child.Job.Id, child.FenceToken, TimeProvider.System))
            (await Execution(db, StoppedCustody(), PolicyDecision(TargetDecoder, true)).FinishAsync(child.Job.Id, 1, failedChild ? MediaReconciliationOutcomeKind.Failed : MediaReconciliationOutcomeKind.Completed, "child-result", true)).IsError.ShouldBeFalse();
        var finished = await selections.FinishAsync(claim.Job.Id, MediaReconciliationOutcomeKind.Completed, "complete");
        finished.IsError.ShouldBeFalse();
        finished.Value.Kind.ShouldBe(failedChild ? MediaReconciliationOutcomeKind.CompletedWithErrors : MediaReconciliationOutcomeKind.Completed);
        var state = (await selections.ReadAsync(claim.Job.Id)).ShouldNotBeNull();
        state.Progress.Completed.ShouldBe(1);
        state.Progress.Failed.ShouldBe(failedChild ? 1 : 0);
        var restored = (await new MediaReconciliationJobRepository(db, TimeProvider.System).GetByIdAsync(claim.Job.Id)).ShouldNotBeNull();
        restored.ApplyOutcome(finished.Value.Kind, finished.Value.Code, finished.Value.CompletedAt);
        restored.IsTerminal.ShouldBeTrue();
        // Later independent work must not rewrite the selection's committed terminal counts.
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var prior = await db.Set<MediaReconciliationDefinitionEntity>().SingleAsync(row => row.JobId == child.Job.Id);
            (await new MediaReconciliationRequests(db, TimeProvider.System).StageSubjectAsync(new(Guid.NewGuid(),
                new MediaReconciliationSubject.Representation(prior.SubjectId!.Value), false, MediaReconciliationCause.Readiness, null, null))).IsError.ShouldBeFalse();
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        (await selections.ReadAsync(claim.Job.Id)).ShouldBe(state);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Selection_IndependentRetryBeforeSettlement_PreservesCapturedFailureAndCancellationOwnership(bool adopted, bool retryCompleted)
    {
        var (db, graph) = await SetupAsync();
        await using var owned = db;
        int representationId = await db.RomRepresentations.MinAsync(row => row.Id);
        var subject = new MediaReconciliationSubject.Representation(representationId);
        var independent = adopted ? await AcceptExecutionAsync(db, subject, conversion: false) : null;
        var parent = await AcceptSelectionAsync(db, new MediaReconciliationFilter.RetainedRepresentations(), 1, conversion: false);
        var selections = Selections(db, graph);
        using var parentFence = JobExecutionFenceScope.Enter(parent.Job.Id, parent.FenceToken, TimeProvider.System);
        (await selections.AdvanceAsync(parent.Job.Id, 64)).IsError.ShouldBeFalse();
        var item = (await SelectionItemsAsync(db, parent.Job.Id)).ShouldHaveSingleItem();
        item.SubjectKind.ShouldBe("Representation");
        item.SubjectId.ShouldBe(representationId);
        item.IsOwned.ShouldBe(!adopted);
        var child = independent ?? (await new ClaimedJobRepository<MediaReconciliationJob, MediaReconciliationJobRepository>(
            new(db, TimeProvider.System), db, TimeProvider.System, new()).TryClaimExecutionAsync(item.ChildJobId!.Value, "selected-child")).ShouldNotBeNull();
        child.Job.Id.ShouldBe(item.ChildJobId!.Value);
        using (JobExecutionFenceScope.Enter(child.Job.Id, child.FenceToken, TimeProvider.System))
        {
            var execution = Execution(db, StoppedCustody(), PolicyDecision(TargetDecoder, true));
            var demand = (await execution.ReadDemandAsync(child.Job.Id)).ShouldNotBeNull();
            demand.Version.ShouldBe(adopted ? 2 : 1);
            (await execution.FinishAsync(child.Job.Id, demand.Version, MediaReconciliationOutcomeKind.Failed, "selected-child-failed", true)).IsError.ShouldBeFalse();
        }
        Guid actor = await AddActorAsync(db);
        MediaReconciliationAcceptance retry;
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var accepted = await new MediaReconciliationRequests(db, TimeProvider.System).StageRetryAsync(Guid.NewGuid(), child.Job.Id, actor);
            accepted.IsError.ShouldBeFalse();
            retry = accepted.Value;
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        retry.Job.OriginSelectionJobId.ShouldBeNull();
        retry.Job.Id.ShouldNotBe(child.Job.Id);
        if (retryCompleted)
        {
            var claim = (await new ClaimedJobRepository<MediaReconciliationJob, MediaReconciliationJobRepository>(
                new(db, TimeProvider.System), db, TimeProvider.System, new()).TryClaimExecutionAsync(retry.Job.Id, "independent-retry")).ShouldNotBeNull();
            using var retryFence = JobExecutionFenceScope.Enter(claim.Job.Id, claim.FenceToken, TimeProvider.System);
            var execution = Execution(db, StoppedCustody(), PolicyDecision(TargetDecoder, true));
            var demand = (await execution.ReadDemandAsync(claim.Job.Id)).ShouldNotBeNull();
            (await execution.FinishAsync(claim.Job.Id, demand.Version, MediaReconciliationOutcomeKind.Completed, "independent-success", true)).IsError.ShouldBeFalse();
        }
        var state = (await selections.RefreshAsync(parent.Job.Id)).Value;
        state.Progress.Completed.ShouldBe(1);
        state.Progress.Failed.ShouldBe(1);
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var error = await Should.ThrowAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE romd."MediaReconciliationJobs" SET "CompletedCount"=0,"FailedCount"=0 WHERE "JobId"={parent.Job.Id}
                """));
            error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
            await transaction.RollbackAsync();
        }
        var finished = await selections.FinishAsync(parent.Job.Id, MediaReconciliationOutcomeKind.Completed, "selected-work-complete");
        finished.IsError.ShouldBeFalse();
        finished.Value.Kind.ShouldBe(MediaReconciliationOutcomeKind.CompletedWithErrors);
        (await selections.ReadAsync(parent.Job.Id)).ShouldBe(state);
        var retainedItem = (await SelectionItemsAsync(db, parent.Job.Id)).ShouldHaveSingleItem();
        retainedItem.ChildJobId.ShouldBe(child.Job.Id);
        retainedItem.IsOwned.ShouldBe(!adopted);
        var foreign = await db.Set<MediaReconciliationDefinitionEntity>().SingleAsync(row => row.JobId == retry.Job.Id);
        foreign.OriginSelectionJobId.ShouldBeNull();
        foreign.CancelRequested.ShouldBeFalse();
        (await db.Set<MediaReconciliationPinEntity>().CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Selection_StaleFence_RefusesBeforeStagingAnyChildOrProgress()
    {
        var (db, graph) = await SetupAsync();
        await using var owned = db;
        var claim = await AcceptSelectionAsync(db, new MediaReconciliationFilter.AllSubjects(), 10, conversion: false);
        using var fence = JobExecutionFenceScope.Enter(claim.Job.Id, Guid.NewGuid(), TimeProvider.System);
        await Should.ThrowAsync<JobExecutionOwnershipLostException>(() => Selections(db, graph).AdvanceAsync(claim.Job.Id, 64));
        (await db.Jobs.CountAsync()).ShouldBe(1);
        (await SelectionItemsAsync(db, claim.Job.Id)).ShouldBeEmpty();
    }
    [Fact]
    public async Task Selection_ConcurrentParents_AdoptOneChildAndPreserveItsOriginalCancellationOwner()
    {
        var (db, graph) = await SetupAsync();
        await using var owned = db;
        var first = await AcceptSelectionAsync(db, new MediaReconciliationFilter.AllSubjects(), 1, conversion: false);
        var second = await AcceptSelectionAsync(db, new MediaReconciliationFilter.AllSubjects(), 1, conversion: true);
        async Task AdvanceAsync(JobExecutionClaim<MediaReconciliationJob> claim)
        {
            await using var context = _database.CreateContext();
            using var fence = JobExecutionFenceScope.Enter(claim.Job.Id, claim.FenceToken, TimeProvider.System);
            (await Selections(context, graph).AdvanceAsync(claim.Job.Id, 1)).IsError.ShouldBeFalse();
        }
        await Task.WhenAll(AdvanceAsync(first), AdvanceAsync(second));
        var left = (await SelectionItemsAsync(db, first.Job.Id)).ShouldHaveSingleItem();
        var right = (await SelectionItemsAsync(db, second.Job.Id)).ShouldHaveSingleItem();
        left.ChildJobId.ShouldBe(right.ChildJobId);
        left.IsOwned.ShouldBe(!right.IsOwned);
        Guid original = left.IsOwned ? first.Job.Id : second.Job.Id;
        var adopting = left.IsOwned ? second : first;
        (await db.Set<MediaReconciliationDefinitionEntity>().SingleAsync(row => row.JobId == left.ChildJobId)).OriginSelectionJobId.ShouldBe(original);
        var subject = await db.Set<MediaReconciliationSubjectEntity>().SingleAsync();
        subject.RequestedVersion.ShouldBe(2);
        subject.ConversionRequested.ShouldBeTrue();
        using var adopterFence = JobExecutionFenceScope.Enter(adopting.Job.Id, adopting.FenceToken, TimeProvider.System);
        (await Selections(db, graph).StopOwnedAsync(adopting.Job.Id)).IsError.ShouldBeFalse();
        (await db.Set<MediaReconciliationDefinitionEntity>().SingleAsync(row => row.JobId == left.ChildJobId)).CancelRequested.ShouldBeFalse();
        (await Selections(db, graph).FinishAsync(adopting.Job.Id, MediaReconciliationOutcomeKind.Cancelled, "cancelled")).IsError.ShouldBeFalse();
        (await db.Jobs.CountAsync()).ShouldBe(3);
        (await db.JobDispatches.CountAsync()).ShouldBe(3);
        (await db.Set<MediaReconciliationRequestEntity>().CountAsync()).ShouldBe(4);
    }

    [Fact]
    public async Task Selection_HighWatermark_ExcludesLaterSubjectsAndUnownedPhysicalArtifacts()
    {
        var (db, graph) = await SetupAsync();
        await using var owned = db;
        var claim = await AcceptSelectionAsync(db, new MediaReconciliationFilter.AllSubjects(), 10, conversion: false);
        db.OriginalDiscLayouts.Add(new() { Id = 17, SourceJobId = Guid.NewGuid(), SourceRelativePath = "later.cue", EntryPoint = "later.cue", DescriptorFileId = 3 });
        await db.SaveChangesAsync();
        using var fence = JobExecutionFenceScope.Enter(claim.Job.Id, claim.FenceToken, TimeProvider.System);
        var result = await Selections(db, graph).AdvanceAsync(claim.Job.Id, 64);
        result.IsError.ShouldBeFalse();
        result.Value.Progress.Selected.ShouldBe(3);
        result.Value.Progress.EnumerationComplete.ShouldBeTrue();
        (await SelectionItemsAsync(db, claim.Job.Id)).Any(item => item.SubjectKind == "OriginalLayout").ShouldBeFalse();
    }

    [Fact]
    public async Task Selection_NoRetainedOwners_RecordsEmptyCompletionWithoutClaimingOrCollectingTheUnownedGraphs()
    {
        var (db, graph) = await SetupAsync();
        await using var owned = db;
        await db.RomFiles.ExecuteDeleteAsync();
        var claim = await AcceptSelectionAsync(db, new MediaReconciliationFilter.RetainedRepresentations(), 10, conversion: false);
        using var fence = JobExecutionFenceScope.Enter(claim.Job.Id, claim.FenceToken, TimeProvider.System);
        var selections = Selections(db, graph);
        var result = await selections.AdvanceAsync(claim.Job.Id, 64);
        result.IsError.ShouldBeFalse();
        result.Value.Progress.ShouldBe(new(null, 0, 0, 0, 0, 0, 0, true));
        (await selections.FinishAsync(claim.Job.Id, MediaReconciliationOutcomeKind.Completed, "empty")).IsError.ShouldBeFalse();
        (await db.RomRepresentations.CountAsync()).ShouldBe(2);
        (await db.JobDispatches.CountAsync()).ShouldBe(1);
        (await db.Set<MediaReconciliationPinEntity>().CountAsync()).ShouldBe(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Selection_RefreshWhileAnotherChildIsActive_PersistsActualOutcomesWithoutCompletingTheParent(bool failed)
    {
        var (db, graph) = await SetupAsync();
        await using var owned = db;
        var claim = await AcceptSelectionAsync(db, new MediaReconciliationFilter.AllSubjects(), 2, conversion: false);
        var selections = Selections(db, graph);
        using var fence = JobExecutionFenceScope.Enter(claim.Job.Id, claim.FenceToken, TimeProvider.System);
        (await selections.AdvanceAsync(claim.Job.Id, 64)).Value.Progress.EnumerationComplete.ShouldBeTrue();
        var items = await SelectionItemsAsync(db, claim.Job.Id);
        items.Length.ShouldBe(2);
        var child = (await new ClaimedJobRepository<MediaReconciliationJob, MediaReconciliationJobRepository>(new(db, TimeProvider.System), db, TimeProvider.System, new())
            .TryClaimExecutionAsync(items[0].ChildJobId!.Value, "selected-child")).ShouldNotBeNull();
        using (JobExecutionFenceScope.Enter(child.Job.Id, child.FenceToken, TimeProvider.System))
            (await Execution(db, StoppedCustody(), PolicyDecision(TargetDecoder, true)).FinishAsync(child.Job.Id, 1,
                failed ? MediaReconciliationOutcomeKind.Failed : MediaReconciliationOutcomeKind.Completed, "child-result", true)).IsError.ShouldBeFalse();
        (await selections.ReadAsync(claim.Job.Id)).ShouldNotBeNull().Progress.Completed.ShouldBe(1);
        (await db.Set<MediaReconciliationDefinitionEntity>().SingleAsync(row => row.JobId == claim.Job.Id)).CompletedCount.ShouldBe(0);
        var refreshed = await selections.RefreshAsync(claim.Job.Id);
        refreshed.IsError.ShouldBeFalse();
        refreshed.Value.Progress.Completed.ShouldBe(1);
        refreshed.Value.Progress.Failed.ShouldBe(failed ? 1 : 0);
        (await selections.FinishAsync(claim.Job.Id, MediaReconciliationOutcomeKind.Completed, "not-ready")).FirstError.ShouldBe(MediaReconciliationErrors.Changed);
        var restored = (await new MediaReconciliationJobRepository(db, TimeProvider.System).GetByIdAsync(claim.Job.Id)).ShouldNotBeNull();
        restored.SelectionProgress!.Completed.ShouldBe(1);
        restored.SelectionProgress.Failed.ShouldBe(failed ? 1 : 0);
        restored.IsTerminal.ShouldBeFalse();
        (await db.Jobs.SingleAsync(row => row.Id == items[1].ChildJobId)).CompletedAt.ShouldBeNull();
        using (JobExecutionFenceScope.Enter(claim.Job.Id, Guid.NewGuid(), TimeProvider.System))
            await Should.ThrowAsync<JobExecutionOwnershipLostException>(() => selections.RefreshAsync(claim.Job.Id));
        (await selections.ReadAsync(claim.Job.Id)).ShouldBe(refreshed.Value);
    }

}
