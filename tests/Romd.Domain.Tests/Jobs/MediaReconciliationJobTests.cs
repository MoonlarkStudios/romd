using Romd.Domain.Jobs;
using Romd.Domain.Source.Rom;
using Shouldly;
using Xunit;

namespace Romd.Domain.Tests.Jobs;

public sealed class MediaReconciliationJobTests
{
    [Fact]
    public void Subject_EqualIdsInDifferentNamespaces_NeverAliasAndKeepStableOrdering()
    {
        var rom = new MediaReconciliationSubject.LogicalRom(7);
        var layout = new MediaReconciliationSubject.OriginalLayout(7);
        var representation = new MediaReconciliationSubject.Representation(7);
        new HashSet<MediaReconciliationSubject> { rom, layout, representation, new MediaReconciliationSubject.LogicalRom(7) }.Count.ShouldBe(3);
        rom.CompareTo(layout).ShouldBeLessThan(0);
        layout.CompareTo(representation).ShouldBeLessThan(0);
        new MediaReconciliationSubject.LogicalRom(8).CompareTo(rom).ShouldBeGreaterThan(0);
    }

    [Fact]
    public void ActiveSubject_ExposesJournalOutcomeInsteadOfDirectTerminalTransitions()
    {
        var job = Subject();
        job.Start("delivery", startedAt: DateTimeOffset.UtcNow);
        job.SetPhase(MediaReconciliationPhase.Verifying);
        typeof(MediaReconciliationJob).GetMethod(nameof(DirectLifecycleJob.Complete)).ShouldBeNull();
        typeof(MediaReconciliationJob).GetMethod(nameof(DirectLifecycleJob.Fail)).ShouldBeNull();
        typeof(MediaReconciliationJob).GetMethod(nameof(CancellableJob.Cancel)).ShouldBeNull();
        job.IsTerminal.ShouldBeFalse();
        job.PhaseEnum.ShouldBe(MediaReconciliationPhase.Verifying);
    }

    [Theory]
    [InlineData(MediaReconciliationOutcomeKind.Completed)]
    [InlineData(MediaReconciliationOutcomeKind.Superseded)]
    [InlineData(MediaReconciliationOutcomeKind.Failed)]
    [InlineData(MediaReconciliationOutcomeKind.Cancelled)]
    public void ApplyOutcome_ReplayedJournal_PreservesIdentityAndRejectsChangedTerminalFacts(MediaReconciliationOutcomeKind kind)
    {
        var job = Subject();
        var at = DateTimeOffset.UtcNow;
        job.ApplyOutcome(kind, "recorded-result", at);
        job.ApplyOutcome(kind, "recorded-result", at);
        job.IsTerminal.ShouldBeTrue();
        job.CompletedAt.ShouldBe(at);
        job.ProgressPercent.ShouldBe(100);
        Should.Throw<InvalidOperationException>(() => job.ApplyOutcome(kind, "different-result", at));
        Should.Throw<InvalidOperationException>(() => job.ApplyOutcome(kind, "recorded-result", at.AddSeconds(1)));
        Should.Throw<InvalidOperationException>(() => job.SetPhase(MediaReconciliationPhase.Planning));
    }

    [Fact]
    public void Selection_SkippedPrefixExceedsLimit_StillAdoptsRequestedWorkWithoutChangingOriginalOwnership()
    {
        var job = Selection(new(10003, 9, 12, 0), 2);
        job.Start("delivery", startedAt: DateTimeOffset.UtcNow);
        job.ApplySelectionProgress(new(new MediaReconciliationSubject.LogicalRom(10003), 10003, 1, 1, 10001, 1, 0, true));
        job.SelectionProgress!.Adopted.ShouldBe(1);
        job.OriginSelectionJobId.ShouldBeNull();
        Should.Throw<InvalidOperationException>(() => job.ApplyOutcome(MediaReconciliationOutcomeKind.Completed, "selected", DateTimeOffset.UtcNow));
        job.ApplySelectionProgress(new(new MediaReconciliationSubject.LogicalRom(10003), 10003, 1, 1, 10001, 2, 1, true));
        job.ApplyOutcome(MediaReconciliationOutcomeKind.CompletedWithErrors, "selected", DateTimeOffset.UtcNow);
        job.PhaseEnum.ShouldBe(MediaReconciliationPhase.CompletedWithErrors);
    }

