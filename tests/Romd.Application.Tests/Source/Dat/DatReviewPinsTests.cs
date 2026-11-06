using System.Text.RegularExpressions;
using Romd.Admin.Application.Source.Dat;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Source.Dat;

public sealed class DatReviewPinsTests
{
    [Theory]
    [InlineData("", true, false)]
    [InlineData("abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789", true, true)]
    [InlineData("ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789", true, true)]
    [InlineData("short", false, false)]
    [InlineData("zzcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789", false, false)]
    [InlineData(null, false, false)]
    public void Validators_AgreeWithTheDeclaredParameterPatterns(string? value, bool active, bool candidate)
    {
        DatReviewPins.IsActive(value).ShouldBe(active);
        DatReviewPins.IsCandidate(value).ShouldBe(candidate);
        if (value is null)
            return;
        Regex.IsMatch(value, DatReviewPins.ActivePattern).ShouldBe(active);
        Regex.IsMatch(value, DatReviewPins.CandidatePattern).ShouldBe(candidate);
    }

    [Fact]
    public void Failures_NameEachMalformedPinByItsWireName() =>
        DatReviewPins.Failures("short", "").Select(failure => failure.Field).ShouldBe(["activeSha256", "candidateSha256"]);
}
