using Romd.Contracts.Management.Models;

namespace Romd.Contracts.Management.Libraries;

public sealed record ContentRatingPolicyDto
{
    public RatingBasisSelection BasisSelection { get; init; } = RatingBasisSelection.Strictest;
    public IReadOnlyList<RatingBoard> BoardPreference { get; init; } = [];
    public int? MaxMinimumAge { get; init; } = 18;
    public bool AllowRefusedClassification { get; init; }
    public UnknownMetadataPolicy UnknownRatingPolicy { get; init; } = UnknownMetadataPolicy.NeedsReview;
}
