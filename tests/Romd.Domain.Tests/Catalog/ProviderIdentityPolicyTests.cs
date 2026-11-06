using Romd.Domain.Catalog;
using Shouldly;
using Xunit;
namespace Romd.Domain.Tests.Catalog;

public sealed class ProviderIdentityPolicyTests
{
    private static IdentityFacts Facts(string name = "Game", int[]? years = null) => new(name, [], years ?? [], ["SNES"], "DAT");
    private static IdentityCandidate Candidate(string id = "1", string name = "Game", string[]? platforms = null, int[]? years = null, string[]? aliases = null) => new(id, name, aliases ?? [], years ?? [], platforms ?? ["SNES"]);
    [Fact] public void Decide_ExactNameAndLocalPlatform_Accepts() => ProviderIdentityPolicy.Decide(Facts(), new IdentitySearchEvidence([Candidate()], true)).State.ShouldBe("AutoAccepted");
    [Fact] public void Decide_NameAlone_RequiresReview() => ProviderIdentityPolicy.Decide(Facts(), new IdentitySearchEvidence([Candidate(platforms: [])], true)).State.ShouldBe("NeedsReview");
    [Fact] public void Decide_EquallyPlausibleEditions_RequiresReview() => ProviderIdentityPolicy.Decide(Facts(), new IdentitySearchEvidence([Candidate(), Candidate("2")], true)).State.ShouldBe("NeedsReview");
    [Fact] public void Decide_ConflictingYear_VetoesExactName() => ProviderIdentityPolicy.Decide(Facts(years: [1994]), new IdentitySearchEvidence([Candidate(years: [2004])], true)).State.ShouldBe("NeedsReview");
    [Fact] public void Decide_ConflictingPlatform_VetoesExactName() => ProviderIdentityPolicy.Decide(Facts(), new IdentitySearchEvidence([Candidate(platforms: ["PS5"])], true)).State.ShouldBe("NeedsReview");
    [Theory]
    [InlineData("Game [h1]", "Game")]
    [InlineData("Game+", "Game")]
    [InlineData("Game (Hack)", "Game")]
    [InlineData("Game Remastered", "Game")]
    [InlineData("Game", "Game Deluxe")]
    public void Decide_EditionMarkers_NeverCollapse(string local, string remote) => ProviderIdentityPolicy.Decide(Facts(local), new IdentitySearchEvidence([Candidate(name: remote)], true)).State.ShouldBe("NeedsReview");
    [Fact]
    public void Decide_AliasNeedsIndependentYearAndPlatform()
    {
        var candidate = Candidate(name: "Regional name", years: [1994], aliases: ["Game"]);
        ProviderIdentityPolicy.Decide(Facts(), new IdentitySearchEvidence([candidate], true)).State.ShouldBe("NeedsReview");
        ProviderIdentityPolicy.Decide(Facts(years: [1994]), new IdentitySearchEvidence([candidate], true)).State.ShouldBe("AutoAccepted");
    }
    [Fact] public void Decide_NoCandidates_ReturnsNoMatch() => ProviderIdentityPolicy.Decide(Facts(), new IdentitySearchEvidence([], true)).State.ShouldBe("NoMatch");

    [Fact]
    public void Decide_HigherRankedContradiction_DoesNotBlockValidCandidate()
    {
        var decision = ProviderIdentityPolicy.Decide(Facts(years: [1994]),
            new IdentitySearchEvidence([Candidate("a", years: [2004]), Candidate("b")], true));

        decision.State.ShouldBe("AutoAccepted");
        decision.CandidateId.ShouldBe("b");
        decision.Candidates.Single(x => x.CandidateId == "a").Conflicts.ShouldNotBeEmpty();
    }

    [Fact]
    public void Decide_ContradictionAlongsidePlausibleAlternatives_StillRequiresReview()
    {
        var decision = ProviderIdentityPolicy.Decide(Facts(years: [1994]),
            new IdentitySearchEvidence([Candidate("a", years: [2004]), Candidate("b"), Candidate("c")], true));

        decision.State.ShouldBe("NeedsReview");
        decision.CandidateId.ShouldBe("b");
    }

