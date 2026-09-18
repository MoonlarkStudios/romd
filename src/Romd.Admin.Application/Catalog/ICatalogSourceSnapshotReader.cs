using System.Collections.Immutable;
using Romd.Domain.Catalog;
using Romd.Domain.Hashing;

namespace Romd.Admin.Application.Catalog;

/// <summary>
///     Catalog-owned read boundary for immutable source facts used to derive canonical release
///     projections. Provider adapters own their payload/source joins and expose only neutral
///     identities, metadata, requirements, taxonomy, and provenance through this contract.
/// </summary>
public interface ICatalogSourceSnapshotReader
{
    Task<CatalogSourceSnapshot> ReadPlatformAsync(
        int platformId,
        CancellationToken cancellationToken = default);
}

/// <summary>
///     Provider adapter seam for catalog source facts. Each provider owns the joins from its
///     payload model to catalog identities; the catalog reader composes every registered
///     provider into one transaction-coherent snapshot.
/// </summary>
public interface ICatalogSourceSnapshotProvider
{
    Task<CatalogSourceSnapshot> ReadPlatformAsync(
        int platformId,
        CancellationToken cancellationToken = default);
}

public sealed record CatalogSourceSnapshot(ImmutableArray<CatalogSourceEntrySnapshot> Entries)
{
    public static CatalogSourceSnapshot Empty { get; } = new(ImmutableArray<CatalogSourceEntrySnapshot>.Empty);
}

public sealed record CatalogSourceEntrySnapshot(
    int SourceEntryId,
    CatalogSourceKind SourceKind,
    int? AssertedTitleId,
    bool HasLocalPayload,
    ImmutableArray<CatalogSourceClaimSnapshot> Claims);

public sealed record CatalogSourceClaimSnapshot(
    string ProviderClaimKey,
    int ClaimPrecedence,
    long ClaimOrder,
    string Name,
    string? Region,
    string? Language,
    string? Revision,
    ImmutableArray<CatalogSourceRequirementSnapshot> Requirements,
    ImmutableArray<int> RegionIds,
    ImmutableArray<int> LanguageIds);

public sealed record CatalogSourceRequirementSnapshot(
    string ProviderRequirementKey,
    long RequirementOrder,
    CatalogSourceRequirementKind Kind,
    string Name,
    long Size,
    Crc32? Crc,
    Md5? Md5,
    Sha1? Sha1,
    string? Status);

public enum CatalogSourceRequirementKind
{
    Rom,
    Disk
}
