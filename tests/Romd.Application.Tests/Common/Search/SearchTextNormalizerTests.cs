using Romd.Application.Common.Search;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Common.Search;

public sealed class SearchTextNormalizerTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    [InlineData("--- *** \"\"")]
    public void Normalize_NoLettersOrDigits_ReturnsEmpty(string? text) =>
        SearchTextNormalizer.Normalize(text).ShouldBe(string.Empty);

    [Theory]
    [InlineData("MARIO Bros", "mario bros")]
    [InlineData("İstanbul", "istanbul")]
    [InlineData("STRASSE straße", "strasse straße")]
    public void Normalize_MixedCase_LowercasesInvariantly(string text, string expected) =>
        SearchTextNormalizer.Normalize(text).ShouldBe(expected);

    [Theory]
    [InlineData("Pokémon", "pokemon")]
    [InlineData("Ōkami", "okami")]
    [InlineData("Über Racer", "uber racer")]
    [InlineData("Señor Fußball", "senor fußball")]
    [InlineData("Ærø", "ærø")]
    public void Normalize_LatinDiacritics_FoldToBaseLetters(string text, string expected) =>
        SearchTextNormalizer.Normalize(text).ShouldBe(expected);

    [Theory]
    [InlineData("Ｆ－ＺＥＲＯ", "f zero")]
    [InlineData("ﬁnal ﬁght", "final fight")]
    [InlineData("Level ①", "level 1")]
    [InlineData("Mega Man Ⅱ", "mega man ii")]
    public void Normalize_CompatibilityForms_FoldToAscii(string text, string expected) =>
        SearchTextNormalizer.Normalize(text).ShouldBe(expected);

    [Theory]
    [InlineData("Link's Awakening", "link s awakening")]
    [InlineData("Dr. Mario", "dr mario")]
    [InlineData("Mega-Man X", "mega man x")]
    [InlineData("Zelda II: The Adventure of Link", "zelda ii the adventure of link")]
    [InlineData("Legend of Zelda, The (USA) [!]", "legend of zelda the usa")]
    [InlineData("R.O.B.", "r o b")]
    public void Normalize_Punctuation_BecomesSingleSeparators(string text, string expected) =>
        SearchTextNormalizer.Normalize(text).ShouldBe(expected);

    [Fact]
    public void Normalize_RepeatedAndEdgeSeparators_AreCollapsedAndTrimmed() =>
        SearchTextNormalizer.Normalize("  --mario***   bros__2  ").ShouldBe("mario bros 2");

    [Theory]
    [InlineData("\"mario\"", "mario")]
    [InlineData("mario*", "mario")]
    [InlineData("mario AND luigi", "mario and luigi")]
    [InlineData("NEAR(mario, luigi)", "near mario luigi")]
    [InlineData("-mario", "mario")]
    [InlineData("^mario", "mario")]
    [InlineData("mario:*", "mario")]
    [InlineData("mario & luigi | peach ! ' \\", "mario luigi peach")]
    public void Normalize_SearchOperatorsAndQuotes_AreDroppedOrLiteral(string text, string expected) =>
        SearchTextNormalizer.Normalize(text).ShouldBe(expected);

    [Theory]
    [InlineData("ドラゴンクエスト", "ドラゴンクエスト")]
    [InlineData("デジモン", "デジモン")]
    [InlineData("ﾃﾞｼﾞﾀﾙ", "デジタル")]
    [InlineData("हिन्दी खेल", "हिन्दी खेल")]
    [InlineData("東方Project", "東方project")]
    public void Normalize_NonLatinScripts_KeepCombiningMarksAndLetters(string text, string expected) =>
        SearchTextNormalizer.Normalize(text).ShouldBe(expected);

    [Theory]
    [InlineData("Pokémon: Ｒed (USA)")]
    [InlineData("Zelda II: The Adventure of Link")]
    [InlineData("デジモン")]
    [InlineData("हिन्दी खेल")]
    [InlineData("  --mario***   bros__2  ")]
    public void Normalize_AppliedTwice_IsIdempotent(string text)
    {
        string once = SearchTextNormalizer.Normalize(text);

        SearchTextNormalizer.Normalize(once).ShouldBe(once);
    }

    [Fact]
    public void Tokenize_NormalizedInput_SplitsOnSingleSeparators() =>
        SearchTextNormalizer.Tokenize("Super Mario Bros. 3 (USA)").ShouldBe(["super", "mario", "bros", "3", "usa"]);

    [Fact]
    public void Tokenize_NoLettersOrDigits_ReturnsEmpty() =>
        SearchTextNormalizer.Tokenize(" *** ").ShouldBeEmpty();
}
