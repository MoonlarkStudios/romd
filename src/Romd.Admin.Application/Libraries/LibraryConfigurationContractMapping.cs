using Contract = Romd.Contracts.Management.Libraries;
using ContractModels = Romd.Contracts.Management.Models;
using DomainRatings = Romd.Domain.Catalog.Ratings;
using DomainLibraryTitleSelectionMode = Romd.Domain.Libraries.LibraryTitleSelectionMode;
using DomainRatingBasisSelection = Romd.Domain.Libraries.RatingBasisSelection;
using DomainUnknownMetadataPolicy = Romd.Domain.Libraries.UnknownMetadataPolicy;

namespace Romd.Admin.Application.Libraries;

/// <summary>
///     Converts between domain policy enums and their management-contract mirrors. Every switch
///     throws for an unmirrored value so adding or changing a domain member cannot silently alter
///     the public contract.
/// </summary>
public static class LibraryConfigurationContractMapping
{
    public static Contract.LibraryTitleSelectionMode ToContract(
        this DomainLibraryTitleSelectionMode value) => value switch
        {
            DomainLibraryTitleSelectionMode.Rules => Contract.LibraryTitleSelectionMode.Rules,
            DomainLibraryTitleSelectionMode.IncludeOnly => Contract.LibraryTitleSelectionMode.IncludeOnly,
            _ => throw UnmirroredDomainValue(value)
        };

    public static DomainLibraryTitleSelectionMode ToDomain(
        this Contract.LibraryTitleSelectionMode value) => value switch
        {
            Contract.LibraryTitleSelectionMode.Rules => DomainLibraryTitleSelectionMode.Rules,
            Contract.LibraryTitleSelectionMode.IncludeOnly => DomainLibraryTitleSelectionMode.IncludeOnly,
            _ => throw UnmirroredContractValue(value)
        };

    public static Contract.RatingBasisSelection ToContract(
        this DomainRatingBasisSelection value) => value switch
        {
            DomainRatingBasisSelection.Strictest => Contract.RatingBasisSelection.Strictest,
            DomainRatingBasisSelection.Preferred => Contract.RatingBasisSelection.Preferred,
            _ => throw UnmirroredDomainValue(value)
        };

    public static DomainRatingBasisSelection ToDomain(
        this Contract.RatingBasisSelection value) => value switch
        {
            Contract.RatingBasisSelection.Strictest => DomainRatingBasisSelection.Strictest,
            Contract.RatingBasisSelection.Preferred => DomainRatingBasisSelection.Preferred,
            _ => throw UnmirroredContractValue(value)
        };

    public static Contract.UnknownMetadataPolicy ToContract(
        this DomainUnknownMetadataPolicy value) => value switch
        {
            DomainUnknownMetadataPolicy.Allow => Contract.UnknownMetadataPolicy.Allow,
            DomainUnknownMetadataPolicy.Hide => Contract.UnknownMetadataPolicy.Hide,
            DomainUnknownMetadataPolicy.NeedsReview => Contract.UnknownMetadataPolicy.NeedsReview,
            _ => throw UnmirroredDomainValue(value)
        };

    public static DomainUnknownMetadataPolicy ToDomain(
        this Contract.UnknownMetadataPolicy value) => value switch
        {
            Contract.UnknownMetadataPolicy.Allow => DomainUnknownMetadataPolicy.Allow,
            Contract.UnknownMetadataPolicy.Hide => DomainUnknownMetadataPolicy.Hide,
            Contract.UnknownMetadataPolicy.NeedsReview => DomainUnknownMetadataPolicy.NeedsReview,
            _ => throw UnmirroredContractValue(value)
        };

    public static ContractModels.RatingBoard ToContract(
        this DomainRatings.RatingBoard value) => value switch
        {
            DomainRatings.RatingBoard.Esrb => ContractModels.RatingBoard.Esrb,
            DomainRatings.RatingBoard.Pegi => ContractModels.RatingBoard.Pegi,
            DomainRatings.RatingBoard.Cero => ContractModels.RatingBoard.Cero,
            DomainRatings.RatingBoard.Usk => ContractModels.RatingBoard.Usk,
            DomainRatings.RatingBoard.Grac => ContractModels.RatingBoard.Grac,
            DomainRatings.RatingBoard.ClassInd => ContractModels.RatingBoard.ClassInd,
            DomainRatings.RatingBoard.Acb => ContractModels.RatingBoard.Acb,
            _ => throw UnmirroredDomainValue(value)
        };

    public static DomainRatings.RatingBoard ToDomain(
        this ContractModels.RatingBoard value) => value switch
        {
            ContractModels.RatingBoard.Esrb => DomainRatings.RatingBoard.Esrb,
            ContractModels.RatingBoard.Pegi => DomainRatings.RatingBoard.Pegi,
            ContractModels.RatingBoard.Cero => DomainRatings.RatingBoard.Cero,
            ContractModels.RatingBoard.Usk => DomainRatings.RatingBoard.Usk,
            ContractModels.RatingBoard.Grac => DomainRatings.RatingBoard.Grac,
            ContractModels.RatingBoard.ClassInd => DomainRatings.RatingBoard.ClassInd,
            ContractModels.RatingBoard.Acb => DomainRatings.RatingBoard.Acb,
            _ => throw UnmirroredContractValue(value)
        };

    private static ArgumentOutOfRangeException UnmirroredDomainValue<T>(T value)
        where T : struct, Enum =>
        new(nameof(value), value, "Domain enum value has no management-contract mirror.");

    private static ArgumentOutOfRangeException UnmirroredContractValue<T>(T value)
        where T : struct, Enum =>
        new(nameof(value), value, "Management-contract enum value has no domain mirror.");
}
