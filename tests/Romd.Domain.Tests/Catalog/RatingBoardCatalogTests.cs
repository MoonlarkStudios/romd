using Romd.Domain.Catalog.Ratings;
using Shouldly;
using Xunit;

namespace Romd.Domain.Tests.Catalog;

/// <summary>
///     Exhaustive coverage of the canonical board table — the safety-critical artifact
///     behind parental controls. Every (board, code) pair is pinned, including pending
///     and refused-classification designations and rejection of unknown codes.
/// </summary>
public class RatingBoardCatalogTests
{
    public static TheoryData<RatingBoard, string, string, RatingDesignation, int?> CanonicalTable => new()
    {
        // ESRB
        { RatingBoard.Esrb, "EC", "EC", RatingDesignation.Rated, 3 },
        { RatingBoard.Esrb, "E", "E", RatingDesignation.Rated, 0 },
        { RatingBoard.Esrb, "E10+", "E10+", RatingDesignation.Rated, 10 },
        { RatingBoard.Esrb, "T", "T", RatingDesignation.Rated, 13 },
        { RatingBoard.Esrb, "M", "M", RatingDesignation.Rated, 17 },
        { RatingBoard.Esrb, "AO", "AO", RatingDesignation.Rated, 18 },
        { RatingBoard.Esrb, "RP", "RP", RatingDesignation.RatingPending, null },

        // PEGI
        { RatingBoard.Pegi, "PEGI 3", "PEGI 3", RatingDesignation.Rated, 3 },
        { RatingBoard.Pegi, "PEGI 7", "PEGI 7", RatingDesignation.Rated, 7 },
        { RatingBoard.Pegi, "PEGI 12", "PEGI 12", RatingDesignation.Rated, 12 },
        { RatingBoard.Pegi, "PEGI 16", "PEGI 16", RatingDesignation.Rated, 16 },
        { RatingBoard.Pegi, "PEGI 18", "PEGI 18", RatingDesignation.Rated, 18 },

        // CERO
        { RatingBoard.Cero, "CERO A", "CERO A", RatingDesignation.Rated, 0 },
        { RatingBoard.Cero, "CERO B", "CERO B", RatingDesignation.Rated, 12 },
        { RatingBoard.Cero, "CERO C", "CERO C", RatingDesignation.Rated, 15 },
        { RatingBoard.Cero, "CERO D", "CERO D", RatingDesignation.Rated, 17 },
        { RatingBoard.Cero, "CERO Z", "CERO Z", RatingDesignation.Rated, 18 },

        // USK
        { RatingBoard.Usk, "USK 0", "USK 0", RatingDesignation.Rated, 0 },
        { RatingBoard.Usk, "USK 6", "USK 6", RatingDesignation.Rated, 6 },
        { RatingBoard.Usk, "USK 12", "USK 12", RatingDesignation.Rated, 12 },
        { RatingBoard.Usk, "USK 16", "USK 16", RatingDesignation.Rated, 16 },
        { RatingBoard.Usk, "USK 18", "USK 18", RatingDesignation.Rated, 18 },

        // GRAC
        { RatingBoard.Grac, "GRAC ALL", "GRAC ALL", RatingDesignation.Rated, 0 },
        { RatingBoard.Grac, "GRAC 12", "GRAC 12", RatingDesignation.Rated, 12 },
        { RatingBoard.Grac, "GRAC 15", "GRAC 15", RatingDesignation.Rated, 15 },
        { RatingBoard.Grac, "GRAC 18", "GRAC 18", RatingDesignation.Rated, 18 },
        { RatingBoard.Grac, "GRAC TESTING", "GRAC TESTING", RatingDesignation.RatingPending, null },

        // ClassInd
        { RatingBoard.ClassInd, "L", "L", RatingDesignation.Rated, 0 },
        { RatingBoard.ClassInd, "10", "10", RatingDesignation.Rated, 10 },
        { RatingBoard.ClassInd, "12", "12", RatingDesignation.Rated, 12 },
        { RatingBoard.ClassInd, "14", "14", RatingDesignation.Rated, 14 },
        { RatingBoard.ClassInd, "16", "16", RatingDesignation.Rated, 16 },
        { RatingBoard.ClassInd, "18", "18", RatingDesignation.Rated, 18 },

        // ACB — PG/M carry the documented conservative ages for advisory categories
        { RatingBoard.Acb, "G", "G", RatingDesignation.Rated, 0 },
        { RatingBoard.Acb, "PG", "PG", RatingDesignation.Rated, 8 },
        { RatingBoard.Acb, "M", "M", RatingDesignation.Rated, 15 },
        { RatingBoard.Acb, "MA15+", "MA15+", RatingDesignation.Rated, 15 },
        { RatingBoard.Acb, "R18+", "R18+", RatingDesignation.Rated, 18 },
        { RatingBoard.Acb, "RC", "RC", RatingDesignation.RefusedClassification, null }
    };

