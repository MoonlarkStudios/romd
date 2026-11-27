using Romd.Admin.Application.MetadataProviders;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.MetadataProviders;

public sealed partial class ProviderIdentityServiceTests
{
    [Theory]
    [InlineData("matching retail release", "AutoAccepted", null, true)]
    [InlineData("artwork only", "NeedsReview", "42", false)]
    [InlineData("no retail evidence", "NeedsReview", "42", false)]
    [InlineData("different matched release title", "NeedsReview", "42", false)]
    [InlineData("independent year", "AutoAccepted", null, true)]
    [InlineData("provider limited", "NeedsReview", "42", false)]
    [InlineData("budget limited", "NeedsReview", null, false)]
    [InlineData("alternate candidate", "NeedsReview", null, false)]
    [InlineData("plausible undated alternative", "NeedsReview", "42", false)]
    [InlineData("unresolved DAT", "NeedsReview", null, false)]
    public async Task DiscoverAsync_IgdbRetailTrustRequiresUnambiguousCorroboration(string scenario,
        string expectedState, string? provisionalGameId, bool autoAccepted)
    {
        var game = new ProviderGame("42", "Game", "https://provider.test/game", Platforms: ["SNES"],
            Releases: [new(1994, "SNES")]);
        ProviderGame[] results = scenario switch
        {
            "alternate candidate" => [game, game with { Id = "43" }],
            "plausible undated alternative" => [game, game with { Id = "43", Platforms = [], Releases = [] }],
            _ => [game]
        };
        var completeness = scenario switch
        {
            "provider limited" => ProviderGameSearchCompleteness.ProviderLimited,
            "budget limited" => ProviderGameSearchCompleteness.BudgetLimited,
            _ => ProviderGameSearchCompleteness.Complete
        };
        var retailTitles = scenario switch
        {
            "no retail evidence" => Array.Empty<string>(),
            "different matched release title" => ["Game", "Game II"],
            _ => ["Game"]
        };
        var service = Create("Game", [], _ => results, completeness: completeness, providerId: "igdb",
            datFamily: new(["Game"], scenario == "unresolved DAT") { MatchedRetailTitles = retailTitles },
            years: scenario == "independent year" ? [1994] : []);

        if (scenario == "artwork only")
            await service.DiscoverArtworkAsync(1, null, default);
        else
            await service.DiscoverAsync(1, null, default);

        var decision = _decision.ShouldNotBeNull();
        decision.State.ShouldBe(expectedState);
        decision.ProvisionalGameId.ShouldBe(provisionalGameId);
        _accepted.ShouldBe(autoAccepted ? game : null);
    }
}
