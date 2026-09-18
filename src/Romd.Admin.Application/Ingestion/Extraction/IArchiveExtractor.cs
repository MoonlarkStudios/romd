namespace Romd.Admin.Application.Ingestion.Extraction;

/// <summary>
///     Port for extracting files from archives (ZIP, 7z, RAR, etc.).
/// </summary>
public interface IArchiveExtractor
{
    /// <summary>
    ///     Gets the supported file extensions for this extractor.
    /// </summary>
    IReadOnlyList<string> SupportedExtensions { get; }

    /// <summary>
    ///     Checks if this extractor can handle the given filename.
    /// </summary>
    bool CanHandle(string filename);

    /// <summary>
    ///     Enumerates all entries in the archive without extracting.
    /// </summary>
    IAsyncEnumerable<ArchiveEntry> EnumerateAsync(
        Stream archiveStream,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Extracts a specific entry from the archive.
    /// </summary>
    /// <param name="archiveStream">The archive stream (must be seekable).</param>
    /// <param name="entryPath">The path of the entry to extract.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A stream containing the extracted file content.</returns>
    Task<Stream> ExtractAsync(
        Stream archiveStream,
        string entryPath,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Extracts all entries from the archive to a target directory in a single pass, charging
    ///     the quota live: one entry per created file and per created directory (including
    ///     implicit parents at their creation site) and bytes per write chunk. Implementations
    ///     throw <see cref="ExtractionQuotaExceededException" /> the moment a limit is crossed,
    ///     leaving partial output for the caller to clean up, and
    ///     <see cref="ArchiveEntryCollisionException" /> when two entries normalize to the same
    ///     extraction target path.
    /// </summary>
    /// <param name="archiveStream">The archive stream.</param>
    /// <param name="targetDirectory">The directory to extract files to.</param>
    /// <param name="quota">Extraction quota charged as entries and bytes are created.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of extracted entries with their paths.</returns>
    Task<IReadOnlyList<ExtractedEntry>> ExtractAllAsync(
        Stream archiveStream,
        string targetDirectory,
        IExtractionQuota quota,
        CancellationToken cancellationToken = default);
}

/// <summary>
///     Represents a file extracted from an archive.
/// </summary>
public sealed record ExtractedEntry(
    string RelativePath,
    string AbsolutePath,
    long Size,
    bool IsDirectory);

/// <summary>
///     Represents an entry within an archive.
/// </summary>
public sealed record ArchiveEntry(
    string Path,
    long CompressedSize,
    long UncompressedSize,
    bool IsDirectory)
{
    /// <summary>
    ///     Gets the filename (without directory path).
    /// </summary>
    public string Name => System.IO.Path.GetFileName(Path.TrimEnd('/'));
}

/// <summary>
///     Thrown by extractors when two archive entries normalize to the same extraction target
///     path (duplicate names, "\" spellings, or "../" aliases). The archive is rejected: a
///     second write to the same path would silently replace the first entry's bytes and charge
///     the quota for an inode that does not exist. Callers convert this into their named
///     collision failure and clean the workspace.
/// </summary>
public sealed class ArchiveEntryCollisionException(string entryPath)
    : Exception($"Archive entry '{entryPath}' normalizes to the same extracted path as an earlier entry.")
{
    public string EntryPath { get; } = entryPath;
}

/// <summary>
///     Resolves the appropriate archive extractor for a given file.
/// </summary>
public interface IArchiveExtractorResolver
{
    /// <summary>
    ///     Gets an extractor that can handle the given filename, or null if unsupported.
    /// </summary>
    IArchiveExtractor? GetExtractor(string filename);

    /// <summary>
    ///     Checks if any extractor supports the given filename.
    /// </summary>
    bool IsArchive(string filename);

    /// <summary>
    ///     Gets all supported archive extensions.
    /// </summary>
    IReadOnlyList<string> SupportedExtensions { get; }
}
