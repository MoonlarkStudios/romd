using ErrorOr;

namespace Romd.Admin.Application.Export;

public static class ExportErrors
{
    public static Error AlreadyInProgress() => Error.Conflict("Export.AlreadyInProgress", "An export is already in progress.");

    public static Error InvalidPaths() => Error.Validation("Export.InvalidPaths",
        "Archive paths are unsafe or collide. Correct the source before exporting.");

    public static Error FileUnavailable() => Error.Conflict("Export.FileUnavailable",
        "An archive file is no longer available. Refresh and try again.");

    public static Error CurrentUserRequired() =>
        Error.Unauthorized("Export.CurrentUserRequired", "An authenticated user is required.");

    public static Error LibraryRequired() =>
        Error.Forbidden(
            "Export.LibraryRequired",
            "A current, materialized library assignment is required to export content.");

    public static Error AdminRoleRequired() =>
        Error.Forbidden(
            "Export.AdminRoleRequired",
            "Only administrators can export another library or the entire catalog.");

    public static Error LibraryUnavailable() =>
        Error.Forbidden(
            "Export.LibraryUnavailable",
            "The selected library does not have a current materialized projection.");

    public static Error AccessDenied() =>
        Error.Forbidden("Export.AccessDenied", "You do not have access to this export.");

    public static Error NotFound() =>
        Error.NotFound("Export.NotFound", "Export not found.");

    public static Error FileNotFound() =>
        Error.NotFound("Export.FileNotFound", "The file was not found or is no longer stored.");
}
