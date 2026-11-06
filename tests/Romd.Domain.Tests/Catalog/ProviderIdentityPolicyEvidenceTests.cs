using Romd.Domain.Catalog;
using Shouldly;
using Xunit;

namespace Romd.Domain.Tests.Catalog;

public sealed class ProviderIdentityPolicyEvidenceTests
{
    private static IdentityFacts Facts() => new("Game", [], [1994], ["SNES"], "DAT");

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Decide_IncompleteSearch_VetoesApparentUniquenessOrNoMatch(bool hasCandidate, bool artworkScope)
    {
        IdentityCandidate[] candidates = hasCandidate ? [new("1", "Game", [], [1994], ["SNES"])] : [];

        var decision = ProviderIdentityPolicy.Decide(Facts(), new IdentitySearchEvidence(candidates, false),
            artworkScope ? ProviderIdentityScope.CrossPlatformArtwork : ProviderIdentityScope.Release);

        decision.ShouldBeOfType<IdentityDecision.NeedsReview>();
        decision.State.ShouldBe("NeedsReview");
        decision.CandidateId.ShouldBeNull();
        decision.Explanation.ShouldBe("Provider search evidence is incomplete. Refine the query or confirm an explicit provider game identity.");
        decision.Candidates.Count.ShouldBe(candidates.Length);
    }