    [Fact]
    public void Selection_HighWatermarksAcrossNamespaces_UsesLongCountersWithoutOverflow()
    {
        var job = Selection(new(int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue), 4L * int.MaxValue);
        long count = 4L * int.MaxValue;
        job.ApplySelectionProgress(new(new MediaReconciliationSubject.CatalogCdLayout(int.MaxValue), count, count - 1, 1, 0, count, 0, true));
        job.ApplyOutcome(MediaReconciliationOutcomeKind.Completed, "selected", DateTimeOffset.UtcNow);
        job.SelectionProgress!.Selected.ShouldBe(count);
    }

    [Theory]
    [InlineData("cursor")]
    [InlineData("created")]
    [InlineData("selected")]
    [InlineData("enumeration")]
    [InlineData("bounds")]
    [InlineData("balance")]
    public void Selection_StaleOrInvalidSnapshot_RefusesStructuralRegression(string mutation)
    {
        var job = Selection(new(20, 30, 40, 0), 4);
        job.ApplySelectionProgress(new(new MediaReconciliationSubject.OriginalLayout(10), 4, 1, 2, 1, 2, 1, true));
        var next = new MediaReconciliationSelectionProgress(
            mutation == "cursor" ? new MediaReconciliationSubject.LogicalRom(20) :
            mutation == "bounds" ? new MediaReconciliationSubject.OriginalLayout(31) : new MediaReconciliationSubject.OriginalLayout(10),
            mutation == "selected" ? 3 : 4, mutation == "created" ? 0 : 1, 2,
            mutation == "balance" ? 0 : mutation is "selected" or "created" ? 0 : 1, 2, 1, mutation != "enumeration");
        Should.Throw<InvalidOperationException>(() => job.ApplySelectionProgress(next));
        job.SelectionProgress!.Selected.ShouldBe(4);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Selection_IndependentRetry_CannotRegressCapturedChildOutcomeCounts(bool completion)
    {
        var job = Selection(new(20, 0, 0, 0), 4);
        job.ApplySelectionProgress(new(new MediaReconciliationSubject.LogicalRom(20), 4, 1, 2, 1, 2, 1, true));
        Should.Throw<InvalidOperationException>(() => job.ApplySelectionProgress(
            new(new MediaReconciliationSubject.LogicalRom(20), 4, 1, 2, 1, completion ? 1 : 2, completion ? 1 : 0, true)));
        job.SelectionProgress!.Completed.ShouldBe(2);
        job.SelectionProgress.Failed.ShouldBe(1);
        Should.Throw<InvalidOperationException>(() => job.ApplyOutcome(MediaReconciliationOutcomeKind.Completed, "selected", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void SetPhase_RequestModes_KeepSelectionOutOfMediaExecutionAndSubjectsOutOfEnumeration()
    {
        var subject = Subject();
        var selection = Selection(new(1, 1, 1, 0), 1);
        Should.Throw<InvalidOperationException>(() => subject.SetPhase(MediaReconciliationPhase.Selecting));
        Should.Throw<InvalidOperationException>(() => selection.SetPhase(MediaReconciliationPhase.Encoding));
        Should.Throw<InvalidOperationException>(() => subject.ApplySelectionProgress(new(null, 0, 0, 0, 0, 0, 0, false)));
        Should.Throw<ArgumentException>(() => new MediaReconciliationRequest.SelectionWork(new MediaReconciliationFilter.RetainedRepresentations(), new(0, 0, 4, 0), 4, true));
    }

    private static MediaReconciliationJob Subject() => MediaReconciliationJob.Create(Guid.NewGuid(),
        new MediaReconciliationRequest.SubjectWork(new MediaReconciliationSubject.Representation(7)), "retained archive",
        null, Guid.NewGuid(), DateTimeOffset.UtcNow);
    private static MediaReconciliationJob Selection(MediaReconciliationBounds bounds, long limit) => MediaReconciliationJob.Create(Guid.NewGuid(),
        new MediaReconciliationRequest.SelectionWork(new MediaReconciliationFilter.AllSubjects(), bounds, limit, false), "selection",
        null, Guid.NewGuid(), DateTimeOffset.UtcNow);
}
