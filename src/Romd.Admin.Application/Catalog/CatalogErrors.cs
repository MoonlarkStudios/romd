using ErrorOr;
using Romd.Domain.Catalog.Ratings;
using Romd.Domain.Hashing;

namespace Romd.Admin.Application.Catalog;

public static class CatalogErrors
{
    // DAT-related errors
    public static Error DatNotFound(int id) =>
        Error.NotFound("Catalog.DatNotFound", $"DAT file with ID {id} not found");

    public static Error GameNotFound(int id) =>
        Error.NotFound("Catalog.GameNotFound", $"Game with ID {id} not found");

    public static Error GamePlatformRequired(int id) =>
        Error.Validation(
            "Catalog.GamePlatformRequired",
            $"Game with ID {id} belongs to an unrouted DAT and requires platform assignment");

    public static Error DatAlreadyExists(Sha256 fileSha256) =>
        Error.Conflict("Catalog.DatAlreadyExists",
            $"A DAT file with this content already exists (SHA-256: {fileSha256.ToShortHex()}...)");

    public static Error SourceNotFound(int id) =>
        Error.NotFound("Catalog.SourceNotFound", $"Catalog source with ID {id} not found");

    public static Error DatabaseFailed(string reason) =>
        Error.Failure("Catalog.DatabaseFailed", $"Database operation failed: {reason}");

    public static Error IngestFailed(string reason) =>
        Error.Failure("Catalog.IngestFailed", $"Failed to ingest DAT file: {reason}");

    public static Error PlatformNotFound(int platformId) =>
        Error.NotFound("Catalog.PlatformNotFound", $"Platform with ID {platformId} not found");

    public static Error TitleNotFound(int titleId) =>
        Error.NotFound("Catalog.TitleNotFound", $"Title with ID {titleId} not found");

    public static Error TitleNotFound() =>
        Error.NotFound("Catalog.TitleNotFound", "Title not found");

    public static Error TitleConflict() =>
        Error.Conflict("Catalog.TitleConflict", "The title was changed by another operation. Reload and try again.");

    public static Error TitleUpdateFailed() =>
        Error.Failure("Catalog.TitleUpdateFailed", "Title update failed");

    public static Error CatalogReleaseNotFound(int releaseId) =>
        Error.NotFound("Catalog.ReleaseNotFound", $"Catalog release with ID {releaseId} not found");

    public static Error PinnedReleaseTitleMismatch(int releaseId, int titleId) =>
        Error.Validation(
            "Catalog.PinnedReleaseTitleMismatch",
            $"Catalog release {releaseId} does not belong to title {titleId}");

    public static Error InvalidTrackingSelector() =>
        Error.Validation(
            "Catalog.InvalidTrackingSelector",
            "Specify exactly one tracking selector: titleIds, platformId, or datSourceId");

    public static Error MediaNotFound(int mediaId) =>
        Error.NotFound("Catalog.MediaNotFound", $"Media with ID {mediaId} not found");

    public static Error DatAlreadyHasPlatform(int datId) =>
        Error.Conflict("Catalog.DatAlreadyHasPlatform",
            $"DAT file with ID {datId} already has a platform assigned. Remove the DAT and re-import to change platform.");

    // ROM-related errors (kept with Library.* codes for backward compatibility)
    public static Error RomNotFound() =>
        Error.NotFound("Library.RomNotFound", "ROM file not found");

    public static Error RomNotFoundBySha1(Sha1 sha1) =>
        Error.NotFound("Library.RomNotFoundBySha1", $"ROM file with SHA-1 {sha1.ToShortHex()}... not found");

    public static Error RomStorageFailed(string reason) =>
        Error.Failure("Library.StorageFailed", $"Failed to store ROM file: {reason}");

    public static Error RomStorageFailed() =>
        Error.Failure("Library.StorageFailed", "ROM storage operation failed");

    public static Error RomDatabaseFailed(string reason) =>
        Error.Failure("Library.DatabaseFailed", $"Database operation failed: {reason}");

    public static Error RomDatabaseFailed() =>
        Error.Failure("Library.DatabaseFailed", "Database operation failed");

    public static Error RomIngestFailed(string reason) =>
        Error.Failure("Library.IngestFailed", $"Failed to ingest ROM file: {reason}");

    /// <summary>
    ///     Returned when an uploaded file does not match any entry in imported DAT files.
    ///     The file is not stored - the user should import the appropriate DAT first.
    /// </summary>
    public static Error UnidentifiedRom(Sha1 sha1, Md5 md5, Crc32 crc32, long size) =>
        Error.Validation(
            "Library.UnidentifiedRom",
            $"File does not match any DAT entry. Import the appropriate DAT file first. " +
            $"SHA1: {sha1}, MD5: {md5}, CRC32: {crc32}, Size: {size}");

    // Enrichment-related errors
    public static Error EnrichmentRequestFailed() =>
        Error.Failure("Enrichment.RequestFailed", "Enrichment request failed");

    public static Error ProviderNotFound(string providerId) =>
        Error.NotFound("Enrichment.ProviderNotFound", $"Metadata provider '{providerId}' not found");

    public static Error ProviderNotConfigured(string providerId) =>
        Error.Failure("Enrichment.ProviderNotConfigured", $"Metadata provider '{providerId}' is not configured");

    // Curation-related errors
    public static Error MergeIntoSelf() =>
        Error.Validation("Catalog.MergeIntoSelf", "Cannot merge a title into itself");

    public static Error PlatformMismatch(int sourcePlatformId, int targetPlatformId) =>
        Error.Validation("Catalog.PlatformMismatch",
            $"Cannot merge/move across platforms. Source: {sourcePlatformId}, Target: {targetPlatformId}");

    public static Error InvalidMoveParameters() =>
        Error.Validation("Catalog.InvalidMoveParameters",
            "Specify either TargetTitleId OR NewTitleName, not both");

    public static Error TitleAlreadyExists(string normalizedName) =>
        Error.Conflict("Catalog.TitleAlreadyExists",
            $"A title with normalized name '{normalizedName}' already exists on this platform");

    // Content rating errors
    public static Error InvalidContentRating(RatingBoard board, string code) =>
        Error.Validation("Catalog.InvalidContentRating",
            $"'{code}' is not a recognized {board} content rating code.");
}
