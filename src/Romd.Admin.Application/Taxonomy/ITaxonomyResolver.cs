using Romd.Domain.Taxonomy;

namespace Romd.Admin.Application.Taxonomy;

/// <summary>
///     Resolves raw comma-separated tokens into canonical taxonomy entity IDs.
///     Auto-creates unknown tokens as new entities. One instance serves one ingest transaction: the ids it returns
///     stay valid only until that transaction ends.
/// </summary>
public interface ITaxonomyResolver<TEntity> where TEntity : class, ITaxonomyEntity
{
    Task<IReadOnlyList<int>> ResolveAsync(string? rawInput, CancellationToken ct = default);
}
