namespace Romd.Admin.Application.Export;

public interface IExportAuthorizationReader
{
    /// <summary>
    ///     Resolves the user's current persisted library assignment only when the library is valid
    ///     and its materialized projection is current enough to authorize delivery.
    /// </summary>
    Task<ExportScope.Library?> GetEffectiveLibraryScopeAsync(
        Guid userId,
        CancellationToken ct = default);

    /// <summary>
    ///     Resolves a specific library only when its configuration and materialized projection
    ///     are current. Used when an administrator explicitly selects a library scope.
    /// </summary>
    Task<ExportScope.Library?> GetCurrentLibraryScopeAsync(
        int libraryId,
        CancellationToken ct = default);
}
