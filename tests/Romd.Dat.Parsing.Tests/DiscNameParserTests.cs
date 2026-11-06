using System.Globalization;
using Romd.Dat.Parsing.Models;
using Shouldly;
using Xunit;

namespace Romd.Dat.Parsing.Tests;

public sealed class DiscNameParserTests
{
    [Theory]
    [InlineData("Final Fantasy VII (USA) (Disc 1)", "Final Fantasy VII (USA)", 1, null)]
    [InlineData("Game (Japan) (En,Ja) (Disc 2 of 3) (Rev 1)", "Game (Japan) (En,Ja) (Rev 1)", 2, 3)]
    [InlineData(" Game   (USA)  (dIsC 01) ", "Game (USA)", 1, null)]
    [InlineData("Game (Disc 10) (Europe) [! + unknown]", "Game (Europe) [! + unknown]", 10, null)]
    [InlineData("Game (Disc 99 of 99)", "Game", 99, 99)]
    [InlineData("Night Trap (Europe) (Disc 1) (Mega-CD 32X)", "Night Trap (Europe) (Mega-CD 32X)", 1, null)]
    [InlineData("Night Trap (USA) (Disc 2) (Sega CD 32X) (RE-1)", "Night Trap (USA) (Sega CD 32X) (RE-1)", 2, null)]
    [InlineData("Martialbeat 2 (Japan) (Disc 1) (Disc-B)", "Martialbeat 2 (Japan) (Disc-B)", 1, null)]
    public void Parse_SupportedMarker_PreservesEdition(string name, string edition, int ordinal, int? total)
    {
        var result = DiscNameParser.Parse(name);

        result.Status.ShouldBe(DiscNameStatus.Parsed);
        result.Issue.ShouldBeNull();
        result.OriginalName.ShouldBe(name);
        result.EditionName.ShouldBe(edition);
        result.GroupingKey.ShouldBe(edition.ToUpperInvariant());
        result.DiscNumber.ShouldBe(ordinal);
        result.DeclaredDiscCount.ShouldBe(total);
    }

    [Theory]
    [InlineData("Discworld (USA)")]
    [InlineData("Game (USA) (Rev 1)")]
    [InlineData("CD Player (Europe)")]
    [InlineData("Disco (Japan)")]
    [InlineData("Odekake Lester - Lelele no Le (^^; (Japan)")]
    [InlineData("Corpse Killer (Europe) (Mega-CD 32X)")]
    [InlineData("Corpse Killer (USA) (Sega CD 32X)")]
    public void Parse_NoDiscMarker_DoesNotInventMembership(string name)
    {
        var result = DiscNameParser.Parse(name);

        result.Status.ShouldBe(DiscNameStatus.NoDiscMarker);
        result.GroupingKey.ShouldBeNull();
        result.DiscNumber.ShouldBeNull();
    }

