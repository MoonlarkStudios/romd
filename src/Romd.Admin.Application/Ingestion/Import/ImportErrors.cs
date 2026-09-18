using ErrorOr;

namespace Romd.Admin.Application.Ingestion.Import;

public static class ImportErrors
{
    public static Error FeatureDisabled() =>
        Error.Validation(
            "Import.FeatureDisabled",
            "Server-side path import is disabled. Configure Romd:AllowedImportPaths with at least one absolute directory to enable it.");

    public static Error PathNotAbsolute(string path) =>
        Error.Validation("Import.PathNotAbsolute", $"Import path must be absolute: '{path}'");

    public static Error PathNotAllowed(string path) =>
        Error.Validation(
            "Import.PathNotAllowed",
            $"Import path is not under any configured Romd:AllowedImportPaths entry: '{path}'");

    public static Error LinkRejected(string path) =>
        Error.Validation(
            "Import.LinkRejected",
            $"Import path contains a symbolic link, junction, or other reparse point: '{path}'");

    public static Error PathNotFound(string path) =>
        Error.NotFound("Import.PathNotFound", $"Import path does not exist: '{path}'");

    public static Error NoFilesFound(string path) =>
        Error.Validation("Import.NoFilesFound", $"No importable files were found under '{path}'");

    public static Error TooManyFiles(int fileCount, int maxFileCount) =>
        Error.Validation(
            "Import.TooManyFiles",
            $"Import source contains {fileCount:N0} files, which exceeds the limit of {maxFileCount:N0}.");

    public static Error InsufficientSpace(long requiredBytes, long availableBytes) =>
        Error.Validation(
            "Import.InsufficientSpace",
            $"Insufficient disk space on the workspace volume: {requiredBytes:N0} bytes required (source size plus headroom), {availableBytes:N0} bytes available.");

    public static Error ArchiveEntryLimitExceeded(string archiveName, int entryCount, int maxEntryCount) =>
        Error.Validation(
            "Import.ArchiveEntryLimitExceeded",
            $"Archive '{archiveName}' contains {entryCount:N0} entries, which exceeds the limit of {maxEntryCount:N0} per archive.");

    public static Error ArchiveExpansionRatioExceeded(
        string archiveName,
        long uncompressedBytes,
        long compressedBytes,
        long maxRatio) =>
        Error.Validation(
            "Import.ArchiveExpansionRatioExceeded",
            $"Archive '{archiveName}' declares {uncompressedBytes:N0} uncompressed bytes from {compressedBytes:N0} compressed bytes, which exceeds the {maxRatio}:1 expansion limit.");

    public static Error ArchiveExpansionExceedsSpace(string archiveName, long uncompressedBytes, long remainingBytes) =>
        Error.Validation(
            "Import.ArchiveExpansionExceedsSpace",
            $"Archive '{archiveName}' declares {uncompressedBytes:N0} uncompressed bytes, but only {remainingBytes:N0} bytes remain in the import's extraction space budget (workspace free space minus the reserved floor and bytes already extracted).");

    public static Error ArchiveEntryCollision(string archiveName, string entryPath) =>
        Error.Validation(
            "Import.ArchiveEntryCollision",
            $"Archive '{archiveName}' entry '{entryPath}' normalizes to the same extracted path as another entry; the archive was rejected to prevent overwriting extracted bytes.");

    public static Error ExtractionBudgetExceeded(string archiveName, long declaredEntries, long remainingEntries) =>
        Error.Validation(
            "Import.ExtractionBudgetExceeded",
            $"Archive '{archiveName}' declares {declaredEntries:N0} entries, but only {remainingEntries:N0} remain in the import-wide extraction budget.");

    public static Error ExtractionBudgetExceededDuringExtraction(
        string archiveName,
        long remainingEntries,
        long remainingBytes) =>
        Error.Validation(
            "Import.ExtractionBudgetExceeded",
            $"Archive '{archiveName}' exceeded the import-wide extraction budget while extracting ({remainingEntries:N0} entries and {remainingBytes:N0} bytes remained); output was stopped at the budget boundary.");
}
