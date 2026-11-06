using System.IO.Abstractions;
using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Ingestion.Extraction;
using Romd.Admin.Application.Ingestion.Import;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Infrastructure.Import;

namespace Romd.Infrastructure.Jobs.Executors;

/// <summary>
///     Recursively extracts archives inside an upload workspace under a job-wide
///     <see cref="ExtractionBudget" />. The workspace root is scanned once; that scan counts
///     every staged entry so the entry budget opens at the ceiling minus staged content, and
///     each round then scans only the extraction directories created by the previous round, so
///     nested archives are re-checked without whole-workspace rescans. Before extracting each
///     archive the workspace volume's free space is compared against the extraction floor and
///     the byte budget is re-clamped to it, the archive's declared shape is validated via
///     <see cref="ImportPreflight.ValidateArchiveExpansion" />, and its declared totals are
///     pre-checked against the remaining budget. The budget is then charged live by the extractor
///     for every created file, directory, and write chunk, so declared lies are stopped at the
///     budget boundary mid-write. Extraction always targets a fresh, collision-free directory —
///     staged files are never overwritten — and archives whose entries collide on one extracted
///     path are rejected outright. Violations throw and fail the job with the named error.
/// </summary>
public sealed class ArchiveExtractionStep
{
    private readonly IArchiveExtractorResolver _archiveResolver;
    private readonly IFileSystem _fileSystem;
    private readonly ArchiveExtractionLimits _limits;
    private readonly ILogger<ArchiveExtractionStep> _logger;

    public ArchiveExtractionStep(
        IArchiveExtractorResolver archiveResolver,
        IFileSystem fileSystem,
        ArchiveExtractionLimits limits,
        ILogger<ArchiveExtractionStep> logger)
    {
        _archiveResolver = archiveResolver;
        _fileSystem = fileSystem;
        _limits = limits;
        _logger = logger;
    }

