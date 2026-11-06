using Romd.Domain.Catalog;
using Shouldly;
using Xunit;

namespace Romd.Domain.Tests.Catalog;

public sealed class DiscSetDecisionPolicyTests
{
    [Theory]
    [InlineData("accept", "normal", "accepted")]
    [InlineData("correct", "normal", "accepted")]
    [InlineData("reject", "normal", "rejected")]
    [InlineData("reset", "normal", "proposed")]
    [InlineData("correct", "optional-total", "accepted")]
    public void Decide_ValidTransition_PreservesDecisionAndCorrectionRules(string decision, string variation, string expected)
    {
        var proposal = Definition();
        var facts = new DiscSetDecisionFacts(1, "accepted", proposal, proposal);
        var result = DiscSetDecisionPolicy.Decide(Input(decision, variation), facts, Claims());

        result.Problem.ShouldBe(DiscSetDecisionProblem.None);
        var change = result.Change.ShouldNotBeNull();
        change.Decision.ShouldBe(expected);
        change.TitleIds.ShouldBe([10]);
        change.ReplaceCorrection.ShouldBe(decision != "reject");
        if (decision == "reset")
            change.Correction.ShouldBeNull();
        else
        {
            var correction = change.Correction.ShouldNotBeNull();
            correction.Members.Select(member => member.EntryKey).ShouldBe(["a", "b"]);
            correction.Name.ShouldBe(decision == "correct" ? "Manual" : "Game");
            correction.RuleVersion.ShouldBe(decision == "correct" ? "manual-v1" : "proposal-v1");
            correction.ExpectedDiscCount.ShouldBe(variation == "optional-total" ? null : 2);
        }
    }

    [Theory]
    [InlineData("unknown", "normal", DiscSetDecisionProblem.Decision)]
    [InlineData("reject", "long-reason", DiscSetDecisionProblem.Decision)]
    [InlineData("accept", "review", DiscSetDecisionProblem.Review)]
    [InlineData("accept", "missing", DiscSetDecisionProblem.Changed)]
    [InlineData("accept", "conflict", DiscSetDecisionProblem.Changed)]
    [InlineData("accept", "fingerprint", DiscSetDecisionProblem.Changed)]
    [InlineData("correct", "blank-name", DiscSetDecisionProblem.Members)]
    [InlineData("correct", "long-name", DiscSetDecisionProblem.Members)]
    [InlineData("correct", "null-members", DiscSetDecisionProblem.Members)]
    [InlineData("correct", "empty-members", DiscSetDecisionProblem.Members)]
    [InlineData("correct", "null-member", DiscSetDecisionProblem.Members)]
    [InlineData("correct", "empty-key", DiscSetDecisionProblem.Members)]
    [InlineData("correct", "long-key", DiscSetDecisionProblem.Members)]
    [InlineData("correct", "zero-ordinal", DiscSetDecisionProblem.Members)]
    [InlineData("correct", "large-ordinal", DiscSetDecisionProblem.Members)]
    [InlineData("correct", "same-ordinal", DiscSetDecisionProblem.Members)]
    [InlineData("correct", "same-key", DiscSetDecisionProblem.Members)]
    [InlineData("correct", "large-total", DiscSetDecisionProblem.Members)]
    [InlineData("correct", "missing", DiscSetDecisionProblem.Source)]
    [InlineData("correct", "conflict", DiscSetDecisionProblem.Source)]
    [InlineData("correct", "titles", DiscSetDecisionProblem.Scope)]
    [InlineData("correct", "same-fingerprint", DiscSetDecisionProblem.Scope)]
    [InlineData("correct", "small-total", DiscSetDecisionProblem.Scope)]
    public void Decide_RejectedFacts_ReturnsTheExistingProblemCategory(string decision, string variation, DiscSetDecisionProblem expected)
    {
        var proposal = Definition(variation == "review" ? ["NeedsReview"] : []);
        var result = DiscSetDecisionPolicy.Decide(Input(decision, variation), new(1, "proposed", proposal, null), Claims(variation));
        result.Problem.ShouldBe(expected);
        result.Change.ShouldBeNull();
    }

