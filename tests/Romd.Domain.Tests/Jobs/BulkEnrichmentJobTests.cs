using Romd.Domain.Catalog;
using Romd.Domain.Jobs;
using Shouldly;
using Xunit;

namespace Romd.Domain.Tests.Jobs;

public class BulkEnrichmentJobTests
{
    [Fact]
    public void Create_SetsInitialState()
    {
        var job = BulkEnrichmentJob.Create(platformId: 5, platformName: "SNES", createdByUserId: Guid.NewGuid());

        job.PlatformId.ShouldBe(5);
        job.PhaseEnum.ShouldBe(BulkEnrichmentPhase.Pending);
        job.Phase.ShouldBe("Pending");
        job.TotalTitles.ShouldBe(0);
        job.ProcessedCount.ShouldBe(0);
        job.EnrichedCount.ShouldBe(0);
        job.NotFoundCount.ShouldBe(0);
        job.FailedCount.ShouldBe(0);
        job.SkippedCount.ShouldBe(0);
        job.IsTerminal.ShouldBeFalse();
        job.ProgressPercent.ShouldBe(0);
    }

    [Fact]
    public void Create_DefaultsToTrackedScope()
    {
        var job = BulkEnrichmentJob.Create(platformId: 5, platformName: "SNES");

        job.Scope.ShouldBe(EnrichmentScope.Tracked);
    }

    [Fact]
    public void Create_HonorsExplicitScope()
    {
        var job = BulkEnrichmentJob.Create(platformId: 5, platformName: "SNES", scope: EnrichmentScope.All);

        job.Scope.ShouldBe(EnrichmentScope.All);
    }

    [Fact]
    public void Start_TransitionsToPending_ToEnriching()
    {
        var job = BulkEnrichmentJob.Create(platformId: 5, platformName: "SNES");

        job.Start("hangfire-1");

        job.PhaseEnum.ShouldBe(BulkEnrichmentPhase.Enriching);
        job.StartedAt.ShouldNotBeNull();
    }

    [Fact]
    public void RecordEnriched_IncrementsCounters()
    {
        var job = CreateStartedJob();
        job.SetTotalTitles(10);

        job.RecordEnriched();
        job.RecordEnriched();

        job.ProcessedCount.ShouldBe(2);
        job.EnrichedCount.ShouldBe(2);
        job.NotFoundCount.ShouldBe(0);
    }

    [Fact]
    public void RecordNotFound_IncrementsCounters()
    {
        var job = CreateStartedJob();
        job.SetTotalTitles(10);

        job.RecordNotFound();

        job.ProcessedCount.ShouldBe(1);
        job.NotFoundCount.ShouldBe(1);
        job.EnrichedCount.ShouldBe(0);
    }

    [Fact]
    public void RecordFailed_IncrementsCounters_AddsError()
    {
        var job = CreateStartedJob();
        job.SetTotalTitles(10);

        job.RecordFailed(42, "Super Mario World", "API timeout", JobErrorReason.ProviderError);

        job.ProcessedCount.ShouldBe(1);
        job.FailedCount.ShouldBe(1);
        job.Errors.Count.ShouldBe(1);
        job.Errors[0].Item.ShouldBe("Super Mario World");
        job.Errors[0].Message.ShouldBe("API timeout");
        job.Errors[0].EntityId.ShouldBe(42);
        job.Errors[0].Reason.ShouldBe(JobErrorReason.ProviderError);
    }

    [Fact]
    public void RecordSkipped_IncrementsCounters()
    {
        var job = CreateStartedJob();
        job.SetTotalTitles(10);

        job.RecordSkipped();

        job.ProcessedCount.ShouldBe(1);
        job.SkippedCount.ShouldBe(1);
    }

    [Fact]
    public void ProgressPercent_CalculatedCorrectly()
    {
        var job = CreateStartedJob();
        job.SetTotalTitles(10);

        job.ProgressPercent.ShouldBe(0); // 0/10

        job.RecordEnriched(); // 1/10
        job.ProgressPercent.ShouldBe(10);

        job.RecordNotFound(); // 2/10
        job.ProgressPercent.ShouldBe(20);

        job.RecordFailed(1, "test", "err", JobErrorReason.ProviderError); // 3/10
        job.ProgressPercent.ShouldBe(30);
    }

    [Fact]
    public void ProgressPercent_ZeroTotal_Returns50()
    {
        var job = CreateStartedJob();
        job.SetTotalTitles(0);

        job.ProgressPercent.ShouldBe(50);
    }

    [Fact]
    public void Complete_WithErrors_SetsCompletedWithErrors()
    {
        var job = CreateStartedJob();
        job.SetTotalTitles(2);
        job.RecordEnriched();
        job.RecordFailed(1, "Game", "Error", JobErrorReason.ProviderError);

        job.Complete();

        job.PhaseEnum.ShouldBe(BulkEnrichmentPhase.CompletedWithErrors);
        job.IsTerminal.ShouldBeTrue();
        job.ProgressPercent.ShouldBe(100);
    }

    [Fact]
    public void Complete_WithoutErrors_SetsCompleted()
    {
        var job = CreateStartedJob();
        job.SetTotalTitles(2);
        job.RecordEnriched();
        job.RecordNotFound();

        job.Complete();

        job.PhaseEnum.ShouldBe(BulkEnrichmentPhase.Completed);
        job.IsTerminal.ShouldBeTrue();
    }

    [Fact]
    public void Fail_SetsFailedPhase()
    {
        var job = CreateStartedJob();

        job.Fail("Something went wrong");

        job.PhaseEnum.ShouldBe(BulkEnrichmentPhase.Failed);
        job.IsTerminal.ShouldBeTrue();
    }

    [Fact]
    public void Cancel_SetsCancelledPhase()
    {
        var job = CreateStartedJob();

        job.Cancel();

        job.PhaseEnum.ShouldBe(BulkEnrichmentPhase.Cancelled);
        job.IsTerminal.ShouldBeTrue();
    }

    [Fact]
    public void RecordEnriched_InPendingPhase_Throws()
    {
        var job = BulkEnrichmentJob.Create(platformId: 5, platformName: "SNES");

        Should.Throw<InvalidOperationException>(() => job.RecordEnriched());
    }

    [Fact]
    public void MixedCounters_AllTrackIndependently()
    {
        var job = CreateStartedJob();
        job.SetTotalTitles(5);

        job.RecordEnriched();
        job.RecordNotFound();
        job.RecordFailed(1, "g1", "e1", JobErrorReason.ProviderError);
        job.RecordSkipped();
        job.RecordEnriched();

        job.ProcessedCount.ShouldBe(5);
        job.EnrichedCount.ShouldBe(2);
        job.NotFoundCount.ShouldBe(1);
        job.FailedCount.ShouldBe(1);
        job.SkippedCount.ShouldBe(1);
    }

    private static BulkEnrichmentJob CreateStartedJob()
    {
        var job = BulkEnrichmentJob.Create(platformId: 5, platformName: "SNES");
        job.Start("hangfire-1");
        return job;
    }
}
