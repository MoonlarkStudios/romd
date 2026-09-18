using Romd.Application.Common.Systems;
using Romd.Application.Common.Artwork;
using Romd.Application.Common.Ids;
using Romd.Application.Common.Pagination;
using Romd.Consumer.Application.Browse.ReadModels;
using Romd.Contracts.Consumer.Browse;
using Romd.Contracts.Consumer.Releases;
using CommonModels = Romd.Contracts.Common.Models;
using Romd.Contracts.Common.Models;

namespace Romd.Consumer.Application.Browse;

public static class ConsumerBrowseMapping
{
    public static CommonModels.Page<ConsumerPlatformSummaryDto> ToContract(
        this PagedList<ConsumerPlatformSummaryData> page) =>
        new()
        {
            Items = page.Items.Select(item => new ConsumerPlatformSummaryDto
            {
                Key = item.Key,
                Name = item.Name,
                ShortName = item.ShortName,
                Manufacturer = item.Manufacturer,
                TitleCount = item.TitleCount,
                CoverUrl = item.CoverMediaId is { } mediaId ? MediaUrl(mediaId) : null
            }).ToList(),
            NextCursor = page.NextCursor,
            HasNextPage = page.HasNextPage
        };

    public static ConsumerPlatformDetailDto ToContract(this ConsumerPlatformDetailData item) =>
        new()
        {
            Key = item.Key,
            Name = item.Name,
            ShortName = item.ShortName,
            Manufacturer = item.Manufacturer,
            TitleCount = item.TitleCount,
            Media = item.Media.Select(ToContract).ToList()
        };

    public static CommonModels.Page<ConsumerTitleCardDto> ToContract(this PagedList<ConsumerTitleCardData> page) =>
        new()
        {
            Items = page.Items.Select(ToContract).ToList(),
            NextCursor = page.NextCursor,
            HasNextPage = page.HasNextPage
        };

    public static ConsumerTitleDetailDto ToContract(this ConsumerTitleDetailData item) =>
        new()
        {
            Id = IdCoder.Encode(item.Id),
            Artwork = item.Artwork.Select(ArtworkContractMapping.ToContract).ToArray(),
            System = item.System.ToContract(),
            ContentRatings = item.ContentRatings.Select(rating => new ConsumerContentRatingDto
            {
                Board = rating.Board.ToString(),
                Code = rating.Code,
                BoardName = rating.BoardName ?? rating.Board.ToString(),
                Name = rating.Name ?? $"{rating.Board} {rating.Code}",
                Description = rating.Description,
                Icon = rating.Icon?.ToContract()
            }).ToArray(),
            Name = item.Name,
            Description = item.Description,
            Publisher = item.Publisher,
            Developer = item.Developer,
            Genre = item.Genre,
            ReleaseDate = item.ReleaseDate,
            Players = item.Players,
            Rating = item.Rating,
            Media = item.Media.Select(ToContract).ToList(),
            Releases = item.Releases.Select(ToContract).ToList(),
            DefaultReleaseId = item.DefaultReleaseId is null ? null : IdCoder.Encode(item.DefaultReleaseId.Value)
        };

    private static ConsumerTitleCardDto ToContract(ConsumerTitleCardData item) =>
        new()
        {
            Id = IdCoder.Encode(item.Id),
            Artwork = item.Artwork.Select(ArtworkContractMapping.ToContract).ToArray(),
            System = item.System.ToContract(),
            ContentRatings = item.ContentRatings.Select(rating => new ConsumerContentRatingDto
            {
                Board = rating.Board.ToString(),
                Code = rating.Code,
                BoardName = rating.BoardName ?? rating.Board.ToString(),
                Name = rating.Name ?? $"{rating.Board} {rating.Code}",
                Description = rating.Description,
                Icon = rating.Icon?.ToContract()
            }).ToArray(),
            Name = item.Name,
            Players = item.Players,
            EsrbRating = item.EsrbRating,
            CoverUrl = item.CoverMediaId is { } mediaId ? MediaUrl(mediaId) : null,
            Genre = item.Genre,
            ReleaseDate = item.ReleaseDate,
            Rating = item.Rating,
            ReleaseCount = item.ReleaseCount,
            DefaultReleaseId = item.DefaultReleaseId is null ? null : IdCoder.Encode(item.DefaultReleaseId.Value)
        };

    public static ConsumerReleaseDto ToContract(this ConsumerReleaseData item) =>
        new()
        {
            Id = IdCoder.Encode(item.Id),
            Name = item.Name,
            Revision = item.Revision,
            Regions = item.Regions,
            Languages = item.Languages,
            SizeBytes = (ByteCount)item.SizeBytes,
            IsComplete = item.IsComplete
        };

    private static ConsumerMediaRefDto ToContract(ConsumerMediaData item) =>
        new()
        {
            Id = IdCoder.Encode(item.Id),
            Type = item.Type,
            Url = MediaUrl(item.Id),
            IsPrimary = item.IsPrimary
        };

    private static string MediaUrl(int mediaId) => $"/media/{IdCoder.Encode(mediaId)}";
}