    [Theory]
    [InlineData("accepted", true, "normal", true, true)]
    [InlineData("proposed", true, "normal", false, false)]
    [InlineData("accepted", false, "normal", false, false)]
    [InlineData("accepted", true, "missing", false, false)]
    [InlineData("accepted", true, "conflict", false, false)]
    [InlineData("accepted", true, "outside", true, false)]
    public void Eligibility_SeparatesResolutionFromLibraryScope(string decision, bool corrected, string variation, bool valid, bool eligible)
    {
        var facts = new DiscSetDecisionFacts(1, decision, Definition(), corrected ? Definition() : null);
        var result = DiscSetDecisionPolicy.Eligibility(facts, Claims(variation), variation == "outside" ? new HashSet<int> { 101 } : new HashSet<int> { 101, 102 });
        result.ShouldBe(new DiscSetEligibility(valid, eligible));
    }

    [Fact]
    public void OwnedFactsAndResult_DoNotRetainMutableInputCollections()
    {
        var members = Definition().Members.ToList();
        var reasons = new List<string>();
        var edits = new List<DiscSetMemberChange?> { new("a", 1), new("b", 2) };
        var source = new List<DiscSetSourceClaim> { new(1, "a", "A", "fp:a", 101, 10, false), new(1, "b", "B", "fp:b", 102, 10, false) };
        var facts = new DiscSetDecisionFacts(1, "proposed", new("Game", 2, members, reasons, "proposal-v1"), null);
        var input = new DiscSetDecisionInput("correct", "Manual", 2, edits, null);
        var claims = new DiscSetClaims(source);
        members.Clear();
        reasons.Add("Changed");
        edits.Clear();
        source.Clear();

        var result = DiscSetDecisionPolicy.Decide(input, facts, claims);

        result.Problem.ShouldBe(DiscSetDecisionProblem.None);
        facts.Proposal.Members.Count.ShouldBe(2);
        facts.Proposal.ReviewReasons.ShouldBeEmpty();
        Should.Throw<NotSupportedException>(() => ((IList<DiscSetMember>)result.Change!.Correction!.Members).Clear());
        Should.Throw<NotSupportedException>(() => ((IList<int>)result.Change!.TitleIds).Clear());
        Should.Throw<NotSupportedException>(() => ((IList<string>)facts.Proposal.ReviewReasons).Add("Changed"));
    }

    [Fact]
    public void Claims_AmbiguityIsRetainedForTheRequestedMemberAndConflictIsNotOverwritten()
    {
        var claims = new DiscSetClaims([
            new(1, "a", "A", "first", 101, 10, false), new(1, "a", "A", "second", 102, 10, true),
            new(2, "a", "A", "other source", 201, 20, true)]);
        claims.Find(2, "a")!.Conflict.ShouldBeTrue();
        claims.Find(1, "missing").ShouldBeNull();
        Should.Throw<InvalidOperationException>(() => claims.Find(1, "a"));
        Should.Throw<InvalidOperationException>(() => DiscSetDecisionPolicy.Eligibility(new(1, "proposed", Definition(), null), claims, new HashSet<int>()));
    }

    private static DiscSetDefinition Definition(IReadOnlyList<string>? reasons = null) => new("Game", 2,
        [new("a", "A", "fp:a", 1, 10), new("b", "B", "fp:b", 2, 10)], reasons ?? [], "proposal-v1");

    private static DiscSetClaims Claims(string variation = "normal") => new(variation == "missing"
        ? [new(1, "a", "A", "fp:a", 101, 10, false)]
        : [new(1, "a", "A", "fp:a", 101, 10, false),
            new(1, "b", "B", variation == "fingerprint" ? "changed" : variation == "same-fingerprint" ? "fp:a" : "fp:b", 102,
                variation == "titles" ? 20 : 10, variation == "conflict")]);

    private static DiscSetDecisionInput Input(string decision, string variation)
    {
        IReadOnlyList<DiscSetMemberChange?>? members = variation switch
        {
            "null-members" => null,
            "empty-members" => [],
            "null-member" => [null],
            "empty-key" => [new("", 1)],
            "long-key" => [new(new string('x', 501), 1)],
            "zero-ordinal" => [new("a", 0)],
            "large-ordinal" => [new("a", 100)],
            "same-ordinal" => [new("a", 1), new("b", 1)],
            "same-key" => [new("a", 1), new("a", 2)],
            _ => [new("b", 2), new("a", 1)]
        };
        return new(decision, variation == "blank-name" ? " " : variation == "long-name" ? new string('x', 501) : " Manual ",
            variation == "optional-total" ? null : variation == "large-total" ? 100 : variation == "small-total" ? 1 : 2,
            members, variation == "long-reason" ? new string('x', 1001) : "Reviewed");
    }
}
