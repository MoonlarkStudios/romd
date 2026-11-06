using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Romd.Dat.Parsing.Formats.Logiqx;
using Romd.Dat.Parsing.Models;
using Shouldly;
using Xunit;

namespace Romd.Dat.Parsing.Tests;

public sealed class DiscGroupingPreviewTests
{
    [Fact]
    public void Create_SegaCd32xEdition_FormsItsOwnOrderedCandidate()
    {
        var result = DiscGroupingPreview.Create("redump-mcd", [
            "Night Trap (Europe) (Disc 1) (Mega-CD 32X)",
            "Night Trap (Europe) (Disc 2) (Mega-CD 32X)",
            "Night Trap (Europe) (Disc 1)",
            "Night Trap (Europe) (Disc 2)"
        ]);

        result.ReviewEntries.ShouldBeEmpty();
        result.Groups.Length.ShouldBe(2);
        result.Groups.ShouldAllBe(g => g.IsNameCandidate);
        result.Groups.Single(g => g.EditionName.EndsWith("(Mega-CD 32X)"))
            .Members.Select(m => m.DiscNumber).ShouldBe([1, 2]);
    }

    [Fact]
    public void Create_ModifierFirstRole_PreservesReviewEntryWithoutAddingMember()
    {
        var result = DiscGroupingPreview.Create("source", ["Game (USA) (Disc 1)", "Game (USA) (Disc 2)", "Game (USA) (Bonus Disc)"]);

        result.ReviewEntries.ShouldHaveSingleItem().OriginalName.ShouldBe("Game (USA) (Bonus Disc)");
        var group = result.Groups.ShouldHaveSingleItem();
        group.Members.Select(m => m.DiscNumber).ShouldBe([1, 2]);
        group.DeclaredDiscCount.ShouldBeNull();
    }

    [Fact]
    public void Create_ClearNames_OrdersDiscsWithoutInventingTotal()
    {
        var result = DiscGroupingPreview.Create("ps1-sample", [
            "Final Fantasy VII (USA) (Disc 3)", "Final Fantasy VII (USA) (Disc 1)",
            "Final Fantasy VII (USA) (Disc 2)", "Discworld (USA)"
        ]);

        result.EntriesExamined.ShouldBe(4);
        result.WithoutDiscMarker.ShouldBe(1);
        result.RuleVersion.ShouldBe("dat-disc-name/v2");
        var group = result.Groups.ShouldHaveSingleItem();
        group.Members.Select(m => m.DiscNumber).ShouldBe([1, 2, 3]);
        group.IsNameCandidate.ShouldBeTrue();
        group.DeclaredDiscCount.ShouldBeNull();
        group.HasAllDeclaredDiscNames.ShouldBeFalse();
    }

    [Theory]
    [InlineData("Game (Europe) (Disc 2)")]
    [InlineData("Game (USA) (Rev 1) (Disc 2)")]
    [InlineData("Game (USA) (En) (Disc 2)")]
    [InlineData("Game (USA) (Demo) (Disc 2)")]
    [InlineData("Game (USA) (Unknown Edition) (Disc 2)")]
    [InlineData("Game (USA) [b] (Disc 2)")]
    [InlineData("Game (USA, Canada) (Disc 2)")]
    public void Create_DifferentEditionQualifiers_LeavesSeparateReviewGroups(string other)
    {
        var result = DiscGroupingPreview.Create("source", ["Game (USA) (Disc 1)", other]);

        result.Groups.Length.ShouldBe(2);
        result.Groups.ShouldAllBe(g => !g.IsNameCandidate);
        result.Groups.ShouldAllBe(g => g.ReviewReasons.Contains(DiscGroupReviewReason.SingleObservedDisc));
    }

    [Fact]
    public void Create_DifferentSources_DoesNotFillEachOthersMissingSlots()
    {
        var first = DiscGroupingPreview.Create("source-A", ["Game (USA) (Disc 1)"]);
        var second = DiscGroupingPreview.Create("source-B", ["Game (USA) (Disc 2)"]);

        first.SourceScope.ShouldNotBe(second.SourceScope);
        first.Groups.ShouldHaveSingleItem().IsNameCandidate.ShouldBeFalse();
        second.Groups.ShouldHaveSingleItem().MissingDiscNumbers.ShouldBe([1]);
    }

    [Fact]
    public void Create_ExplicitCount_OnlyClaimsAllDeclaredNamesWhenPresent()
    {
        var group = DiscGroupingPreview.Create("source", ["Game (Disc 1 of 2)", "Game (Disc 2)"])
            .Groups.ShouldHaveSingleItem();

        group.DeclaredDiscCount.ShouldBe(2);
        group.HasAllDeclaredDiscNames.ShouldBeTrue();
    }

    [Fact]
    public void Create_OneDiscEditionWithExplicitTotal_RemainsReviewable()
    {
        var group = DiscGroupingPreview.Create("source", ["Game (Disc 1 of 1)"]).Groups.ShouldHaveSingleItem();

        group.ReviewReasons.ShouldContain(DiscGroupReviewReason.SingleObservedDisc);
        group.HasAllDeclaredDiscNames.ShouldBeTrue();
    }

