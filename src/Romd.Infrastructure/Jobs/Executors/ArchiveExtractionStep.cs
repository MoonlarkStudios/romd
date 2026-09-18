using System.IO.Abstractions;
using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Ingestion.Extraction;
using Romd.Admin.Application.Ingestion.Import;
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
        long availableBytes = _fileSystem.DriveInfo.New(directory).AvailableFreeSpace;
        (long stagedEntryCount, List<string> archives) = ScanStagedWorkspace(directory);
        var budget = new ExtractionBudget(_limits, availableBytes, stagedEntryCount);
        var pending = new Queue<(string Path, int Depth)>(archives.Select(path => (path, 1)));

        while (pending.TryDequeue(out var archive))
        {
            ct.ThrowIfCancellationRequested();
            if (archive.Depth > _limits.MaxNestingDepth)
                throw new InvalidOperationException($"Archive nesting exceeds the limit of {_limits.MaxNestingDepth} levels.");
            if (onArchive is not null) await onArchive(Path.GetRelativePath(directory, archive.Path), ct);

            string extractedDir = await ExtractArchiveAsync(archive.Path, directory, budget, ct);
            foreach (string nested in FindArchives(extractedDir))
            {
                pending.Enqueue((nested, archive.Depth + 1));
            }
        }
    }

    private async Task<string> ExtractArchiveAsync(
        string file,
        string workspaceDirectory,
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
            string targetDir = CreateFreshExtractionDirectory(file, workspaceDirectory, budget);
            _logger.LogDebug("Extracting {Archive} to {Target}", file, targetDir);

            await using (var stream = File.OpenRead(file))
            {
                await extractor.ExtractAllAsync(stream, targetDir, budget, ct);
            }

            File.Delete(file);
            return targetDir;
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
        foreach (FileSystemInfo entry in new DirectoryInfo(root)
                     .EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
        {
            stagedEntryCount++;
            if (entry is FileInfo && _archiveResolver.IsArchive(entry.Name))
            {
                archives.Add(entry.FullName);
            }
        }

        archives.Sort(StringComparer.Ordinal);
        return (stagedEntryCount, archives);
    }

    private List<string> FindArchives(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(file => _archiveResolver.IsArchive(Path.GetFileName(file)))
            .Order(StringComparer.Ordinal)
            .ToList();

    private static void ThrowIfError(ErrorOr<Success> result)
    {
        if (result.IsError)
        {
            throw new InvalidOperationException(result.FirstError.Description);
        }
    }

    /// <summary>
    ///     Reserves a fresh, collision-free extraction directory next to the archive. Staged paths
    ///     are never writable by extraction: if the archive's base name is already taken (file or
    ///     directory), a ".extracted" / ".extracted-N" suffix is probed until unused, so a manifest
    ///     entry's workspace-relative path always binds to exactly its staged bytes. The directory
    ///     is an extraction-created inode, so it charges the budget like any other entry.
    /// </summary>
    private static string CreateFreshExtractionDirectory(
        string archivePath,
        string workspaceDirectory,
        ExtractionBudget budget)
    {
        string parent = Path.GetDirectoryName(archivePath) ?? workspaceDirectory;
        string baseName = Path.GetFileNameWithoutExtension(archivePath);
        string candidate = Path.Combine(parent, baseName);
        for (int attempt = 1; File.Exists(candidate) || Directory.Exists(candidate); attempt++)
        {
            string suffix = attempt == 1 ? ".extracted" : $".extracted-{attempt}";
            candidate = Path.Combine(parent, baseName + suffix);
        }

        if (!budget.TryChargeEntry())
        {
            throw new ExtractionQuotaExceededException(
                "Creating the extraction directory would exceed the remaining extraction entry budget.");
        }

        Directory.CreateDirectory(candidate);
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
            entryCount++;
            if (!entry.IsDirectory)
            {
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
