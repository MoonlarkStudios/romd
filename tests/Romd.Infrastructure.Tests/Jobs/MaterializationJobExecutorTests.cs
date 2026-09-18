using Microsoft.Extensions.Logging;
using NSubstitute;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Libraries;
using Romd.Domain.Jobs;
using Romd.Domain.Libraries;
using Romd.Infrastructure.Jobs.Executors;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Jobs;

public sealed class MaterializationJobExecutorTests
{
    [Fact]
    public async Task ExecuteAsync_ActivationCommittedBeforeJobCheckpoint_DoesNotMaterializeAgain()
    {
        var service = Substitute.For<ILibraryMaterializationService>();
        var libraries = Substitute.For<ILibraryRepository>();
        libraries.GetByIdAsync(7, Arg.Any<CancellationToken>()).Returns(Library.Rehydrate(
            7, "Arcade", new LibraryConfiguration(), LibraryConfigurationState.Valid,
            null, false, false, DateTimeOffset.UtcNow, 4, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        var executor = new MaterializationJobExecutor(service, libraries,
            Substitute.For<ILogger<MaterializationJobExecutor>>());
        var job = MaterializationJob.Create(7, "Arcade");
        job.Start("original-delivery");
        job.SetHangfireJobId("redelivery");
        await executor.ExecuteAsync(job, new JobContext(null, _ => Task.CompletedTask, default));
        job.Complete();
        job.PhaseEnum.ShouldBe(MaterializationPhase.Completed);
        await service.DidNotReceive().MaterializeAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_MaterializationResult_TracksTitleProgressWithNamedResultFields()
    {
        var materializationService = Substitute.For<ILibraryMaterializationService>();
        var libraryRepo = Substitute.For<ILibraryRepository>();
        var executor = new MaterializationJobExecutor(
            materializationService,
            libraryRepo,
            Substitute.For<ILogger<MaterializationJobExecutor>>());
        var job = MaterializationJob.Create(7, "Arcade");
        var library = Library.Rehydrate(
            7,
            "Arcade",
            new LibraryConfiguration(),
            LibraryConfigurationState.Valid,
            configurationError: null,
            isDefault: false,
            needsMaterialization: true,
            lastMaterializedAt: null,
            itemCount: 0,
            createdAt: DateTimeOffset.UtcNow,
            updatedAt: null);
        var context = new JobContext(
            workspacePath: null,
            _ => Task.CompletedTask,
            CancellationToken.None);

        job.Start("hangfire");
        libraryRepo.GetByIdAsync(7, Arg.Any<CancellationToken>())
            .Returns(library);
        materializationService.MaterializeAsync(7, Arg.Any<CancellationToken>())
            .Returns(new MaterializationResult(
                IncludedReleaseCount: 4,
                ExcludedTitleCount: 2,
                TotalTitleCount: 5));

        await executor.ExecuteAsync(job, context);

        job.TotalTitles.ShouldBe(5);
        job.IncludedCount.ShouldBe(3);
        job.ExcludedCount.ShouldBe(2);
        job.ProcessedCount.ShouldBe(5);

        // JobRunner completes the job after the executor; a materialized run stays Completed.
        job.Complete();
        job.PhaseEnum.ShouldBe(MaterializationPhase.Completed);
        job.HasErrors.ShouldBeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_DeferredResult_JobCompletesIntoDeferredPhaseWithZeroCounts()
    {
        var materializationService = Substitute.For<ILibraryMaterializationService>();
        var libraryRepo = Substitute.For<ILibraryRepository>();
        var executor = new MaterializationJobExecutor(
            materializationService,
            libraryRepo,
            Substitute.For<ILogger<MaterializationJobExecutor>>());
        var job = MaterializationJob.Create(7, "Arcade");
        var library = Library.Rehydrate(
            7,
            "Arcade",
            new LibraryConfiguration(),
            LibraryConfigurationState.Valid,
            configurationError: null,
            isDefault: false,
            needsMaterialization: true,
            lastMaterializedAt: null,
            itemCount: 0,
            createdAt: DateTimeOffset.UtcNow,
            updatedAt: null);
        var context = new JobContext(
            workspacePath: null,
            _ => Task.CompletedTask,
            CancellationToken.None);

        job.Start("hangfire");
        libraryRepo.GetByIdAsync(7, Arg.Any<CancellationToken>())
            .Returns(library);
        materializationService.MaterializeAsync(7, Arg.Any<CancellationToken>())
            .Returns(MaterializationResult.Deferred());

        await executor.ExecuteAsync(job, context);

        // JobRunner completes the job after the executor; the deferral marker routes it to Deferred.
        job.Complete();

        job.PhaseEnum.ShouldBe(MaterializationPhase.Deferred);
        job.IsTerminal.ShouldBeTrue();
        job.HasErrors.ShouldBeFalse();
        job.TotalTitles.ShouldBe(0);
        job.IncludedCount.ShouldBe(0);
        job.ExcludedCount.ShouldBe(0);
        job.ProcessedCount.ShouldBe(0);
    }
}
