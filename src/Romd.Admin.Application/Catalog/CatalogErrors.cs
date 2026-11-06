using ErrorOr;
using Romd.Domain.Catalog.Ratings;
using Romd.Domain.Hashing;

namespace Romd.Admin.Application.Catalog;

/// <summary>A not-found answer is public, so its detail names no internal id.</summary>
public static class CatalogErrors
{
    public static Error UntrackedRom() =>
        Error.Validation("Library.UntrackedRom", "Skipped: ROM does not match a currently tracked title.");

    // DAT-related errors
    public static Error DatNotFound() =>
        Error.NotFound("Catalog.DatNotFound", "DAT file not found");

    public static Error GameNotFound() =>
        Error.NotFound("Catalog.GameNotFound", "Game not found");

    public static Error GamePlatformRequired() =>
        Error.Validation(
            "Catalog.GamePlatformRequired",
            "The game belongs to an unrouted DAT and requires system assignment");

    public static Error DatAlreadyExists(Sha256 fileSha256) =>
        Error.Conflict("Catalog.DatAlreadyExists",
            $"A DAT file with this content already exists (SHA-256: {fileSha256.ToShortHex()}...)");

    public static Error SourceNotFound() =>
        Error.NotFound("Catalog.SourceNotFound", "Catalog source not found");

    public static Error DatabaseFailed(string reason) =>
        Error.Failure("Catalog.DatabaseFailed", $"Database operation failed: {reason}");

    public static Error IngestFailed(string reason) =>
        Error.Failure("Catalog.IngestFailed", $"Failed to ingest DAT file: {reason}");

    public static Error PlatformNotFound() =>
        Error.NotFound("Catalog.PlatformNotFound", "Platform not found");

    public static Error TitleNotFound() =>
        Error.NotFound("Catalog.TitleNotFound", "Title not found");

    /// <summary>409: repeated concurrent edits prevented a consistent title read.</summary>
    public static Error TitleReadConflict() =>
        Error.Conflict("Titles.ReadConflict", "The title changed while it was being read. Retry the request.");

    /// <summary>
    ///     412: the title's <c>If-Match</c> validator is no longer current. Nothing was written; the client reloads
    ///     and applies its change again (docs/decisions/api-contract-policy.md, "Idempotency And Concurrency").
    /// </summary>
    public static Error TitlePreconditionFailed() =>
        Error.Custom(412, "Titles.PreconditionFailed",
            "The title changed since it was read. Reload it, then apply the change again.");

    public const string MergeSourceChangedCode = "Titles.MergeSourceChanged";

    /// <summary>409: the batch revision of a merge's source title is stale.</summary>
    public static Error MergeSourceChanged() =>
        Error.Conflict(MergeSourceChangedCode, "The source title changed since it was read. Reload it, then merge again.");

    public static Error TitleUpdateFailed() =>
        Error.Failure("Catalog.TitleUpdateFailed", "Title update failed");

    public static Error CatalogReleaseNotFound() =>
        Error.NotFound("Catalog.ReleaseNotFound", "Catalog release not found");

    public static Error PinnedReleaseTitleMismatch() =>
        Error.Validation(
            "Catalog.PinnedReleaseTitleMismatch",
            "The catalog release does not belong to the title");

    public static Error InvalidTrackingSelector() =>
        Error.Validation(
            "Catalog.InvalidTrackingSelector",
            "Specify exactly one tracking selector: titleIds, platformId, or datSourceId");

    public static Error MediaNotFound() =>
        Error.NotFound("Catalog.MediaNotFound", "Media not found");

    public static Error MediaContentNotFound() =>
        Error.NotFound("Catalog.MediaContentNotFound", "Stored content for the media not found");

    public static Error DatAlreadyHasPlatform() =>
        Error.Conflict("Catalog.DatAlreadyHasPlatform",
            "The DAT already has a system assigned. Remove the DAT and re-import to change its system.");

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

    // Curation-related errors
    public static Error MergeIntoSelf() =>
        Error.Validation("Catalog.MergeIntoSelf", "Cannot merge a title into itself");

    public static Error PlatformMismatch() =>
        Error.Validation("Catalog.PlatformMismatch",
            "Cannot merge or move across different systems.");

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
