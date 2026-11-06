using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Domain.Jobs;
using Romd.Persistence.Entities;

namespace Romd.Persistence.Repositories;

/// <summary>
///     Unified repository for retrieving all job types.
/// </summary>
public sealed class JobRepository : IJobRepository
{
    private readonly RomdDbContext _context;
    private readonly TimeProvider _timeProvider;

    public JobRepository(RomdDbContext context, TimeProvider timeProvider)
    {
        _context = context;
        _timeProvider = timeProvider;
    }

    private IQueryable<JobEntity> ReadableJobs() => _context.Jobs
        .Include(row => ((MediaReconciliationJobEntity)row).Definition).ThenInclude(definition => definition.Outcome);

    public async Task<IReadOnlyList<Job>> ListAsync(JobListFilter filter, CancellationToken ct = default)
    {
        var query = ReadableJobs().AsNoTracking();
        if (filter.Empty)
            return [];
        if (filter.CorrelationId is { } correlation)
            query = query.Where(job => job.CorrelationId == correlation);
        if (!filter.IncludeMediaConvergence)
            query = query.Where(job => !JobVisibility.AdminOnlyJobTypes.Contains(job.JobType));
        if (filter.OwnerId is { } owner)
            query = query.Where(job => job.CreatedByUserId == owner);
        if (filter.Archive != "all")
            query = query.Where(job => job.IsArchived == (filter.Archive == "archived"));
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            string search = filter.Search.ToLowerInvariant();
            if (Guid.TryParse(search, out var id))
                query = query.Where(job => job.Id == id || job.CorrelationId == id || job.SourceFilename.ToLower().Contains(search));
            else
                query = query.Where(job => job.SourceFilename.ToLower().Contains(search));
        }
        if (filter.JobType is not null)
            query = query.Where(job => job.JobType == filter.JobType);
        if (filter.From is { } from)
            query = query.Where(job => job.CreatedAt >= from);
        if (filter.Before is { } before)
            query = query.Where(job => job.CreatedAt < before);
        string[] terminal = ["Completed", "CompletedWithErrors", "Failed", "Cancelled", "Deferred", "Superseded"];
        string[] media = JobVisibility.AdminOnlyJobTypes;
        query = filter.Outcome switch
        {
            "queued" => query.Where(job => job.Phase == "Pending" &&
                (!media.Contains(job.JobType) || job.NextAttemptAtUtc == null)),
            "running" => query.Where(job => job.Phase != "Pending" && !terminal.Contains(job.Phase) &&
                (!media.Contains(job.JobType) || job.NextAttemptAtUtc == null)),
            "completed" => query.Where(job => job.Phase == "Completed" && job.ErrorsJson == "[]"),
            "partial" => query.Where(job => job.Phase == "CompletedWithErrors" || (job.Phase == "Completed" && job.ErrorsJson != "[]")),
            "failed" => query.Where(job => job.Phase == "Failed"),
            "cancelled" => query.Where(job => job.Phase == "Cancelled"),
            "superseded" => query.Where(job => job.Phase == "Superseded"),
            "deferred" => query.Where(job => job.Phase == "Deferred" ||
                (media.Contains(job.JobType) && job.NextAttemptAtUtc != null && !terminal.Contains(job.Phase))),
            // Persisted types have a closed set of terminal phases. Unknown is reserved for future DTOs.
            "unknown" => query.Where(job => false),
            _ => query
        };
        if (filter.CursorCreatedAt is { } createdAt && filter.CursorId is { } cursorId)
            query = query.Where(job => job.CreatedAt < createdAt || (job.CreatedAt == createdAt && job.Id.CompareTo(cursorId) < 0));
        var entities = await query.OrderByDescending(job => job.CreatedAt).ThenByDescending(job => job.Id)
            .Take(filter.Limit + 1).ToListAsync(ct);
        return entities.Select(ToDomain).ToList();
    }

    public async Task<Job?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await ReadableJobs()
            .FirstOrDefaultAsync(j => j.Id == id, ct);

        return entity switch
        {
            UploadJobEntity upload => upload.ToDomain(),
            ReplaceDatJobEntity replace => replace.ToDomain(),
            EnrichmentJobEntity enrichment => enrichment.ToDomain(),
            ArtworkCompletionJobEntity completion => completion.ToDomain(),
            MetadataRematerializationJobEntity metadata => metadata.ToDomain(),
            BulkEnrichmentJobEntity bulk => bulk.ToDomain(),
            ExportJobEntity export => export.ToDomain(),
            MaterializationJobEntity materialization => materialization.ToDomain(),
            ArtworkImportJobEntity artwork => artwork.ToDomain(),
            MediaReconciliationJobEntity reconciliation => reconciliation.ToDomain(),
            _ => null
        };
    }

    public async Task<IReadOnlyList<Job>> GetRecentAsync(
        int limit = 50,
        bool includeArchived = false,
        CancellationToken ct = default)
    {
        var query = ReadableJobs();

        if (!includeArchived)
        {
            query = query.Where(j => !j.IsArchived);
        }

        var entities = await query
            .OrderByDescending(j => j.CreatedAt)
            .Take(limit)
            .ToListAsync(ct);

        return entities.Select(ToDomain).ToList();
    }

    public async Task<Job?> SetArchivedAsync(Guid jobId, bool isArchived, JobVisibilityScope visibility, CancellationToken ct = default)
    {
        if (visibility.Empty)
            return null;
        string[] terminal = ["Completed", "CompletedWithErrors", "Failed", "Cancelled", "Deferred", "Superseded"];
        var now = _timeProvider.GetUtcNow();
        int updated = await _context.Jobs.Where(row => row.Id == jobId && terminal.Contains(row.Phase)
            && (visibility.OwnerId == null || row.CreatedByUserId == visibility.OwnerId)
            && (visibility.IncludeAdminOnly || !JobVisibility.AdminOnlyJobTypes.Contains(row.JobType)))
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.IsArchived, isArchived)
                .SetProperty(row => row.ArchivedAt, row => isArchived ? row.ArchivedAt ?? now : null)
                .SetProperty(row => row.UpdatedAt, now), ct);
        return updated == 1 ? await GetByIdAsync(jobId, ct) : null;
    }

    public async Task UpdateStagedAsync(Job job, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (_context.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Job checkpoint staging requires a caller-owned transaction.");
        if (job is MediaReconciliationJob reconciliation)
        {
            await new Reconciliation.MediaReconciliationJobRepository(_context, _timeProvider).UpdateStagedAsync(reconciliation, ct);
            return;
        }
        if (job is MetadataRematerializationJob metadata)
        {
            await new MetadataRematerializationJobRepository(_context).UpdateStagedAsync(metadata, ct);
            return;
        }
        if (job is ArtworkImportJob artwork)
        {
            await new ArtworkImportJobRepository(_context, _timeProvider).UpdateStagedAsync(artwork, ct);
            return;
        }

        var updatedAt = _timeProvider.GetUtcNow();

        switch (job)
        {
            case UploadJob upload:
                var uploadEntity = UploadJobEntity.FromDomain(upload, updatedAt);
                _context.Set<UploadJobEntity>().Update(uploadEntity);
                break;

            case ReplaceDatJob replace:
                var replaceEntity = ReplaceDatJobEntity.FromDomain(replace, updatedAt);
                _context.Set<ReplaceDatJobEntity>().Update(replaceEntity);
                break;

            case EnrichmentJob enrichment:
                var enrichmentEntity = EnrichmentJobEntity.FromDomain(enrichment, updatedAt);
                _context.Set<EnrichmentJobEntity>().Update(enrichmentEntity);
                break;

            case ArtworkCompletionJob completion:
                var completionEntity = ArtworkCompletionJobEntity.FromDomain(completion);
                completionEntity.ScopeJson = await _context.Set<ArtworkCompletionJobEntity>().Where(x => x.Id == completion.Id).Select(x => x.ScopeJson).SingleAsync(ct);
                _context.Update(completionEntity);
                break;
            case BulkEnrichmentJob bulk:
                var bulkEntity = BulkEnrichmentJobEntity.FromDomain(bulk, updatedAt);
                _context.Set<BulkEnrichmentJobEntity>().Update(bulkEntity);
                break;

            case ExportJob export:
                var exportEntity = ExportJobEntity.FromDomain(export, updatedAt);
                _context.Set<ExportJobEntity>().Update(exportEntity);
                break;

            case MaterializationJob materialization:
                var materializationEntity = MaterializationJobEntity.FromDomain(materialization, updatedAt);
                _context.Set<MaterializationJobEntity>().Update(materializationEntity);
                break;

            default:
                throw new InvalidOperationException($"Unknown job type: {job.GetType().Name}");
        }

    }

    public Task<int> ArchiveCompletedAsync(CancellationToken ct = default) => ArchiveCompletedVisibleAsync(new JobVisibilityScope(null, true, false), ct);

    public async Task<int> ArchiveCompletedVisibleAsync(JobVisibilityScope visibility, CancellationToken ct = default)
    {
        if (visibility.Empty)
            return 0;
        var now = _timeProvider.GetUtcNow();

        // Upload terminal phases
        var uploadTerminalPhases = new[]
        {
            UploadPhase.Completed.ToString(),
            UploadPhase.CompletedWithErrors.ToString(),
            UploadPhase.Failed.ToString(),
            UploadPhase.Cancelled.ToString()
        };

        // ReplaceDat terminal phases
        var replaceDatTerminalPhases = new[]
        {
            ReplaceDatPhase.Completed.ToString(),
            ReplaceDatPhase.CompletedWithErrors.ToString(),
            ReplaceDatPhase.Failed.ToString(),
            ReplaceDatPhase.Cancelled.ToString()
        };

        // Enrichment terminal phases
        var enrichmentTerminalPhases = new[]
        {
            EnrichmentJobPhase.Completed.ToString(),
            EnrichmentJobPhase.Failed.ToString(),
            EnrichmentJobPhase.Cancelled.ToString()
        };

        // Bulk enrichment terminal phases
        var bulkEnrichmentTerminalPhases = new[]
        {
            BulkEnrichmentPhase.Completed.ToString(),
            BulkEnrichmentPhase.CompletedWithErrors.ToString(),
            BulkEnrichmentPhase.Failed.ToString(),
            BulkEnrichmentPhase.Cancelled.ToString()
        };

        // Export terminal phases
        var exportTerminalPhases = new[]
        {
            ExportPhase.Completed.ToString(),
            ExportPhase.CompletedWithErrors.ToString(),
            ExportPhase.Failed.ToString(),
            ExportPhase.Cancelled.ToString()
        };

        // Materialization terminal phases
        var materializationTerminalPhases = new[]
        {
            MaterializationPhase.Completed.ToString(),
            MaterializationPhase.CompletedWithErrors.ToString(),
            MaterializationPhase.Failed.ToString(),
            MaterializationPhase.Cancelled.ToString(),
            MaterializationPhase.Deferred.ToString()
        };

        var allTerminalPhases = uploadTerminalPhases
            .Concat(replaceDatTerminalPhases)
            .Concat(enrichmentTerminalPhases)
            .Concat(bulkEnrichmentTerminalPhases)
            .Concat(exportTerminalPhases)
            .Concat(materializationTerminalPhases).Append("Superseded")
            .ToArray();

        return await _context.Jobs
            .Where(j => !j.IsArchived && allTerminalPhases.Contains(j.Phase) &&
                (visibility.OwnerId == null || j.CreatedByUserId == visibility.OwnerId) &&
                (visibility.IncludeAdminOnly || !JobVisibility.AdminOnlyJobTypes.Contains(j.JobType)))
            .ExecuteUpdateAsync(
                s => s.SetProperty(j => j.IsArchived, true)
                    .SetProperty(j => j.ArchivedAt, now),
                ct);
    }

    public async Task<int> PurgeArchivedAsync(TimeSpan olderThan, CancellationToken ct = default)
    {
        var cutoff = _timeProvider.GetUtcNow() - olderThan;

        return await _context.Jobs
            .Where(j => j.IsArchived && j.ArchivedAt != null && j.ArchivedAt < cutoff &&
                !JobVisibility.AdminOnlyJobTypes.Contains(j.JobType))
            .ExecuteDeleteAsync(ct);
    }

    private static Job ToDomain(JobEntity entity) =>
        entity switch
        {
            UploadJobEntity upload => upload.ToDomain(),
            ReplaceDatJobEntity replace => replace.ToDomain(),
            EnrichmentJobEntity enrichment => enrichment.ToDomain(),
            ArtworkCompletionJobEntity completion => completion.ToDomain(),
            MetadataRematerializationJobEntity metadata => metadata.ToDomain(),
            BulkEnrichmentJobEntity bulk => bulk.ToDomain(),
            ExportJobEntity export => export.ToDomain(),
            MaterializationJobEntity materialization => materialization.ToDomain(),
            ArtworkImportJobEntity artwork => artwork.ToDomain(),
            MediaReconciliationJobEntity reconciliation => reconciliation.ToDomain(),
            _ => throw new InvalidOperationException($"Unknown job type: {entity.JobType}")
        };
}
