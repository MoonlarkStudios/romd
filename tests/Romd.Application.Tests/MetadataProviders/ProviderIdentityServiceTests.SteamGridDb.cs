using ErrorOr;
using NSubstitute;
using Romd.Admin.Application.Artwork.Providers;
using Romd.Admin.Application.MetadataProviders;
using Romd.Domain.Catalog;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.MetadataProviders;

public sealed partial class ProviderIdentityServiceTests
{
    [Theory]
    [InlineData(ProviderGameSearchCompleteness.Complete)]
    [InlineData(ProviderGameSearchCompleteness.ProviderLimited)]
    public async Task DiscoverAsync_SonicWithTrustedIgdbGenesisRelease_Accepts1991WithoutBridge(
        ProviderGameSearchCompleteness completeness)
    {
        var igdb = SonicIgdb();
        // Use IDs whose default lexical order favors the 2006 game.
        var reboot = SonicGame("1", 2006);
        var original = SonicGame("35571", 1991);
        var bridge = Substitute.For<IArtworkIdentityBridge>();
        bridge.SourceProviderId.Returns("igdb");
        bridge.TargetProviderId.Returns("steamgriddb");
        bridge.ResolveAsync("3192", Arg.Any<CancellationToken>()).Returns((ErrorOr<ArtworkBridgeMatch?>)ArtworkProviderErrors.NotFound());
        var service = CreateSonicSteamGridDb([reboot, original], [igdb], [bridge], completeness, new(["Sonic the Hedgehog"], false));

        await service.DiscoverAsync(1, null, default);

        var decision = _decision.ShouldNotBeNull();
        decision.State.ShouldBe("AutoAccepted");
        decision.ProvisionalGameId.ShouldBeNull();
        decision.Explanation.ShouldContain("1991");
        decision.Explanation.ShouldContain("trusted IGDB release on the local platform");
        decision.Candidates.Select(candidate => candidate.Game.Id).ShouldBe(["35571", "1"]);
        decision.Candidates[0].Score.ShouldBe(decision.Candidates[1].Score);
        decision.Candidates[0].Evidence.ShouldContain("Release year matches the trusted IGDB game on the local platform.");
        decision.Candidates[1].Evidence.ShouldNotContain("Release year matches the trusted IGDB game on the local platform.");
        decision.LocalEvidence.ShouldNotBeNull().Years.ShouldBeEmpty();
        decision.Dependencies.ShouldNotBeNull().ShouldHaveSingleItem().ShouldBe(new("igdb", "3192", igdb.Revision));
        _accepted.ShouldBe(original);
    }

