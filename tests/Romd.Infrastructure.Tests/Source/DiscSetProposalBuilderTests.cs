using Romd.Domain.Catalog;
using Romd.Infrastructure.Source;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Source;

public sealed class DiscSetProposalBuilderTests
{
    [Fact]
    public void Create_BonusDiscSibling_PreservesSeparateReviewAndRequiresNumberedSetCorrection()
    {
        // Arrange
        DiscSetSourceClaim[] claims =
        [
            new(7, "disc-2", "Game (USA) (Disc 2)", "sha1:disc-2", 202, 77, false),
            new(7, "bonus", "Game (USA) (Bonus Disc)", "sha1:bonus", 203, 77, false),
            new(7, "disc-1", "Game (USA) (Disc 1)", "sha1:disc-1", 201, 77, false)
        ];

        // Act
        var proposals = DiscSetProposalBuilder.Create(claims);

        // Assert: a role is visible for correction, never an inferred third disc.
        proposals.Count.ShouldBe(2);
        var numbered = proposals.Single(x => x.Value.Name == "Game (USA)");
        numbered.Source.ShouldBe(7);
        numbered.Value.Members.Select(x => x.EntryKey).ShouldBe(["disc-1", "disc-2"]);
        numbered.Value.Members.Select(x => x.Ordinal).ShouldBe([1, 2]);
        numbered.Value.ExpectedDiscCount.ShouldBeNull();
        // Accept rejects proposals with review reasons, so the numeric pair cannot be
        // blindly accepted while its unusual sibling remains unresolved.
        numbered.Value.ReviewReasons.ShouldBe(["UnresolvedDiscRoleSibling"]);

        var role = proposals.Single(x => x.Value.Name == "Game (USA) (Bonus Disc)");
        role.Source.ShouldBe(7);
        role.Key.ShouldNotBe(numbered.Key);
        role.Value.Members.ShouldHaveSingleItem().EntryKey.ShouldBe("bonus");
        role.Value.Members[0].Name.ShouldBe("Game (USA) (Bonus Disc)");
        role.Value.ReviewReasons.ShouldBe(["UnsupportedDiscMarker"]);
        role.Value.ExpectedDiscCount.ShouldBeNull();
    }
}
