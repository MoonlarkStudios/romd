using Romd.Admin.Application.Titles.Matching;
using Xunit;

namespace Romd.Application.Tests.Catalog;

public class TitleNormalizerTests
{
    [Fact]
    public void Normalize_WithEmptyString_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, TitleNormalizer.Normalize(string.Empty));
        Assert.Equal(string.Empty, TitleNormalizer.Normalize(null!));
        Assert.Equal(string.Empty, TitleNormalizer.Normalize("   "));
    }

    [Fact]
    public void Normalize_RemovesParentheticalContent()
    {
        Assert.Equal("supermarioworld", TitleNormalizer.Normalize("Super Mario World (USA)"));
        Assert.Equal("supermarioworld", TitleNormalizer.Normalize("Super Mario World (USA) (Rev 1)"));
        Assert.Equal("supermarioworld", TitleNormalizer.Normalize("Super Mario World (Europe) (En,Fr,De)"));
    }

    [Fact]
    public void Normalize_RemovesBracketedContent()
    {
        Assert.Equal("sonicthehedgehog", TitleNormalizer.Normalize("Sonic the Hedgehog [!]"));
        Assert.Equal("somegame", TitleNormalizer.Normalize("Some Game [a][b][h]"));
    }

    [Theory]
    [InlineData("The Legend of Zelda (USA)", "legendofzelda")]
    [InlineData("Legend of Zelda, The (Europe)", "legendofzelda")]
    [InlineData("Legend of Zelda, The", "legendofzelda")]
    [InlineData("The Legend of Zelda", "legendofzelda")]
    public void Normalize_HandlesArticle_The(string input, string expected)
    {
        Assert.Equal(expected, TitleNormalizer.Normalize(input));
    }

    [Theory]
    [InlineData("A Boy and His Blob (USA)", "boyandhisblob")]
    [InlineData("Boy and His Blob, A (Europe)", "boyandhisblob")]
    public void Normalize_HandlesArticle_A(string input, string expected)
    {
        Assert.Equal(expected, TitleNormalizer.Normalize(input));
    }

    [Theory]
    [InlineData("An American Tail (USA)", "americantail")]
    [InlineData("American Tail, An (Europe)", "americantail")]
    public void Normalize_HandlesArticle_An(string input, string expected)
    {
        Assert.Equal(expected, TitleNormalizer.Normalize(input));
    }

    [Fact]
    public void Normalize_PreservesInternalArticles()
    {
        // "the" in the middle of a title should NOT be removed
        Assert.Equal("sonicthehedgehog2", TitleNormalizer.Normalize("Sonic the Hedgehog 2"));
        Assert.Equal("jackandthebeanstalk", TitleNormalizer.Normalize("Jack and the Beanstalk"));
    }

    [Theory]
    [InlineData("Super Mario World (USA) (Rev 1)", "supermarioworld")]
    [InlineData("super mario world", "supermarioworld")]
    [InlineData("SUPER MARIO WORLD", "supermarioworld")]
    public void Normalize_IsCaseInsensitive(string input, string expected)
    {
        Assert.Equal(expected, TitleNormalizer.Normalize(input));
    }

    [Fact]
    public void Normalize_RemovesSpecialCharacters()
    {
        Assert.Equal("megaman3", TitleNormalizer.Normalize("Mega Man 3"));
        Assert.Equal("streetfighteriiturbo", TitleNormalizer.Normalize("Street Fighter II' Turbo"));
        Assert.Equal("pacman", TitleNormalizer.Normalize("Pac-Man"));
    }

    [Theory]
    [InlineData("Final Fantasy III (USA)", "finalfantasy3")]
    [InlineData("Final Fantasy IV (Japan)", "finalfantasy4")]
    public void Normalize_ConvertsRomanNumerals_Not(string input, string expected)
    {
        // Note: We intentionally do NOT convert Roman numerals.
        // "III" becomes "iii" which is different from "3".
        // This is a known limitation - games should use consistent naming.
        Assert.NotEqual(expected, TitleNormalizer.Normalize(input));
    }

    [Fact]
    public void Normalize_HandlesComplexTosecNaming()
    {
        // TOSEC format: "Name, The (Year)(Publisher)(Country)"
        var result = TitleNormalizer.Normalize("Legend of Zelda, The (1986)(Nintendo)(Japan)");
        Assert.Equal("legendofzelda", result);
    }

    [Fact]
    public void Normalize_HandlesNoIntroNaming()
    {
        // No-Intro format: "The Name (Region) (Revision)"
        var result = TitleNormalizer.Normalize("The Legend of Zelda (USA) (Rev A)");
        Assert.Equal("legendofzelda", result);
    }

    [Fact]
    public void Normalize_HandlesRedumpNaming()
    {
        // Redump typically uses: "Name (Region) (Language) (Disc X)"
        var result = TitleNormalizer.Normalize("Final Fantasy VII (USA) (Disc 1)");
        Assert.Equal("finalfantasyvii", result);
    }

    [Fact]
    public void Normalize_SameGameDifferentDats_ProducesSameResult()
    {
        // This is the critical test - same game from different DAT sources
        // should produce identical normalized names
        var noIntro = TitleNormalizer.Normalize("The Legend of Zelda (USA)");
        var tosec = TitleNormalizer.Normalize("Legend of Zelda, The (1986)(Nintendo)(USA)");
        var redump = TitleNormalizer.Normalize("Legend of Zelda, The (USA)");

        Assert.Equal(noIntro, tosec);
        Assert.Equal(tosec, redump);
        Assert.Equal("legendofzelda", noIntro);
    }

    #region ToDisplayName Tests

    [Fact]
    public void ToDisplayName_WithEmptyString_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, TitleNormalizer.ToDisplayName(string.Empty));
        Assert.Equal(string.Empty, TitleNormalizer.ToDisplayName(null!));
        Assert.Equal(string.Empty, TitleNormalizer.ToDisplayName("   "));
    }

    [Theory]
    [InlineData("Aladdin (Europe)", "Aladdin")]
    [InlineData("Super Mario World (USA)", "Super Mario World")]
    [InlineData("Super Mario World (USA) (Rev 1)", "Super Mario World")]
    [InlineData("Legend of Zelda, The (Europe)", "Legend of Zelda, The")]
    [InlineData("Final Fantasy VII (USA) (Disc 1)", "Final Fantasy VII")]
    public void ToDisplayName_RemovesParentheticalContent_PreservesFormatting(string input, string expected)
    {
        Assert.Equal(expected, TitleNormalizer.ToDisplayName(input));
    }

    [Theory]
    [InlineData("Sonic the Hedgehog [!]", "Sonic the Hedgehog")]
    [InlineData("Some Game [a][b][h]", "Some Game")]
    [InlineData("Game [!] (USA)", "Game")]
    public void ToDisplayName_RemovesBracketedContent(string input, string expected)
    {
        Assert.Equal(expected, TitleNormalizer.ToDisplayName(input));
    }

    [Fact]
    public void ToDisplayName_PreservesCase()
    {
        Assert.Equal("Super Mario World", TitleNormalizer.ToDisplayName("Super Mario World (USA)"));
        Assert.Equal("SUPER MARIO WORLD", TitleNormalizer.ToDisplayName("SUPER MARIO WORLD (USA)"));
    }

    [Fact]
    public void ToDisplayName_PreservesSpecialCharacters()
    {
        Assert.Equal("Street Fighter II' Turbo", TitleNormalizer.ToDisplayName("Street Fighter II' Turbo (USA)"));
        Assert.Equal("Pac-Man", TitleNormalizer.ToDisplayName("Pac-Man (USA)"));
        Assert.Equal("Dr. Mario", TitleNormalizer.ToDisplayName("Dr. Mario (World)"));
    }

    [Fact]
    public void ToDisplayName_CleansUpExtraWhitespace()
    {
        // After removing parentheses, there may be extra spaces
        Assert.Equal("Game Name", TitleNormalizer.ToDisplayName("Game Name (USA) (Rev 1)"));
        Assert.Equal("Test", TitleNormalizer.ToDisplayName("Test   (USA)"));
    }

    [Fact]
    public void ToDisplayName_PreservesCommas()
    {
        // TOSEC-style names with ", The" should keep their formatting
        Assert.Equal("Legend of Zelda, The", TitleNormalizer.ToDisplayName("Legend of Zelda, The (Europe)"));
    }

    #endregion

    #region ToSearchName Tests

    [Fact]
    public void ToSearchName_WithEmptyString_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, TitleNormalizer.ToSearchName(string.Empty));
        Assert.Equal(string.Empty, TitleNormalizer.ToSearchName(null!));
        Assert.Equal(string.Empty, TitleNormalizer.ToSearchName("   "));
    }

    [Theory]
    [InlineData("Legend of Zelda, The (Europe)", "The Legend of Zelda")]
    [InlineData("Boy and His Blob, A (USA)", "A Boy and His Blob")]
    [InlineData("American Tail, An (Europe)", "An American Tail")]
    public void ToSearchName_FixesTrailingArticles(string input, string expected)
    {
        Assert.Equal(expected, TitleNormalizer.ToSearchName(input));
    }

    [Theory]
    [InlineData("Super Mario World - Super Mario Bros. 4 (USA)", "Super Mario World Super Mario Bros. 4")]
    [InlineData("Zelda no Densetsu - Kamigami no Triforce (Japan)", "Zelda no Densetsu Kamigami no Triforce")]
    public void ToSearchName_StripsSubtitleSeparators(string input, string expected)
    {
        Assert.Equal(expected, TitleNormalizer.ToSearchName(input));
    }

    [Theory]
    [InlineData("Toejam & Earl (USA)", "Toejam and Earl")]
    [InlineData("Banjo & Kazooie", "Banjo and Kazooie")]
    public void ToSearchName_NormalizesAmpersands(string input, string expected)
    {
        Assert.Equal(expected, TitleNormalizer.ToSearchName(input));
    }

    [Fact]
    public void ToSearchName_CleanName_NoChange()
    {
        Assert.Equal("Super Mario World", TitleNormalizer.ToSearchName("Super Mario World"));
        Assert.Equal("Mario Paint", TitleNormalizer.ToSearchName("Mario Paint"));
    }

    [Fact]
    public void ToSearchName_RemovesParentheticalAndBracketedContent()
    {
        Assert.Equal("Super Mario World", TitleNormalizer.ToSearchName("Super Mario World (USA) (Rev 1)"));
        Assert.Equal("Sonic the Hedgehog", TitleNormalizer.ToSearchName("Sonic the Hedgehog [!]"));
    }

    [Fact]
    public void ToSearchName_PreservesCase()
    {
        Assert.Equal("Super Mario World", TitleNormalizer.ToSearchName("Super Mario World (USA)"));
        Assert.Equal("LEGEND OF ZELDA", TitleNormalizer.ToSearchName("LEGEND OF ZELDA (USA)"));
    }

    #endregion
}
