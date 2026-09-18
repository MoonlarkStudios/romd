namespace Romd.Contracts.Management.Models;

public sealed record PublishedDatCatalogDto(string CatalogId, string SystemId, string Name, string Provider,
    string Health, string? DocumentHash, int EntryCount, int FileCount, DateTimeOffset? LastChangedAt);
public sealed record DatCatalogSubscriptionDto(string Id, string CatalogId, string? SystemId, string Name,
    string? SystemKey, string? ActiveDatId, string State, DateTimeOffset? LastCheckedAt, string? Message,
    string? CandidateSha256, Guid? JobId, DateTimeOffset? NextCheckAt = null, bool AutomaticChecksPaused = false);
public sealed record DatCatalogDirectoryDto(bool Enabled, string? Message,
    IReadOnlyList<PublishedDatCatalogDto> Catalogs, IReadOnlyList<DatCatalogSubscriptionDto> Subscriptions);
public sealed record CheckDatCatalogRequest(string CatalogId);
