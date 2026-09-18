using System.IO.Compression;
using System.Runtime.CompilerServices;
using Romd.Admin.Application.Ingestion.Extraction;

namespace Romd.Infrastructure.Storage;

/// <summary>
///     Archive extractor for ZIP files using System.IO.Compression.
/// </summary>
public sealed class ZipArchiveExtractor : IArchiveExtractor
{
    private static readonly string[] Extensions = [".zip"];

    public IReadOnlyList<string> SupportedExtensions => Extensions;

    public bool CanHandle(string filename)
    {
        var extension = Path.GetExtension(filename);
        return extension.Equals(".zip", StringComparison.OrdinalIgnoreCase);
    }

    public async IAsyncEnumerable<ArchiveEntry> EnumerateAsync(
        Stream archiveStream,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // ZipArchive requires a seekable stream
        if (!archiveStream.CanSeek)
        {
            throw new ArgumentException("Archive stream must be seekable", nameof(archiveStream));
        }

        archiveStream.Position = 0;

        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: true);

        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Skip directory entries (they end with /)
            bool isDirectory = string.IsNullOrEmpty(entry.Name) || entry.FullName.EndsWith('/');

            yield return new ArchiveEntry(
                Path: entry.FullName,
                CompressedSize: entry.CompressedLength,
                UncompressedSize: entry.Length,
                IsDirectory: isDirectory);
        }

        await Task.CompletedTask; // Make async enumerable happy
    }

    public async Task<Stream> ExtractAsync(
        Stream archiveStream,
        string entryPath,
        CancellationToken cancellationToken = default)
    {
        if (!archiveStream.CanSeek)
        {
            throw new ArgumentException("Archive stream must be seekable", nameof(archiveStream));
        }

        archiveStream.Position = 0;

        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: true);

        var entry = archive.GetEntry(entryPath)
            ?? throw new FileNotFoundException($"Entry '{entryPath}' not found in archive");

        // Extract to a MemoryStream since we need to return an independent stream
        var memoryStream = new MemoryStream();
        await using (var entryStream = entry.Open())
        {
            await entryStream.CopyToAsync(memoryStream, cancellationToken);
        }

        memoryStream.Position = 0;
        return memoryStream;
    }

    public async Task<IReadOnlyList<ExtractedEntry>> ExtractAllAsync(
        Stream archiveStream,
        string targetDirectory,
        IExtractionQuota quota,
        CancellationToken cancellationToken = default)
    {
        if (!archiveStream.CanSeek)
        {
            throw new ArgumentException("Archive stream must be seekable", nameof(archiveStream));
        }

        archiveStream.Position = 0;
        CreateDirectoryCharged(targetDirectory, quota);

        var extractedEntries = new List<ExtractedEntry>();

        // Duplicate entry names, "\" spellings, and "../" aliases can all normalize to the same
        // target path: the second write would silently replace the first entry's bytes and
        // charge an entry for an inode that does not exist, so a collision rejects the archive.
        // Comparison is Ordinal, NOT the filesystem's case semantics — case-only collisions on a
        // case-insensitive volume slip past this set and are caught by the FileMode.CreateNew
        // guard below instead.
        var claimedTargetPaths = new HashSet<string>(StringComparer.Ordinal);

        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: true);

        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Sanitize path to prevent zip slip attacks
            var relativePath = entry.FullName.Replace('\\', '/');
            if (relativePath.StartsWith('/'))
            {
                relativePath = relativePath.TrimStart('/');
            }

            var absolutePath = Path.GetFullPath(Path.Combine(targetDirectory, relativePath));

            // Ensure the path is within the target directory (zip slip protection)
            if (!absolutePath.StartsWith(Path.GetFullPath(targetDirectory) + Path.DirectorySeparatorChar)
                && absolutePath != Path.GetFullPath(targetDirectory))
            {
                continue; // Skip entries that would extract outside target
            }

            bool isDirectory = string.IsNullOrEmpty(entry.Name) || entry.FullName.EndsWith('/');

            if (!claimedTargetPaths.Add(Path.TrimEndingDirectorySeparator(absolutePath)))
            {
                throw new ArchiveEntryCollisionException(entry.FullName);
            }

            if (isDirectory)
            {
                CreateDirectoryCharged(absolutePath, quota, entry.FullName);
                extractedEntries.Add(new ExtractedEntry(
                    RelativePath: relativePath,
                    AbsolutePath: absolutePath,
                    Size: 0,
                    IsDirectory: true));
            }
            else
            {
                // Ensure parent directory exists
                var parentDir = Path.GetDirectoryName(absolutePath);
                if (!string.IsNullOrEmpty(parentDir))
                {
                    CreateDirectoryCharged(parentDir, quota, entry.FullName);
                }

                ChargeEntry(quota);
                await using var entryStream = entry.Open();
                // Fresh-extraction-directory invariant: nothing legitimate pre-exists inside the
                // target, so open CreateNew — any path that somehow already exists (including a
                // filesystem-case collision the Ordinal set cannot see) is a collision, reported
                // through the same named contract as detected duplicates so behavior stays
                // platform-independent. Unrelated I/O failures propagate untouched.
                await using var output = new QuotaEnforcingStream(
                    OpenCreateNewTranslatingCollision(absolutePath, entry.FullName), quota);
                await entryStream.CopyToAsync(output, cancellationToken);

                extractedEntries.Add(new ExtractedEntry(
                    RelativePath: relativePath,
                    AbsolutePath: absolutePath,
                    Size: entry.Length,
                    IsDirectory: false));
            }
        }

        return extractedEntries;
    }

    /// <summary>
    ///     Opens the extraction target with CreateNew semantics, translating an
    ///     already-exists failure into <see cref="ArchiveEntryCollisionException" /> so
    ///     case-only collisions on case-insensitive filesystems surface through the same
    ///     named contract as Ordinal-detected duplicates. The existence re-check in the
    ///     filter distinguishes collisions from unrelated I/O failures, which propagate.
    /// </summary>
    private static FileStream OpenCreateNewTranslatingCollision(string absolutePath, string entryName)
    {
        try
        {
            return File.Open(absolutePath, FileMode.CreateNew, FileAccess.Write);
        }
        catch (IOException) when (Path.Exists(absolutePath))
        {
            // Path.Exists sees files AND directories: a directory entry squatting on the
            // target (e.g. "Payload.rom/" before "payload.rom" on a case-insensitive volume)
            // is a collision too, not a generic filesystem failure.
            throw new ArchiveEntryCollisionException(entryName);
        }
    }

    /// <summary>
    ///     Creates a directory, charging the quota one entry for every path segment that does not
    ///     yet exist — implicit parents are inodes too and must not extract for free.
    /// </summary>
    private static void CreateDirectoryCharged(
        string path,
        IExtractionQuota quota,
        string? collisionEntryName = null)
    {
        // Trim the trailing separator a directory-entry path carries, otherwise the walk sees
        // "d0/" and "d0" as two missing segments and double-charges the same inode.
        var missing = new Stack<string>();
        for (string? current = Path.TrimEndingDirectorySeparator(path);
             !string.IsNullOrEmpty(current) && !Directory.Exists(current);
             current = Path.GetDirectoryName(current))
        {
            missing.Push(current);
        }

        while (missing.TryPop(out string? directory))
        {
            ChargeEntry(quota);
            try
            {
                Directory.CreateDirectory(directory);
            }
            catch (IOException) when (File.Exists(directory))
            {
                // The inverse collision: a FILE squats where a directory (or a parent
                // component of a deeper entry) must go — e.g. "payload.rom" before
                // "Payload.rom/" on a case-insensitive volume, or the entry "a" before
                // "a/b.rom" on any volume. Same named contract; unrelated I/O propagates.
                // Report the logical archive entry, never the absolute workspace segment.
                throw new ArchiveEntryCollisionException(
                    collisionEntryName ?? Path.GetFileName(Path.TrimEndingDirectorySeparator(directory)));
            }
        }
    }

    private static void ChargeEntry(IExtractionQuota quota)
    {
        if (!quota.TryChargeEntry())
        {
            throw new ExtractionQuotaExceededException(
                "Creating another file or directory would exceed the remaining extraction entry budget.");
        }
    }
}
