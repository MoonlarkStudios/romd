using Romd.Admin.Application.Titles.Enrichment;
using Romd.Domain.Catalog;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Titles.Enrichment;

public class EnrichmentResultTests
{
    [Fact]
    public void Found_SetsAllProperties()
    {
        var data = new EnrichmentData { Description = "A game" };
        var media = new Dictionary<MediaType, string>
        {
            [MediaType.Cover] = "https://example.com/cover.jpg"
        };

        var result = EnrichmentResult.Found("12345", 0.85f, data, media);

        result.Outcome.ShouldBe(EnrichmentOutcome.Found);
        result.ExternalId.ShouldBe("12345");
        result.MatchConfidence.ShouldBe(0.85f);
        result.Data.ShouldBe(data);
        result.MediaUrls.Count.ShouldBe(1);
        result.ErrorMessage.ShouldBeNull();
    }

    [Fact]
    public void Found_WithoutMedia_EmptyDictionary()
    {
        var data = new EnrichmentData { Description = "A game" };

        var result = EnrichmentResult.Found("12345", 0.85f, data);

        result.MediaUrls.ShouldBeEmpty();
    }

    [Fact]
    public void NotFound_SetsCorrectOutcome()
    {
        var result = EnrichmentResult.NotFound();

        result.Outcome.ShouldBe(EnrichmentOutcome.NotFound);
        result.ExternalId.ShouldBeNull();
        result.Data.ShouldBeNull();
        result.MatchConfidence.ShouldBe(0f);
    }

    [Fact]
    public void Error_SetsMessageAndOutcome()
    {
        var result = EnrichmentResult.Error("API rate limited");

        result.Outcome.ShouldBe(EnrichmentOutcome.Error);
        result.ErrorMessage.ShouldBe("API rate limited");
        result.ExternalId.ShouldBeNull();
        result.Data.ShouldBeNull();
    }

    [Fact]
    public void PlatformNotSupported_SetsCorrectOutcome()
    {
        var result = EnrichmentResult.PlatformNotSupported();

        result.Outcome.ShouldBe(EnrichmentOutcome.PlatformNotSupported);
    }
}
