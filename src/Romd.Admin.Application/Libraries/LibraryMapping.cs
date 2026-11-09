using Romd.Admin.Application.Export;
using Romd.Application.Common.Content;
using Romd.Application.Common.Ids;
using Romd.Application.Common.Systems;
using Romd.Contracts.Management.Libraries;
using Romd.Contracts.Management.Realtime;
using Romd.Domain.Catalog.Ratings;
using Romd.Domain.Libraries;

using ContractContentRatingPolicy = Romd.Contracts.Management.Libraries.ContentRatingPolicy;
using ContractLibrary = Romd.Contracts.Management.Libraries.Library;
using ContractLibraryConfiguration = Romd.Contracts.Management.Libraries.LibraryConfiguration;
using ContractLibraryTitleReleaseDiagnostics = Romd.Contracts.Management.Libraries.LibraryTitleReleaseDiagnostics;

using ContentRatingPolicy = Romd.Domain.Libraries.ContentRatingPolicy;
using Library = Romd.Domain.Libraries.Library;
using LibraryConfiguration = Romd.Domain.Libraries.LibraryConfiguration;

namespace Romd.Admin.Application.Libraries;

public static class LibraryMapping
{
    public static ContractLibrary ToContract(this Library library, SystemKeys systemKeys, RomDeliveryPolicy deliveryPolicy) =>
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
            ExportFormats = ArchiveFormatMapping.ToContract(
                ExportArchivePlanner.StoredFormats(ExportSystems(library, systemKeys, deliveryPolicy), deliveryPolicy),
                deliveryPolicy.Formats),
            CreatedAt = library.CreatedAt,
            UpdatedAt = library.UpdatedAt
        };

    /// <summary>
    ///     The systems a library export may hold: its allowed systems, or every system the delivery policy covers when it
    ///     allows all. An invalid configuration exports nothing.
    /// </summary>
    private static IEnumerable<string> ExportSystems(Library library, SystemKeys systemKeys, RomDeliveryPolicy deliveryPolicy) =>
        !library.HasValidConfiguration ? []
        : library.Configuration.AllowedPlatformIds.Count == 0 ? deliveryPolicy.PlatformKeys
        : library.Configuration.AllowedPlatformIds.Select(systemKeys.Required);

    public static LibraryFacet ToContract(this PlatformFacet facet, SystemKeys systemKeys) =>
        new(systemKeys.Required(facet.PlatformId), facet.PlatformName, facet.Count);

    public static LibraryFacet ToContract(this GenreFacet facet) =>
        new(facet.Genre, facet.Genre, facet.Count);

    public static LibraryCollection ToContract(this CollectionFacet facet) =>
        new(
            IdCoder.Encode(facet.CollectionId),
            facet.CollectionName,
            facet.MatchingCount,
            facet.TotalCount);

    public static ContractLibraryTitleReleaseDiagnostics ToContract(
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
            ExposureReason = release.ExposureReason,
            VfsPathError = release.VfsPathError
        };

    public static AdminRealtimeLibraryUpdatedPayload ToUpdatedPayload(this Library library) =>
        new(
            IdCoder.Encode(library.Id),
            library.Name,
            library.NeedsMaterialization,
            library.ItemCount,
            library.ConfigurationState.ToString());

    private static ContractLibraryConfiguration ToContract(LibraryConfiguration config, SystemKeys systemKeys) =>
        new()
        {
            TitleSelectionMode = config.TitleSelectionMode.ToContract(),
            AllowedSystemKeys = config.AllowedPlatformIds.Select(systemKeys.Required).ToList(),
            ExcludedDatIds = config.ExcludedDatIds.Select(IdCoder.Encode).ToList(),
            ContentRatingPolicy = ToContract(config.ContentRatingPolicy),
            AllowedGenres = config.AllowedGenres.ToList(),
            UnknownGenrePolicy = config.UnknownGenrePolicy.ToContract(),
            IncludeUntrackedTitles = config.IncludeUntrackedTitles,
            ShowMissingGames = config.ShowMissingGames,
            SelectedDiscSetIds = config.SelectedDiscSetIds.Select(IdCoder.Encode).ToList(),
            IncludeTitleIds = config.IncludeTitleIds.Select(IdCoder.Encode).ToList(),
            ExcludeTitleIds = config.ExcludeTitleIds.Select(IdCoder.Encode).ToList()
        };

    private static ContractContentRatingPolicy ToContract(ContentRatingPolicy policy) =>
        new()
        {
            BasisSelection = policy.BasisSelection.ToContract(),
            BoardPreference = policy.BoardPreference.Select(board => board.ToContract()).ToList(),
            MaxMinimumAge = policy.MaxMinimumAge,
            AllowRefusedClassification = policy.AllowRefusedClassification,
            UnknownRatingPolicy = policy.UnknownRatingPolicy.ToContract()
        };
}
