using ErrorOr;

namespace Romd.Consumer.Application;

public static class ConsumerErrors
{
    public static Error CurrentUserRequired() =>
        Error.Unauthorized("Consumer.CurrentUserRequired", "An authenticated user is required.");

    public static Error InvalidCredentials() =>
        Error.Unauthorized("Consumer.InvalidCredentials", "Invalid credentials.");

    public static Error PasswordChangeFailed(string message) =>
        Error.Validation("Consumer.PasswordChangeFailed", message);

    public static Error InvalidSettings(string message) =>
        Error.Validation("Consumer.InvalidSettings", message);

    public static Error LibraryNotFound() =>
        Error.NotFound("Consumer.LibraryNotFound", "No library is available for the current user.");

    public static Error CurrentLibraryUnavailable() =>
        Error.Conflict(
            "Consumer.CurrentLibraryUnavailable",
            "No current Library projection is available for the authenticated user.");

    public static Error PlatformNotFound() =>
        Error.NotFound("Consumer.PlatformNotFound", "The requested platform was not found in the current library.");

    public static Error TitleNotFound() =>
        Error.NotFound("Consumer.TitleNotFound", "The requested title was not found in the current library.");

    public static Error CollectionNotFound() =>
        Error.NotFound("Consumer.CollectionNotFound", "Collection is not available for the current library.");

    public static Error ContentNotFound() =>
        Error.NotFound("Consumer.ContentNotFound", "The requested content could not be found.");

    public static Error InvalidContentGrant() =>
        Error.Unauthorized("Consumer.InvalidContentGrant", "The content grant is invalid.");

    public static Error ExpiredContentGrant() =>
        Error.Unauthorized("Consumer.ExpiredContentGrant", "The content grant has expired.");

    public static Error UnknownContentGrantKey() =>
        Error.Unauthorized("Consumer.UnknownContentGrantKey", "The content grant signing key is not recognized.");

    public static Error InvalidCursor() =>
        Error.Validation("Consumer.InvalidCursor", "The supplied cursor is invalid.");

    public static Error InvalidPlaySession(string message) =>
        Error.Validation("PlayActivity.InvalidSession", message);

    public static Error PlaySessionNotFound() =>
        Error.NotFound("PlayActivity.SessionNotFound", "The requested play session was not found.");

    public static Error PlaySessionConflict() =>
        Error.Conflict("PlayActivity.SessionConflict", "The session snapshot conflicts with previously accepted activity.");

}