    [Theory]
    [InlineData("untrusted")]
    [InlineData("suppressed")]
    [InlineData("different game")]
    [InlineData("another platform")]
    [InlineData("original year only on another platform")]
    public async Task DiscoverAsync_SonicWithoutApplicableTrustedPlatformYear_PreservesBasicRanking(string scenario)
    {
        var igdb = SonicIgdb();
        var game = igdb.TrustedGame.ShouldNotBeNull();
        var source = scenario switch
        {
            "untrusted" => igdb with { Trusted = false },
            "suppressed" => igdb with { Suppressed = true },
            "different game" => igdb with { TrustedGame = game with { Id = "42" } },
            "another platform" => igdb with { TrustedGame = game with { Releases = [new(1991, "Arcade")] } },
            "original year only on another platform" => igdb with
            {
                TrustedGame = game with { Releases = [new(1991, "Arcade"), new(2000, "Genesis"), new(2001, "Genesis")] }
            },
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
        var service = CreateSonicSteamGridDb([SonicGame("1", 2006), SonicGame("2", 1991)], [source]);

        await service.DiscoverAsync(1, null, default);

        var decision = _decision.ShouldNotBeNull();
        decision.State.ShouldBe("NeedsReview");
        decision.Candidates.Select(candidate => candidate.Game.Id).ShouldBe(["1", "2"]);
        decision.Candidates.SelectMany(candidate => candidate.Evidence)
            .ShouldNotContain("Release year matches the trusted IGDB game on the local platform.");
        _accepted.ShouldBeNull();
    }

    [Theory]
    [InlineData("same year")]
    [InlineData("unknown year")]
    [InlineData("edition conflict")]
    [InlineData("ambiguous DAT")]
    [InlineData("truncated")]
    [InlineData("conflicting local year")]
    public async Task DiscoverAsync_SteamGridDbUnresolvedEvidence_StaysInReview(string scenario)
    {
        var original = SonicGame("2", 1991);
        ProviderGame[] games = scenario switch
        {
            "same year" => [original, SonicGame("3", 1991)],
            "unknown year" => [original, SonicGame("3", null)],
            "edition conflict" => [original with { Name = "Sonic the Hedgehog (Prototype)" }],
            _ => [original]
        };
        var completeness = scenario == "truncated" ? ProviderGameSearchCompleteness.BudgetLimited
            : ProviderGameSearchCompleteness.ProviderLimited;
        var datFamily = new DatFamilySearchEvidence(["Sonic the Hedgehog"], scenario == "ambiguous DAT");
        var service = CreateSonicSteamGridDb(games, [SonicIgdb()], completeness: completeness, datFamily: datFamily,
            years: scenario == "conflicting local year" ? [2006] : []);

        await service.DiscoverAsync(1, null, default);

        var decision = _decision.ShouldNotBeNull();
        decision.State.ShouldBe("NeedsReview");
        decision.ProvisionalGameId.ShouldBeNull();
        _accepted.ShouldBeNull();
    }

    [Theory]
    [InlineData("Donkey Kong Country", "36958", 1994)]
    [InlineData("Super Mario Kart", "37465", 1992)]
    [InlineData("Super Mario World", "36886", 1990)]
    public async Task DiscoverAsync_SteamGridDbExactTitleAndCatalogYear_AcceptsWithoutIgdb(string title, string id, int year)
    {
        var original = new ProviderGame(id, title, "https://provider.test/game", Year: year);
        var service = Create(title, [], _ => ErrorOrFactory.From<IReadOnlyList<ProviderGame>>([original]),
            completeness: ProviderGameSearchCompleteness.ProviderLimited, providerId: "steamgriddb", years: [year]);
        _adapter.IdentityScope.Returns(ProviderIdentityScope.CrossPlatformArtwork);

        await service.DiscoverAsync(1, null, default);

        var decision = _decision.ShouldNotBeNull();
        decision.State.ShouldBe("AutoAccepted");
        decision.Explanation.ShouldContain("local catalog evidence");
        decision.Dependencies.ShouldNotBeNull().ShouldBeEmpty();
        _accepted.ShouldBe(original);
    }

    [Theory]
    [InlineData(null, 1991)]
    [InlineData(1991, null)]
    public async Task DiscoverAsync_SteamGridDbSingleExactTitleWithoutCorroboratedYear_StaysInReview(int? localYear, int? providerYear)
    {
        var service = CreateSonicSteamGridDb([SonicGame("2", providerYear)], [], years: localYear is { } year ? [year] : []);

        await service.DiscoverAsync(1, null, default);

        var decision = _decision.ShouldNotBeNull();
        decision.State.ShouldBe("NeedsReview");
        decision.Explanation.ShouldContain("corroborated release year");
        _accepted.ShouldBeNull();
    }

    [Fact]
    public async Task DiscoverAsync_SteamGridDbExactTitleAndYearFromCappedSearch_StaysInReview()
    {
        var game = SonicGame("35571", 1991);
        var service = CreateSonicSteamGridDb([game], [SonicIgdb()]);
        _adapter.SearchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((ErrorOr<ProviderGameSearchResult>)new ProviderGameSearchResult([game], ProviderGameSearchCompleteness.ProviderLimited, 1));

        await service.DiscoverAsync(1, null, default);

        _decision.ShouldNotBeNull().State.ShouldBe("NeedsReview");
        _accepted.ShouldBeNull();
    }

    [Fact]
    public async Task DiscoverAsync_SteamGridDbVerifiedBridge_PreservesAutomaticAcceptance()
    {
        var original = SonicGame("2", 1991);
        var bridge = Substitute.For<IArtworkIdentityBridge>();
        bridge.SourceProviderId.Returns("igdb");
        bridge.TargetProviderId.Returns("steamgriddb");
        bridge.ResolveAsync("3192", Arg.Any<CancellationToken>())
            .Returns((ErrorOr<ArtworkBridgeMatch?>)new ArtworkBridgeMatch(original, "Verified store ID."));
        var igdb = SonicIgdb();
        var service = CreateSonicSteamGridDb([], [igdb], [bridge], ProviderGameSearchCompleteness.ProviderLimited);

        await service.DiscoverAsync(1, null, default);

        _decision.ShouldNotBeNull().State.ShouldBe("AutoAccepted");
        _decision.Dependencies.ShouldNotBeNull().ShouldHaveSingleItem().ShouldBe(new("igdb", "3192", igdb.Revision));
        _accepted.ShouldBe(original);
        _queries.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("same", false)]
    [InlineData("replacement", true)]
    [InlineData("lost trust", true)]
    [InlineData("new evidence", true)]
    [InlineData("previous provisional policy", true)]
    [InlineData("previous artwork policy", true)]
    public async Task DiscoverAsync_SteamGridDbRecentReview_RechecksWhenIgdbDependencyChanges(string scenario, bool shouldSearch)
    {
        var igdb = SonicIgdb();
        var cachedDependencies = scenario == "new evidence" ? Array.Empty<ProviderIdentityDependency>() :
            [new("igdb", "3192", scenario == "replacement" ? Guid.NewGuid() : igdb.Revision)];
        var cached = new ProviderIdentityDecision("NeedsReview", "Previous review.", "catalog", "fingerprint",
            [], cachedDependencies)
        {
            ProvisionalPolicyVersion = scenario == "previous provisional policy" ? null : ProvisionalProviderMatchPolicy.Version,
            ArtworkReleasePolicyVersion = scenario == "previous artwork policy" ? null : "steamgriddb-title-year-1"
        };
        var service = CreateSonicSteamGridDb([SonicGame("2", 1991)],
            [igdb with { Trusted = scenario != "lost trust" },
                new("steamgriddb", null, false, false, false, Guid.Empty, "fingerprint", _clock.GetUtcNow(), cached)]);

        await service.DiscoverAsync(1, null, default);

        _queries.Count.ShouldBe(shouldSearch ? 1 : 0);
        (_accepted?.Id).ShouldBe(shouldSearch && scenario != "lost trust" ? "2" : null);
    }

    private ProviderIdentityService CreateSonicSteamGridDb(IReadOnlyList<ProviderGame> games,
        IReadOnlyList<ProviderIdentityLink> links, IReadOnlyList<IArtworkIdentityBridge>? bridges = null,
        ProviderGameSearchCompleteness completeness = ProviderGameSearchCompleteness.Complete,
        DatFamilySearchEvidence? datFamily = null, IReadOnlyList<int>? years = null)
    {
        var service = Create("Sonic the Hedgehog", [], _ => ErrorOrFactory.From(games), links, bridges, completeness,
            datFamily: datFamily, providerId: "steamgriddb", years: years ?? [], platforms: ["genesis"]);
        _adapter.IdentityScope.Returns(ProviderIdentityScope.CrossPlatformArtwork);
        return service;
    }

    private static ProviderGame SonicGame(string id, int? year) => new(id, "Sonic the Hedgehog", "https://provider.test/game", Year: year);

    private static ProviderIdentityLink SonicIgdb() =>
        new("igdb", "3192", false, false, true, Guid.NewGuid(), null, null, null)
        {
            TrustedGame = SonicGame("3192", 1991) with { Releases = [new(1991, "Genesis"), new(2000, "Genesis"), new(2006, "Arcade")] }
        };
}
