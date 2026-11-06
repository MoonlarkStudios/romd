using ErrorOr;
using Romd.Admin.Application.Ingestion.Classification;
using Romd.Application.Common.Validation;

namespace Romd.Admin.Application.Ingestion.Jobs;

public static class UploadErrors
{
    public static Error FileRequired() => FieldErrors.Validation(
        "Upload.FileRequired", "A non-empty file is required.", "file");

    public static Error FileTooLarge(long limit) => FieldErrors.Validation(
        "Upload.FileTooLarge", $"File must not exceed {limit} bytes.", "file");

    public static Error InvalidConcurrency() => FieldErrors.Validation(
        "Upload.InvalidConcurrency", "Parallel ROM processing must be between 1 and 32.", "maxParallelRoms");

    public static Error InvalidIdentity() => FieldErrors.Validation(
        "Upload.InvalidIdentity", "Provide both non-empty request and batch IDs, or neither.", "requestId");

    public static Error NoTrackedTitles() => FieldErrors.Validation(
        "Upload.NoTrackedTitles", "Track at least one title before importing tracked-title ROMs.", "trackedOnly");

    public static Error InvalidDatFile(FileType type) => FieldErrors.Validation(
        "Upload.InvalidDatFile", $"File does not appear to be a DAT file. Detected type: {type}", "file");

    public static Error InvalidRomFile() => FieldErrors.Validation(
        "Upload.InvalidRomFile", "File appears to be a DAT file. Use POST /upload/dat instead.", "file");

    public static Error RequestIdReused() =>
        Error.Conflict("Upload.RequestIdReused", "This upload request ID has already been used for another import.");

    public static Error InsufficientStorage() =>
        Error.Conflict("Upload.InsufficientStorage", "The server does not have enough free space to stage this upload.");
}
