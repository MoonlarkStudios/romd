using Microsoft.Extensions.Logging.Abstractions;
using Romd.Admin.Application.Common.Persistence;
using ErrorOr;
using NSubstitute;
using Romd.Admin.Application.Artwork.Providers;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.MetadataProviders;
using Romd.Contracts.Management.MetadataProviders;
using Romd.Domain.Catalog;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.MetadataProviders;

public sealed partial class ProviderIdentityServiceTests
{
    private readonly IProviderIdentityStore _store = Substitute.For<IProviderIdentityStore>();
    private readonly IUnitOfWork _work = Substitute.For<IUnitOfWork>();
    private readonly ITransaction _transaction = Substitute.For<ITransaction>();

    public ProviderIdentityServiceTests() =>
        _work.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(_transaction);
    private readonly IProviderIdentityAdapter _adapter = Substitute.For<IProviderIdentityAdapter>();
    private readonly List<string> _queries = [];
    private readonly FixedClock _clock = new();
    private ProviderIdentityDecisionDto? _decision;
    private ProviderGameDto? _accepted;

    private ProviderIdentityService Create(string name, string[] aliases,
        Func<string, ErrorOr<IReadOnlyList<ProviderGameDto>>> search,
        IReadOnlyList<ProviderIdentityLink>? links = null, IReadOnlyList<IArtworkIdentityBridge>? bridges = null,
        ProviderGameSearchCompleteness completeness = ProviderGameSearchCompleteness.Complete)
    {
        _adapter.Id.Returns("provider");
        _adapter.Capabilities.Returns(new[] { "metadata" });
        _adapter.GetAvailabilityAsync(Arg.Any<CancellationToken>()).Returns(new ProviderAvailability(true, true));
        _adapter.SearchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var query = call.ArgAt<string>(0);
            _queries.Add(query);
            var result = search(query);
            return result.IsError ? (ErrorOr<ProviderGameSearchResultDto>)result.Errors : new ProviderGameSearchResultDto(result.Value, completeness, 500);
        });
        _store.ReadAsync(1, Arg.Any<CancellationToken>()).Returns(new ProviderIdentitySnapshot(1, Guid.NewGuid(), "fingerprint",
            new(name, aliases, [1994], ["SNES"], "catalog"), links ?? []));
        _store.StageDecisionAsync(Arg.Any<ProviderIdentitySnapshot>(), "provider", Guid.Empty,
            Arg.Any<ProviderIdentityDecisionDto>(), Arg.Any<ProviderGameDto?>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            _decision = call.ArgAt<ProviderIdentityDecisionDto>(3);
            _accepted = call.ArgAt<ProviderGameDto?>(4);
            var receipt = Substitute.For<IStagedProviderIdentityDecision>();
            receipt.Applied.Returns(true);
            return receipt;
        });
        return new(_store, [_adapter], bridges ?? [], _clock, _work, NullLogger<ProviderIdentityService>.Instance);
    }

    private static ProviderGameDto Game(string id, string name) => new(id, name, "https://provider.test/game", Year: 1994, Platforms: ["SNES"]);

    [Fact]
    public async Task GetTrustedAsync_TrustChanges_RechecksCurrentIdentityWithoutLoadingDiscoveryFacts()
    {
        _store.ReadTrustedAsync(1, "provider", Arg.Any<CancellationToken>()).Returns("42", (string?)null);
        var service = new ProviderIdentityService(_store, [], [], _clock, _work, NullLogger<ProviderIdentityService>.Instance);

        (await service.GetTrustedAsync(1, "provider", default)).ShouldBe("42");
        (await service.GetTrustedAsync(1, "provider", default)).ShouldBeNull();

        await _store.Received(2).ReadTrustedAsync(1, "provider", Arg.Any<CancellationToken>());
        await _store.DidNotReceive().ReadAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(ProviderIdentityScope.CrossPlatformArtwork, "AutoAccepted")]
    [InlineData(ProviderIdentityScope.Release, "NeedsReview")]
    public async Task DiscoverAsync_ExactNameWithoutCorroboration_UsesProviderScope(ProviderIdentityScope scope, string state)
    {
        var service = Create("Flushed Away", [], _ => new[] { new ProviderGameDto("36590", "Flushed Away", "https://provider.test/game") });
        _adapter.IdentityScope.Returns(scope);

        await service.DiscoverAsync(1, null, default);

        _decision.ShouldNotBeNull().State.ShouldBe(state);
        (_accepted is not null).ShouldBe(scope == ProviderIdentityScope.CrossPlatformArtwork);
    }

    [Fact]
    public async Task DiscoverAsync_LocalAliasFindsGame_CombinesQueriesBeforeAcceptance()
    {
        var service = Create("Local title", ["Regional title"], query =>
            query == "REGIONAL TITLE" ? new[] { Game("1", "Regional title") } : Array.Empty<ProviderGameDto>());

        await service.DiscoverAsync(1, null, default);

        _queries.ShouldBe(["LOCAL TITLE", "REGIONAL TITLE"]);
        _decision.ShouldNotBeNull().State.ShouldBe("AutoAccepted");
        _accepted.ShouldNotBeNull().Id.ShouldBe("1");
    }

    [Fact]
    public async Task DiscoverAsync_AliasRevealsAlternative_DoesNotAcceptFirstQueryWinner()
    {
        var service = Create("Local title", ["Regional title"], query =>
            new[] { Game(query == "LOCAL TITLE" ? "1" : "2", query) });

        await service.DiscoverAsync(1, null, default);

        _decision.ShouldNotBeNull().State.ShouldBe("NeedsReview");
        _accepted.ShouldBeNull();
    }

    [Fact]
    public async Task DiscoverAsync_LaterQueryUnavailable_DoesNotTrustIncompleteDiscovery()
    {
        var service = Create("Local title", ["Regional title"], query =>
            query == "LOCAL TITLE" ? new[] { Game("1", "Local title") } : ProviderMatchErrors.Unavailable);

        await service.DiscoverAsync(1, null, default);

        _decision.ShouldNotBeNull().State.ShouldBe("Unavailable");
        _accepted.ShouldBeNull();
    }

    [Fact]
    public async Task DiscoverAsync_DuplicateResults_AreAssessedOnce()
    {
        var service = Create("Game - Subtitle", [], _ => new[] { Game("1", "Game: Subtitle") });

        await service.DiscoverAsync(1, null, default);

        _queries.ShouldBe(["GAME - SUBTITLE", "GAME SUBTITLE"]);
        _decision.ShouldNotBeNull().Candidates.Count.ShouldBe(1);
        _decision.State.ShouldBe("AutoAccepted");
    }

    [Fact]
    public async Task DiscoverAsync_DuplicateIdentityWithChangedEvidence_DoesNotHideLaterContradiction()
    {
        var service = Create("Local title", ["Regional title"], query =>
            new[] { Game("1", "Local title") with { Year = query == "LOCAL TITLE" ? 1994 : 2001 } });

        await service.DiscoverAsync(1, null, default);

        _decision.ShouldNotBeNull().State.ShouldBe("NeedsReview");
        _decision.Candidates.Count.ShouldBe(1);
        _accepted.ShouldBeNull();
    }

    [Fact]
    public async Task DiscoverAsync_ManyAliases_UsesDeterministicBoundedQueries()
    {
        var service = Create("Game+", ["Z", "F", "E", "D", "C", "B", "A", "Game+"], _ => Array.Empty<ProviderGameDto>());

        await service.DiscoverAsync(1, null, default);

        _queries.ShouldBe(["GAME+", "A", "B", "C", "D", "E"]);
        _decision.ShouldNotBeNull().State.ShouldBe("NeedsReview");
        _accepted.ShouldBeNull();
    }

    [Fact]
    public async Task DiscoverAsync_QueryBudgetHidesUnsearchedAliases_DoesNotAcceptApparentUniqueWinner()
    {
        var service = Create("Game", ["A", "B", "C", "D", "E", "F"], _ => new[] { Game("1", "Game") });

        await service.DiscoverAsync(1, null, default);

        _queries.Count.ShouldBe(6);
        _decision.ShouldNotBeNull().State.ShouldBe("NeedsReview");
        _decision.Candidates.Count.ShouldBe(1);
        _accepted.ShouldBeNull();
    }

    [Theory]
    [InlineData(ProviderGameSearchCompleteness.ProviderLimited, true)]
    [InlineData(ProviderGameSearchCompleteness.ProviderLimited, false)]
    [InlineData(ProviderGameSearchCompleteness.BudgetLimited, true)]
    [InlineData(ProviderGameSearchCompleteness.BudgetLimited, false)]
    public async Task DiscoverAsync_IncompleteSearch_NeverEstablishesUniquenessOrNoMatch(
        ProviderGameSearchCompleteness completeness, bool hasCandidate)
    {
        var service = Create("Game", [], _ => hasCandidate ? new[] { Game("1", "Game") } : [], completeness: completeness);
        _adapter.IdentityScope.Returns(ProviderIdentityScope.CrossPlatformArtwork);

        await service.DiscoverAsync(1, null, default);

        _decision.ShouldNotBeNull().State.ShouldBe("NeedsReview");
        _decision.Explanation.ShouldContain("incomplete");
        _decision.Candidates.Count.ShouldBe(hasCandidate ? 1 : 0);
        _accepted.ShouldBeNull();
    }

    [Fact]
    public async Task DiscoverAsync_NoSearchableName_RequiresReviewWithoutProviderIo()
    {
        var service = Create("   ", [], _ => Array.Empty<ProviderGameDto>());

        await service.DiscoverAsync(1, null, default);

        _queries.ShouldBeEmpty();
        _decision.ShouldNotBeNull().State.ShouldBe("NeedsReview");
        _accepted.ShouldBeNull();
    }

    [Fact]
    public async Task DiscoverAsync_TrustedBridge_DoesNotDependOnLimitedSearch()
    {
        var bridge = Substitute.For<IArtworkIdentityBridge>();
        bridge.SourceProviderId.Returns("source");
        bridge.TargetProviderId.Returns("provider");
        bridge.ResolveAsync("42", Arg.Any<CancellationToken>())
            .Returns((ErrorOr<ArtworkBridgeMatch?>)new ArtworkBridgeMatch(Game("1", "Game"), "Trusted Steam bridge."));
        var service = Create("Game", [], _ => Array.Empty<ProviderGameDto>(),
            [new("source", "42", true, false, true, Guid.NewGuid(), null, null, null)], [bridge],
            ProviderGameSearchCompleteness.ProviderLimited);

        await service.DiscoverAsync(1, null, default);

        _queries.ShouldBeEmpty();
        _decision.ShouldNotBeNull().State.ShouldBe("AutoAccepted");
        _accepted.ShouldNotBeNull().Id.ShouldBe("1");
        _decision.Dependencies.ShouldNotBeNull().Single().ProviderId.ShouldBe("source");
    }

    [Fact]
    public async Task DiscoverAsync_PartialQuerySetThrottled_ReturnsProviderHintWithoutCachingOrAcceptance()
    {
        var due = _clock.GetUtcNow().AddSeconds(10);
        var throttled = true;
        var service = Create("Local title", ["Regional title"], query =>
            query == "REGIONAL TITLE" && throttled
                ? ArtworkProviderErrors.RateLimited(due)
                : new[] { Game("1", "Local title") });
        var owner = new JobContext(null, _ => Task.CompletedTask, default);

        var result = await service.DiscoverAsync(1, owner, default);

        result.RetryAfterByProvider["provider"].ShouldBe(due);
        owner.DeferredUntil.ShouldBeNull();
        _decision.ShouldBeNull();
        _accepted.ShouldBeNull();
        await _store.DidNotReceive().StageDecisionAsync(Arg.Any<ProviderIdentitySnapshot>(), Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<ProviderIdentityDecisionDto>(), Arg.Any<ProviderGameDto?>(), Arg.Any<CancellationToken>());

        throttled = false;
        await service.DiscoverAsync(1, new JobContext(null, _ => Task.CompletedTask, default), default);
        _decision.ShouldNotBeNull().State.ShouldBe("AutoAccepted");
    }

    [Fact]
    public async Task DiscoverAsync_OwnerlessThrottle_DoesNotHideThrottleFromSubsequentOwnedDiscovery()
    {
        var service = Create("Local title", [], _ => ArtworkProviderErrors.RateLimited());

        await service.ResolveTrustedAsync(1, "provider", default);
        _decision.ShouldBeNull();
        var owner = new JobContext(null, _ => Task.CompletedTask, default);
        var result = await service.DiscoverAsync(1, owner, default);

        result.RetryAfterByProvider["provider"].ShouldBe(_clock.GetUtcNow().AddMinutes(1));
        owner.DeferredUntil.ShouldBeNull();
        _queries.Count.ShouldBe(2);
        _decision.ShouldBeNull();
    }

    [Fact]
    public async Task DiscoverAsync_BridgeThrottled_ReturnsProviderHintWithoutFallbackSearchOrCaching()
    {
        var bridge = Substitute.For<IArtworkIdentityBridge>();
        bridge.SourceProviderId.Returns("source");
        bridge.TargetProviderId.Returns("provider");
        bridge.ResolveAsync("42", Arg.Any<CancellationToken>())
            .Returns((ErrorOr<ArtworkBridgeMatch?>)ArtworkProviderErrors.RateLimited(_clock.GetUtcNow().AddSeconds(5)));
        var service = Create("Local title", [], _ => new[] { Game("1", "Local title") },
            [new("source", "42", true, false, true, Guid.NewGuid(), null, null, null)], [bridge]);
        var owner = new JobContext(null, _ => Task.CompletedTask, default);

        var result = await service.DiscoverAsync(1, owner, default);

        result.RetryAfterByProvider["provider"].ShouldBe(_clock.GetUtcNow().AddSeconds(5));
        owner.DeferredUntil.ShouldBeNull();
        _queries.ShouldBeEmpty();
        _decision.ShouldBeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiscoverAsync_FirstProviderThrottled_StillAcceptsLaterProvider(bool useBridge)
    {
        var due = _clock.GetUtcNow().AddSeconds(10);
        var bridge = Substitute.For<IArtworkIdentityBridge>();
        bridge.SourceProviderId.Returns("source");
        bridge.TargetProviderId.Returns("provider");
        bridge.ResolveAsync("42", Arg.Any<CancellationToken>())
            .Returns((ErrorOr<ArtworkBridgeMatch?>)ArtworkProviderErrors.RateLimited(due));
        _ = Create("Local title", [], _ => ArtworkProviderErrors.RateLimited(due),
            [new("source", "42", true, false, true, Guid.NewGuid(), null, null, null)]);
        var later = Substitute.For<IProviderIdentityAdapter>();
        later.Id.Returns("second");
        later.Capabilities.Returns(new[] { "metadata" });
        later.GetAvailabilityAsync(Arg.Any<CancellationToken>()).Returns(new ProviderAvailability(true, true));
        later.SearchAsync("LOCAL TITLE", Arg.Any<CancellationToken>())
            .Returns((ErrorOr<ProviderGameSearchResultDto>)new ProviderGameSearchResultDto([Game("2", "Local title")], ProviderGameSearchCompleteness.Complete, 500));
        var service = new ProviderIdentityService(_store, [_adapter, later], useBridge ? [bridge] : [], _clock, _work, NullLogger<ProviderIdentityService>.Instance);
        var owner = new JobContext(null, _ => Task.CompletedTask, default);

        var result = await service.DiscoverAsync(1, owner, default);

        result.RetryAfterByProvider.Count.ShouldBe(1);
        result.RetryAfterByProvider["provider"].ShouldBe(due);
        owner.DeferredUntil.ShouldBeNull();
        await _store.Received(1).StageDecisionAsync(Arg.Any<ProviderIdentitySnapshot>(), "second", Guid.Empty,
            Arg.Is<ProviderIdentityDecisionDto>(decision => decision.State == "AutoAccepted"),
            Arg.Is<ProviderGameDto>(game => game.Id == "2"), Arg.Any<CancellationToken>());
        await _store.DidNotReceive().StageDecisionAsync(Arg.Any<ProviderIdentitySnapshot>(), "provider", Arg.Any<Guid>(),
            Arg.Any<ProviderIdentityDecisionDto>(), Arg.Any<ProviderGameDto?>(), Arg.Any<CancellationToken>());
        if (useBridge)
            _queries.ShouldBeEmpty();
    }

    [Fact]
    public async Task DiscoverAsync_TrustedBridgeWithoutCorroboration_PreservesEvidenceAndDependency()
    {
        var revision = Guid.NewGuid();
        var bridge = Substitute.For<IArtworkIdentityBridge>();
        bridge.SourceProviderId.Returns("source");
        bridge.TargetProviderId.Returns("provider");
        bridge.ResolveAsync("42", Arg.Any<CancellationToken>())
            .Returns((ErrorOr<ArtworkBridgeMatch?>)new ArtworkBridgeMatch(
                new("1", "Game", "https://provider.test/game"), "Trusted Steam bridge."));
        var service = Create("Game", [], _ => Array.Empty<ProviderGameDto>(),
            [new("source", "42", true, false, true, revision, null, null, null)], [bridge]);

        await service.DiscoverAsync(1, null, default);

        _queries.ShouldBeEmpty();
        _decision.ShouldNotBeNull().State.ShouldBe("AutoAccepted");
        _decision.Explanation.ShouldBe("Trusted Steam bridge.");
        _decision.InputFingerprint.ShouldBe("fingerprint");
        _decision.Provenance.ShouldBe("catalog");
        _decision.Dependencies.ShouldNotBeNull().ShouldHaveSingleItem().ShouldBe(new("source", "42", revision));
        _decision.LocalEvidence.ShouldNotBeNull().Years.ShouldBe([1994]);
        var candidate = _decision.Candidates.ShouldHaveSingleItem();
        candidate.Score.ShouldBe(100);
        candidate.Eligible.ShouldBeFalse();
        _accepted.ShouldBe(candidate.Game);
    }

    [Theory]
    [InlineData("Game Deluxe", 1994, "SNES")]
    [InlineData("Game", 2004, "SNES")]
    [InlineData("Game", 1994, "PS5")]
    public async Task DiscoverAsync_TrustedBridgeContradictsLocalEvidence_RequiresReview(string name, int year, string platform)
    {
        var bridge = Substitute.For<IArtworkIdentityBridge>();
        bridge.SourceProviderId.Returns("source");
        bridge.TargetProviderId.Returns("provider");
        bridge.ResolveAsync("42", Arg.Any<CancellationToken>())
            .Returns((ErrorOr<ArtworkBridgeMatch?>)new ArtworkBridgeMatch(
                new("1", name, "https://provider.test/game", Year: year, Platforms: [platform]), "Trusted Steam bridge."));
        var service = Create("Game", [], _ => Array.Empty<ProviderGameDto>(),
            [new("source", "42", true, false, true, Guid.NewGuid(), null, null, null)], [bridge]);

        await service.DiscoverAsync(1, null, default);

        _decision.ShouldNotBeNull().State.ShouldBe("NeedsReview");
        _decision.Candidates.ShouldHaveSingleItem().Conflicts.ShouldNotBeEmpty();
        _decision.Dependencies.ShouldNotBeNull().Count.ShouldBe(1);
        _accepted.ShouldBeNull();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task DiscoverAsync_BridgeSourceIsNotCurrentlyUsable_SearchesWithoutBridge(bool trusted, bool suppressed)
    {
        var bridge = Substitute.For<IArtworkIdentityBridge>();
        bridge.SourceProviderId.Returns("source");
        bridge.TargetProviderId.Returns("provider");
        var service = Create("Game", [], _ => new[] { Game("1", "Game") },
            [new("source", "42", true, suppressed, trusted, Guid.NewGuid(), null, null, null)], [bridge]);

        await service.DiscoverAsync(1, null, default);

        await bridge.DidNotReceive().ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        _queries.ShouldBe(["GAME"]);
        _decision.ShouldNotBeNull().State.ShouldBe("AutoAccepted");
        _decision.Dependencies.ShouldNotBeNull().ShouldBeEmpty();
    }

    [Fact]
    public async Task DiscoverAsync_CompleteEmptySearch_PreservesNoMatchExplanation()
    {
        var service = Create("Game", [], _ => Array.Empty<ProviderGameDto>());

        await service.DiscoverAsync(1, null, default);

        _decision.ShouldNotBeNull().State.ShouldBe("NoMatch");
        _decision.Explanation.ShouldBe("Provider search returned no candidates.");
        _accepted.ShouldBeNull();
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);
    }
}
