using Romd.Application.Common.Systems;
using Romd.Application.Common.Artwork;
using Romd.Admin.Application.Search.ReadModels;
using Romd.Application.Common.Ids;
using Romd.Application.Common.Pagination;
using Romd.Admin.Application.Source;
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
        TotalVersionCount = item.TotalVersionCount,
        CoverUrl = item.CoverMediaId.HasValue ? $"/media/{IdCoder.Encode(item.CoverMediaId.Value)}" : null,
        Artwork = item.Artwork.Select(artwork => artwork.ToContract()).ToArray(),
        Genre = item.Genre,
        ReleaseDate = item.ReleaseDate,
        Rating = item.Rating
    };

    /// <summary>
    ///     Maps a Title entity to its contract representation.
    /// </summary>
    /// <param name="entity">The title entity.</param>
    /// <param name="hasLocalPayload">Whether the title has locally stored payload.</param>
    public static Models.Title ToContract(this Title entity, SystemKeys systemKeys, bool hasLocalPayload = false) => new()
    {
        Id = IdCoder.Encode(entity.Id),
        SystemKey = systemKeys.Required(entity.PlatformId),
        Name = entity.Name,
        HasLocalPayload = hasLocalPayload,
        EnrichmentStatus = entity.EnrichmentStatus.ToString(),
        Description = entity.Description,
        Publisher = entity.Publisher,
        Developer = entity.Developer,
        Genre = entity.Genre,
        ReleaseDate = entity.ReleaseDate,
        Players = entity.Players,
        Rating = entity.Rating,
        CreatedAt = entity.CreatedAt,
        LastEnrichedAt = entity.LastEnrichedAt
    };

    /// <param name="entities">The title entities.</param>
    extension(IEnumerable<Title> entities)
    {
        /// <summary>
        ///     Maps a collection of Title entities to contract models with local payload availability supplied.
        /// </summary>
        /// <param name="titleIdsWithLocalPayload">Set of title IDs with locally stored payload.</param>
        public IEnumerable<Models.Title>
            ToContract(SystemKeys systemKeys, IReadOnlySet<int> titleIdsWithLocalPayload) =>
            entities.Select(e => e.ToContract(systemKeys, titleIdsWithLocalPayload.Contains(e.Id)));

        /// <summary>
        ///     Maps a collection of Title entities to contract models (all marked as locally available).
        ///     Use this when the source query already filtered to titles with local payload.
        /// </summary>
        public IEnumerable<Models.Title> ToContractWithLocalPayload(SystemKeys systemKeys) =>
            entities.Select(e => e.ToContract(systemKeys, true));

        /// <summary>
        ///     Maps a collection of Title entities to contract models (all marked as lacking local payload).
        /// </summary>
        public IEnumerable<Models.Title> ToContract(SystemKeys systemKeys) =>
            entities.Select(e => e.ToContract(systemKeys));
    }

    /// <summary>
    ///     Maps a PagedList of Title entities to a Page contract.
    /// </summary>
    public static CommonModels.Page<Models.Title> ToContract(this PagedList<Title> pagedList, SystemKeys systemKeys) => new()
    {
        Items = pagedList.Items.Select(t => t.ToContract(systemKeys)).ToList(),
        NextCursor = pagedList.NextCursor,
        HasNextPage = pagedList.HasNextPage
    };

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
