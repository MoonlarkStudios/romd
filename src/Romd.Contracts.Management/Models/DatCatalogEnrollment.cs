namespace Romd.Contracts.Management.Models;

public sealed record PublishedDatCatalog(string CatalogId, string SystemId, string Name, string Provider,
    string Health, string? DocumentHash, int EntryCount, int FileCount, DateTimeOffset? LastChangedAt);
public sealed record DatCatalogSubscription(string Id, string CatalogId, string? SystemId, string Name,
    string? SystemKey, string? ActiveDatId, string State, DateTimeOffset? LastCheckedAt, string? Message,
    string? CandidateSha256, Guid? JobId, DateTimeOffset? NextCheckAt = null, bool AutomaticChecksPaused = false);
public sealed record DatCatalogDirectory(bool Enabled, string? Message, IReadOnlyList<PublishedDatCatalog> Catalogs);

/// <summary>Enrolls a publisher catalog; <c>DatId</c> optionally binds one installed active source.</summary>
public sealed record CreateDatSubscriptionRequest(string CatalogId, string? DatId = null);

/// <summary>
///     Approves the reviewed candidate. The two hashes are a snapshot binding of the reviewed pair, not a
///     precondition: a stale pair is a 409.
/// </summary>
public sealed record ApplyDatSubscription(string ActiveSha256, string CandidateSha256);
