using ErrorOr;

namespace Romd.Admin.Application.Export;

public static class ExportErrors
{
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
}
