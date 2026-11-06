namespace Romd.Admin.Application.Taxonomy.ReadModels;

/// <summary>One alias of a region or language and who owns it: <c>Romd</c>, <c>Installation</c> or <c>Unresolved</c>.</summary>
public sealed record TaxonomyAlias(int Id, string Alias, string Ownership);

/// <summary>
///     A row's first aliases in ascending id order, at most <see cref="TaxonomyAliasPageRequest.EmbeddedLimit" />;
///     <see cref="HasNextPage" /> says whether its alias collection continues after the last of them.
/// </summary>
public sealed record TaxonomyAliasPage(IReadOnlyList<TaxonomyAlias> Items, bool HasNextPage);

/// <summary>
///     The admin management projection of one region or language row, registered or not. <see cref="Revision" />
///     versions the whole projection, its embedded first alias page included. <see cref="Code" /> is set for languages
///     only.
/// </summary>
public sealed record TaxonomyEntry(
    string Key,
    string? Ownership,
    int? BuiltInVersion,
    string Name,
    string? Description,
    int SortOrder,
    bool Retired,
    bool IsAutoCreated,
    string? Code,
    TaxonomyAliasPage Aliases,
    string Revision)
{
    /// <summary>Only unregistered rows merge, the same predicate the merge guard applies.</summary>
    public bool CanMerge => Ownership is null;
}
