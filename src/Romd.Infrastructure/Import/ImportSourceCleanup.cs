using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Ingestion.Import;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Application.Common.Configuration;
using Romd.Domain.Jobs;

namespace Romd.Infrastructure.Import;

/// <summary>
///     Move-mode source cleanup for path imports. A source original is deleted only when ALL of
///     the following hold: the job's PERSISTED phase is <see cref="UploadPhase.Completed" /> (the
///     caller's gate, re-checked here), the entry's <see cref="JobItem" /> outcome proves durable
///     storage (Ingested/Deduplicated ROM or successful DAT), and the file still passes
///     containment/link revalidation and matches its staged size+mtime fingerprint. Everything
///     else is retained. Idempotent and retryable: a source that is no longer visible is skipped
///     without counting as deleted and the manifest is kept so the orphaned-job sweep can retry
///     (bounded by its age-out). Never throws — cleanup must not fail a finished job.
/// </summary>
public sealed class ImportSourceCleanup
{
    private readonly IJobItemRepository _jobItems;
    private readonly ILogger<ImportSourceCleanup> _logger;
    private readonly IRomdOptions _options;
    private readonly IPathValidator _pathValidator;

    public ImportSourceCleanup(
        IRomdOptions options,
        IPathValidator pathValidator,
        IJobItemRepository jobItems,
        ILogger<ImportSourceCleanup> logger)
    {
        _options = options;
        _pathValidator = pathValidator;
        _jobItems = jobItems;
        _logger = logger;
    }

    /// <summary>
    ///     Deletes durably-stored move-mode sources for a job whose persisted phase is Completed.
    ///     Callers must pass a job re-read from (or just successfully saved to) persistence; the
    ///     phase is the durable gate, never an in-memory-only state.
    /// </summary>
    public async Task RunCompletedAsync(UploadJob job, CancellationToken ct)
    {
        if (!job.ImportMove || job.PhaseEnum != UploadPhase.Completed)
        {
            return;
        }

        try
        {
            await DeleteEligibleSourcesAsync(job, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Move-mode source cleanup failed for import job {JobId}", job.Id);
        }
    }

    private async Task DeleteEligibleSourcesAsync(UploadJob job, CancellationToken ct)
    {
        string manifestPath = ImportManifest.PathFor(_options.DataDirectory, job.Id);
        var manifest = await ImportManifest.ReadAsync(manifestPath, ct);
        if (manifest is null)
        {
            return;
        }

        if (manifest.Version != ImportManifest.CurrentVersion || manifest.Entries is null)
        {
            _logger.LogWarning(
                "Import job {JobId} manifest {ManifestPath} has unsupported version {Version}; all source files retained",
                job.Id, manifestPath, manifest.Version);
            return;
        }

        var durablyStored = await LoadDurableDispositionsAsync(job.Id, ct);

        int deleted = 0, retained = 0, notVisible = 0, deleteFailed = 0;
        foreach (var entry in manifest.Entries)
        {
            switch (TryDeleteEntry(job.Id, manifest.AllowedRoot, entry, durablyStored))
            {
                case EntryResult.Deleted:
                    deleted++;
                    break;
                case EntryResult.Retained:
                    retained++;
                    break;
                case EntryResult.NotVisible:
                    notVisible++;
                    break;
                case EntryResult.DeleteFailed:
                    deleteFailed++;
                    break;
            }
        }

        _logger.LogInformation(
            "Import job {JobId} move cleanup: {Deleted} deleted, {Retained} retained, {NotVisible} not visible, {DeleteFailed} delete failures of {Total} sources",
            job.Id, deleted, retained, notVisible, deleteFailed, manifest.Entries.Count);

        if (notVisible > 0 || deleteFailed > 0)
        {
            // Keep the manifest so the orphaned-job sweep can retry; its age-out bounds this.
            return;
        }

        try
        {
            File.Delete(manifestPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete import manifest {ManifestPath}", manifestPath);
        }
    }

    /// <summary>
    ///     Maps each JobItem join key (workspace-relative file name) to whether the file's content
    ///     is proven durably stored. Conflicting records for one key resolve to not-stored.
    /// </summary>
    private async Task<Dictionary<string, bool>> LoadDurableDispositionsAsync(Guid jobId, CancellationToken ct)
    {
        var items = await _jobItems.GetAllByJobAsync(jobId, ct);
        var durablyStored = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            string key = ImportManifest.NormalizeRelativePath(item.FileName);
            bool stored = item.Outcome is JobItemOutcome.Ingested
                or JobItemOutcome.Deduplicated
                or JobItemOutcome.DatRouted
                or JobItemOutcome.DatUnrouted;
            durablyStored[key] = stored && (!durablyStored.TryGetValue(key, out bool existing) || existing);
        }

        return durablyStored;
    }

    private EntryResult TryDeleteEntry(
        Guid jobId,
        string allowedRoot,
        ImportManifestEntry entry,
        Dictionary<string, bool> durablyStored)
    {
        if (!durablyStored.TryGetValue(ImportManifest.NormalizeRelativePath(entry.WorkspaceRelativePath), out bool stored)
            || !stored)
        {
            _logger.LogInformation(
                "Import job {JobId}: retaining source {SourceRelativePath} — no job item proves durable storage",
                jobId, entry.SourceRelativePath);
            return EntryResult.Retained;
        }

        string absolutePath = Path.GetFullPath(Path.Combine(allowedRoot, entry.SourceRelativePath));
        var validation = _pathValidator.ValidateImportFile(absolutePath);
        if (validation.IsError)
        {
            if (validation.FirstError.Type == ErrorType.NotFound)
            {
                _logger.LogWarning(
                    "Import job {JobId}: source {AbsolutePath} is not visible from this host — verify both hosts mount the import root at an identical path; retrying via the orphaned-job sweep",
                    jobId, absolutePath);
                return EntryResult.NotVisible;
            }

            _logger.LogWarning(
                "Import job {JobId}: retaining source {AbsolutePath} — revalidation failed: {Reason}",
                jobId, absolutePath, validation.FirstError.Description);
            return EntryResult.Retained;
        }

        var info = new FileInfo(absolutePath);
        if (info.Length != entry.SizeBytes || info.LastWriteTimeUtc != entry.LastWriteTimeUtc)
        {
            _logger.LogWarning(
                "Import job {JobId}: retaining source {AbsolutePath} — size/mtime changed since staging (staged {StagedBytes} bytes at {StagedMtime:O}, found {FoundBytes} bytes at {FoundMtime:O})",
                jobId, absolutePath, entry.SizeBytes, entry.LastWriteTimeUtc, info.Length, info.LastWriteTimeUtc);
            return EntryResult.Retained;
        }

        try
        {
            File.Delete(absolutePath);
            return EntryResult.Deleted;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Import job {JobId}: failed to delete moved source {AbsolutePath}", jobId, absolutePath);
            return EntryResult.DeleteFailed;
        }
    }

    private enum EntryResult
    {
        Deleted,
        Retained,
        NotVisible,
        DeleteFailed
    }
}
