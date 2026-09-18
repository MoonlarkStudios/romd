using Romd.Domain.Catalog.Ratings;
using Romd.Domain.Libraries;
using Shouldly;
using Xunit;

namespace Romd.Domain.Tests.Libraries;

public sealed class ContentRatingPolicyEvaluatorTests
{
    [Fact]
    public void Evaluate_StrictestBasis_BlocksWhenHighestRatedBoardExceedsCeiling()
    {
        var policy = new ContentRatingPolicy { MaxMinimumAge = 12 };
        var ratings = new[]
        {
            Rating(RatingBoard.Esrb, "T", 13),
            Rating(RatingBoard.Pegi, "PEGI 12", 12)
        };

        var verdict = ContentRatingPolicyEvaluator.Evaluate(policy, ratings);

        verdict.Outcome.ShouldBe(RatingOutcome.Blocked);
        verdict.Reason.ShouldBe("ContentRating");
        verdict.Basis.ShouldNotBeNull();
        verdict.Basis.Board.ShouldBe(RatingBoard.Esrb);
    }

    [Fact]
    public void Evaluate_PreferredBasis_UsesFirstPreferredRatedBoard()
    {
        var policy = new ContentRatingPolicy
        {
            BasisSelection = RatingBasisSelection.Preferred,
            BoardPreference = [RatingBoard.Pegi, RatingBoard.Esrb],
            MaxMinimumAge = 12
        };
        var ratings = new[]
        {
            Rating(RatingBoard.Esrb, "T", 13),
            Rating(RatingBoard.Pegi, "PEGI 12", 12)
        };

        var verdict = ContentRatingPolicyEvaluator.Evaluate(policy, ratings);

        verdict.Outcome.ShouldBe(RatingOutcome.Allowed);
        verdict.Basis.ShouldNotBeNull();
        verdict.Basis.Board.ShouldBe(RatingBoard.Pegi);
    }

    [Fact]
    public void Evaluate_RefusedClassification_BlockedByDefault()
    {
        var verdict = ContentRatingPolicyEvaluator.Evaluate(
            new ContentRatingPolicy { MaxMinimumAge = null },
            [RefusedClassification()]);

        verdict.Outcome.ShouldBe(RatingOutcome.Blocked);
        verdict.Reason.ShouldBe("RefusedClassification");
    }

    [Fact]
    public void Evaluate_UnknownRatingPolicyNeedsReview_ReturnsNeedsReview()
    {
        var verdict = ContentRatingPolicyEvaluator.Evaluate(new ContentRatingPolicy(), []);

        verdict.Outcome.ShouldBe(RatingOutcome.NeedsReview);
        verdict.Reason.ShouldBe("UnknownRatingNeedsReview");
    }

    private static ContentRating Rating(RatingBoard board, string code, int minimumAge) =>
        new()
        {
            Board = board,
            Code = code,
            Designation = RatingDesignation.Rated,
            MinimumAge = minimumAge,
            SourceId = "test"
        };

    private static ContentRating RefusedClassification() =>
        new()
        {
            Board = RatingBoard.Acb,
            Code = "RC",
            Designation = RatingDesignation.RefusedClassification,
            MinimumAge = null,
            SourceId = "test"
        };
}
