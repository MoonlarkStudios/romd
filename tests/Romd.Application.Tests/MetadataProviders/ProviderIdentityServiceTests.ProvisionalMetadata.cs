using Romd.Admin.Application.MetadataProviders;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.MetadataProviders;

public sealed partial class ProviderIdentityServiceTests
{
    [Theory]
    [InlineData("matching hash", "NeedsReview", "42", false)]
    [InlineData("independent year", "AutoAccepted", null, true)]
    [InlineData("provider limited", "NeedsReview", "42", false)]
    [InlineData("budget limited", "NeedsReview", null, false)]
    [InlineData("alternate candidate", "NeedsReview", null, false)]
    [InlineData("unresolved DAT", "NeedsReview", null, false)]
    public async Task DiscoverAsync_IgdbRequiresCurrentCorroborationForProvisionalMetadata(string scenario,
        string expectedState, string? provisionalGameId, bool autoAccepted)
    {
        var game = new ProviderGame("42", "Game", "https://provider.test/game", Platforms: ["SNES"],
            Releases: [new(1994, "SNES")]);
        ProviderGame[] results = scenario == "alternate candidate" ? [game, game with { Id = "43" }] : [game];
        var completeness = scenario switch
        {
            "provider limited" => ProviderGameSearchCompleteness.ProviderLimited,
            "budget limited" => ProviderGameSearchCompleteness.BudgetLimited,
            _ => ProviderGameSearchCompleteness.Complete
        };
        var service = Create("Game", [], _ => results, completeness: completeness,
            datFamily: new(["Game"], scenario == "unresolved DAT"), providerId: "igdb",
            years: scenario == "independent year" ? [1994] : []);

        await service.DiscoverAsync(1, null, default);

        var decision = _decision.ShouldNotBeNull();
        decision.State.ShouldBe(expectedState);
        decision.ProvisionalGameId.ShouldBe(provisionalGameId);
        _accepted.ShouldBe(autoAccepted ? game : null);
    }
}
