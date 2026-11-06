using Romd.Domain.Catalog;
using Shouldly;
using Xunit;

namespace Romd.Domain.Tests.Catalog;

public sealed class ProviderIdentityPolicyParityTests
{
    private static IdentityFacts Facts() => new("Game", [], [1994], ["SNES"], "DAT");

    [Fact]
    public void Decide_ExactNameWithCorroboration_PreservesScoreAndOrderedExplanation()
    {
        var decision = ProviderIdentityPolicy.Decide(Facts(), new IdentitySearchEvidence([new("1", "Game", [], [1994], ["SNES"])], true));

        decision.State.ShouldBe("AutoAccepted");
        decision.CandidateId.ShouldBe("1");
        decision.Explanation.ShouldBe("Edition-preserving name matches local evidence. Platform matches the local system. Release year matches independent catalog evidence.");
        var candidate = decision.Candidates.ShouldHaveSingleItem();
        candidate.Score.ShouldBe(140);
        candidate.Eligible.ShouldBeTrue();
        candidate.Conflicts.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(true, "NeedsReview", 130, "alias")]
    [InlineData(false, "AutoAccepted", 140, "exact")]
    public void Decide_NearbyContender_PreservesScoreMargin(bool useAlias, string state, int score, string id)
    {
        var best = useAlias ? new IdentityCandidate("alias", "Regional name", ["Game"], [1994], ["SNES"])
            : new IdentityCandidate("exact", "Game", [], [1994], ["SNES"]);

        var decision = ProviderIdentityPolicy.Decide(Facts(), new IdentitySearchEvidence([new("other", "Game", [], [], ["SNES"]), best], true));

        decision.State.ShouldBe(state);
        decision.CandidateId.ShouldBe(id);
        decision.Candidates.Select(candidate => candidate.Score).ShouldBe([score, 120]);
        if (useAlias)
            decision.Explanation.ShouldBe("Multiple plausible candidates; review the edition.");
    }

    [Fact]
    public void Decide_Contradictions_PreservesConflictOrderAndScore()
    {
        var decision = ProviderIdentityPolicy.Decide(Facts(), new IdentitySearchEvidence([new("1", "Game Deluxe", [], [2004], ["PS5"])], true));

        decision.State.ShouldBe("NeedsReview");
        decision.CandidateId.ShouldBe("1");
        decision.Explanation.ShouldBe("Edition or modification markers differ from the local title. No identity-preserving name or alias match. Provider platforms contradict the local system. Release years conflict with local catalog evidence.");
        decision.Candidates.ShouldHaveSingleItem().Score.ShouldBe(0);
    }

    [Fact]
    public void Decide_DuplicateIdsAndTiedScores_PreservesFirstEvidenceAndOrdinalOrdering()
    {
        var decision = ProviderIdentityPolicy.Decide(Facts(),
        new IdentitySearchEvidence([
            new("b", "Game", [], [1994], ["SNES"]),
            new("a", "Game", [], [1994], ["SNES"]),
            new("a", "Different game", [], [], [])
        ], true));

        decision.State.ShouldBe("NeedsReview");
        decision.CandidateId.ShouldBe("a");
        decision.Candidates.Select(candidate => candidate.CandidateId).ShouldBe(["a", "b"]);
        decision.Candidates.All(candidate => candidate.Score == 140 && candidate.Eligible).ShouldBeTrue();
    }
}
