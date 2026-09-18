using Romd.Domain.Source.Platform;
using Shouldly;
using Xunit;

namespace Romd.Domain.Tests.Source.Platform;

public sealed class DatHeaderPlatformMatcherTests
{
    private const int Snes = 1;
    private const int Genesis = 2;
    private const int PlayStation = 3;

    private static Dictionary<string, int> DefaultLookup() => new(StringComparer.Ordinal)
    {
        // Platform names
        ["super nintendo entertainment system"] = Snes,
        ["sega genesis"] = Genesis,
        ["playstation"] = PlayStation,

        // Short names
        ["snes"] = Snes,
        ["genesis"] = Genesis,
        ["psx"] = PlayStation,

        // Name aliases
        ["super famicom"] = Snes,
        ["mega drive"] = Genesis
    };

    [Fact]
    public void Match_ExactPlatformName_ReturnsPlatform()
    {
        DatHeaderPlatformMatcher.Match("Super Nintendo Entertainment System", DefaultLookup())
            .ShouldBe(Snes);
    }

    [Fact]
    public void Match_NoIntroVendorPrefix_DropsPrefixAndMatches()
    {
        DatHeaderPlatformMatcher.Match("Nintendo - Super Nintendo Entertainment System", DefaultLookup())
            .ShouldBe(Snes);
    }

    [Fact]
    public void Match_ParentCloneSuffix_StripsParentheticalAndMatches()
    {
        DatHeaderPlatformMatcher.Match(
                "Nintendo - Super Nintendo Entertainment System (Parent-Clone)",
                DefaultLookup())
            .ShouldBe(Snes);
    }

    [Fact]
    public void Match_BracketedSuffix_StripsBracketsAndMatches()
    {
        DatHeaderPlatformMatcher.Match("Sony - PlayStation [Retool 2026-01-01]", DefaultLookup())
            .ShouldBe(PlayStation);
    }

    [Fact]
    public void Match_MultiDashHeader_MatchesLastSegmentShortName()
    {
        // No-Intro's real name for the platform — last segment hits the "genesis" short name
        DatHeaderPlatformMatcher.Match("Sega - Mega Drive - Genesis", DefaultLookup())
            .ShouldBe(Genesis);
    }

    [Fact]
    public void Match_IndividualSegmentAlias_MatchesWhenSuffixesDoNot()
    {
        var lookup = new Dictionary<string, int>(StringComparer.Ordinal) { ["mega drive"] = Genesis };

        DatHeaderPlatformMatcher.Match("Sega - Mega Drive - Genesis", lookup).ShouldBe(Genesis);
    }

    [Fact]
    public void Match_AliasMatch_RoutesByUserAlias()
    {
        DatHeaderPlatformMatcher.Match("Nintendo - Super Famicom", DefaultLookup()).ShouldBe(Snes);
    }

    [Fact]
    public void Match_VendorPrefixAlone_NeverMatches()
    {
        // The leading segment is a vendor prefix; "nintendo" must not be tried alone
        var lookup = new Dictionary<string, int>(StringComparer.Ordinal) { ["nintendo"] = Snes };

        DatHeaderPlatformMatcher.Match("Nintendo - Unknown Handheld 9000", lookup).ShouldBeNull();
    }

    [Fact]
    public void Match_UnknownHeader_ReturnsNull()
    {
        DatHeaderPlatformMatcher.Match("Bandai - WonderSwan", DefaultLookup()).ShouldBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Match_BlankHeader_ReturnsNull(string header)
    {
        DatHeaderPlatformMatcher.Match(header, DefaultLookup()).ShouldBeNull();
    }

    [Fact]
    public void Match_EmptyLookup_ReturnsNull()
    {
        DatHeaderPlatformMatcher.Match(
                "Nintendo - Super Nintendo Entertainment System",
                new Dictionary<string, int>())
            .ShouldBeNull();
    }

    [Fact]
    public void Match_CaseAndWhitespaceInsensitive()
    {
        DatHeaderPlatformMatcher.Match("NINTENDO   -   SUPER  NINTENDO ENTERTAINMENT SYSTEM", DefaultLookup())
            .ShouldBe(Snes);
    }

    [Fact]
    public void Match_FullHeaderPreferredOverLooserCandidates()
    {
        // A full-string key must win before any segment-derived candidate
        var lookup = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["sega - mega drive - genesis"] = PlayStation,
            ["genesis"] = Genesis
        };

        DatHeaderPlatformMatcher.Match("Sega - Mega Drive - Genesis", lookup).ShouldBe(PlayStation);
    }

    [Fact]
    public void EnumerateCandidates_HeaderOnlyParenthetical_YieldsFullOnly()
    {
        var candidates = DatHeaderPlatformMatcher.EnumerateCandidates("(Parent-Clone)").ToList();

        candidates.ShouldBe(new[] { "(parent-clone)" });
    }
}
