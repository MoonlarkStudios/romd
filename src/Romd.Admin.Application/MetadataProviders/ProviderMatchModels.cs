namespace Romd.Admin.Application.MetadataProviders;

/// <summary>The current provider association and its supporting decision for one title.</summary>
public sealed record TitleProviderMatch(
    string ProviderId,
    string ProviderName,
    bool IsAvailable,
    IReadOnlyList<string> Capabilities,
    Guid Revision,
    string State,
    string? ExternalId,
    ProviderGame? Game,
    float? Confidence,
    string? LastError,
    bool MetadataNeedsRefresh,
    string? Evidence = null,
    string? MatcherVersion = null,
    ProviderIdentityDecision? Decision = null,
    Guid? PendingJobId = null);

public sealed record ProviderIdentityCandidate(
    ProviderGame Game,
    int Score,
    bool Eligible,
    IReadOnlyList<string> Evidence,
    IReadOnlyList<string> Conflicts);

public sealed record ProviderIdentityDecision(
    string State,
    string Explanation,
    string Provenance,
    string InputFingerprint,
    IReadOnlyList<ProviderIdentityCandidate> Candidates,
    IReadOnlyList<ProviderIdentityDependency>? Dependencies = null,
    ProviderIdentityFacts? LocalEvidence = null);

public sealed record ProviderIdentityFacts(string Name, IReadOnlyList<string> Names, IReadOnlyList<int> Years, IReadOnlyList<string> Platforms);
public sealed record ProviderIdentityDependency(string ProviderId, string GameId, Guid Revision);
