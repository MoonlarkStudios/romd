using Romd.Admin.Application.Search;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Search;

public sealed class EnrichmentFilterParserTests
{
    [Fact]
    public void ParseFlags_DuplicatesInAnyOrder_ReturnsDistinctFlagsInChipOrder() =>
        EnrichmentFilterParser.ParseFlags(["Automatic", "NeedsReview", "Processing", "NeedsReview", "Blocked"]).Value.ShouldBe([
            EnrichmentFlagFilter.NeedsReview, EnrichmentFlagFilter.Blocked, EnrichmentFlagFilter.Processing, EnrichmentFlagFilter.Automatic]);

    [Fact]
    public void ParseFlags_RepeatedSettled_ReturnsSettledAlone() =>
        EnrichmentFilterParser.ParseFlags(["Settled", "Settled"]).Value.ShouldBe([EnrichmentFlagFilter.Settled]);

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    [InlineData("needsreview")]
    [InlineData("0")]
    [InlineData("NeedsReview,Blocked")]
    [InlineData("NeedsReview, Blocked")]
    [InlineData("Completed")]
    public void ParseFlags_ValueOutsideVocabulary_ReturnsInvalidEnrichmentFilter(string? value)
    {
        var result = EnrichmentFilterParser.ParseFlags([value]);

        result.IsError.ShouldBeTrue();
        result.Errors.ShouldHaveSingleItem().Code.ShouldBe("Search.InvalidEnrichmentFilter");
    }

    [Fact]
    public void ParseFlags_NoValues_ReturnsEnrichmentFilterRequired() =>
        EnrichmentFilterParser.ParseFlags([]).FirstError.Code.ShouldBe("Search.EnrichmentFilterRequired");

    [Fact]
    public void ParseFlags_SettledWithFlagAndRepeatedUnknownValue_PreservesEveryFailure()
    {
        var result = EnrichmentFilterParser.ParseFlags(["Settled", "Blocked", "Bogus", "Bogus"]);

        result.Errors.Select(error => error.Code).ShouldBe(["Search.InvalidEnrichmentFilter", "Search.EnrichmentFilterConflict"]);
        result.Errors[0].Description.ShouldContain("'Bogus'");
        result.Errors[1].Description.ShouldBe("Settled cannot be combined with other enrichment filters.");
    }

    [Theory]
    [InlineData("None", true)]
    [InlineData("Pending", true)]
    [InlineData("Completed", true)]
    [InlineData("Failed", true)]
    [InlineData("NotFound", true)]
    [InlineData("LowConfidence", true)]
    [InlineData("NeedsReview", false)]
    [InlineData("Blocked", false)]
    [InlineData("Processing", false)]
    [InlineData("Automatic", false)]
    [InlineData("pending", false)]
    [InlineData("1", false)]
    [InlineData("Pending, Completed", false)]
    public void IsLastAttemptStatus_OnlyExactLastAttemptNames_ReturnsTrue(string value, bool expected) =>
        EnrichmentFilterParser.IsLastAttemptStatus(value).ShouldBe(expected);
}
