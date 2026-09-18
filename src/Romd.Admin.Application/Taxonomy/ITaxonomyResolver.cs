using Romd.Domain.Taxonomy;

namespace Romd.Admin.Application.Taxonomy;

/// <summary>
///     Resolves raw comma-separated tokens into canonical taxonomy entity IDs.
///     Auto-creates unknown tokens as new entities.
/// </summary>
public interface ITaxonomyResolver<TEntity> where TEntity : class, ITaxonomyEntity
{
    Task<IReadOnlyList<int>> ResolveAsync(string? rawInput, CancellationToken ct = default);
    void InvalidateCache();
}
