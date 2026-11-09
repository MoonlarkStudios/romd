using System.Text.Json.Serialization;

namespace Romd.Contracts.Management.MetadataProviders;

public sealed record ProviderGame(string Id, string Name, string Url,
    string? ThumbnailUrl = null, int? Year = null, IReadOnlyList<string>? Platforms = null, IReadOnlyList<string>? Aliases = null, IReadOnlyList<ProviderRelease>? Releases = null);
public sealed record ProviderRelease(int? Year, string? Platform);

/// <summary>Whether this query's observed response was exhausted; never a provider catalog or snapshot guarantee.</summary>
public enum ProviderGameSearchCompleteness { Complete, ProviderLimited, BudgetLimited }

/// <summary>
///     Bounded provider suggestions in live relevance order. Incomplete searches require a narrower query or explicit
///     id/page-URL resolution; their absence of another candidate cannot establish a unique automatic identity.
/// </summary>
public sealed record ProviderGameSearchResult(IReadOnlyList<ProviderGame> Items,
    ProviderGameSearchCompleteness Completeness, int Limit);

/// <summary>
///     One provider's match for a title. <see cref="Revision" /> is the match's validator, also sent as its ETag.
///     <see cref="PendingJobId" /> is the job a set or confirm queued or joined to apply the match; reads leave it
///     null.
/// </summary>
public sealed record TitleProviderMatch(string ProviderId, string ProviderName,
    bool IsAvailable, IReadOnlyList<string> Capabilities, Guid Revision,
    string State, string? ExternalId, ProviderGame? Game, float? Confidence,
    string? LastError, bool MetadataNeedsRefresh, string? Evidence = null, string? MatcherVersion = null, ProviderIdentityDecision? Decision = null,
    Guid? PendingJobId = null);
public sealed record ProviderIdentityCandidate(ProviderGame Game, int Score, bool Eligible, IReadOnlyList<string> Evidence, IReadOnlyList<string> Conflicts);
public sealed record ProviderIdentityDecision(string State, string Explanation, string Provenance, string InputFingerprint, IReadOnlyList<ProviderIdentityCandidate> Candidates, IReadOnlyList<ProviderIdentityDependency>? Dependencies = null, ProviderIdentityFacts? LocalEvidence = null);
public sealed record ProviderIdentityFacts(string Name, IReadOnlyList<string> Names, IReadOnlyList<int> Years, IReadOnlyList<string> Platforms);
public sealed record ProviderIdentityDependency(string ProviderId, string GameId, Guid Revision);
public sealed record SetProviderMatchRequest([property: JsonRequired] string ExternalId);
