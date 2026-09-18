using Romd.Domain.Catalog.Ratings;

namespace Romd.Domain.Libraries;

public enum RatingBasisSelection
{
    Strictest = 0,
    Preferred = 1
}

public enum RatingOutcome
{
    Allowed = 0,
    Blocked = 1,
    NeedsReview = 2
}

public sealed record ContentRatingPolicy
{
    public RatingBasisSelection BasisSelection { get; init; } = RatingBasisSelection.Strictest;

    public IReadOnlyList<RatingBoard> BoardPreference { get; init; } =
        RatingBoardCatalog.DefaultBoardPreference;

    public int? MaxMinimumAge { get; init; } = 18;
    public bool AllowRefusedClassification { get; init; }
    public UnknownMetadataPolicy UnknownRatingPolicy { get; init; } = UnknownMetadataPolicy.NeedsReview;
}

public sealed record RatingVerdict(
    RatingOutcome Outcome,
    string Reason,
    ContentRating? Basis);

public static class ContentRatingPolicyEvaluator
{
    public static RatingVerdict Evaluate(
        ContentRatingPolicy policy,
        IReadOnlyCollection<ContentRating> ratings)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(ratings);

        var refused = ratings.FirstOrDefault(r =>
            r.Designation == RatingDesignation.RefusedClassification);
        if (refused is not null && !policy.AllowRefusedClassification)
            return Blocked("RefusedClassification", refused);

        var rated = ratings
            .Where(r => r.Designation == RatingDesignation.Rated)
            .ToList();
        var basis = policy.BasisSelection switch
        {
            RatingBasisSelection.Preferred => PreferredBasis(policy, rated),
            _ => rated.OrderByDescending(r => r.MinimumAge ?? -1).FirstOrDefault()
        };

        if (basis is not null)
        {
            return policy.MaxMinimumAge is null || basis.MinimumAge <= policy.MaxMinimumAge
                ? Allowed(basis)
                : Blocked("ContentRating", basis);
        }

        if (refused is not null && policy.AllowRefusedClassification)
            return Allowed(refused);

        return policy.UnknownRatingPolicy switch
        {
            UnknownMetadataPolicy.Allow => Allowed(null),
            UnknownMetadataPolicy.Hide => Blocked("UnknownRatingHidden", null),
            _ => new RatingVerdict(RatingOutcome.NeedsReview, "UnknownRatingNeedsReview", null)
        };
    }

    private static ContentRating? PreferredBasis(
        ContentRatingPolicy policy,
        IReadOnlyCollection<ContentRating> ratings) =>
        policy.BoardPreference
            .Select(board => ratings.FirstOrDefault(r => r.Board == board))
            .FirstOrDefault(r => r is not null);

    private static RatingVerdict Allowed(ContentRating? basis) =>
        new(RatingOutcome.Allowed, "Allowed", basis);

    private static RatingVerdict Blocked(string reason, ContentRating? basis) =>
        new(RatingOutcome.Blocked, reason, basis);
}
