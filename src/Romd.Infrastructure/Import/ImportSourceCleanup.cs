using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Ingestion.Import;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Application.Common.Configuration;
using Romd.Domain.Jobs;

namespace Romd.Infrastructure.Import;

/// <summary>
///     Removes unchanged move-mode originals only after a persisted completion and durable
///     storage outcomes for their payloads. The orphan sweep retries interrupted cleanup.
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
    ///     Runs cleanup for a persisted completion. Returns false when the manifest should be
    ///     discarded without deleting originals. Only server-folder jobs clean up mixed results.
    /// </summary>
    public async Task<bool> RunCompletedAsync(UploadJob job, CancellationToken ct)
    {
        if (!job.ImportMove || job.PhaseEnum is not (UploadPhase.Completed or UploadPhase.CompletedWithErrors))
            return false;

        try
        {
            string manifestPath = ImportManifest.PathFor(_options.DataDirectory, job.Id);
            var manifest = await ImportManifest.ReadAsync(manifestPath, ct);
            if (manifest is null)
                return true;
            if (manifest.Version == ImportManifest.CurrentVersion && !manifest.Move)
                return false;
            if (job.PhaseEnum == UploadPhase.CompletedWithErrors && !manifest.ServerFolder)
                return false;

            await DeleteEligibleSourcesAsync(job, manifestPath, manifest, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Move-mode source cleanup failed for import job {JobId}", job.Id);
        }
        return true;
    }

    private async Task DeleteEligibleSourcesAsync(UploadJob job, string manifestPath, ImportManifest manifest, CancellationToken ct)
    {
        if (manifest.Version != ImportManifest.CurrentVersion || manifest.Entries is null)
        {
            _logger.LogWarning(
                "Import job {JobId} manifest {ManifestPath} has unsupported version {Version}; all source files retained",
                job.Id, manifestPath, manifest.Version);
            return;
        }

        string? serverFolder = null;
        if (manifest.ServerFolder)
        {
            serverFolder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(manifest.AllowedRoot, manifest.SourceRoot)));
            if (job.ImportSourcePath is not { } sourcePath || Path.TrimEndingDirectorySeparator(sourcePath) != serverFolder)
            {
                _logger.LogWarning("Import job {JobId} manifest does not match its server folder; all sources retained", job.Id);
                return;
            }
        }

        var durablyStored = await LoadDurableDispositionsAsync(job.Id, ct);

        int deleted = 0, retained = 0, notVisible = 0, deleteFailed = 0;
        foreach (var entry in manifest.Entries)
        {
            ct.ThrowIfCancellationRequested();
            switch (TryDeleteEntry(job.Id, manifest.AllowedRoot, serverFolder, entry, durablyStored))
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
            File.Delete(manifestPath + ".pending");
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
        string? serverFolder,
        ImportManifestEntry entry,
        Dictionary<string, bool> durablyStored)
    {
        bool stored = entry.ArchiveMembers is { } members && serverFolder is not null
            ? members.Count > 0 && members.All(member => durablyStored.GetValueOrDefault(member))
            : durablyStored.GetValueOrDefault(entry.WorkspaceRelativePath);
        if (!stored)
        {
            _logger.LogInformation(
                "Import job {JobId}: retaining source {SourceRelativePath} — no job item proves durable storage",
                jobId, entry.SourceRelativePath);
            return EntryResult.Retained;
        }

        string absolutePath = Path.GetFullPath(Path.Combine(allowedRoot, entry.SourceRelativePath));
        if (serverFolder is not null)
        {
            string relative = ImportManifest.NormalizeRelativePath(Path.GetRelativePath(serverFolder, absolutePath));
            if (relative == ".." || relative.StartsWith("../", StringComparison.Ordinal) ||
                Path.IsPathRooted(relative) || relative != entry.WorkspaceRelativePath)
            {
                _logger.LogWarning("Import job {JobId}: source {AbsolutePath} is outside its manifest location", jobId, absolutePath);
                return EntryResult.Retained;
            }
        }

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
