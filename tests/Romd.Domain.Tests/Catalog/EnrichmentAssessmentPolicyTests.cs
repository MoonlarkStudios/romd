using Romd.Domain.Catalog;
using Shouldly;
using Xunit;
namespace Romd.Domain.Tests.Catalog;

public sealed class EnrichmentAssessmentPolicyTests
{
    [Theory]
    [InlineData(true, true, true, true, EnrichmentRequirementState.Satisfied)]
    [InlineData(false, true, false, true, EnrichmentRequirementState.NeedsReview)]
    [InlineData(false, false, true, true, EnrichmentRequirementState.Processing)]
    [InlineData(false, false, true, false, EnrichmentRequirementState.Blocked)]
    [InlineData(false, false, false, false, EnrichmentRequirementState.Automatic)]
    public void Classify_CurrentFacts_SeparatesDecisionsFromExecution(bool satisfied, bool decision, bool blocked, bool processing, EnrichmentRequirementState expected)
    {
        var result = EnrichmentAssessmentPolicy.Classify.Compile()(new() { Satisfied = satisfied, RequiresDecision = decision, Blocked = blocked, Processing = processing });
        result.State.ShouldBe(expected);
    }

    [Theory]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, false, true)]
    [InlineData(false, false, true, false)]
    [InlineData(false, false, false, false)]
    public void Classify_CauseAndSatisfaction_PassOnlyForTheirState(bool satisfied, bool decision, bool blocked, bool processing)
    {
        var facts = new EnrichmentRequirementFacts
        {
            Satisfied = satisfied,
            RequiresDecision = decision,
            Blocked = blocked,
            Processing = processing,
            Cause = "Discover",
            SatisfiedBy = "Trusted"
        };

        var result = EnrichmentAssessmentPolicy.Classify.Compile()(facts);

        result.Cause.ShouldBe(satisfied ? null : "Discover");
        result.SatisfiedBy.ShouldBe(satisfied ? "Trusted" : null);
    }
}
