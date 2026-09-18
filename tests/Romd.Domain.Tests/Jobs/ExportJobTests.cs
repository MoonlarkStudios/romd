using Romd.Domain.Jobs;
using Shouldly;
using Xunit;

namespace Romd.Domain.Tests.Jobs;

public class ExportJobTests
{
    [Fact]
    public void Create_SetsInitialState()
    {
        var userId = Guid.NewGuid();
        var job = ExportJob.CreateLibrary(3, 4, userId);

        job.ScopeKind.ShouldBe(ExportScopeKind.Library);
        job.LibraryId.ShouldBe(3);
        job.AuthorizedMaterializationGeneration.ShouldBe(4);
        job.CreatedByUserId.ShouldBe(userId);
        job.PhaseEnum.ShouldBe(ExportPhase.Pending);
        job.Phase.ShouldBe("Pending");
        job.SourceFilename.ShouldBe("Library Export");
        job.TotalTitles.ShouldBe(0);
        job.ProcessedTitles.ShouldBe(0);
        job.TotalFiles.ShouldBe(0);
        job.ProcessedFiles.ShouldBe(0);
        job.SkippedFiles.ShouldBe(0);
        job.ExportPath.ShouldBeNull();
        job.IsTerminal.ShouldBeFalse();
        job.ProgressPercent.ShouldBe(0);
    }

    [Fact]
    public void CreateAllCatalog_SetsExplicitScopeWithoutLibraryCoordinates()
    {
        var job = ExportJob.CreateAllCatalog();

        job.ScopeKind.ShouldBe(ExportScopeKind.AllCatalog);
        job.LibraryId.ShouldBeNull();
        job.AuthorizedMaterializationGeneration.ShouldBeNull();
    }

    [Fact]
    public void Start_TransitionsToCollecting()
    {
        var job = ExportJob.CreateAllCatalog();

        job.Start("hangfire-1");

        job.PhaseEnum.ShouldBe(ExportPhase.Collecting);
        job.StartedAt.ShouldNotBeNull();
    }

    [Fact]
    public void BeginPackaging_TransitionsFromCollecting()
    {
        var job = CreateCollectingJob();

        job.BeginPackaging();

        job.PhaseEnum.ShouldBe(ExportPhase.Packaging);
    }

    [Fact]
    public void BeginPackaging_FromPending_Throws()
    {
        var job = ExportJob.CreateAllCatalog();

        Should.Throw<InvalidOperationException>(() => job.BeginPackaging());
    }

    [Fact]
    public void BeginStoring_TransitionsFromPackaging()
    {
        var job = CreatePackagingJob();

        job.BeginStoring();

        job.PhaseEnum.ShouldBe(ExportPhase.Storing);
    }

    [Fact]
    public void BeginStoring_FromCollecting_Throws()
    {
        var job = CreateCollectingJob();

        Should.Throw<InvalidOperationException>(() => job.BeginStoring());
    }

    [Fact]
    public void SetTotals_SetsValues()
    {
        var job = ExportJob.CreateAllCatalog();

        job.SetTotals(totalTitles: 5, totalFiles: 20);

        job.TotalTitles.ShouldBe(5);
        job.TotalFiles.ShouldBe(20);
    }

    [Fact]
    public void RecordTitleProcessed_IncrementsCounter()
    {
        var job = CreatePackagingJob();

        job.RecordTitleProcessed();
        job.RecordTitleProcessed();

        job.ProcessedTitles.ShouldBe(2);
    }

    [Fact]
    public void RecordTitleProcessed_WrongPhase_Throws()
    {
        var job = CreateCollectingJob();

        Should.Throw<InvalidOperationException>(() => job.RecordTitleProcessed());
    }

    [Fact]
    public void RecordFileProcessed_IncrementsCounter()
    {
        var job = CreatePackagingJob();

        job.RecordFileProcessed();
        job.RecordFileProcessed();

        job.ProcessedFiles.ShouldBe(2);
        job.SkippedFiles.ShouldBe(0);
    }

    [Fact]
    public void RecordFileSkipped_IncrementsBothCounters()
    {
        var job = CreatePackagingJob();

        job.RecordFileSkipped();

        job.ProcessedFiles.ShouldBe(1);
        job.SkippedFiles.ShouldBe(1);
    }

    [Fact]
    public void SetExportPath_SetsValue()
    {
        var job = ExportJob.CreateAllCatalog();

        job.SetExportPath("/data/exports/test.zip");

        job.ExportPath.ShouldBe("/data/exports/test.zip");
    }

    [Fact]
    public void ProgressPercent_Pending_ReturnsZero()
    {
        var job = ExportJob.CreateAllCatalog();

        job.ProgressPercent.ShouldBe(0);
    }

    [Fact]
    public void ProgressPercent_Collecting_ReturnsFive()
    {
        var job = CreateCollectingJob();

        job.ProgressPercent.ShouldBe(5);
    }

