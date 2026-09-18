using Romd.Application.Common.Systems;
using Romd.Application.Common.Ids;
using Romd.Contracts.Management.Libraries;
using Romd.Contracts.Management.Realtime;
using Romd.Domain.Catalog.Ratings;
using Romd.Domain.Libraries;

namespace Romd.Admin.Application.Libraries;

public static class LibraryMapping
{
    public static LibraryDto ToContract(this Library library, SystemKeys systemKeys) =>
        new()
        {
            Id = IdCoder.Encode(library.Id),
            Name = library.Name,
            Configuration = library.HasValidConfiguration ? ToContract(library.Configuration, systemKeys) : null,
            ConfigurationState = library.ConfigurationState.ToString(),
            ConfigurationError = library.ConfigurationError,
            IsDefault = library.IsDefault,
            NeedsMaterialization = library.NeedsMaterialization,
            LastMaterializedAt = library.LastMaterializedAt,
            ItemCount = library.ItemCount,
            CreatedAt = library.CreatedAt,
            UpdatedAt = library.UpdatedAt
        };

    public static LibraryFacetDto ToContract(this PlatformFacet facet, SystemKeys systemKeys) =>
        new(systemKeys.Required(facet.PlatformId), facet.PlatformName, facet.Count);

    public static LibraryFacetDto ToContract(this GenreFacet facet) =>
        new(facet.Genre, facet.Genre, facet.Count);

    public static LibraryCollectionDto ToContract(this CollectionFacet facet) =>
        new(
            IdCoder.Encode(facet.CollectionId),
            facet.CollectionName,
            facet.MatchingCount,
            facet.TotalCount);

    public static LibraryTitleReleaseDiagnosticsDto ToContract(
        this LibraryTitleReleaseDiagnostics release) =>
        new()
        {
            Id = IdCoder.Encode(release.ReleaseId),
            DatId = IdCoder.Encode(release.DatFileId),
            Name = release.Name,
            IsEligible = release.IsEligible,
            IsBlocked = release.IsBlocked,
            BlockReason = release.BlockReason,
            IsExposed = release.IsExposed,
            ExposureReason = release.ExposureReason
        };

    public static AdminRealtimeLibraryUpdatedPayload ToUpdatedPayload(this Library library) =>
        new(
            IdCoder.Encode(library.Id),
            library.Name,
            library.NeedsMaterialization,
            library.ItemCount,
            library.ConfigurationState.ToString());

    private static LibraryConfigurationDto ToContract(LibraryConfiguration config, SystemKeys systemKeys) =>
        new()
        {
            TitleSelectionMode = config.TitleSelectionMode.ToContract(),
            AllowedSystemKeys = config.AllowedPlatformIds.Select(systemKeys.Required).ToList(),
            ExcludedDatIds = config.ExcludedDatIds.Select(IdCoder.Encode).ToList(),
            ContentRatingPolicy = ToContract(config.ContentRatingPolicy),
            AllowedGenres = config.AllowedGenres.ToList(),
            UnknownGenrePolicy = config.UnknownGenrePolicy.ToContract(),
            ShowMissingGames = config.ShowMissingGames,
            IncludeTitleIds = config.IncludeTitleIds.Select(IdCoder.Encode).ToList(),
            ExcludeTitleIds = config.ExcludeTitleIds.Select(IdCoder.Encode).ToList()
        };

    private static ContentRatingPolicyDto ToContract(ContentRatingPolicy policy) =>
        new()
        {
            BasisSelection = policy.BasisSelection.ToContract(),
            BoardPreference = policy.BoardPreference.Select(board => board.ToContract()).ToList(),
            MaxMinimumAge = policy.MaxMinimumAge,
            AllowRefusedClassification = policy.AllowRefusedClassification,
            UnknownRatingPolicy = policy.UnknownRatingPolicy.ToContract()
        };
}
