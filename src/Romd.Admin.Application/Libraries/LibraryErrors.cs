using ErrorOr;

namespace Romd.Admin.Application.Libraries;

public static class LibraryErrors
{
    public static Error NotFound() =>
        Error.NotFound("Libraries.NotFound", "Library not found.");

    public static Error NameRequired() =>
        Error.Validation("Libraries.NameRequired", "Name is required.");

    public static Error InvalidConfiguration() =>
        Error.Validation("Libraries.InvalidConfiguration", "Library configuration is invalid.");

    public static Error DefaultLibraryUndeletable() =>
        Error.Conflict(
            "Libraries.DefaultLibraryUndeletable",
            "Cannot delete the default library. Assign another library as default or clear the flag first.");

    public static Error PersistenceFailed() =>
        Error.Failure("Libraries.PersistenceFailed", "Library operation failed.");
}
