using Romd.Domain.Catalog;

namespace Romd.Admin.Application.Catalog;

/// <summary>
///     Reads the distinct catalog sources backing a title — derived from entry-level links,
///     never stored (design rule 1, docs/decisions/neutral-source-identity.md). Truth-level:
///     returns every status so curation surfaces can show dormant backing; effective
///     visibility is the effective reads' concern.
/// </summary>
public interface ITitleSourceReferenceReader
{
    Task<IReadOnlyList<TitleSourceReference>> GetReferencesAsync(
        int titleId,
        CancellationToken cancellationToken = default);
}

/// <summary>
///     One backing source: identity, kind, per-kind display name (null when the provider has
///     no resolvable display name), status, and how many of its entries link to the title.
/// </summary>
public sealed record TitleSourceReference(
    int CatalogSourceId,
    CatalogSourceKind Kind,
    string? Name,
    CatalogSourceStatus Status,
    int EntryCount, int? DatId = null, int? PlatformId = null, bool HasActiveDefinition = false);