    [Theory]
    [MemberData(nameof(CanonicalTable))]
    public void TryResolve_CanonicalCode_ResolvesToExpectedCategory(
        RatingBoard board, string input, string expectedCode, RatingDesignation expectedDesignation, int? expectedAge)
    {
        var category = RatingBoardCatalog.TryResolve(board, input);

        category.ShouldNotBeNull();
        category.Board.ShouldBe(board);
        category.Code.ShouldBe(expectedCode);
        category.Designation.ShouldBe(expectedDesignation);
        category.MinimumAge.ShouldBe(expectedAge);
    }

    [Theory]
    [MemberData(nameof(CanonicalTable))]
    public void TryResolve_MinimumAgeSetIffRated(
        RatingBoard board, string input, string expectedCode, RatingDesignation expectedDesignation, int? expectedAge)
    {
        _ = expectedCode;
        _ = expectedAge;

        var category = RatingBoardCatalog.TryResolve(board, input);

        category.ShouldNotBeNull();
        category.MinimumAge.HasValue.ShouldBe(expectedDesignation == RatingDesignation.Rated);
    }

    public static TheoryData<RatingBoard, string, string> ProviderSpellings => new()
    {
        // IGDB-style enum spellings and common variants
        { RatingBoard.Esrb, "E10", "E10+" },
        { RatingBoard.Esrb, "Everyone 10+", "E10+" },
        { RatingBoard.Esrb, "everyone", "E" },
        { RatingBoard.Esrb, "KA", "E" },
        { RatingBoard.Esrb, "Kids to Adults", "E" },
        { RatingBoard.Esrb, "Early Childhood", "EC" },
        { RatingBoard.Esrb, "Teen", "T" },
        { RatingBoard.Esrb, "Mature 17+", "M" },
        { RatingBoard.Esrb, "Adults Only 18+", "AO" },
        { RatingBoard.Esrb, "Rating Pending", "RP" },
        { RatingBoard.Esrb, "RP_Likely_Mature17Plus", "RP" },
        { RatingBoard.Pegi, "Three", "PEGI 3" },
        { RatingBoard.Pegi, "Seven", "PEGI 7" },
        { RatingBoard.Pegi, "PEGI Twelve", "PEGI 12" },
        { RatingBoard.Pegi, "Sixteen", "PEGI 16" },
        { RatingBoard.Pegi, "Eighteen", "PEGI 18" },
        { RatingBoard.Pegi, "12", "PEGI 12" },
        { RatingBoard.Cero, "CERO_A", "CERO A" },
        { RatingBoard.Cero, "b", "CERO B" },
        { RatingBoard.Cero, "Z", "CERO Z" },
        { RatingBoard.Usk, "USK_0", "USK 0" },
        { RatingBoard.Usk, "Zero", "USK 0" },
        { RatingBoard.Usk, "Six", "USK 6" },
        { RatingBoard.Usk, "USK_Sixteen", "USK 16" },
        { RatingBoard.Grac, "GRAC_ALL", "GRAC ALL" },
        { RatingBoard.Grac, "GRAC_Fifteen", "GRAC 15" },
        { RatingBoard.Grac, "GRAC_Eighteen", "GRAC 18" },
        { RatingBoard.Grac, "TESTING", "GRAC TESTING" },
        { RatingBoard.ClassInd, "CLASS_IND_Ten", "10" },
        { RatingBoard.ClassInd, "ClassInd 14", "14" },
        { RatingBoard.ClassInd, "Livre", "L" },
        { RatingBoard.Acb, "ACB_MA15", "MA15+" },
        { RatingBoard.Acb, "MA 15+", "MA15+" },
        { RatingBoard.Acb, "ACB_R18", "R18+" },
        { RatingBoard.Acb, "Refused Classification", "RC" },
        { RatingBoard.Acb, "Parental Guidance", "PG" },
        { RatingBoard.Acb, "General", "G" }
    };

    [Theory]
    [MemberData(nameof(ProviderSpellings))]
    public void TryResolve_ProviderSpelling_NormalizesToCanonicalCode(
        RatingBoard board, string input, string expectedCode)
    {
        var category = RatingBoardCatalog.TryResolve(board, input);

        category.ShouldNotBeNull();
        category.Code.ShouldBe(expectedCode);
    }

