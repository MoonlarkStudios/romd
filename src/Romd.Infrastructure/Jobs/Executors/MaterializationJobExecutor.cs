using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Libraries;
using Romd.Domain.Jobs;

namespace Romd.Infrastructure.Jobs.Executors;

public sealed class MaterializationJobExecutor(
    ILibraryMaterializationService materializationService,
    ILibraryRepository libraryRepo,
    ILogger<MaterializationJobExecutor> logger) : IJobExecutor<MaterializationJob>
{
    public async Task ExecuteAsync(MaterializationJob job, JobContext context)
    {
        var ct = context.CancellationToken;

        var library = await libraryRepo.GetByIdAsync(job.LibraryId, ct);
        if (library is null)
        {
            throw new InvalidOperationException($"Library {job.LibraryId} not found");
        }

        // Projection activation and its flag clear commit together. A process can die after
        // that commit but before the job's final checkpoint; replay must not advance the
        // library generation a second time for already-converged work.
        if (!library.NeedsMaterialization)
        {
            logger.LogInformation("Library {LibraryId} is already materialized; completing job {JobId}",
                library.Id, job.Id);
            return;
        }

        job.SetCurrentItem(library.Name);
        await context.CheckpointAsync(ct);

        logger.LogInformation(
            "Executing materialization job for library '{Name}' (ID: {Id})",
            library.Name, library.Id);

        var result = await materializationService.MaterializeAsync(library.Id, ct);

        if (result.Outcome is MaterializationOutcome.Deferred)
        {
            job.MarkDeferred();
            logger.LogInformation(
                "Materialization job deferred for library '{Name}' (ID: {Id}); catalog projection rebuild pending",
                library.Name,
                library.Id);
            return;
        }

        job.SetTotalTitles(result.TotalTitleCount);
        int includedTitleCount = result.TotalTitleCount - result.ExcludedTitleCount;
        for (int i = 0; i < includedTitleCount; i++)
            job.RecordIncluded();
        for (int i = 0; i < result.ExcludedTitleCount; i++)
            job.RecordExcluded();

        await context.CheckpointAsync(CancellationToken.None);

        logger.LogInformation(
            "Materialization job complete for library '{Name}': {IncludedReleaseCount} releases included; {ExcludedTitleCount} titles excluded from {TotalTitleCount} total titles",
            library.Name,
            result.IncludedReleaseCount,
            result.ExcludedTitleCount,
            result.TotalTitleCount);
    }
}
