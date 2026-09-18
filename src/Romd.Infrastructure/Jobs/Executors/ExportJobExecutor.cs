using System.IO.Compression;
using Microsoft.Extensions.Logging;
using Romd.Application.Common.Configuration;
using Romd.Admin.Application.Export;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Storage.Files;
using Romd.Domain.Jobs;

namespace Romd.Infrastructure.Jobs.Executors;

public sealed class ExportJobExecutor(
    IExportRepository exportRepository,
    IFileStorageService fileStorage,
    IRomdOptions romdOptions,
    ILogger<ExportJobExecutor> logger) : IJobExecutor<ExportJob>
{
    private const int CheckpointInterval = 10;

    public async Task ExecuteAsync(ExportJob job, JobContext context)
    {
        var ct = context.CancellationToken;

        var scope = job.ScopeKind switch
        {
            ExportScopeKind.Library when
                job.LibraryId is > 0 and { } libraryId &&
                job.AuthorizedMaterializationGeneration is >= 0 and { } generation =>
                (ExportScope)new ExportScope.Library(libraryId, generation),
            ExportScopeKind.AllCatalog when
                job.LibraryId is null &&
                job.AuthorizedMaterializationGeneration is null =>
                new ExportScope.AllCatalog(),
            _ => throw new InvalidOperationException(
                $"Export job {job.Id} has an invalid or legacy authorization scope.")
        };
        var files = await exportRepository.GetExportFilesAsync(scope, ct);

        var distinctTitles = files.Select(f => f.TitleId).Distinct().Count();
        job.SetTotals(distinctTitles, files.Count);
        await context.CheckpointAsync(ct);

        if (files.Count == 0)
        {
            logger.LogInformation("Export job {JobId}: no files to export", job.Id);
            return;
        }

        // Begin packaging
        job.BeginPackaging();
        await context.CheckpointAsync(ct);

        var exportsDir = Path.Combine(romdOptions.DataDirectory, "exports");
        Directory.CreateDirectory(exportsDir);

        // Attempts never share a writable artifact. Only the fenced checkpoint publishes its path;
        // a stale attempt can clean up its own partial file without deleting a successor's output.
        var zipPath = Path.Combine(exportsDir, $"{job.Id:N}-{Guid.NewGuid():N}.zip");

        logger.LogInformation(
            "Export job {JobId}: packaging {FileCount} files from {TitleCount} titles",
            job.Id, files.Count, distinctTitles);

        try
        {
            await using (var zipStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: false))
            {
                var grouped = files
                    .GroupBy(f => (f.PlatformName, f.TitleId, f.TitleName))
                    .OrderBy(g => g.Key.PlatformName)
                    .ThenBy(g => g.Key.TitleName);

                int titlesSinceCheckpoint = 0;

                foreach (var titleGroup in grouped)
                {
                    ct.ThrowIfCancellationRequested();

                    var (platformName, _, titleName) = titleGroup.Key;
                    job.SetCurrentItem(titleName);

                    // Deduplicate by RomFileId within the same title
                    var deduped = titleGroup.DistinctBy(f => f.RomFileId);

                    foreach (var file in deduped)
                    {
                        ct.ThrowIfCancellationRequested();

                        var entryPath = $"{SanitizePath(platformName)}/{SanitizePath(titleName)}/{SanitizePath(file.OriginalFilename)}";

                        var stream = await fileStorage.RetrieveByIdAsync(file.FileId, ct);
                        if (stream is null)
                        {
                            logger.LogWarning(
                                "Export job {JobId}: file {FileId} not found in storage, skipping",
                                job.Id, file.FileId);
                            job.RecordFileSkipped();
                            continue;
                        }

                        await using (stream)
                        {
                            var entry = archive.CreateEntry(entryPath, CompressionLevel.NoCompression);
                            await using var entryStream = entry.Open();
                            await stream.CopyToAsync(entryStream, ct);
                        }

                        job.RecordFileProcessed();
                    }

                    job.RecordTitleProcessed();
                    titlesSinceCheckpoint++;

                    if (titlesSinceCheckpoint >= CheckpointInterval)
                    {
                        await context.CheckpointAsync(ct);
                        titlesSinceCheckpoint = 0;
                    }
                }
            }

            // Storing phase
            job.BeginStoring();
            job.SetExportPath(zipPath);
            await context.CheckpointAsync(ct);

            logger.LogInformation(
                "Export job {JobId}: completed. {Processed} files packaged, {Skipped} skipped",
                job.Id, job.ProcessedFiles - job.SkippedFiles, job.SkippedFiles);
        }
        catch
        {
            // Clean up partial zip on failure
            if (File.Exists(zipPath))
            {
                try { File.Delete(zipPath); }
                catch { /* best effort */ }
            }

            throw;
        }
    }

    private static string SanitizePath(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c));
    }
}
