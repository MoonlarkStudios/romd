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

    public async Task<IReadOnlyList<Job>> GetHistoryAsync(JobHistoryFilter filter, CancellationToken ct = default)
    {
        var query = _context.Jobs.AsNoTracking();
        if (filter.OwnerId is { } owner) query = query.Where(job => job.CreatedByUserId == owner);
        if (filter.Archive != "all") query = query.Where(job => job.IsArchived == (filter.Archive == "archived"));
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            string search = filter.Search.ToLowerInvariant();
            if (Guid.TryParse(search, out var id))
                query = query.Where(job => job.Id == id || job.CorrelationId == id || job.SourceFilename.ToLower().Contains(search));
            else query = query.Where(job => job.SourceFilename.ToLower().Contains(search));
        }
        if (filter.JobType is not null) query = query.Where(job => job.JobType == filter.JobType);
        if (filter.From is { } from) query = query.Where(job => job.CreatedAt >= from);
        if (filter.Before is { } before) query = query.Where(job => job.CreatedAt < before);
        string[] terminal = ["Completed", "CompletedWithErrors", "Failed", "Cancelled", "Deferred"];
        query = filter.Outcome switch
        {
            "queued" => query.Where(job => job.Phase == "Pending"),
            "running" => query.Where(job => job.Phase != "Pending" && !terminal.Contains(job.Phase)),
            "completed" => query.Where(job => job.Phase == "Completed" && job.ErrorsJson == "[]"),
            "partial" => query.Where(job => job.Phase == "CompletedWithErrors" || (job.Phase == "Completed" && job.ErrorsJson != "[]")),
            "failed" => query.Where(job => job.Phase == "Failed"),
            "cancelled" => query.Where(job => job.Phase == "Cancelled"),
            "deferred" => query.Where(job => job.Phase == "Deferred"),
            // Persisted types have a closed set of terminal phases. Unknown is reserved for future DTOs.
            "unknown" => query.Where(job => false),
            _ => query
        };
        if (filter.CursorCreatedAt is { } createdAt && filter.CursorId is { } cursorId)
            query = query.Where(job => job.CreatedAt < createdAt || (job.CreatedAt == createdAt && job.Id.CompareTo(cursorId) < 0));
        var entities = await query.OrderByDescending(job => job.CreatedAt).ThenByDescending(job => job.Id)
            .Take(Math.Clamp(filter.Limit, 1, 100) + 1).ToListAsync(ct);
        return entities.Select(ToDomain).ToList();
    }

    public async Task<Job?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await _context.Jobs
            .FirstOrDefaultAsync(j => j.Id == id, ct);

        return entity switch
        {
            UploadJobEntity upload => upload.ToDomain(),
            ReplaceDatJobEntity replace => replace.ToDomain(),
            EnrichmentJobEntity enrichment => enrichment.ToDomain(),
            BulkEnrichmentJobEntity bulk => bulk.ToDomain(),
            ExportJobEntity export => export.ToDomain(),
            MaterializationJobEntity materialization => materialization.ToDomain(),
            ArtworkImportJobEntity artwork => artwork.ToDomain(),
            _ => null
        };
    }

    public async Task<IReadOnlyList<Job>> GetRecentAsync(
        int limit = 50,
        bool includeArchived = false,
        CancellationToken ct = default)
    {
        var query = _context.Jobs.AsQueryable();

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

    public async Task<IReadOnlyList<Job>> GetRecentForUserAsync(
        Guid createdByUserId,
        int limit = 50,
        bool includeArchived = false,
        CancellationToken ct = default)
    {
        var query = _context.Jobs
            .Where(j => j.CreatedByUserId == createdByUserId);

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

    public async Task UpdateAsync(Job job, CancellationToken ct = default)
    {
        if (job is ArtworkImportJob artwork)
        {
            await new ArtworkImportJobRepository(_context, _timeProvider).UpdateAsync(artwork, ct);
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

        await _context.SaveChangesAsync(ct);
    }

    public async Task<int> ArchiveCompletedAsync(CancellationToken ct = default)
    {
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
            .Concat(materializationTerminalPhases)
            .ToArray();

        return await _context.Jobs
            .Where(j => !j.IsArchived && allTerminalPhases.Contains(j.Phase))
            .ExecuteUpdateAsync(
                s => s.SetProperty(j => j.IsArchived, true)
                    .SetProperty(j => j.ArchivedAt, now),
                ct);
    }

    public async Task<int> PurgeArchivedAsync(TimeSpan olderThan, CancellationToken ct = default)
    {
        var cutoff = _timeProvider.GetUtcNow() - olderThan;

        return await _context.Jobs
            .Where(j => j.IsArchived && j.ArchivedAt != null && j.ArchivedAt < cutoff)
            .ExecuteDeleteAsync(ct);
    }

    private static Job ToDomain(JobEntity entity) =>
        entity switch
        {
            UploadJobEntity upload => upload.ToDomain(),
            ReplaceDatJobEntity replace => replace.ToDomain(),
            EnrichmentJobEntity enrichment => enrichment.ToDomain(),
            BulkEnrichmentJobEntity bulk => bulk.ToDomain(),
            ExportJobEntity export => export.ToDomain(),
            MaterializationJobEntity materialization => materialization.ToDomain(),
            ArtworkImportJobEntity artwork => artwork.ToDomain(),
            _ => throw new InvalidOperationException($"Unknown job type: {entity.JobType}")
        };
}
