using Romd.Application.Common.Pagination;
using Romd.Admin.Application.Titles.ReadModels;

namespace Romd.Admin.Application.Titles;

/// <summary>
///     Read port for scalar title facts on the admin surface. Returns read models, never a
///     <see cref="Romd.Domain.Catalog.Title" /> aggregate, so a result cannot be passed to a title write.
/// </summary>
public interface ITitleReader
{
    /// <summary>Gets one title's summary, or <c>null</c> when the title does not exist.</summary>
    Task<TitleSummary?> GetSummaryAsync(int titleId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Gets a title's current revision without locking it, or <c>null</c> when the title does not exist. A write
    ///     uses it only to fail a stale precondition early; its authoritative compare runs under the title lock.
    /// </summary>
    Task<Guid?> GetRevisionAsync(int titleId, CancellationToken cancellationToken = default);

    /// <summary>Reads a bounded platform-title page after applying the request's library and payload scope.</summary>
    Task<PagedList<TitleSummary>> ListByPlatformAsync(
        PlatformTitlePageRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Gets every title ID on a platform.</summary>
    Task<IReadOnlyList<int>> GetIdsByPlatformAsync(int platformId, CancellationToken cancellationToken = default);
}
