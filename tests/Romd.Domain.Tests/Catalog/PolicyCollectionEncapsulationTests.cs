using Romd.Domain.Catalog;
using Romd.Domain.Catalog.Ratings;
using Shouldly;
using Xunit;

namespace Romd.Domain.Tests.Catalog;

public sealed class PolicyCollectionEncapsulationTests
{
    [Fact]
    public void CoreMetadataFields_MutableInterface_CannotReplaceGlobalRequirements()
    {
        var fields = EnrichmentAssessmentPolicy.CoreMetadataFields;
        var first = fields[0];
        try
        {
            Should.Throw<NotSupportedException>(() => ((IList<string>)fields)[0] = "ChangedRequirement");
            Should.Throw<NotSupportedException>(() => ((ICollection<string>)fields).Clear());

            fields.ShouldBe(["Description", "Genre", "ReleaseDate"]);
        }
        finally
        {
            if (fields[0] != first)
                ((IList<string>)fields)[0] = first;
        }
    }

    [Fact]
    public void GetCategories_MutableInterface_CannotReplaceCanonicalBoardKnowledge()
    {
        var categories = RatingBoardCatalog.GetCategories(RatingBoard.Esrb);
        var first = categories[0];
        var changed = first with { Code = "ChangedCode" };
        try
        {
            Should.Throw<NotSupportedException>(() => ((IList<ContentRatingCategory>)categories)[0] = changed);
            Should.Throw<NotSupportedException>(() => ((ICollection<ContentRatingCategory>)categories).Clear());

            RatingBoardCatalog.GetCategories(RatingBoard.Esrb).ShouldBeSameAs(categories);
            categories[0].ShouldBe(first);
            RatingBoardCatalog.TryResolve(first.Board, first.Code).ShouldBe(first);
        }
        finally
        {
            if (categories[0] != first)
                ((IList<ContentRatingCategory>)categories)[0] = first;
        }
    }
}
