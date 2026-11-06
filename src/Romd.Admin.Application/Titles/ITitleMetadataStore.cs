using Romd.Domain.Catalog;

namespace Romd.Admin.Application.Titles;

/// <summary>
///     Port for title metadata state: effective fields, provenance, overrides, metadata layers, provider
///     links, and enrichment results. Every write requires the caller's transaction, verifies and locks
///     the snapshot revision, rotates it, and leaves flush and commit to the caller.
/// </summary>
public interface ITitleMetadataStore
{
    /// <summary>
    ///     Stages an enrichment result: upserts the snapshot's provider links and metadata layers,
    ///     inserts newly attached media, and writes rematerialized state as
    ///     <see cref="UpdateMaterializedMetadataStagedAsync" /> does. Additive: children absent from
    ///     the snapshot are never deleted. The caller owns the flush and commit.
    /// </summary>
    Task ApplyEnrichmentStagedAsync(Title title, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Stages rematerialized metadata, provenance, field overrides, primary-media selection,
    ///     and authoritative content ratings without replacing persistence-only title state or
    ///     owning the flush.
    /// </summary>
    Task UpdateMaterializedMetadataStagedAsync(Title title, CancellationToken cancellationToken = default);

    /// <summary>Stages the user metadata layer and rematerialized state in the caller-owned transaction.</summary>
    Task UpdateUserMetadataStagedAsync(Title title, CancellationToken cancellationToken = default);
}
