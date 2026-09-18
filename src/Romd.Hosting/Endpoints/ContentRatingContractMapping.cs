using Romd.Admin.Application.Libraries;
using Romd.Domain.Catalog.Ratings;
using Models = Romd.Contracts.Management.Models;

namespace Romd.Host.Endpoints;

/// <summary>
///     Converts between the domain rating enums and their contract mirrors. Contracts cannot
///     reference the domain, so the contract declares mirrored enums and the host maps at the
///     boundary. The switches are exhaustive on purpose: adding a domain member without its
///     contract mirror fails loudly here instead of silently serializing a number.
/// </summary>
internal static class ContentRatingContractMapping
{
    public static Models.RatingBoard ToContract(this RatingBoard board) =>
        LibraryConfigurationContractMapping.ToContract(board);

    public static Models.RatingDesignation ToContract(this RatingDesignation designation) => designation switch
    {
        RatingDesignation.Rated => Models.RatingDesignation.Rated,
        RatingDesignation.RatingPending => Models.RatingDesignation.RatingPending,
        RatingDesignation.RefusedClassification => Models.RatingDesignation.RefusedClassification,
        _ => throw new ArgumentOutOfRangeException(nameof(designation), designation, "Domain rating designation has no contract mirror.")
    };

    public static RatingBoard ToDomain(this Models.RatingBoard board) =>
        LibraryConfigurationContractMapping.ToDomain(board);
}