    [Theory]
    [InlineData("Game - Subtitle", "Game: Subtitle")]
    [InlineData("Game — Subtitle", "Game: Subtitle")]
    [InlineData("Game, The", "The Game")]
    [InlineData("Game & Friends", "Game and Friends")]
    public void Decide_ConservativeSpellingEquivalence_StillRequiresCorroboration(string local, string remote)
    {
        ProviderIdentityPolicy.Decide(Facts(local), new IdentitySearchEvidence([Candidate(name: remote)], true)).State.ShouldBe("AutoAccepted");
        ProviderIdentityPolicy.Decide(Facts(local), new IdentitySearchEvidence([Candidate(name: remote, platforms: [])], true)).State.ShouldBe("NeedsReview");
    }

    [Theory]
    [InlineData("The Game", "Game")]
    [InlineData("Game 2", "Game")]
    [InlineData("Game II", "Game 2")]
    [InlineData("Game+", "Game")]
    [InlineData("Game (Special Edition)", "Game")]
    [InlineData("Game Remastered", "Game")]
    [InlineData("Game-Boy", "Game Boy")]
    public void Decide_DistinctTitleMeaning_RemainsDistinct(string local, string remote) =>
        ProviderIdentityPolicy.Decide(Facts(local), new IdentitySearchEvidence([Candidate(name: remote)], true)).State.ShouldBe("NeedsReview");
    [Fact]
    public void Decide_ArtworkUniqueExactNameWithoutDateOrPlatform_Accepts()
    {
        var decision = ProviderIdentityPolicy.Decide(Facts("Flushed Away"),
            new IdentitySearchEvidence([Candidate("36590", "Flushed Away", []), Candidate("2", "Flushed", [], [2025]), Candidate("3", "Flushed Forward", [], [2027])], true),
            ProviderIdentityScope.CrossPlatformArtwork);
        decision.State.ShouldBe("AutoAccepted");
        decision.CandidateId.ShouldBe("36590");
    }

    [Fact]
    public void Decide_ArtworkExactNamesWithoutTieBreaker_RequiresReview() =>
        ProviderIdentityPolicy.Decide(Facts(), new IdentitySearchEvidence([Candidate("1", platforms: []), Candidate("2", platforms: [])], true),
            ProviderIdentityScope.CrossPlatformArtwork).State.ShouldBe("NeedsReview");

    [Fact]
    public void Decide_ArtworkDateBreaksExactNameTie()
    {
        var decision = ProviderIdentityPolicy.Decide(Facts(years: [1994]),
            new IdentitySearchEvidence([Candidate("1", platforms: []), Candidate("2", platforms: [], years: [1994])], true),
            ProviderIdentityScope.CrossPlatformArtwork);
        decision.State.ShouldBe("AutoAccepted");
        decision.CandidateId.ShouldBe("2");
    }

    [Fact]
    public void Decide_ArtworkDifferentPortDate_DoesNotVetoUniqueName() =>
        ProviderIdentityPolicy.Decide(Facts(years: [1994]), new IdentitySearchEvidence([Candidate(platforms: [], years: [1995])], true),
            ProviderIdentityScope.CrossPlatformArtwork).State.ShouldBe("AutoAccepted");

    [Theory]
    [InlineData("Game Remastered")]
    [InlineData("Game+")]
    [InlineData("Game 2")]
    [InlineData("Game (Hack)")]
    public void Decide_ArtworkDifferentEditionOrSequel_RequiresReview(string name) =>
        ProviderIdentityPolicy.Decide(Facts(), new IdentitySearchEvidence([Candidate(name: name, platforms: [])], true),
            ProviderIdentityScope.CrossPlatformArtwork).State.ShouldBe("NeedsReview");

    [Fact]
    public void Decide_ArtworkAliasOnlyWithoutCorroboration_RequiresReview() =>
        ProviderIdentityPolicy.Decide(Facts(), new IdentitySearchEvidence([Candidate(name: "Regional name", platforms: [], aliases: ["Game"])], true),
            ProviderIdentityScope.CrossPlatformArtwork).State.ShouldBe("NeedsReview");

    [Fact]
    public void Decide_ArtworkConflictingPlatform_RequiresReview() =>
        ProviderIdentityPolicy.Decide(Facts(), new IdentitySearchEvidence([Candidate(platforms: ["PS5"])], true),
            ProviderIdentityScope.CrossPlatformArtwork).State.ShouldBe("NeedsReview");
}
