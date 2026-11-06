using System.Collections.Immutable;
using System.IO.Abstractions;
using System.Runtime.ExceptionServices;
using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Ingestion.Import;
using Romd.Admin.Application.Ingestion.Preparation;
using Romd.Domain.Jobs;
using Romd.Infrastructure.Jobs;

namespace Romd.Infrastructure.Import;

internal sealed record InspectedPathImport(
    string DataDirectory, string RequestedPath, string AllowedRoot, ImmutableArray<ImportFile> Files, PathImportFacts Facts) : IPathImportPlan;

/// <summary>Shared source inspection and copying; it never saves jobs or deletes source originals.</summary>
internal static class PathImportFiles
{
    internal const string TargetCleanupFailureKey = "Romd.PathImport.TargetCleanupFailure";

    internal static ErrorOr<InspectedPathImport> Inspect(
        string dataDirectory, string sourcePath, IPathValidator validator, IFileSystem fileSystem, CancellationToken ct)
    {
        var rootValidation = validator.ValidateImportRoot(sourcePath);
        if (rootValidation.IsError)
            return rootValidation.Errors;
        string sourceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourcePath));
        var enumeration = new ImportSourceEnumerator(validator).Enumerate(sourceRoot, ImportPreflight.MaxFileCount, ct);
        if (enumeration.IsError)
            return enumeration.Errors;
        var (files, skipped) = enumeration.Value;
        if (files.Count == 0)
            return ImportErrors.NoFilesFound(sourcePath);
        if (files.Any(file => file.RelativePath.Length > JobItem.MaxFileNameLength))
            return Error.Validation("Import.PathTooLong", "Upload relative paths must fit the 1000-character outcome journal.");
        string jobs = Path.Combine(dataDirectory, "temp", "jobs");
        Directory.CreateDirectory(jobs);
        long available = fileSystem.DriveInfo.New(jobs).AvailableFreeSpace;
        var preflight = ImportPreflight.Validate(files.Count, files.Sum(file => file.SizeBytes), available);
        if (preflight.IsError)
            return preflight.Errors;
        return new InspectedPathImport(Path.GetFullPath(dataDirectory), sourcePath, rootValidation.Value, files.ToImmutableArray(),
            new PathImportFacts(sourceRoot, $"{Path.GetFileName(sourceRoot)}/", files.Count,
                skipped.Select(issue => new ImportPreparationIssue(issue.Path, issue.Reason)).ToImmutableArray()));
    }

    internal static async Task<List<ImportManifestEntry>> PopulateAsync(
        InspectedPathImport plan, Guid jobId, IPathValidator validator, IFileSystem fileSystem, TimeProvider clock,
        Action<ImportCopyIssue> recordIssue, CancellationToken ct)
    {
        string workDirectory = WorkspaceInputCopies.DirectoryFor(plan.DataDirectory, jobId);
        Directory.CreateDirectory(workDirectory);
        var copied = new List<ImportManifestEntry>(plan.Files.Length);
        foreach (var file in plan.Files)
        {
            ct.ThrowIfCancellationRequested();
            var revalidation = validator.ValidateImportFile(file.AbsolutePath);
            if (revalidation.IsError)
            {
                recordIssue(new(file.AbsolutePath, revalidation.FirstError.Description, clock.GetUtcNow()));
                continue;
            }
            string targetPath = Path.Combine(workDirectory, file.RelativePath);
            try
            {
                // The original size+mtime fence remains authoritative for later move-mode deletion.
                var info = new FileInfo(file.AbsolutePath);
                var entry = new ImportManifestEntry(
                    ImportManifest.NormalizeRelativePath(Path.GetRelativePath(plan.AllowedRoot, file.AbsolutePath)),
                    ImportManifest.NormalizeRelativePath(file.RelativePath), info.Length, info.LastWriteTimeUtc);
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                await CopyOwnedAsync(
                    () => fileSystem.File.Open(file.AbsolutePath, FileMode.Open, FileAccess.Read, FileShare.Read),
                    () => fileSystem.File.Create(targetPath), ct);
                // A buffered target can still fail while disposing. Admit only fully settled copies.
                copied.Add(entry);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                try
                {
                    fileSystem.File.Delete(targetPath);
                    if (fileSystem.File.Exists(targetPath))
                        throw new IOException("The failed copy remains in the import workspace.");
                }
                catch (Exception cleanup)
                {
                    // An unsafe target cannot be handed to the worker. Abort preparation without
                    // replacing the copy/flush failure with its secondary cleanup failure.
                    ex.Data[TargetCleanupFailureKey] = cleanup;
                    ExceptionDispatchInfo.Capture(ex).Throw();
                }
                recordIssue(new(file.AbsolutePath, $"Failed to copy into workspace: {ex.Message}", clock.GetUtcNow()));
            }
        }
        return copied;
    }

    internal static async Task CopyOwnedAsync(Func<Stream> openSource, Func<Stream> openTarget, CancellationToken ct)
    {
        Stream? source = null;
        Stream? target = null;
        Exception? primary = null;
        Exception? cleanupFailure = null;
        try
        {
            source = openSource();
            target = openTarget();
            await source.CopyToAsync(target, ct);
        }
        catch (Exception error)
        {
            primary = error;
            throw;
        }
        finally
        {
            await DisposeAsync(target);
            await DisposeAsync(source);
            if (primary is null && cleanupFailure is not null)
                ExceptionDispatchInfo.Capture(cleanupFailure).Throw();

            async Task DisposeAsync(Stream? stream)
            {
                if (stream is null)
                    return;
                try
                {
                    await stream.DisposeAsync();
                }
                catch (Exception error)
                {
                    // Retain the copy/open fault or the first cleanup fault, including its OCE token.
                    cleanupFailure ??= error;
                }
            }
        }
    }

    internal static Task WriteManifestAsync(
        InspectedPathImport plan, Guid jobId, bool move, List<ImportManifestEntry> copied, CancellationToken ct)
    {
        if (!move)
            return Task.CompletedTask;
        var manifest = new ImportManifest(ImportManifest.CurrentVersion, plan.AllowedRoot,
            ImportManifest.NormalizeRelativePath(Path.GetRelativePath(plan.AllowedRoot, plan.Facts.SourceRoot)), move, copied);
        return ImportManifest.WriteAsync(ImportManifest.PathFor(plan.DataDirectory, jobId), manifest, ct);
    }

    internal static void DeleteWorkspaceAndManifest(string dataDirectory, Guid jobId, ILogger logger)
    {
        string workDirectory = WorkspaceInputCopies.DirectoryFor(dataDirectory, jobId);
        string manifestPath = ImportManifest.PathFor(dataDirectory, jobId);
        try
        {
            if (Directory.Exists(workDirectory))
                Directory.Delete(workDirectory, true);
            if (File.Exists(manifestPath))
                File.Delete(manifestPath);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to clean up path-import staging at {WorkDirectory}", workDirectory);
        }
    }
}
