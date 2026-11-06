using ErrorOr;
using Romd.Consumer.Application.Libraries;

namespace Romd.Consumer.Application.Vfs;

public static class ConsumerVfsErrors
{
    public static Error NotFound() => Error.NotFound(
        "Consumer.Vfs.NotFound", "The requested VFS entry is unavailable.");

    public static Error InconsistentProjection() => Error.Conflict(
        "Consumer.Vfs.InconsistentProjection", "The library projection changed or is inconsistent.");

    public static Error EnumerationLimit() => Error.Failure(
        "Consumer.Vfs.EnumerationLimit", "The directory exceeds the configured enumeration limit.");

    public static Error InvalidLayout() => Error.Conflict(
        "Consumer.Vfs.InvalidLayout",
        "The release contains unsupported or conflicting file paths or invalid content metadata.");

    public static Error FromRead<T>(ConsumerLibraryReadResult<T> result) where T : notnull => result switch
    {
        ConsumerLibraryReadResult<T>.Rejected rejected => rejected.Error,
        ConsumerLibraryReadResult<T>.ProjectionInconsistent => InconsistentProjection(),
        _ => NotFound()
    };
}
