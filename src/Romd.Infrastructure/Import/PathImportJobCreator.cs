using System.IO.Abstractions;
using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Ingestion.Import;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Application.Common.Configuration;
using Romd.Domain.Jobs;
using Romd.Infrastructure.Jobs;

namespace Romd.Infrastructure.Import;

/// <summary>
///     Creates upload jobs from an allowlisted server-local directory. Population always copies
///     into the job workspace, preserving relative structure; move mode only writes a source
///     manifest outside the workspace, and <see cref="ImportSourceCleanup" /> deletes originals
///     per-file only after the persisted job completes and durable storage is proven.
/// </summary>
public sealed class PathImportJobCreator : IPathImportJobCreator
{
    private readonly IFileSystem _fileSystem;
    private readonly IUploadJobRepository _jobRepo;
    private readonly ILogger<PathImportJobCreator> _logger;
    private readonly IRomdOptions _options;
    private readonly IPathValidator _pathValidator;

    public PathImportJobCreator(
        IUploadJobRepository jobRepo,
        IRomdOptions options,
        IPathValidator pathValidator,
        IFileSystem fileSystem,
        ILogger<PathImportJobCreator> logger)
    {
        _jobRepo = jobRepo;
        _options = options;
        _pathValidator = pathValidator;
        _fileSystem = fileSystem;
        _logger = logger;
    }

    private string JobsBasePath => Path.Combine(_options.DataDirectory, "temp", "jobs");

    public async Task<ErrorOr<UploadJobCreationResult>> CreateFromPathAsync(
        string sourcePath,
        UploadJobOptions options,
        bool move,
        CancellationToken ct = default)
    {
        var rootValidation = _pathValidator.ValidateImportRoot(sourcePath);
        if (rootValidation.IsError)
        {
            return rootValidation.Errors;
        }

        string allowedRoot = rootValidation.Value;
        string sourceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourcePath));
        var enumeration = new ImportSourceEnumerator(_pathValidator)
            .Enumerate(sourceRoot, ImportPreflight.MaxFileCount, ct);
        if (enumeration.IsError)
        {
            return enumeration.Errors;
        }

        var (files, skipped) = enumeration.Value;
        if (files.Count == 0)
        {
            return ImportErrors.NoFilesFound(sourcePath);
        }

        var preflight = RunPreflight(files);
        if (preflight.IsError)
        {
            return preflight.Errors;
        }

        var job = UploadJob.Create(
            $"{Path.GetFileName(sourceRoot)}/",
            options.PlatformId,
            options.CreatedByUserId);
        job.SetMaxParallelRoms(options.MaxParallelRoms);
        job.SetAllowUnidentified(options.AllowUnidentified);
        job.SetArchiveOnly(options.ArchiveOnly);
        job.SetTrackedOnly(options.TrackedOnly);
        job.SetImportSource(sourceRoot, move);
        foreach (var (path, reason) in skipped)
        {
            job.RecordError(path, reason);
        }

        string workDirectory = Path.Combine(JobsBasePath, job.Id.ToString("N"));
        string manifestPath = ImportManifest.PathFor(_options.DataDirectory, job.Id);

        try
        {
            var copied = await PopulateWorkspaceAsync(job, files, workDirectory, allowedRoot, ct);
            if (copied.Count == 0)
            {
                DeleteWorkspaceAndManifest(workDirectory, manifestPath);
                return ImportErrors.NoFilesFound(sourcePath);
            }

            if (move)
            {
                var manifest = new ImportManifest(
                    ImportManifest.CurrentVersion,
                    allowedRoot,
                    ImportManifest.NormalizeRelativePath(Path.GetRelativePath(allowedRoot, sourceRoot)),
                    move,
                    copied);
                await ImportManifest.WriteAsync(manifestPath, manifest, ct);
            }

            await _jobRepo.AddAsync(job, ct);
        }
        catch
        {
            DeleteWorkspaceAndManifest(workDirectory, manifestPath);
            throw;
        }

        _logger.LogInformation(
            "Created path-import job {JobId} for {SourcePath} ({Files} files, move={Move})",
            job.Id, sourceRoot, files.Count, move);

        return new UploadJobCreationResult(job.Id, job.Id.ToString("N"), $"/jobs/{job.Id}");
    }

    private ErrorOr<Success> RunPreflight(IReadOnlyList<ImportFile> files)
    {
        Directory.CreateDirectory(JobsBasePath);
        long availableBytes = _fileSystem.DriveInfo.New(JobsBasePath).AvailableFreeSpace;
        return ImportPreflight.Validate(files.Count, files.Sum(file => file.SizeBytes), availableBytes);
    }

    /// <summary>
    ///     Copies each file into the workspace, preserving relative structure. Sources are never
    ///     moved during population. Each file is re-validated immediately before open to close the
    ///     path-swap window between enumeration and read; the residual handle-level race (an
    ///     attacker with local filesystem write access swapping content after open) is accepted —
    ///     the size+mtime fingerprint captured here is re-checked before any move-mode deletion.
    /// </summary>
    private async Task<List<ImportManifestEntry>> PopulateWorkspaceAsync(
        UploadJob job,
        IReadOnlyList<ImportFile> files,
        string workDirectory,
        string allowedRoot,
        CancellationToken ct)
    {
        Directory.CreateDirectory(workDirectory);
        var copied = new List<ImportManifestEntry>(files.Count);

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            var revalidation = _pathValidator.ValidateImportFile(file.AbsolutePath);
            if (revalidation.IsError)
            {
                job.RecordError(file.AbsolutePath, revalidation.FirstError.Description);
                continue;
            }

            string targetPath = Path.Combine(workDirectory, file.RelativePath);
            try
            {
                // Fingerprint before opening: if the source changes during or after the copy the
                // delete-time size/mtime comparison fails closed and the original is retained.
                var info = new FileInfo(file.AbsolutePath);
                var entry = new ImportManifestEntry(
                    ImportManifest.NormalizeRelativePath(Path.GetRelativePath(allowedRoot, file.AbsolutePath)),
                    ImportManifest.NormalizeRelativePath(file.RelativePath),
                    info.Length,
                    info.LastWriteTimeUtc);

                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                await using var source = File.Open(
                    file.AbsolutePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                await using var target = File.Create(targetPath);
                await source.CopyToAsync(target, ct);
                copied.Add(entry);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                job.RecordError(file.AbsolutePath, $"Failed to copy into workspace: {ex.Message}");
            }
        }

        return copied;
    }

    private void DeleteWorkspaceAndManifest(string workDirectory, string manifestPath)
    {
        try
        {
            if (Directory.Exists(workDirectory))
            {
                Directory.Delete(workDirectory, true);
            }

            if (File.Exists(manifestPath))
            {
                File.Delete(manifestPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clean up path-import staging at {WorkDirectory}", workDirectory);
        }
    }
}