    public async Task ExtractRecursivelyAsync(string directory, CancellationToken ct,
        Func<string, CancellationToken, Task>? onArchive = null)
    {
        directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var state = await ArchiveExtractionJournal.ReadAsync(directory, _limits, ct);
        long availableBytes = _fileSystem.DriveInfo.New(directory).AvailableFreeSpace;
        if (state is null)
        {
            (long stagedEntryCount, List<string> archives) = ScanStagedWorkspace(directory);
            var initialBudget = new ExtractionBudget(_limits, availableBytes, stagedEntryCount);
            state = ArchiveExtractionJournal.Create(directory, _limits, initialBudget, archives);
            await state.SaveAsync(ct);
        }

        // Publication was recorded before unlinking the source. Finish an interrupted unlink
        // before touching nested archives or admitting the workspace to classification.
        state.FinishSourceCleanup();
        state = state with { Cleanup = null };
        var budget = ExtractionBudget.Restore(_limits, availableBytes, state.RemainingEntries, state.RemainingBytes);

        while (state.Pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var archive = state.Pending[0];
            if (archive.Depth > _limits.MaxNestingDepth)
                throw new InvalidOperationException($"Archive nesting exceeds the limit of {_limits.MaxNestingDepth} levels.");
            archive.ValidateSource(directory);
            string source = ArchiveExtractionJournal.Resolve(directory, archive.Source);
            if (onArchive is not null)
                await onArchive(archive.Source, ct);

            if (archive.Target is null)
            {
                archive = archive with { Target = Path.GetRelativePath(directory, ChooseExtractionDirectory(source, directory)) };
                state.Pending[0] = archive;
                // The chosen name and pre-attempt quota precede every output mutation.
                await state.SaveAsync(ct);
            }
            string target = ArchiveExtractionJournal.Resolve(directory, archive.Target);
            if (Directory.Exists(target))
                Directory.Delete(target, recursive: true);
            if (File.Exists(target))
                throw new JobPermanentFailureException("The extraction output path was replaced by a file.");

            await ExtractArchiveAsync(source, directory, target, budget, ct);
            foreach (string output in Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories))
                if (Path.GetRelativePath(directory, output).Length > Romd.Domain.Jobs.JobItem.MaxFileNameLength)
                    throw new JobPermanentFailureException("An extracted upload path exceeds the 1000-character journal limit.");
            var pending = state.Pending.Skip(1).ToList();
            pending.AddRange(FindArchives(target).Select(path => ArchiveExtractionWork.Create(directory, path, archive.Depth + 1)));
            state = state with
            {
                Pending = pending,
                Cleanup = archive,
                RemainingEntries = budget.RemainingEntries,
                RemainingBytes = budget.RemainingBytes
            };
            await state.SaveAsync(ct);
            state.FinishSourceCleanup();
            state = state with { Cleanup = null };
        }
        // Empty queue is also a checkpoint: a crash before the DB phase transition must not
        // rescan extracted archives as new inputs or reset cumulative quotas/depth.
        await state.SaveAsync(ct);
    }

    private async Task ExtractArchiveAsync(
        string file,
        string workspaceDirectory,
        string targetDir,
        ExtractionBudget budget,
        CancellationToken ct)
    {
        string archiveName = Path.GetFileName(file);
        var extractor = _archiveResolver.GetExtractor(archiveName)
            ?? throw new InvalidOperationException($"No archive extractor available for '{archiveName}'.");

        // Compressed inputs can expand well past the preflight estimate, so re-check free space
        // on the workspace volume before extracting each archive.
        long availableBytes = _fileSystem.DriveInfo.New(workspaceDirectory).AvailableFreeSpace;
        if (availableBytes < _limits.SpaceFloorBytes)
        {
            throw new InvalidOperationException(
                $"Insufficient disk space to extract '{archiveName}': " +
                $"{availableBytes:N0} bytes available on the workspace volume; at least " +
                $"{_limits.SpaceFloorBytes:N0} bytes required.");
        }

        // Other jobs can consume the volume mid-import: the byte budget captured at job start
        // must shrink to what the volume can actually hold right now.
        budget.ClampBytesToAvailableSpace(availableBytes);

        var declared = await ReadDeclaredTotalsAsync(extractor, file, ct);
        ThrowIfError(ImportPreflight.ValidateArchiveExpansion(
            archiveName, declared.EntryCount, declared.UncompressedBytes, declared.CompressedBytes, _limits));
        ThrowIfError(budget.ValidateDeclared(archiveName, declared.EntryCount, declared.UncompressedBytes));

        try
        {
            if (!budget.TryChargeEntry())
                throw new ExtractionQuotaExceededException("Creating the extraction directory exceeds the entry budget.");
            Directory.CreateDirectory(targetDir);
            _logger.LogDebug("Extracting {Archive} to {Target}", file, targetDir);

            await using (var stream = File.OpenRead(file))
            {
                await extractor.ExtractAllAsync(stream, targetDir, budget, ct);
            }

        }
        catch (ExtractionQuotaExceededException)
        {
            throw new InvalidOperationException(ImportErrors.ExtractionBudgetExceededDuringExtraction(
                archiveName, budget.RemainingEntries, budget.RemainingBytes).Description);
        }
        catch (ArchiveEntryCollisionException ex)
        {
            throw new InvalidOperationException(
                ImportErrors.ArchiveEntryCollision(archiveName, ex.EntryPath).Description);
        }
    }

    /// <summary>
    ///     Single walk of the staged workspace that counts every staged entry (files AND
    ///     directories — both are inodes) and collects the archives to extract. The entry budget
    ///     opens at <see cref="ArchiveExtractionLimits.MaxTotalEntries" /> minus this count, so
    ///     extraction can never grow the workspace past the import-wide ceiling. Policy for
    ///     archives: a staged archive counts in the staged total AND its extracted contents
    ///     charge the budget, with no credit-back when the extracted archive file is deleted —
    ///     conservative and monotonic, matching the byte budget's no-credit-back policy. If
    ///     staged content alone meets or exceeds the ceiling, the budget opens at zero and the
    ///     first archive fails with the named budget error.
    /// </summary>
    private (long StagedEntryCount, List<string> Archives) ScanStagedWorkspace(string root)
    {
        long stagedEntryCount = 0;
        var archives = new List<string>();
        var directories = new Stack<string>();
        directories.Push(root);
        while (directories.TryPop(out string? directory))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new JobPermanentFailureException("Extraction cannot traverse symbolic links.");
            foreach (FileSystemInfo entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                if (++stagedEntryCount > _limits.MaxTotalEntries)
                    throw new JobPermanentFailureException("The upload workspace exceeds the extraction entry limit.");
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new JobPermanentFailureException("Extraction cannot traverse symbolic links.");
                if (entry is DirectoryInfo)
                    directories.Push(entry.FullName);
                else if (_archiveResolver.IsArchive(entry.Name))
                    archives.Add(entry.FullName);
            }
        }

        archives.Sort(StringComparer.Ordinal);
        return (stagedEntryCount, archives);
    }

    private List<string> FindArchives(string root) => ScanStagedWorkspace(root).Archives;

    private static void ThrowIfError(ErrorOr<Success> result)
    {
        if (result.IsError)
        {
            throw new InvalidOperationException(result.FirstError.Description);
        }
    }

    /// <summary>
    ///     Chooses a fresh, collision-free extraction directory next to the archive. Staged paths
    ///     are never writable by extraction: if the archive's base name is already taken (file or
    ///     directory), a ".extracted" / ".extracted-N" suffix is probed until unused, so a manifest
    ///     entry's workspace-relative path always binds to exactly its staged bytes. The caller
    ///     journals this choice before creation; creating the directory charges the entry budget.
    /// </summary>
    private static string ChooseExtractionDirectory(
        string archivePath,
        string workspaceDirectory)
    {
        string parent = Path.GetDirectoryName(archivePath) ?? workspaceDirectory;
        string baseName = Path.GetFileNameWithoutExtension(archivePath);
        string candidate = Path.Combine(parent, baseName);
        for (int attempt = 1; File.Exists(candidate) || Directory.Exists(candidate); attempt++)
        {
            string suffix = attempt == 1 ? ".extracted" : $".extracted-{attempt}";
            candidate = Path.Combine(parent, baseName + suffix);
        }

        return candidate;
    }

    private static async Task<DeclaredArchiveTotals> ReadDeclaredTotalsAsync(
        IArchiveExtractor extractor,
        string file,
        CancellationToken ct)
    {
        long compressedBytes = new FileInfo(file).Length;
        int entryCount = 0;
        long uncompressedBytes = 0;

        await using var stream = File.OpenRead(file);
        await foreach (var entry in extractor.EnumerateAsync(stream, ct))
        {
            entryCount = checked(entryCount + 1);
            if (!entry.IsDirectory)
            {
                if (entry.UncompressedSize < 0 || entry.UncompressedSize > long.MaxValue - uncompressedBytes)
                    throw new InvalidDataException("Archive declared uncompressed totals exceed the supported size range.");
                uncompressedBytes += entry.UncompressedSize;
            }
        }

        return new DeclaredArchiveTotals(entryCount, uncompressedBytes, compressedBytes);
    }

    private sealed record DeclaredArchiveTotals(
        int EntryCount,
        long UncompressedBytes,
        long CompressedBytes);
}
