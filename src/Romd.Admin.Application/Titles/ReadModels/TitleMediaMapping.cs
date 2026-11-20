using Romd.Admin.Application.Storage;
using Romd.Application.Common.Ids;
using Romd.Contracts.Management.Models;
using Romd.Domain.Catalog;

namespace Romd.Admin.Application.Titles.ReadModels;

/// <summary>The one admin projection of a title media item, from the read model or a just-written domain row.</summary>
public static class TitleMediaMapping
{
    public static TitleMediaRef ToRef(TitleMediaData media) => new()
    {
        Id = IdCoder.Encode(media.Id),
        Type = media.Type,
        Url = $"/media/{IdCoder.Encode(media.Id)}",
        SourceId = media.SourceId,
        Attribution = media.Attribution,
        SourcePageUrl = media.SourcePageUrl,
        IsPrimary = media.IsPrimary,
        ContentType = media.ContentType,
        CatalogReleaseId = media.CatalogReleaseId is { } releaseId ? IdCoder.Encode(releaseId) : null,
        Region = media.Region,
        Language = media.Language,
        IsPreferred = media.IsPreferred,
        ProviderAssetId = media.ProviderAssetId,
        File = StoredFileRefMapping.ToStoredFileRef(media.SizeBytes, media.SizeOnDiskBytes, media.IsCompressed)
    };

    public static TitleMediaRef ToRef(TitleMediaSummary media) => new()
    {
        Id = IdCoder.Encode(media.Id),
        Type = media.Type.ToString(),
        Url = $"/media/{IdCoder.Encode(media.Id)}",
        SourceId = media.SourceId,
        Attribution = media.Attribution,
        SourcePageUrl = media.SourcePageUrl,
        IsPrimary = media.IsPrimary,
        ContentType = media.ContentType,
        CatalogReleaseId = media.Placement?.CatalogReleaseId is { } releaseId ? IdCoder.Encode(releaseId) : null,
        Region = media.Placement?.Region,
        Language = media.Placement?.Language,
        IsPreferred = media.Placement?.IsPreferred ?? false,
        ProviderAssetId = media.ProviderAssetId
    };

    public static TitleMediaRef ToRef(TitleMedia media) => new()
    {
        Id = IdCoder.Encode(media.Id),
        Type = media.Type.ToString(),
        Url = $"/media/{IdCoder.Encode(media.Id)}",
        SourceId = media.SourceId,
        Attribution = media.Attribution,
        SourcePageUrl = media.SourcePageUrl,
        IsPrimary = media.IsPrimary,
        ContentType = media.ContentType,
        CatalogReleaseId = media.CatalogReleaseId is { } releaseId ? IdCoder.Encode(releaseId) : null,
        Region = media.Region,
        Language = media.Language,
        IsPreferred = media.IsPreferred,
        ProviderAssetId = media.ProviderAssetId
    };
}
