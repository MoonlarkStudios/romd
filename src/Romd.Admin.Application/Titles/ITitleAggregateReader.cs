using Romd.Domain.Catalog;

namespace Romd.Admin.Application.Titles;

/// <summary>
///     The only port that returns a <see cref="Title" />. It always loads the complete aggregate, so a
///     title write can never receive an under-loaded snapshot that treats missing children as deletions.
///     Lookups that need less use the read ports and their read models instead.
/// </summary>
public interface ITitleAggregateReader
{
    /// <summary>
    ///     Gets the complete, detached title aggregate: external IDs, media, metadata layers, and
    ///     authoritative content ratings, with the revision that staged writes verify. <c>null</c> when
    ///     the title does not exist.
    /// </summary>
    Task<Title?> GetAggregateAsync(int titleId, CancellationToken cancellationToken = default);
}
