using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Source.Platform;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Domain.Catalog;
using Romd.Domain.Jobs;
using Romd.Persistence.Repositories;

namespace Romd.Infrastructure.Jobs;

public sealed class EnrichmentScheduler : IEnrichmentScheduler
{
    private readonly EnrichmentJobRepository _jobRepo;
    private readonly IBulkEnrichmentJobRepository _bulkJobRepo;
    private readonly IJobRepository<EnrichmentJob> _genericJobRepo;
    private readonly IPlatformRepository _platformRepo;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<EnrichmentScheduler> _logger;
    private readonly IUnitOfWork _unitOfWork;

    public EnrichmentScheduler(
        EnrichmentJobRepository jobRepo,
        IBulkEnrichmentJobRepository bulkJobRepo,
        IJobRepository<EnrichmentJob> genericJobRepo,
        IPlatformRepository platformRepo,
        TimeProvider timeProvider,
        IUnitOfWork unitOfWork,
        ILogger<EnrichmentScheduler> logger)
    {
        _jobRepo = jobRepo;
        _bulkJobRepo = bulkJobRepo;
        _genericJobRepo = genericJobRepo;
        _platformRepo = platformRepo;
        _timeProvider = timeProvider;
        _unitOfWork = unitOfWork;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task EnqueueAsync(int titleId, string titleName, int platformId, CancellationToken ct = default)
    {
        await using var transaction = await _unitOfWork.BeginTransactionAsync(ct);
        // Check for existing pending/active job (deduplication)
        if (await _jobRepo.HasPendingForTitleAsync(titleId, ct))
        {
            _logger.LogDebug("Enrichment job already pending for title {TitleId}", titleId);
            return;
        }

        var job = EnrichmentJob.Create(titleName, titleId, platformId, _timeProvider);
        await _genericJobRepo.AddAsync(job, ct);
        await transaction.CommitAsync(ct);

        _logger.LogDebug("Queued enrichment job {JobId} for title {TitleId}", job.Id, titleId);
    }

    /// <inheritdoc />
    public async Task EnqueueBatchAsync(
        IEnumerable<(int TitleId, string TitleName, int PlatformId)> titles,
        CancellationToken ct = default)
    {
        foreach (var (titleId, titleName, platformId) in titles)
        {
            await EnqueueAsync(titleId, titleName, platformId, ct);
        }
    }

    /// <inheritdoc />
    public async Task EnqueueBulkAsync(
        int platformId,
        EnrichmentScope scope = EnrichmentScope.Tracked,
        Guid? createdByUserId = null,
        CancellationToken ct = default)
    {
        await using var transaction = await _unitOfWork.BeginTransactionAsync(ct);
        if (await _bulkJobRepo.HasPendingForPlatformAsync(platformId, ct))
        {
            _logger.LogDebug("Bulk enrichment job already pending for platform {PlatformId}", platformId);
            return;
        }

        var platform = await _platformRepo.GetByIdAsync(platformId, ct);
        var job = BulkEnrichmentJob.Create(platformId, platform?.ShortName ?? string.Empty, scope, createdByUserId);
        await _bulkJobRepo.AddAsync(job, ct);
        await transaction.CommitAsync(ct);

        _logger.LogInformation("Queued bulk enrichment job {JobId} for platform {PlatformId}", job.Id, platformId);
    }
}
