using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Libraries;
using Romd.Domain.Jobs;

namespace Romd.Infrastructure.Libraries;

public sealed class LibraryMaterializationScheduler(
    ILibraryRepository libraryRepo,
    IMaterializationJobRepository jobRepo,
    ILogger<LibraryMaterializationScheduler> logger) : ILibraryMaterializationScheduler
{
    public async Task<bool> EnqueueIfNeededAsync(int libraryId, CancellationToken ct = default)
    {
        var library = await libraryRepo.GetByIdAsync(libraryId, ct);
        if (library is null || !library.NeedsMaterialization || !library.HasValidConfiguration)
        {
            return false;
        }

        var job = MaterializationJob.Create(libraryId, library.Name);
        if (!await jobRepo.TryAddIfNoActiveForLibraryAsync(job, ct))
        {
            logger.LogDebug(
                "Skipping materialization for library '{Name}' because a job is already active",
                library.Name);
            return false;
        }

        logger.LogInformation(
            "Enqueued materialization job {JobId} for library '{Name}'",
            job.Id,
            library.Name);

        return true;
    }
}
