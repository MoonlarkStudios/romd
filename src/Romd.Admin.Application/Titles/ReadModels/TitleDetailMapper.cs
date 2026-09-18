using Romd.Application.Common.Systems;
using Romd.Application.Common.Artwork;
using Romd.Admin.Application.Storage;
using Romd.Application.Common.Ids;
using Romd.Contracts.Common.Models;
using Romd.Contracts.Management.Models;

namespace Romd.Admin.Application.Titles.ReadModels;

/// <summary>
///     Maps <see cref="TitleDetailData" /> to the <see cref="TitleDetail" /> contract.
/// </summary>
public static class TitleDetailMapper
{
    public static TitleDetail ToContract(TitleDetailData data, SystemKeys systemKeys)
    {
        var releases = data.Releases.Select(r =>
        {
            var files = r.Files.Select(f => new TitleFileRequirement
            {
                Id = IdCoder.Encode(f.Id),
                Name = f.Name,
                Size = (ByteCount)f.Size,
                Sha1 = f.Sha1,
                Md5 = f.Md5,
                Crc = f.Crc,
                Status = f.Status,
                IsOwned = f.RomFileId.HasValue,
                RomFileId = f.RomFileId.HasValue ? IdCoder.Encode(f.RomFileId.Value) : null
            }).ToList();

            bool isComplete = files.All(f => f.IsOwned || f.Status == "nodump");

            return new TitleRelease
            {
                Id = IdCoder.Encode(r.Id),
                CatalogReleaseId = r.CatalogReleaseId.HasValue
                    ? IdCoder.Encode(r.CatalogReleaseId.Value)
                    : null,
                DatId = IdCoder.Encode(r.DatFileId),
                Name = r.Name,
                Description = r.Description,
                Year = r.Year,
                Manufacturer = r.Manufacturer,
                Region = r.Region,
                Language = r.Language,
                Revision = r.Revision,
                IsComplete = isComplete,
                Files = files,
                Sources = r.Sources.Select(s => new TitleReleaseSource
                {
                    DatGameId = IdCoder.Encode(s.DatGameId),
                    DatId = IdCoder.Encode(s.DatFileId),
                    DatName = s.DatName,
                    GameName = s.GameName
                }).ToList()
            };
        }).ToList();

        var allFiles = releases.SelectMany(r => r.Files).ToList();
        int ownedCount = allFiles.Count(f => f.IsOwned);
        int totalCount = allFiles.Count(f => f.Status != "nodump");
        double completionPercent = totalCount > 0 ? (double)ownedCount / totalCount * 100 : 0;

        Dictionary<string, string>? provenance = data.FieldProvenance?.Count > 0
            ? new Dictionary<string, string>(data.FieldProvenance)
            : null;

        return new TitleDetail
        {
            Artwork = data.Artwork.Select(ArtworkContractMapping.ToContract).ToArray(),
            Id = IdCoder.Encode(data.Id),
            SystemKey = systemKeys.Required(data.PlatformId),
            Name = data.Name,
            EnrichmentStatus = data.EnrichmentStatus,
            Description = data.Description,
            Publisher = data.Publisher,
            Developer = data.Developer,
            Genre = data.Genre,
            ReleaseDate = data.ReleaseDate,
            Players = data.Players,
            Rating = data.Rating,
            ContentRatings = data.ContentRatings.Select(r => new TitleContentRating
            {
                // Persisted board/designation ints are domain enum values; the contract enums
                // mirror the domain member-for-member, so the casts preserve meaning.
                Board = (RatingBoard)r.Board,
                Code = r.Code,
                Designation = (RatingDesignation)r.Designation,
                MinimumAge = r.MinimumAge,
                SourceId = r.SourceId,
                ExternalRatingId = r.ExternalRatingId,
                Descriptors = r.Descriptors,
                Synopsis = r.Synopsis
            }).ToList(),
            CreatedAt = data.CreatedAt,
            LastEnrichedAt = data.LastEnrichedAt,
            FieldProvenance = provenance,
            Media = data.Media.Select(m => new TitleMediaRef
            {
                Id = IdCoder.Encode(m.Id),
                Type = m.Type,
                Url = $"/media/{IdCoder.Encode(m.Id)}",
                SourceId = m.SourceId,
                    Attribution = m.Attribution,
                    SourcePageUrl = m.SourcePageUrl,
                IsPrimary = m.IsPrimary,
                File = StoredFileRefMapping.ToStoredFileRef(m.SizeBytes, m.SizeOnDiskBytes, m.IsCompressed)
            }).ToList(),
            HasLocalPayload = data.HasLocalPayload,
            IsTracked = data.IsTracked,
            PinnedCatalogReleaseId = data.PinnedCatalogReleaseId.HasValue
                ? IdCoder.Encode(data.PinnedCatalogReleaseId.Value)
                : null,
            CompletionPercent = Math.Round(completionPercent, 1),
            Releases = releases
        };
    }
}
