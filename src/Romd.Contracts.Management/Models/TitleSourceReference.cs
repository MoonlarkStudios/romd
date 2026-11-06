namespace Romd.Contracts.Management.Models;

/// <summary>
///     One catalog source backing a title — derived from entry-level links, never stored.
///     Truth-level: every status is returned so curation surfaces can show dormant backing;
///     effective visibility is the effective reads' concern.
/// </summary>
public sealed record TitleSourceReference
{
    /// <summary>
    ///     The backing catalog source's public ID — neutral catalog source identity, distinct
    ///     from any provider-specific source id.
    /// </summary>
    public required string CatalogSourceId { get; init; }

    public required SourceKind Kind { get; init; }

    /// <summary>
    ///     Per-kind display name; null when the source has no resolvable display name.
    /// </summary>
    public string? Name { get; init; }

    public required SourceLifecycleStatus Status { get; init; }

    /// <summary>How many of the source's entries link to the title.</summary>
    public required int EntryCount { get; init; }
    public string? DatId { get; init; }
    public string? SystemKey { get; init; }
    public bool HasActiveDefinition { get; init; }
}
