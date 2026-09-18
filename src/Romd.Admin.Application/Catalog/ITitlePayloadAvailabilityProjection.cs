using System.Collections.Immutable;
using Romd.Domain.Catalog;

namespace Romd.Admin.Application.Catalog;

/// <summary>
///     Sole owner of the canonical title-level local-payload projection. Mutation methods
///     require a caller-owned transaction so the source mutation and its derived fact commit
///     or roll back together.
/// </summary>
public interface ITitlePayloadAvailabilityProjection
{
    /// <summary>Refreshes provider assertions, then rolls up the affected titles.</summary>
    Task RefreshPayloadAssertionsAsync(
        IReadOnlyCollection<int> titleIds,
        CancellationToken cancellationToken = default);

    /// <summary>Refreshes exact provider-neutral source-entry assertions and their linked titles.</summary>
    Task RefreshSourceEntryPayloadAssertionsAsync(
        IReadOnlyCollection<int> sourceEntryIds,
        CancellationToken cancellationToken = default);

    /// <summary>Refreshes one source's assertions relationally, then rolls up its titles.</summary>
    Task RefreshCatalogSourcePayloadAssertionsAsync(
        int catalogSourceId,
        CancellationToken cancellationToken = default);

    /// <summary>Refreshes a platform's assertions relationally, then rolls up its titles.</summary>
    Task RefreshPlatformPayloadAssertionsAsync(
        int platformId,
        CancellationToken cancellationToken = default);

    /// <summary>Rolls up exact titles from persisted source-entry assertions.</summary>
    Task RollupTitlesAsync(
        IReadOnlyCollection<int> titleIds,
        CancellationToken cancellationToken = default);

    /// <summary>Rolls up a bounded keyset range without materializing or parameterizing its IDs.</summary>
    Task RollupTitleRangeAsync(
        int afterTitleId,
        int throughTitleId,
        CancellationToken cancellationToken = default);

    /// <summary>Rolls up a platform relationally from persisted source-entry assertions.</summary>
    Task RollupPlatformAsync(int platformId, CancellationToken cancellationToken = default);

    /// <summary>Rolls up titles linked to one source without materializing their IDs.</summary>
    Task RollupCatalogSourceAsync(int catalogSourceId, CancellationToken cancellationToken = default);

    Task<PayloadAvailabilityAuditResult> AuditBatchAsync(
        int? afterTitleId,
        int limit,
        CancellationToken cancellationToken = default);
}

/// <summary>Narrow provider-neutral local-payload assertion read.</summary>
public interface ICatalogPayloadAssertionReader
{
    Task<ImmutableArray<CatalogPayloadAssertion>> ReadAsync(
        IReadOnlyCollection<int> sourceEntryIds,
        CancellationToken cancellationToken = default);
}

/// <summary>Provider adapter for the narrow assertion read.</summary>
public interface ICatalogPayloadAssertionProvider
{
    CatalogSourceKind SourceKind { get; }

    Task<ImmutableArray<CatalogPayloadAssertion>> ReadAsync(
        IReadOnlyCollection<int> sourceEntryIds,
        CancellationToken cancellationToken = default);

    Task SynchronizeCatalogSourceAsync(
        int catalogSourceId,
        CancellationToken cancellationToken = default);

    Task SynchronizePlatformAsync(
        int platformId,
        CancellationToken cancellationToken = default);
}

/// <summary>Dispatches relational assertion synchronization to the provider that owns each source kind.</summary>
public interface ICatalogPayloadAssertionSynchronizer
{
    Task SynchronizeCatalogSourceAsync(
        int catalogSourceId,
        CancellationToken cancellationToken = default);

    Task SynchronizePlatformAsync(
        int platformId,
        CancellationToken cancellationToken = default);
}

public sealed record CatalogPayloadAssertion(
    int SourceEntryId,
    int? AssertedTitleId,
    bool HasLocalPayload);

public sealed record PayloadAvailabilityAuditResult(int ProcessedCount, int? NextTitleId);