    public static TheoryData<RatingBoard, string> UnknownInputs => new()
    {
        // Unknown codes are rejected, never guessed
        { RatingBoard.Esrb, "FOO" },
        { RatingBoard.Esrb, "PEGI 12" },   // cross-board label under the wrong board
        { RatingBoard.Pegi, "E10+" },
        { RatingBoard.Pegi, "15" },        // PEGI has no 15
        { RatingBoard.Cero, "16" },
        { RatingBoard.Usk, "7" },          // USK has no 7
        { RatingBoard.Grac, "G" },
        { RatingBoard.ClassInd, "13" },
        { RatingBoard.Acb, "AO" },
        { RatingBoard.Pegi, "" },
        { RatingBoard.Pegi, "   " },
        { RatingBoard.Pegi, "PEGI" },      // board name alone is not a rating
        { RatingBoard.Cero, "CERO" }
    };

    [Theory]
    [MemberData(nameof(UnknownInputs))]
    public void TryResolve_UnknownCode_ReturnsNull(RatingBoard board, string input)
    {
        RatingBoardCatalog.TryResolve(board, input).ShouldBeNull();
    }

    [Fact]
    public void TryResolve_Claim_CarriesProvenanceDescriptorsAndSynopsis()
    {
        var claim = new ContentRatingClaim
        {
            Board = RatingBoard.Esrb,
            RawCode = "E10",
            ExternalRatingId = "igdb-rating-42",
            Descriptors = ["Fantasy Violence", "Mild Language"],
            Synopsis = "Cartoon characters bonk each other."
        };

        var rating = RatingBoardCatalog.TryResolve(claim, "igdb");

        rating.ShouldNotBeNull();
        rating.Board.ShouldBe(RatingBoard.Esrb);
        rating.Code.ShouldBe("E10+");
        rating.Designation.ShouldBe(RatingDesignation.Rated);
        rating.MinimumAge.ShouldBe(10);
        rating.SourceId.ShouldBe("igdb");
        rating.ExternalRatingId.ShouldBe("igdb-rating-42");
        rating.Descriptors.ShouldBe(["Fantasy Violence", "Mild Language"]);
        rating.Synopsis.ShouldBe("Cartoon characters bonk each other.");
    }

    [Fact]
    public void TryResolve_Claim_UnknownCode_ReturnsNull()
    {
        var claim = new ContentRatingClaim { Board = RatingBoard.Pegi, RawCode = "NOT A RATING" };

        RatingBoardCatalog.TryResolve(claim, "igdb").ShouldBeNull();
    }

    [Fact]
    public void DefaultBoardPreference_CoversEveryBoardExactlyOnce()
    {
        RatingBoardCatalog.DefaultBoardPreference
            .OrderBy(b => b)
            .ShouldBe(Enum.GetValues<RatingBoard>().OrderBy(b => b));
    }

    [Fact]
    public void GetCategories_Esrb_ReturnsCanonicalCodesYoungestFirstThenPending()
    {
        var codes = RatingBoardCatalog.GetCategories(RatingBoard.Esrb)
            .Select(c => c.Code)
            .ToList();

        codes.ShouldBe(["E", "EC", "E10+", "T", "M", "AO", "RP"]);
    }

    [Fact]
    public void GetCategories_Acb_PlacesRefusedClassificationLast()
    {
        var categories = RatingBoardCatalog.GetCategories(RatingBoard.Acb);

        categories[^1].Code.ShouldBe("RC");
        categories[^1].Designation.ShouldBe(RatingDesignation.RefusedClassification);
    }

    [Theory]
    [InlineData(RatingBoard.Esrb)]
    [InlineData(RatingBoard.Pegi)]
    [InlineData(RatingBoard.Cero)]
    [InlineData(RatingBoard.Usk)]
    [InlineData(RatingBoard.Grac)]
    [InlineData(RatingBoard.ClassInd)]
    [InlineData(RatingBoard.Acb)]
    public void GetCategories_EveryBoard_NonEmptyAndRoundTripsThroughTryResolve(RatingBoard board)
    {
        var categories = RatingBoardCatalog.GetCategories(board);

        categories.ShouldNotBeEmpty();
        foreach (var category in categories)
        {
            // Each enumerated canonical code must resolve back to the identical category —
            // the picker can never offer a code the resolver would reject.
            RatingBoardCatalog.TryResolve(board, category.Code).ShouldBe(category);
        }
    }
}