    [Theory]
    [InlineData(null, DiscNameIssue.EmptyName)]
    [InlineData("   ", DiscNameIssue.EmptyName)]
    [InlineData("Game (USA (Disc 1))", DiscNameIssue.MalformedQualifiers)]
    [InlineData("Game (Disc 1", DiscNameIssue.MalformedQualifiers)]
    [InlineData("Game (Disc 1))", DiscNameIssue.MalformedQualifiers)]
    [InlineData("Game (Disc 1) (Disc 2)", DiscNameIssue.MultipleDiscMarkers)]
    [InlineData("Game (Disc 1) (Disk 2)", DiscNameIssue.MultipleDiscMarkers)]
    [InlineData("Game Disc 2 (Disc 1)", DiscNameIssue.MultipleDiscMarkers)]
    [InlineData("Game (Disc 0)", DiscNameIssue.DiscNumberOutOfRange)]
    [InlineData("Game (Disc 100)", DiscNameIssue.DiscNumberOutOfRange)]
    [InlineData("Game (Disc 999999999999999999999999)", DiscNameIssue.DiscNumberOutOfRange)]
    [InlineData("Game (Disc 2 of 1)", DiscNameIssue.InvalidDiscTotal)]
    [InlineData("Game (Disc 1 of 0)", DiscNameIssue.InvalidDiscTotal)]
    [InlineData("Game (Disc 1 of 100)", DiscNameIssue.InvalidDiscTotal)]
    [InlineData("Game (Disc 1 of 999999999999999999)", DiscNameIssue.InvalidDiscTotal)]
    [InlineData("Game (USA) (Bonus Disc)", DiscNameIssue.UnsupportedDiscMarker)]
    [InlineData("Game (Install Disc)", DiscNameIssue.UnsupportedDiscMarker)]
    [InlineData("Game (Opening Disc)", DiscNameIssue.UnsupportedDiscMarker)]
    [InlineData("Game (Special Bonus Disc)", DiscNameIssue.UnsupportedDiscMarker)]
    [InlineData("Game (Bonus Disc 2)", DiscNameIssue.UnsupportedDiscMarker)]
    [InlineData("Game (Audio CD)", DiscNameIssue.UnsupportedDiscMarker)]
    [InlineData("Game (Data Disk)", DiscNameIssue.UnsupportedDiscMarker)]
    [InlineData("Game (Disc 1) (Bonus Disc)", DiscNameIssue.MultipleDiscMarkers)]
    [InlineData("Game (Disc 1) (Install Disc)", DiscNameIssue.MultipleDiscMarkers)]
    [InlineData("Game (Bonus Disc", DiscNameIssue.MalformedQualifiers)]
    [InlineData("Game (Disc One)", DiscNameIssue.UnsupportedDiscMarker)]
    [InlineData("Game (Disc 1/3)", DiscNameIssue.UnsupportedDiscMarker)]
    [InlineData("Game (Disc -1)", DiscNameIssue.UnsupportedDiscMarker)]
    [InlineData("Game (Disc ١)", DiscNameIssue.UnsupportedDiscMarker)]
    [InlineData("Game (Disc1)", DiscNameIssue.UnsupportedDiscMarker)]
    [InlineData("Game (Disk 1)", DiscNameIssue.UnsupportedDiscMarker)]
    [InlineData("Game (CD 1)", DiscNameIssue.UnsupportedDiscMarker)]
    [InlineData("Game Disc 1 (USA)", DiscNameIssue.UnsupportedDiscMarker)]
    [InlineData("(Disc 1)", DiscNameIssue.MissingTitle)]
    [InlineData("(USA) (Disc 1)", DiscNameIssue.MissingTitle)]
    [InlineData("Sister Princess 2 - Premium Fan Disc (Japan) (Disc A)", DiscNameIssue.UnsupportedDiscMarker)]
    [InlineData("Game-Ware Vol. 4 (Japan) (Disc B)", DiscNameIssue.UnsupportedDiscMarker)]
    [InlineData("PC Engine Hyper Catalog CD-ROM - 1994 Summer (Japan) (Disc A)", DiscNameIssue.UnsupportedDiscMarker)]
    [InlineData("Enemy Zero (Europe) (Disc 0) (Opening Disc)", DiscNameIssue.DiscNumberOutOfRange)]
    public void Parse_UnsupportedOrMalformedName_RequiresReview(string? name, DiscNameIssue issue)
    {
        var result = DiscNameParser.Parse(name);

        result.Status.ShouldBe(DiscNameStatus.NeedsReview);
        result.Issue.ShouldBe(issue);
        result.GroupingKey.ShouldBeNull();
        result.DiscNumber.ShouldBeNull();
    }

    [Fact]
    public void Parse_NameLengthBound_RejectsBeforeScanning()
    {
        DiscNameParser.Parse(new string('x', DiscNameParser.MaxNameLength + 1)).Issue
            .ShouldBe(DiscNameIssue.NameTooLong);
        DiscNameParser.Parse(new string('x', DiscNameParser.MaxNameLength)).Status
            .ShouldBe(DiscNameStatus.NoDiscMarker);
    }

    [Fact]
    public void Parse_DiscSpecificLabel_RetainsItAsAnUnresolvedQualifier()
    {
        var result = DiscNameParser.Parse("Command & Conquer (USA) (Disc 1) (Allies)");

        result.EditionName.ShouldBe("Command & Conquer (USA) (Allies)");
        result.Qualifiers.ShouldBe(["USA", "Allies"]);
    }

    [Fact]
    public void Parse_TurkishCulture_UsesInvariantGroupingAndMarkerMatching()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            DiscNameParser.Parse("Final Fantasy VII (USA) (disc 1)").GroupingKey
                .ShouldBe("FINAL FANTASY VII (USA)");
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }
}
