using ErrorOr;
using Romd.Admin.Application.Ingestion.Import;

namespace Romd.Infrastructure.Import;

/// <summary>
///     Pure preflight decisions for path imports: enforced file-count limit and an advisory
///     free-space check with headroom of max(1GB, 10% of source size). Extraction of compressed
///     inputs can exceed the nominal estimate, so the extraction step re-checks free space against
///     <see cref="ArchiveExtractionLimits.SpaceFloorBytes" />, validates each archive's declared
///     shape via <see cref="ValidateArchiveExpansion" />, pre-checks its declared totals against
///     an <see cref="ExtractionBudget" />, and charges that budget live as output is created.
/// </summary>
public static class ImportPreflight
{
    public const int MaxFileCount = 100_000;
    public const long MinimumHeadroomBytes = 1L << 30;

    public static ErrorOr<Success> Validate(int fileCount, long totalSourceBytes, long availableBytes)
    {
        if (fileCount > MaxFileCount)
        {
            return ImportErrors.TooManyFiles(fileCount, MaxFileCount);
        }

        long requiredBytes = totalSourceBytes + Math.Max(MinimumHeadroomBytes, totalSourceBytes / 10);
        return availableBytes >= requiredBytes
            ? Result.Success
            : ImportErrors.InsufficientSpace(requiredBytes, availableBytes);
    }

    /// <summary>
    ///     Guards a single archive's declared shape before extraction: entry count is capped per
    ///     archive and the uncompressed/compressed ratio is bounded against decompression bombs.
    ///     Whether the declared output fits the job's remaining allowance is the
    ///     <see cref="ExtractionBudget" />'s decision.
    /// </summary>
    public static ErrorOr<Success> ValidateArchiveExpansion(
        string archiveName,
        int entryCount,
        long totalUncompressedBytes,
        long compressedBytes,
        ArchiveExtractionLimits limits)
    {
        if (entryCount > limits.MaxPerArchiveEntries)
        {
            return ImportErrors.ArchiveEntryLimitExceeded(archiveName, entryCount, limits.MaxPerArchiveEntries);
        }

        return totalUncompressedBytes <= compressedBytes * limits.MaxExpansionRatio
            ? Result.Success
            : ImportErrors.ArchiveExpansionRatioExceeded(
                archiveName, totalUncompressedBytes, compressedBytes, limits.MaxExpansionRatio);
    }
}
