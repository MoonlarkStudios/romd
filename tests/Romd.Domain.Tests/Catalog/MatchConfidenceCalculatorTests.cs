using Romd.Domain.Catalog;
using Shouldly;
using Xunit;

namespace Romd.Domain.Tests.Catalog;

public class MatchConfidenceCalculatorTests
{
    private readonly MatchConfidenceCalculator _calculator = new();

    [Fact]
    public void HashMatch_ReturnsHighConfidence()
    {
        var confidence = _calculator.Calculate(new MatchContext
        {
            SearchName = "Super Mario World",
            ResultName = "Super Mario World",
            HashMatched = true
        });

        confidence.ShouldBe(0.95f);
    }

    [Fact]
    public void HashMatch_WithYear_ReturnsPerfectConfidence()
    {
        var confidence = _calculator.Calculate(new MatchContext
        {
            SearchName = "Super Mario World",
            ResultName = "Super Mario World",
            HashMatched = true,
            YearMatched = true
        });

        confidence.ShouldBe(1.0f);
    }

    [Fact]
    public void ExactNameMatch_NoYear_HighConfidence()
    {
        var confidence = _calculator.Calculate(new MatchContext
        {
            SearchName = "Super Mario World",
            ResultName = "Super Mario World"
        });

        // Name similarity 1.0 * 0.85 + 0.05 exact bonus = 0.90
        confidence.ShouldBe(0.9f, tolerance: 0.01f);
    }

    [Fact]
    public void ExactNameMatch_WithYear_VeryHighConfidence()
    {
        var confidence = _calculator.Calculate(new MatchContext
        {
            SearchName = "Super Mario World",
            ResultName = "Super Mario World",
            YearMatched = true
        });

        // 0.85 + 0.05 exact + 0.1 year = 1.0 (capped)
        confidence.ShouldBeGreaterThanOrEqualTo(0.95f);
        confidence.ShouldBeLessThanOrEqualTo(1.0f);
    }

    [Fact]
    public void CloseNameMatch_ClearsThreshold()
    {
        var confidence = _calculator.Calculate(new MatchContext
        {
            SearchName = "Super Mario Bros",
            ResultName = "Super Mario Bros."
        });

        confidence.ShouldBeGreaterThanOrEqualTo(0.6f);
    }

    [Fact]
    public void FuzzyName_ModerateConfidence()
    {
        // "Mario" is contained in "Super Mario RPG", so containment boost applies
        var confidence = _calculator.Calculate(new MatchContext
        {
            SearchName = "Mario",
            ResultName = "Super Mario RPG"
        });

        // Short substring in long name → moderate confidence, not high
        confidence.ShouldBeGreaterThanOrEqualTo(0.5f);
        confidence.ShouldBeLessThan(0.75f);
    }

    [Fact]
    public void CompletelyDifferentName_VeryLow()
    {
        var confidence = _calculator.Calculate(new MatchContext
        {
            SearchName = "Super Mario World",
            ResultName = "Doom"
        });

        confidence.ShouldBeLessThan(0.4f);
    }

    [Fact]
    public void NullResultName_Zero()
    {
        var confidence = _calculator.Calculate(new MatchContext
        {
            SearchName = "Super Mario World",
            ResultName = null
        });

        confidence.ShouldBe(0.0f);
    }

    [Fact]
    public void YearMatch_AddsPointOne()
    {
        var withYear = _calculator.Calculate(new MatchContext
        {
            SearchName = "Super Mario World",
            ResultName = "Super Mario World",
            YearMatched = true
        });
        var withoutYear = _calculator.Calculate(new MatchContext
        {
            SearchName = "Super Mario World",
            ResultName = "Super Mario World",
            YearMatched = false
        });

        (withYear - withoutYear).ShouldBe(0.1f, tolerance: 0.001f);
    }

    [Fact]
    public void NameWithRegionSuffix_Normalized()
    {
        var confidence = _calculator.Calculate(new MatchContext
        {
            SearchName = "Super Mario World (USA)",
            ResultName = "Super Mario World"
        });

        // "(USA)" is stripped during normalization, names match exactly
        confidence.ShouldBeGreaterThanOrEqualTo(0.85f);
    }

    [Fact]
    public void CappedAtOne()
    {
        var confidence = _calculator.Calculate(new MatchContext
        {
            SearchName = "Test",
            ResultName = "Test",
            HashMatched = true,
            YearMatched = true
        });

        confidence.ShouldBeLessThanOrEqualTo(1.0f);
    }

    [Fact]
    public void ContainmentMatch_SubstringBoost()
    {
        var confidence = _calculator.Calculate(new MatchContext
        {
            SearchName = "Zelda",
            ResultName = "Legend of Zelda A Link to the Past"
        });

        // Containment boost: (0.6 + lengthRatio * 0.3) * 0.85
        // "zelda" (5 chars) vs "legend of zelda a link to the past" (~34 chars) → low ratio
        confidence.ShouldBeGreaterThanOrEqualTo(0.5f);
        confidence.ShouldBeLessThan(0.75f);
    }

    [Fact]
    public void ContainmentMatch_HighLengthRatio()
    {
        var confidence = _calculator.Calculate(new MatchContext
        {
            SearchName = "Super Mario World",
            ResultName = "Super Mario World 2"
        });

        // High length ratio containment → high similarity
        confidence.ShouldBeGreaterThan(0.75f);
    }
}
