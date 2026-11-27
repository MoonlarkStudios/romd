using System.Collections.Frozen;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Domain.Catalog;
namespace Romd.Admin.Application.MetadataProviders;

public sealed record ProviderIdentitySnapshot(int TitleId, Guid TitleRevision, string Fingerprint, IdentityFacts Facts,
    IReadOnlyList<ProviderIdentityLink> Links)
{
    public DatFamilySearchEvidence? DatFamily { get; init; }
    public DatParentMatchEvidence? DatParent { get; init; }
    /// <summary>Declared DAT release names mapped to their unqualified lookup names.</summary>
    public IReadOnlyDictionary<string, string> DatLookupNames { get; init; } = FrozenDictionary<string, string>.Empty;
    public IReadOnlyList<ArtworkPlatformAlias> ArtworkPlatformAliases { get; init; } = [];
}

/// <summary>Hash-backed DAT names for discovery, or a requirement for curator review.</summary>
public sealed record DatFamilySearchEvidence(IReadOnlyList<string> Names, bool RequiresReview)
{
    public static DatFamilySearchEvidence Ambiguous { get; } = new([], true);
    public DatParentRelease? Parent { get; init; }
}

public sealed record DatParentRelease(int DatFileId, int GameId, string Name);
public sealed record DatParentMatch(int TitleId, int DatFileId, int DatGameId, string GameId, Guid Revision);
public sealed record DatParentMatchEvidence(DatParentMatch Match, string TitleName, string ReleaseName);
public sealed record ArtworkPlatformAlias(int Id, string Name);

public sealed record ProviderIdentityLink(string ProviderId, string? GameId, bool Confirmed, bool Suppressed,
    bool Trusted, Guid Revision, string? InputFingerprint, DateTimeOffset? LastAttempt, ProviderIdentityDecision? Decision)
{
    public ProviderGame? TrustedGame { get; init; }
}
/// <summary>Provider-local retry hints. Callers decide whether alternatives satisfy their unfinished work.</summary>
public sealed record ProviderIdentityDiscoveryResult(IReadOnlyDictionary<string, DateTimeOffset> RetryAfterByProvider)
{
    public static ProviderIdentityDiscoveryResult Empty { get; } = new(
        System.Collections.Frozen.FrozenDictionary<string, DateTimeOffset>.Empty);
}
public interface IProviderIdentityStore
{
    Task<ProviderIdentitySnapshot?> ReadAsync(int titleId, CancellationToken ct);
    /// <summary>Current trusted identity in one database statement, including suppression and live evidence checks.</summary>
    Task<string?> ReadTrustedAsync(int titleId, string providerId, CancellationToken ct);
    /// <summary>Stages a conditional decision in the caller's ReadCommitted transaction.</summary>
    Task<IStagedProviderIdentityDecision> StageDecisionAsync(ProviderIdentitySnapshot snapshot, string providerId, Guid expectedRevision,
        ProviderIdentityDecision decision, ProviderGame? accepted, CancellationToken ct);
}
/// <summary>Retain through the owner's commit or rollback; disposal releases only this decision's tracking.</summary>
public interface IStagedProviderIdentityDecision : IDisposable
{
    bool Applied { get; }
}
public interface IProviderIdentityService
{
    Task<string?> GetTrustedAsync(int titleId, string providerId, CancellationToken ct);
    Task<ProviderIdentityDiscoveryResult> DiscoverAsync(int titleId, JobContext? owner, CancellationToken ct);
    Task<string?> ResolveTrustedAsync(int titleId, string providerId, CancellationToken ct);
}
