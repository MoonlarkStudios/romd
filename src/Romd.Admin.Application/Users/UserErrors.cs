using ErrorOr;
using Romd.Application.Common.Validation;

namespace Romd.Admin.Application.Users;

public static class UserErrors
{
    public static Error NotFound() =>
        Error.NotFound("Users.NotFound", "User not found.");

    public static Error MissingRequiredFields() =>
        Error.Validation("Users.MissingRequiredFields", "Email and password are required.");

    public static Error InvalidRole(string role) =>
        FieldErrors.Validation(
            "Users.InvalidRole",
            $"'{role}' is not a valid role. Accepted values: User, Contributor, Manager, Admin.",
            "role");

    public static Error DuplicateEmail(string email) =>
        Error.Validation("Users.DuplicateEmail", $"A user with email {email} already exists.");

    public static Error CreateFailed() =>
        Error.Validation("Users.CreateFailed", "Failed to create user.");

    public static Error AssignRoleFailed() =>
        Error.Validation("Users.AssignRoleFailed", "Failed to assign role.");

    public static Error UpdateEmailFailed() =>
        Error.Validation("Users.UpdateEmailFailed", "Failed to update email.");

    public static Error UpdatePasswordFailed() =>
        Error.Validation("Users.UpdatePasswordFailed", "Failed to update password.");

    public static Error UpdateFailed() =>
        Error.Failure("Users.UpdateFailed", "User update failed.");

    public static Error DeleteFailed() =>
        Error.Failure("Users.DeleteFailed", "User deletion failed.");

    public static Error InvalidLibraryId(string libraryId) =>
        FieldErrors.Validation("Users.InvalidLibraryId", $"'{libraryId}' is not a valid library ID.", "libraryId");

    public static Error LibraryNotFound() =>
        FieldErrors.Validation("Users.LibraryNotFound", "Library not found.", "libraryId");

    public static Error InvalidLibraryConfiguration() =>
        Error.Conflict(
            "Users.LibraryConfigurationInvalid",
            "Library configuration is invalid and cannot be assigned.");

    public static Error AssignLibraryFailed() =>
        Error.Validation("Users.AssignLibraryFailed", "Failed to assign library.");

    public static Error NoDefaultLibrary() =>
        Error.Conflict("Users.NoDefaultLibrary", "No default library is configured.");

    public static Error ProtectedUser(string detail) =>
        Error.Conflict("Users.ProtectedUser", detail);

    public static Error PersistenceFailed() =>
        Error.Failure("Users.PersistenceFailed", "User operation failed.");
}
