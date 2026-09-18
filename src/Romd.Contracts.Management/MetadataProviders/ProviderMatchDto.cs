namespace Romd.Contracts.Management.MetadataProviders;

public sealed record ProviderGameDto(string Id, string Name, string Url,
    string? ThumbnailUrl = null, int? Year = null, IReadOnlyList<string>? Platforms = null);

public sealed record TitleProviderMatchDto(string ProviderId, string ProviderName,
    bool IsAvailable, IReadOnlyList<string> Capabilities, Guid Revision,
    string State, string? ExternalId, ProviderGameDto? Game, float? Confidence,
    string? LastError, bool MetadataNeedsRefresh);

public sealed record ProviderMatchSearchRequest(string Query, bool Resolve = false);
public sealed record SetProviderMatchRequest(Guid ExpectedRevision, string ExternalId);
public sealed record ChangeProviderMatchRequest(Guid ExpectedRevision);
