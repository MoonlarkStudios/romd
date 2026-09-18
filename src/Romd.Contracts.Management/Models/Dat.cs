namespace Romd.Contracts.Management.Models;

/// <summary>
///     A DAT file definition containing ROM metadata.
/// </summary>
public sealed record Dat
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string Type { get; init; }
    public string? SystemKey { get; init; }
    public string? Version { get; init; }
    public string? Author { get; init; }
    public string? Url { get; init; }
    public int GameCount { get; init; }
    public int RomCount { get; init; }
    public DateTimeOffset ImportedAt { get; init; }

    /// <summary>
    ///     The stable identity anchor shared by all versions of this DAT across replacements.
    /// </summary>
    public required string SourceId { get; init; }

    /// <summary>
    ///     Neutral catalog source identity — correlate with
    ///     <see cref="TitleSourceReference.CatalogSourceId" />; <see cref="SourceId" /> remains
    ///     the DAT-provider source id.
    /// </summary>
    public required string CatalogSourceId { get; init; }

    /// <summary>
    ///     Lifecycle of this version within its source. Listings return Active versions only;
    ///     superseded versions remain addressable by id for provenance.
    /// </summary>
    public required DatLifecycle Lifecycle { get; init; }

    /// <summary>
    ///     Lifecycle status of the DAT's catalog source; non-Active hides its references
    ///     from effective reads.
    /// </summary>
    public required SourceLifecycleStatus SourceStatus { get; init; }

    /// <summary>
    ///     The stored source DAT file in CAS (its sizes and compression). Null when the source file
    ///     is no longer present in storage.
    /// </summary>
    public StoredFileRef? SourceFile { get; init; }
}
