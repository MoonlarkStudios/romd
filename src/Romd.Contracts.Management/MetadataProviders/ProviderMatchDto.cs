using System.Text.Json.Serialization;

namespace Romd.Contracts.Management.MetadataProviders;

public sealed record ProviderGameDto(string Id, string Name, string Url,
    string? ThumbnailUrl = null, int? Year = null, IReadOnlyList<string>? Platforms = null, IReadOnlyList<string>? Aliases = null, IReadOnlyList<ProviderReleaseDto>? Releases = null);
public sealed record ProviderReleaseDto(int? Year, string? Platform);

/// <summary>Whether this query's observed response was exhausted; never a provider catalog or snapshot guarantee.</summary>
public enum ProviderGameSearchCompleteness { Complete, ProviderLimited, BudgetLimited }

/// <summary>
///     Bounded provider suggestions in live relevance order. Incomplete searches require a narrower query or explicit
///     id/page-URL resolution; their absence of another candidate cannot establish a unique automatic identity.
/// </summary>
public sealed record ProviderGameSearchResultDto(IReadOnlyList<ProviderGameDto> Items,
    ProviderGameSearchCompleteness Completeness, int Limit);

/// <summary>
///     One provider's match for a title. <see cref="Revision" /> is the match's validator, also sent as its ETag.
///     <see cref="PendingJobId" /> is the job a set or confirm queued or joined to apply the match; reads leave it
///     null.
/// </summary>
public sealed record TitleProviderMatchDto(string ProviderId, string ProviderName,
    bool IsAvailable, IReadOnlyList<string> Capabilities, Guid Revision,
    string State, string? ExternalId, ProviderGameDto? Game, float? Confidence,
    string? LastError, bool MetadataNeedsRefresh, string? Evidence = null, string? MatcherVersion = null, ProviderIdentityDecisionDto? Decision = null,
    Guid? PendingJobId = null);
public sealed record ProviderIdentityCandidateDto(ProviderGameDto Game, int Score, bool Eligible, IReadOnlyList<string> Evidence, IReadOnlyList<string> Conflicts);
public sealed record ProviderIdentityDecisionDto(string State, string Explanation, string Provenance, string InputFingerprint, IReadOnlyList<ProviderIdentityCandidateDto> Candidates, IReadOnlyList<ProviderIdentityDependencyDto>? Dependencies = null, ProviderIdentityFactsDto? LocalEvidence = null);
public sealed record ProviderIdentityFactsDto(string Name, IReadOnlyList<string> Names, IReadOnlyList<int> Years, IReadOnlyList<string> Platforms);
public sealed record ProviderIdentityDependencyDto(string ProviderId, string GameId, Guid Revision);
public sealed record SetProviderMatchRequest([property: JsonRequired] string ExternalId);