    [Fact]
    public void ProgressPercent_Packaging_CalculatesCorrectly()
    {
        var job = CreatePackagingJob();
        job.SetTotals(totalTitles: 2, totalFiles: 10);

        job.ProgressPercent.ShouldBe(5); // 5 + 85 * 0/10

        job.RecordFileProcessed(); // 1/10
        job.ProgressPercent.ShouldBe(13.5); // 5 + 85 * 1/10

        for (var i = 0; i < 9; i++)
            job.RecordFileProcessed(); // 10/10
        job.ProgressPercent.ShouldBe(90); // 5 + 85 * 10/10
    }

    [Fact]
    public void ProgressPercent_Packaging_ZeroFiles_ReturnsFifty()
    {
        var job = CreatePackagingJob();
        job.SetTotals(totalTitles: 0, totalFiles: 0);

        job.ProgressPercent.ShouldBe(50);
    }

    [Fact]
    public void ProgressPercent_Storing_ReturnsNinetyFive()
    {
        var job = CreatePackagingJob();
        job.BeginStoring();

        job.ProgressPercent.ShouldBe(95);
    }

    [Fact]
    public void ProgressPercent_Terminal_ReturnsHundred()
    {
        var job = CreatePackagingJob();
        job.Complete();

        job.ProgressPercent.ShouldBe(100);
    }

    [Fact]
    public void Complete_WithoutErrors_SetsCompleted()
    {
        var job = CreatePackagingJob();
        job.SetTotals(1, 1);
        job.RecordFileProcessed();
        job.RecordTitleProcessed();

        job.Complete();

        job.PhaseEnum.ShouldBe(ExportPhase.Completed);
        job.IsTerminal.ShouldBeTrue();
        job.CompletedAt.ShouldNotBeNull();
    }

    [Fact]
    public void Complete_WithErrors_SetsCompletedWithErrors()
    {
        var job = CreatePackagingJob();
        job.RecordError("file.rom", "Not found");

        job.Complete();

        job.PhaseEnum.ShouldBe(ExportPhase.CompletedWithErrors);
        job.IsTerminal.ShouldBeTrue();
    }

    [Fact]
    public void Complete_FromCollecting_Succeeds()
    {
        var job = CreateCollectingJob();

        job.Complete();

        job.PhaseEnum.ShouldBe(ExportPhase.Completed);
        job.IsTerminal.ShouldBeTrue();
    }

    [Fact]
    public void Complete_FromStoring_Succeeds()
    {
        var job = CreatePackagingJob();
        job.BeginStoring();

        job.Complete();

        job.PhaseEnum.ShouldBe(ExportPhase.Completed);
    }

    [Fact]
    public void Fail_SetsFailedPhase()
    {
        var job = CreateCollectingJob();

        job.Fail("Disk full");

        job.PhaseEnum.ShouldBe(ExportPhase.Failed);
        job.IsTerminal.ShouldBeTrue();
        job.Errors.Count.ShouldBe(1);
    }

    [Fact]
    public void Cancel_SetsCancelledPhase()
    {
        var job = CreatePackagingJob();

        job.Cancel();

        job.PhaseEnum.ShouldBe(ExportPhase.Cancelled);
        job.IsTerminal.ShouldBeTrue();
    }

    [Fact]
    public void Rehydrate_RestoresAllState()
    {
        var id = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var errors = new List<JobError> { new("item", "msg", DateTimeOffset.UtcNow) };
        var created = DateTimeOffset.UtcNow.AddHours(-1);
        var started = DateTimeOffset.UtcNow.AddMinutes(-30);
        var completed = DateTimeOffset.UtcNow;

        var job = ExportJob.Rehydrate(
            id: id,
            correlationId: correlationId,
            sourceFilename: "Library Export",
            platformId: null,
            phase: ExportPhase.Completed,
            hangfireJobId: "hf-1",
            scopeKind: ExportScopeKind.Library,
            libraryId: 7,
            authorizedMaterializationGeneration: 11,
            totalTitles: 10,
            processedTitles: 10,
            totalFiles: 50,
            processedFiles: 48,
            skippedFiles: 2,
            exportPath: "/exports/test.zip",
            currentItem: null,
            errors: errors,
            createdAt: created,
            startedAt: started,
            completedAt: completed,
            isArchived: false,
            archivedAt: null,
            createdByUserId: userId);

        job.Id.ShouldBe(id);
        job.CorrelationId.ShouldBe(correlationId);
        job.PhaseEnum.ShouldBe(ExportPhase.Completed);
        job.ScopeKind.ShouldBe(ExportScopeKind.Library);
        job.LibraryId.ShouldBe(7);
        job.AuthorizedMaterializationGeneration.ShouldBe(11);
        job.TotalTitles.ShouldBe(10);
        job.ProcessedTitles.ShouldBe(10);
        job.TotalFiles.ShouldBe(50);
        job.ProcessedFiles.ShouldBe(48);
        job.SkippedFiles.ShouldBe(2);
        job.ExportPath.ShouldBe("/exports/test.zip");
        job.Errors.Count.ShouldBe(1);
        job.CreatedByUserId.ShouldBe(userId);
        job.IsTerminal.ShouldBeTrue();
    }

    private static ExportJob CreateCollectingJob()
    {
        var job = ExportJob.CreateAllCatalog();
        job.Start("hangfire-1");
        return job;
    }

    private static ExportJob CreatePackagingJob()
    {
        var job = CreateCollectingJob();
        job.BeginPackaging();
        return job;
    }
}