    [Fact]
    public void Create_MissingDiscs_ReportsHolesAndKnownUnseenTail()
    {
        var group = DiscGroupingPreview.Create("source", ["Game (Disc 2 of 5)", "Game (Disc 4)"])
            .Groups.ShouldHaveSingleItem();

        group.Members.Select(m => m.DiscNumber).ShouldBe([2, 4]);
        group.MissingDiscNumbers.ShouldBe([1, 3, 5]);
        group.ReviewReasons.ShouldContain(DiscGroupReviewReason.MissingDiscNumbers);
        group.HasAllDeclaredDiscNames.ShouldBeFalse();
    }

    [Theory]
    [InlineData("Game (Disc 1)", "Game (Disc 1)", DiscGroupReviewReason.DuplicateDiscNumber)]
    [InlineData("Game (Disc 1 of 2)", "Game (Disc 2 of 3)", DiscGroupReviewReason.ConflictingTotals)]
    [InlineData("Game (Disc 1 of 2)", "Game (Disc 3)", DiscGroupReviewReason.ConflictingTotals)]
    public void Create_ConflictingSlotsOrTotals_RequiresReview(string first, string second, DiscGroupReviewReason reason)
    {
        var group = DiscGroupingPreview.Create("source", [first, second]).Groups.ShouldHaveSingleItem();

        group.IsNameCandidate.ShouldBeFalse();
        group.ReviewReasons.ShouldContain(reason);
        group.HasAllDeclaredDiscNames.ShouldBeFalse();
    }

    [Fact]
    public void Create_UnsupportedMarker_PreservesReviewEntryWithoutGroup()
    {
        var result = DiscGroupingPreview.Create("source", ["Game (Disk 1)"]);

        result.Groups.ShouldBeEmpty();
        result.ReviewEntries.ShouldHaveSingleItem().Issue.ShouldBe(DiscNameIssue.UnsupportedDiscMarker);
    }

    [Fact]
    public void Create_ShuffledNamesAndNumericOrder_ProducesIdenticalSerializedPreview()
    {
        string[] names = ["Game (Disc 10)", "Game (Disc 2)", "game (Disc 1)", "Other (Disk 1)", "Other (Disk 2)"];

        var first = DiscGroupingPreview.Create("source", names);
        var reversed = DiscGroupingPreview.Create("source", names.Reverse());

        JsonSerializer.Serialize(first).ShouldBe(JsonSerializer.Serialize(reversed));
        first.Groups.ShouldHaveSingleItem().Members.Select(m => m.DiscNumber).ShouldBe([1, 2, 10]);
    }

    [Fact]
    public void Create_EntryLimit_FailsWithoutReturningPartialPreview()
    {
        Should.Throw<ArgumentException>(() => DiscGroupingPreview.Create("source",
            Enumerable.Repeat("Game", DiscGroupingPreview.MaxEntries + 1)));
    }

    [Fact]
    public void Create_EmptySource_ReturnsEmptyPreview()
    {
        var result = DiscGroupingPreview.Create("source", []);

        result.EntriesExamined.ShouldBe(0);
        result.Groups.ShouldBeEmpty();
        result.ReviewEntries.ShouldBeEmpty();
    }

    [Fact]
    public async Task Create_Ps1Fixture_GroupsCompleteSetsAndRetainsKnownAmbiguities()
    {
        await using var stream = TestResources.OpenRedumpPs1();
        var format = new LogiqxDatFormat(NullLogger<LogiqxDatFormat>.Instance);
        var names = new List<string>();
        await foreach (var game in format.ParseGamesAsync(stream, CancellationToken.None))
        {
            game.IsError.ShouldBeFalse();
            names.Add(game.Value.Name);
        }

        var result = DiscGroupingPreview.Create("ps1-fixture", names);

        result.EntriesExamined.ShouldBe(48);
        result.WithoutDiscMarker.ShouldBe(10);
        var review = result.ReviewEntries.ShouldHaveSingleItem();
        review.OriginalName.ShouldBe("Synthetic Demo Disc 02 (Europe) (En,Fr,De,Es,It)");
        review.Issue.ShouldBe(DiscNameIssue.UnsupportedDiscMarker);
        foreach (string region in new[] { "USA", "Europe", "Japan", "Germany" })
        {
            var group = result.Groups.Single(g => g.EditionName == $"Synthetic Saga VII ({region})");
            group.IsNameCandidate.ShouldBeTrue();
            group.Members.Select(m => m.DiscNumber).ShouldBe([1, 2, 3]);
            group.DeclaredDiscCount.ShouldBeNull();
        }
        result.Groups.Where(g => g.EditionName.StartsWith("Fixture Grand Prix 2")).Count().ShouldBe(2);
        result.Groups.Where(g => g.EditionName.StartsWith("Fixture Grand Prix 2")).ShouldAllBe(g => !g.IsNameCandidate);
        result.Groups.Single(g => g.EditionName == "Example Starfighter III - Comet Run (Europe)")
            .MissingDiscNumbers.ShouldBe([1, 3]);
        result.Groups.Single(g => g.EditionName == "Mock Infiltrator 2 (USA)").IsNameCandidate.ShouldBeTrue();
        result.Groups.Single(g => g.EditionName == "Mock Infiltrator 2 (Europe)").IsNameCandidate.ShouldBeFalse();
        result.Groups.Single(g => g.EditionName == "Mock Infiltrator 2 (Europe, Australia)").IsNameCandidate.ShouldBeFalse();
        result.Groups.Count(g => g.EditionName.StartsWith("Placeholder Stealth Ops (USA)")).ShouldBe(2);
    }
}
