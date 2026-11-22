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

        job.SetCurrentItem(library.Name);
        await context.CheckpointAsync(ct);

        logger.LogInformation(
            "Executing materialization job for library '{Name}' (ID: {Id})",
            library.Name, library.Id);

        await materializationService.PublishForJobAsync(job, ct);
    }
}