    [Theory]
    [InlineData("Game", 100)]
    [InlineData("Regional name", 90)]
    public void Decide_TrustedBridgeWithoutCorroboration_AcceptsWithBridgeEvidence(string name, int score)
    {
        var candidate = new IdentityCandidate("1", name, ["Game"], [], []);
        var evidence = new IdentitySearchEvidence([candidate], true, new("1", "Trusted Steam bridge."));

        var decision = ProviderIdentityPolicy.Decide(Facts(), evidence);

        var accepted = decision.ShouldBeOfType<IdentityDecision.AutoAccepted>();
        accepted.CandidateId.ShouldBe("1");
        accepted.State.ShouldBe("AutoAccepted");
        accepted.Explanation.ShouldBe("Trusted Steam bridge.");
        var assessed = accepted.Candidates.ShouldHaveSingleItem();
        assessed.Score.ShouldBe(score);
        assessed.Eligible.ShouldBeFalse();
        assessed.Conflicts.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("Game Deluxe", 1994, "SNES")]
    [InlineData("Game", 2004, "SNES")]
    [InlineData("Game", 1994, "PS5")]
    public void Decide_TrustedBridgeContradictsLocalFacts_RequiresReview(string name, int year, string platform)
    {
        var candidate = new IdentityCandidate("1", name, [], [year], [platform]);

        var decision = ProviderIdentityPolicy.Decide(Facts(), new IdentitySearchEvidence([candidate], true,
            new("1", "Trusted Steam bridge.")));

        decision.ShouldBeOfType<IdentityDecision.NeedsReview>();
        decision.CandidateId.ShouldBe("1");
        var assessed = decision.Candidates.ShouldHaveSingleItem();
        assessed.Conflicts.ShouldNotBeEmpty();
        decision.Explanation.ShouldBe(string.Join(" ", assessed.Conflicts));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Decide_IncompleteSearchWithBridge_PreservesCompletenessVeto(bool contradiction)
    {
        var candidate = new IdentityCandidate("1", "Game", [], [contradiction ? 2004 : 1994], ["SNES"]);

        var decision = ProviderIdentityPolicy.Decide(Facts(), new IdentitySearchEvidence([candidate], false,
            new("1", "Trusted Steam bridge.")));

        decision.ShouldBeOfType<IdentityDecision.NeedsReview>();
        decision.CandidateId.ShouldBeNull();
        decision.Explanation.ShouldStartWith("Provider search evidence is incomplete.");
        decision.Candidates.ShouldHaveSingleItem().Conflicts.Any().ShouldBe(contradiction);
    }

    [Fact]
    public void Decide_AnotherCandidateContradictsFacts_DoesNotPromoteBridge()
    {
        var evidence = new IdentitySearchEvidence(
            [new("bridge", "Game", [], [], []), new("other", "Different game", [], [], [])],
            true, new("bridge", "Trusted Steam bridge."));

        var decision = ProviderIdentityPolicy.Decide(Facts(), evidence);

        decision.ShouldBeOfType<IdentityDecision.NeedsReview>();
        decision.CandidateId.ShouldBe("bridge");
        decision.Explanation.ShouldBe("Independent corroborating evidence is insufficient.");
    }

    [Fact]
    public void Decide_CompleteSearchWithoutCandidates_ReturnsNoMatchVariant()
    {
        var decision = ProviderIdentityPolicy.Decide(Facts(), new IdentitySearchEvidence([], true));

        decision.ShouldBeOfType<IdentityDecision.NoMatch>();
        decision.State.ShouldBe("NoMatch");
        decision.CandidateId.ShouldBeNull();
        decision.Candidates.ShouldBeEmpty();
    }

    [Fact]
    public void IdentitySearchEvidence_BridgeRefersToUnobservedCandidate_RejectsInvalidFacts()
    {
        Should.Throw<ArgumentException>(() => new IdentitySearchEvidence([], true, new("missing", "bridge")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IdentityCandidate_MissingIdentity_RejectsFactsThatCannotIdentifyAnAcceptedGame(string? id)
    {
        Should.Throw<ArgumentException>(() => new IdentityCandidate(id!, "Game", [], [], []));
    }

    [Fact]
    public void Decide_CallerMutatesCapturedFactsAndCandidates_KeepsOriginalEvidence()
    {
        string[] names = ["Regional name"];
        int[] localYears = [1994];
        string[] localPlatforms = ["SNES"];
        string[] aliases = ["Game"];
        int[] providerYears = [1994];
        string[] providerPlatforms = ["SNES"];
        var facts = new IdentityFacts("Game", names, localYears, localPlatforms, "DAT");
        var candidate = new IdentityCandidate("1", "Other regional name", aliases, providerYears, providerPlatforms);
        IdentityCandidate[] candidates = [candidate];
        var evidence = new IdentitySearchEvidence(candidates, true);

        names[0] = "Different name";
        localYears[0] = 2004;
        localPlatforms[0] = "PS5";
        aliases[0] = "Different alias";
        providerYears[0] = 2005;
        providerPlatforms[0] = "Xbox";
        candidates[0] = new("2", "Different game", [], [], []);
        var decision = ProviderIdentityPolicy.Decide(facts, evidence);

        decision.ShouldBeOfType<IdentityDecision.AutoAccepted>().CandidateId.ShouldBe("1");
        decision.Candidates.ShouldHaveSingleItem().Score.ShouldBe(130);
        facts.Names.ShouldBe(["Regional name"]);
        facts.Years.ShouldBe([1994]);
        facts.Platforms.ShouldBe(["SNES"]);
        candidate.Aliases.ShouldBe(["Game"]);
        candidate.Years.ShouldBe([1994]);
        candidate.Platforms.ShouldBe(["SNES"]);
    }

    [Fact]
    public void Collections_ThroughMutableInterfaces_RejectChangesToFactsAndDecisions()
    {
        var facts = Facts();
        var candidate = new IdentityCandidate("1", "Game", ["Regional name"], [1994], ["SNES"]);
        var evidence = new IdentitySearchEvidence([candidate], true);
        var decision = ProviderIdentityPolicy.Decide(facts, evidence);
        var assessed = decision.Candidates.ShouldHaveSingleItem();

        Should.Throw<NotSupportedException>(() => ((ICollection<string>)facts.Names).Add("Other game"));
        Should.Throw<NotSupportedException>(() => ((IList<int>)facts.Years)[0] = 2004);
        Should.Throw<NotSupportedException>(() => ((IList<string>)facts.Platforms)[0] = "PS5");
        Should.Throw<NotSupportedException>(() => ((IList<string>)candidate.Aliases)[0] = "Other game");
        Should.Throw<NotSupportedException>(() => ((IList<int>)candidate.Years)[0] = 2004);
        Should.Throw<NotSupportedException>(() => ((IList<string>)candidate.Platforms)[0] = "PS5");
        Should.Throw<NotSupportedException>(() => ((ICollection<IdentityCandidate>)evidence.Candidates).Clear());
        Should.Throw<NotSupportedException>(() => ((ICollection<IdentityAssessment>)decision.Candidates).Clear());
        Should.Throw<NotSupportedException>(() => ((IList<string>)assessed.Evidence)[0] = "Changed evidence");
        Should.Throw<NotSupportedException>(() => ((ICollection<string>)assessed.Conflicts).Add("Invented contradiction"));

        ProviderIdentityPolicy.Decide(facts, evidence).State.ShouldBe("AutoAccepted");
        assessed.Score.ShouldBe(140);
        assessed.Conflicts.ShouldBeEmpty();
    }
}
