using Romd.Application.Common.Systems;
using Romd.Application.Common.Artwork;
using Romd.Admin.Application.Search.ReadModels;
using Romd.Application.Common.Ids;
using Romd.Admin.Application.Source;
using Romd.Admin.Application.Titles.ReadModels;
using Romd.Domain.Catalog;
using CommonModels = Romd.Contracts.Common.Models;
using Models = Romd.Contracts.Management.Models;
using Romd.Contracts.Common.Models;

namespace Romd.Admin.Application.Catalog;

/// <summary>
///     Extension methods for mapping domain entities to contract models with encoded IDs.
/// </summary>
public static class CatalogMapping
{
    public static Models.CatalogTitle ToContract(this CatalogTitleData item, SystemKeys systemKeys) => new()
    {
        Id = IdCoder.Encode(item.Id),
        SystemKey = systemKeys.Required(item.PlatformId),
        Name = item.Name,
        HasLocalPayload = item.HasLocalPayload,
        IsTracked = item.IsTracked,
        LocalPayloadVersionCount = item.LocalPayloadVersionCount,
        EnrichmentStatus = item.EnrichmentStatus,
        EnrichmentAssessment = item.EnrichmentAssessment,
        TotalVersionCount = item.TotalVersionCount,
        CoverUrl = item.CoverMediaId.HasValue ? $"/media/{IdCoder.Encode(item.CoverMediaId.Value)}" : null,
        Artwork = item.Artwork.Select(artwork => artwork.ToContract()).ToArray(),
        Genre = item.Genre,
        ReleaseDate = item.ReleaseDate,
        Rating = item.Rating
    };

    /// <summary>
    ///     Maps a title summary to its contract representation, including its projection-owned
    ///     local-payload fact.
    /// </summary>
    public static Models.Title ToContract(this TitleSummary title, SystemKeys systemKeys) => new()
    {
        Id = IdCoder.Encode(title.Id),
        SystemKey = systemKeys.Required(title.PlatformId),
        Name = title.Name,
        HasLocalPayload = title.HasLocalPayload,
        EnrichmentStatus = title.EnrichmentStatus.ToString(),
        Description = title.Description,
        Publisher = title.Publisher,
        Developer = title.Developer,
        Genre = title.Genre,
        ReleaseDate = title.ReleaseDate,
        Players = title.Players,
        Rating = title.Rating,
        CreatedAt = title.CreatedAt,
        LastEnrichedAt = title.LastEnrichedAt
    };

    /// <summary>Maps title summaries to contract models.</summary>
    public static IEnumerable<Models.Title> ToContract(this IEnumerable<TitleSummary> titles, SystemKeys systemKeys) =>
        titles.Select(title => title.ToContract(systemKeys));

    /// <summary>
    ///     Maps a BIOS ownership view to its contract representation with encoded IDs.
    /// </summary>
    public static Models.Bios ToContract(this BiosOwnership entity, SystemKeys systemKeys) => new()
    {
        Id = IdCoder.Encode(entity.BiosId),
        SystemKey = systemKeys.Required(entity.PlatformId),
        Name = entity.Name,
        TotalRoms = entity.TotalRoms,
        OwnedRoms = entity.OwnedRoms,
        IsOwned = entity.IsOwned,
        RequiredBytes = (ByteCount)entity.RequiredBytes,
        OwnedBytes = (ByteCount)entity.OwnedBytes,
        OnDiskBytes = (ByteCount)entity.OnDiskBytes
    };

    /// <summary>
    ///     Maps a collection of BIOS ownership views to contract models.
    /// </summary>
    public static IEnumerable<Models.Bios> ToContract(this IEnumerable<BiosOwnership> entities, SystemKeys systemKeys) =>
        entities.Select(e => e.ToContract(systemKeys));

    /// <summary>
    ///     Maps a title's backing source reference to its contract representation.
    /// </summary>
    public static Models.TitleSourceReference ToContract(this TitleSourceReference reference, SystemKeys systemKeys) => new()
    {
        CatalogSourceId = IdCoder.Encode(reference.CatalogSourceId),
        Kind = reference.Kind.ToContract(),
        Name = reference.Name,
        Status = reference.Status.ToContract(),
        EntryCount = reference.EntryCount,
        DatId = reference.DatId is int dat ? IdCoder.Encode(dat) : null,
        SystemKey = systemKeys.Optional(reference.PlatformId),
        HasActiveDefinition = reference.HasActiveDefinition
    };

    /// <summary>
    ///     Maps a collection of title source references to contract models.
    /// </summary>
    public static IEnumerable<Models.TitleSourceReference> ToContract(
        this IEnumerable<TitleSourceReference> references, SystemKeys systemKeys) =>
        references.Select(r => r.ToContract(systemKeys));
}
