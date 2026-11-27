using System.Text.Json.Serialization;
using Romd.Domain.Catalog;
using Romd.Domain.Catalog.Ratings;

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
    Guid? PendingJobId = null,
    ProvisionalMatchReview? ProvisionalReview = null);

public sealed record ProvisionalMatchReview(string GameId, bool EvidenceCurrent,
    ProvisionalMetadataFields? Fields, IReadOnlyList<ProvisionalMatchRating> Ratings,
    IReadOnlyList<ProvisionalMatchArtwork> Artwork);

public sealed record ProvisionalMetadataFields(string? Description, string? Publisher, string? Developer,
    string? Genre, DateOnly? ReleaseDate, int? Players, double? Rating);

public sealed record ProvisionalMatchRating(RatingBoard Board, string Code);

public sealed record ProvisionalMatchArtwork(ArtworkRole Role, int AssetId, string? Variant, string? Version);

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
    ProviderIdentityFacts? LocalEvidence = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProvisionalGameId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("PosterPolicyVersion")]
    public string? ProvisionalPolicyVersion { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ArtworkReleasePolicyVersion { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? IgdbRetailPolicyVersion { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool RequiresIgdbRetailEvidence { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DatParentMatch? DatParentMatch { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DatParentMatch? EvaluatedDatParent { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ArtworkPlatformAlias>? ArtworkPlatformAliases { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ArtworkPlatformAlias>? EvaluatedArtworkPlatformAliases { get; init; }
}

public sealed record ProviderIdentityFacts(string Name, IReadOnlyList<string> Names, IReadOnlyList<int> Years, IReadOnlyList<string> Platforms);
public sealed record ProviderIdentityDependency(string ProviderId, string GameId, Guid Revision);
