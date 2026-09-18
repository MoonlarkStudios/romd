using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Export;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Domain.Jobs;
using Romd.Persistence.Repositories;

namespace Romd.Infrastructure.Jobs;

public sealed class ExportScheduler : IExportScheduler
{
    private readonly ExportJobRepository _exportJobRepo;
    private readonly IJobRepository<ExportJob> _genericJobRepo;
    private readonly ILogger<ExportScheduler> _logger;
    private readonly IUnitOfWork _unitOfWork;

    public ExportScheduler(
        ExportJobRepository exportJobRepo,
        IJobRepository<ExportJob> genericJobRepo,
        IUnitOfWork unitOfWork,
        ILogger<ExportScheduler> logger)
    {
        _exportJobRepo = exportJobRepo;
        _genericJobRepo = genericJobRepo;
        _unitOfWork = unitOfWork;
        _logger = logger;
    }

    public async Task<Guid> EnqueueLibraryExportAsync(
        ExportScope scope,
        Guid? createdByUserId,
        CancellationToken ct = default)
    {
        await using var transaction = await _unitOfWork.BeginTransactionAsync(ct);
        if (await _exportJobRepo.HasPendingAsync(ct))
        {
            _logger.LogDebug("Export job already pending, skipping");
            // Return empty guid to signal dedup — caller can check
            return Guid.Empty;
        }

        var job = scope switch
        {
            ExportScope.Library library => ExportJob.CreateLibrary(
                library.LibraryId,
                library.MaterializationGeneration,
                createdByUserId),
            ExportScope.AllCatalog => ExportJob.CreateAllCatalog(createdByUserId),
            _ => throw new ArgumentOutOfRangeException(nameof(scope))
        };
        await _genericJobRepo.AddAsync(job, ct);
        await transaction.CommitAsync(ct);

        _logger.LogInformation("Queued export job {JobId}", job.Id);

        return job.Id;
    }
}
