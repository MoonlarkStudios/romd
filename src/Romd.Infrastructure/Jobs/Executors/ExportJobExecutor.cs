using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Export;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Configuration;
using Romd.Application.Common.Content;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Domain.Jobs;

namespace Romd.Infrastructure.Jobs.Executors;

public sealed class ExportJobExecutor(
    IExportRepository exportRepository,
    IExportArchiveWriter archiveWriter,
    IRomdOptions romdOptions,
    ILogger<ExportJobExecutor> logger, IRomContentCatalog contentCatalog, RomDeliveryPolicy deliveryPolicy,
    TimeProvider clock, IReferenceCatalogService? referenceCatalog = null, IRomChdDeliveryAdmission? chdAdmission = null) : IJobExecutor<ExportJob>
{
    private const int _checkpointInterval = 10;

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
        job.BeginArchiveAttempt();
        var files = await exportRepository.GetExportFilesAsync(scope, ct);

        var groups = files.GroupBy(f => (f.PlatformName, f.TitleId, f.TitleName))
            .OrderBy(g => g.Key.PlatformName).ThenBy(g => g.Key.TitleName).ToArray();
        var candidates = new List<ExportArchiveEntry>();
        var titleByMember = new Dictionary<(int RomId, string Parent), (int Id, string Name)>();
        var remainingByTitle = new Dictionary<int, int>();
        foreach (var group in groups)
        {
            string parent = $"{ExportArchivePaths.SafeFilename(group.Key.PlatformName)}/{ExportArchivePaths.SafeFilename(group.Key.TitleName)}/";
            var members = group.DistinctBy(file => file.RomFileId).ToArray();
            remainingByTitle[group.Key.TitleId] = members.Length;
            foreach (var file in members)
            {
                candidates.Add(new(file.FileId, parent + ExportArchivePaths.SafeFilename(file.OriginalFilename), file.RomFileId));
                titleByMember[(file.RomFileId, parent)] = (group.Key.TitleId, group.Key.TitleName);
            }
        }

        var distinctTitles = groups.Length;
        job.SetTotals(distinctTitles, candidates.Count);
        await context.CheckpointAsync(ct);

        if (job.ExportPath is { } priorPath)
        {
            // Retain the durable reference until owned deletion succeeds. A lost claim cannot
            // delete a successor's artifact, and a crash before clearing can safely repeat deletion.
            await context.ExecuteOwnedMutationAsync(token =>
            {
                token.ThrowIfCancellationRequested();
                File.Delete(priorPath);
                return Task.CompletedTask;
            }, ct);
            job.SetExportPath(null);
            await context.CheckpointAsync(ct);
        }

        if (files.Count == 0)
        {
            logger.LogInformation("Export job {JobId}: no files to export", job.Id);
            return;
        }

        var deliverySystems = referenceCatalog is null ? null : await RomDeliverySystems.ResolveAsync(
            deliveryPolicy, referenceCatalog, ct);
        // An export has no single platform, so each member resolves its kind under every platform the scope reaches it through.
        var platformsByRom = files.GroupBy(file => file.RomFileId).ToDictionary(group => group.Key,
            group => group.Select(file => file.PlatformId).Distinct().Select(id => deliverySystems?.KeyFor(id)).ToArray());
        candidates = candidates.Select(entry => entry with { PlatformKeys = platformsByRom[entry.RomId!.Value] }).ToList();
        var planned = await ExportArchivePlanner.BindAsync(candidates, job.Format, contentCatalog, deliveryPolicy, skipMissing: true,
            ct: ct, chdAdmission: chdAdmission);
        if (planned.IsError)
        {
            job.RecordError("archive", string.Join(" ", planned.Errors.Select(error => error.Description)), occurredAt: clock.GetUtcNow());
            await context.CheckpointAsync(ct);
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
            var titlesSinceCheckpoint = 0;
            async Task RecordEntryAsync(ExportArchiveEntry entry, bool written)
            {
                if (written)
                    job.RecordFileProcessed();
                else
                    job.RecordFileSkipped();
                string parent = entry.Path[..(entry.Path.LastIndexOf('/') + 1)];
                var title = titleByMember[(entry.RomId!.Value, parent)];
                if (--remainingByTitle[title.Id] == 0)
                {
                    job.SetCurrentItem(title.Name);
                    job.RecordTitleProcessed();
                    if (++titlesSinceCheckpoint >= _checkpointInterval)
                    {
                        await context.CheckpointAsync(ct);
                        titlesSinceCheckpoint = 0;
                    }
                }
            }
            foreach (var missing in planned.Value.MissingEntries)
                await RecordEntryAsync(missing, written: false);
            await using (var zipStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await archiveWriter.WriteAsync(zipStream, planned.Value.Entries, RecordEntryAsync, skipMissing: true, ct: ct);
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
                try
                { File.Delete(zipPath); }
                catch { /* best effort */ }
            }

            throw;
        }
    }

}
