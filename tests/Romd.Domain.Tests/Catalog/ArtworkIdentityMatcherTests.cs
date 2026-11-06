using Romd.Domain.Catalog;
using Shouldly;
using Xunit;

namespace Romd.Domain.Tests.Catalog;

public sealed class ArtworkIdentityMatcherTests
{
    [Theory]
    [InlineData("Game (USA) [!]", "Game", 1995, 1995, true)]
    [InlineData("Game", "Game", null, 1995, false)]
    [InlineData("Game", "Game", 1995, null, false)]
    [InlineData("Game", "Game", 1995, 2020, false)]
    [InlineData("Game 2", "Game", 1995, 1995, false)]
    [InlineData("Game (2017)", "Game", 2017, 2017, false)]
    [InlineData("Game Remastered", "Game", 1995, 1995, false)]
    [InlineData("Game (Special Edition)", "Game", 1995, 1995, false)]
    [InlineData("Pokémon", "Pokemon", 1995, 1995, false)]
    public void Match_RequiresIdentityAndKnownCorroboration(string name, string candidate, int? year, int? candidateYear, bool trusted)
    {
        ArtworkIdentityMatcher.Match(name, year, [new("1", candidate, candidateYear)]).Trusted.ShouldBe(trusted);
    }

    [Fact]
    public void Match_SameNameAlternatives_RequiresReviewEvenWhenOneYearMatches()
    {
        var decision = ArtworkIdentityMatcher.Match("Game", 1995,
            [new("1", "Game", 1995), new("2", "Game", 2020)]);
        decision.Trusted.ShouldBeFalse();
        decision.CandidateId.ShouldBeNull();
    }

    [Fact]
    public void Normalize_ComposedUnicode_PreservesEquivalentIdentity() =>
        ArtworkIdentityMatcher.Normalize("Poke\u0301mon (Japan)")
            .ShouldBe(ArtworkIdentityMatcher.Normalize("Pokémon"));

    [Fact]
    public void EmptyRole_HidesCoverFallbackUntilReturnedToAutomatic()
    {
        var selection = ArtworkSelection.CreateAutomatic(1, ArtworkRole.Poster);
        selection.LeaveEmpty();
        selection.Mode.ShouldBe(ArtworkSelectionMode.Empty);
        ArtworkResolver.Resolve(selection, [], []).FallbackReason.ShouldBe(ArtworkFallbackReason.NoArtwork);
        selection.ReturnToAutomatic();
        selection.Mode.ShouldBe(ArtworkSelectionMode.Automatic);
        selection.Revision.ShouldBe(2);
    }

    [Fact]
    public void ProviderOrder_PreservesConfiguredOrderAndAddsFallbacks() =>
        ArtworkAcquisitionPolicy.ProviderOrder(ArtworkRole.Poster, ["igdb"])
            .ShouldBe(["igdb", "steamgriddb"]);
}
