using Romd.Domain.Jobs;
using Shouldly;
using Xunit;

namespace Romd.Domain.Tests.Jobs;

public sealed class MaterializationJobTests
{
    [Fact]
    public void Complete_WithoutDeferral_LandsInCompleted()
    {
        var job = MaterializationJob.Create(7, "Arcade");
        job.Start("hangfire-1");

        job.Complete();

        job.PhaseEnum.ShouldBe(MaterializationPhase.Completed);
        job.IsTerminal.ShouldBeTrue();
        job.HasErrors.ShouldBeFalse();
    }

    [Fact]
    public void Complete_AfterMarkDeferred_LandsInDeferredTerminalWithoutErrors()
    {
        var job = MaterializationJob.Create(7, "Arcade");
        job.Start("hangfire-1");

        job.MarkDeferred();
        job.Complete();

        job.PhaseEnum.ShouldBe(MaterializationPhase.Deferred);
        job.Phase.ShouldBe("Deferred");
        job.IsTerminal.ShouldBeTrue();
        job.HasErrors.ShouldBeFalse();
        job.TotalTitles.ShouldBe(0);
        job.ProcessedCount.ShouldBe(0);
        job.IncludedCount.ShouldBe(0);
        job.ExcludedCount.ShouldBe(0);
        job.CompletedAt.ShouldNotBeNull();
    }

    [Fact]
    public void MarkDeferred_FromPending_Throws()
    {
        var job = MaterializationJob.Create(7, "Arcade");

        Should.Throw<InvalidOperationException>(job.MarkDeferred);
    }

    [Fact]
    public void MarkDeferred_AfterComplete_Throws()
    {
        var job = MaterializationJob.Create(7, "Arcade");
        job.Start("hangfire-1");
        job.Complete();

        Should.Throw<InvalidOperationException>(job.MarkDeferred);
    }

    [Fact]
    public void Rehydrate_DeferredPhase_IsTerminalWithoutErrors()
    {
        var job = MaterializationJob.Rehydrate(
            id: Guid.NewGuid(),
            correlationId: Guid.NewGuid(),
            sourceFilename: "Arcade",
            platformId: null,
            phase: MaterializationPhase.Deferred,
            hangfireJobId: "hangfire-1",
            libraryId: 7,
            totalTitles: 0,
            processedCount: 0,
            includedCount: 0,
            excludedCount: 0,
            currentItem: null,
            errors: [],
            createdAt: DateTimeOffset.UtcNow,
            startedAt: DateTimeOffset.UtcNow,
            completedAt: DateTimeOffset.UtcNow,
            isArchived: false,
            archivedAt: null,
            createdByUserId: null);

        job.PhaseEnum.ShouldBe(MaterializationPhase.Deferred);
        job.IsTerminal.ShouldBeTrue();
        job.HasErrors.ShouldBeFalse();
        job.ProgressPercent.ShouldBe(100);
    }
}
