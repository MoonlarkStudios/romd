using Romd.Application.Common.Systems;
using Romd.Application.Common.Artwork;
using Romd.Application.Common.Ids;
using Romd.Contracts.Consumer.Collections;

namespace Romd.Consumer.Application.Collections;

internal static class ConsumerCollectionMapping
{
    public static ConsumerCollectionDto ToDto(this ConsumerCollectionReadModel model) =>
        new()
        {
            Id = IdCoder.Encode(model.Id),
            Name = model.Name,
            Description = model.Description,
            System = model.System?.ToContract(),
            CoverUrl = model.CoverMediaId is null ? null : $"/media/{IdCoder.Encode(model.CoverMediaId.Value)}",
            HeroUrl = null,
            ItemCount = model.ItemCount,
            IsFeatured = model.IsFeatured
        };

    public static ConsumerCollectionTitleDto ToDto(this ConsumerCollectionTitleReadModel model)
    {
        return new ConsumerCollectionTitleDto
        {
            Id = IdCoder.Encode(model.TitleId),
            Artwork = model.Artwork.Select(ArtworkContractMapping.ToContract).ToArray(),
            Name = model.TitleName,
            System = model.System.ToContract(),
            CoverUrl = model.CoverMediaId is null ? null : $"/media/{IdCoder.Encode(model.CoverMediaId.Value)}",
            Genre = model.Genre,
            ReleaseDate = model.ReleaseDate,
            Rating = model.Rating,
            ReleaseCount = model.ReleaseCount,
            DefaultReleaseId = model.DefaultReleaseId is null ? null : IdCoder.Encode(model.DefaultReleaseId.Value)
        };
    }

}
