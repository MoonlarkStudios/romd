namespace Romd.Admin.Application.Common.Persistence;

/// <summary>
///     A stale persistence snapshot could not be written. Interactive use cases should return
///     a conflict; background work must reload and recompute before retrying.
/// </summary>
public sealed class PersistenceConflictException : Exception
{
    public PersistenceConflictException(Exception innerException)
        : base("The record was changed by another operation. Reload and try again.", innerException)
    {
    }
}
