using System.Text.RegularExpressions;
using Romd.Domain.Taxonomy;
using Shouldly;
using Xunit;

namespace Romd.Domain.Tests.Taxonomy;

public sealed class UnregisteredTaxonomyKeyTests
{
    private static readonly Regex KeyShape = new("^dat-[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant);

    [Fact]
    public void Candidates_AsciiToken_PrefersTheSlugThenTheHashedStem()
    {
        var candidates = UnregisteredTaxonomyKey.Candidates("usa").Take(3).ToArray();

        candidates.ShouldBe(["dat-usa", "dat-usa-5fc90ab335", "dat-usa-5fc90ab335-2"]);
    }

    [Fact]
    public void Candidates_PunctuationRuns_CollapseToOneSeparatorAndTrimTheEnds()
    {
        UnregisteredTaxonomyKey.Candidates("hong  kong (hk)").First().ShouldBe("dat-hong-kong-hk");
        UnregisteredTaxonomyKey.Candidates("--a--b--").First().ShouldBe("dat-a-b");
    }

    [Fact]
    public void Candidates_TruncationAtASeparator_TrimsTheExposedSeparator()
    {
        string token = new string('a', 39) + " b";

        var candidates = UnregisteredTaxonomyKey.Candidates(token).Take(2).ToArray();

        candidates.ShouldBe(["dat-" + new string('a', 39), "dat-" + new string('a', 39) + "-cd6b30814f"]);
    }

    [Fact]
    public void Candidates_LongToken_TruncatesTheSlugToItsMaximum()
    {
        string slug = UnregisteredTaxonomyKey.Candidates(new string('x', 90)).First()[UnregisteredTaxonomyKey.Prefix.Length..];

        slug.ShouldBe(new string('x', UnregisteredTaxonomyKey.MaxSlugLength));
    }

    [Fact]
    public void Candidates_EmptySlug_StartsWithTheBareHash()
    {
        var candidates = UnregisteredTaxonomyKey.Candidates("日本語").Take(2).ToArray();

        candidates.ShouldBe(["dat-77710aedc7", "dat-77710aedc7-2"]);
        UnregisteredTaxonomyKey.HashedStem("日本語").ShouldBe("dat-77710aedc7");
    }

    [Fact]
    public void Candidates_NonAsciiLetters_AreSeparatorsInTheSlug()
    {
        UnregisteredTaxonomyKey.Candidates("españa").Take(2)
            .ShouldBe(["dat-espa-a", "dat-espa-a-57380c5b1c"]);
    }

    [Fact]
    public void Candidates_HashFallback_KeepsItsOrderThroughTheLastOrdinal()
    {
        var candidates = UnregisteredTaxonomyKey.Candidates("usa").ToArray();

        candidates.Length.ShouldBe(UnregisteredTaxonomyKey.MaxOrdinal + 1);
        candidates.Distinct(StringComparer.Ordinal).Count().ShouldBe(candidates.Length);
        candidates[^1].ShouldBe($"dat-usa-5fc90ab335-{UnregisteredTaxonomyKey.MaxOrdinal}");
    }

    [Theory]
    [InlineData("usa")]
    [InlineData("hong  kong (hk)")]
    [InlineData("日本語")]
    [InlineData("españa")]
    [InlineData("-")]
    [InlineData("")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa b")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public void Candidates_EveryCandidate_FitsTheReservedKeyShapeAndColumn(string token)
    {
        foreach (string candidate in UnregisteredTaxonomyKey.Candidates(token))
        {
            KeyShape.IsMatch(candidate).ShouldBeTrue(candidate);
            candidate.Length.ShouldBeLessThanOrEqualTo(64);
        }
    }

    [Fact]
    public void Mint_TakenCandidates_ReturnsTheFirstFreeOne()
    {
        var taken = new HashSet<string>(StringComparer.Ordinal) { "dat-usa", "dat-usa-5fc90ab335" };

        UnregisteredTaxonomyKey.Mint("usa", taken.Contains).ShouldBe("dat-usa-5fc90ab335-2");
        UnregisteredTaxonomyKey.Mint("usa", _ => false).ShouldBe("dat-usa");
    }
}
