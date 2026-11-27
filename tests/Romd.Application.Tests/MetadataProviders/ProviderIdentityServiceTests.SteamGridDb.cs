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
    [InlineData(ProviderGameSearchCompleteness.Complete, "AutoAccepted", null)]
    [InlineData(ProviderGameSearchCompleteness.ProviderLimited, "NeedsReview", "35571")]
    public async Task DiscoverAsync_SonicWithTrustedIgdbGenesisRelease_Ranks1991AndRequiresExhaustiveSearchForTrust(
        ProviderGameSearchCompleteness completeness, string state, string? provisionalGameId)
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
        decision.State.ShouldBe(state);
        decision.ProvisionalGameId.ShouldBe(provisionalGameId);
        decision.Explanation.ShouldContain(provisionalGameId is null
            ? "1991, supported by the trusted IGDB release on the local platform"
            : "ROMD can use this artwork provisionally");
        decision.Candidates.Select(candidate => candidate.Game.Id).ShouldBe(["35571", "1"]);
        decision.Candidates[0].Score.ShouldBe(decision.Candidates[1].Score);
        decision.Candidates[0].Evidence.ShouldContain("Release year matches the trusted IGDB game on the local platform.");
        decision.Candidates[1].Evidence.ShouldNotContain("Release year matches the trusted IGDB game on the local platform.");
        decision.LocalEvidence.ShouldNotBeNull().Years.ShouldBeEmpty();
        decision.Dependencies.ShouldNotBeNull().ShouldHaveSingleItem().ShouldBe(new("igdb", "3192", igdb.Revision));
        _accepted.ShouldBe(provisionalGameId is null ? original : null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiscoverAsync_SteamGridDbDatReleaseAnnotations_UsesBudgetForDistinctNames(bool undatedContender)
    {
        const string title = "Donkey Kong Country 2 - Diddy's Kong Quest";
        const string alternate = "Super Donkey Kong 2";
        var release = new ProviderGame("36688", "Donkey Kong Country 2: Diddy's Kong Quest", "https://provider.test/game", Year: 1995);
        var differentGame = new ProviderGame("22131", "Donkey Kong Country 3: Dixie Kong's Double Trouble!",
            "https://provider.test/game", Year: 1996);
        var igdb = new ProviderIdentityLink("igdb", "1092", true, false, true, Guid.NewGuid(), null, null, null)
        {
            TrustedGame = new("1092", release.Name, "https://provider.test/igdb", Year: 1995, Releases: [new(1995, "SNES")])
        };
        string[] aliases =
        [
            $"{title} (Europe) (En,Fr) (Rev 1)",
            $"{title} (Europe) (En,Fr) (Rev 1) (Virtual Console)",
            $"{title} (Germany) (En,De)",
            $"{title} (Germany) (En,De) (Rev 1) (Virtual Console)",
            $"{title} (USA) (En,Fr)",
            $"{title} (USA) (En,Fr) (Rev 1) (Virtual Console)",
            $"{title} (USA, Europe) (En,Fr) (Rev 1) (Switch Online)",
            alternate
        ];
        var service = Create(title, aliases, query => ErrorOrFactory.From<IReadOnlyList<ProviderGame>>(
                query == alternate.ToUpperInvariant() && undatedContender
                    ? [new("undated", alternate, "https://provider.test/game")]
                    : [release, differentGame]),
            [igdb], completeness: ProviderGameSearchCompleteness.ProviderLimited, providerId: "steamgriddb",
            datFamily: new([$"{title} (USA) (En,Fr)"], false), years: [],
            datLookupNames: aliases[..^1].ToDictionary(name => name, _ => title, StringComparer.Ordinal));
        _adapter.IdentityScope.Returns(ProviderIdentityScope.CrossPlatformArtwork);

        await service.DiscoverAsync(1, null, default);

        _queries.ShouldBe([title.ToUpperInvariant(), "DONKEY KONG COUNTRY 2 DIDDY'S KONG QUEST",
            release.Name.ToUpperInvariant(), alternate.ToUpperInvariant()]);
        var decision = _decision.ShouldNotBeNull();
        decision.State.ShouldBe("NeedsReview");
        decision.ProvisionalGameId.ShouldBe(undatedContender ? null : "36688");
        decision.Candidates[0].Game.ShouldBe(release);
        decision.LocalEvidence.ShouldNotBeNull().Names.ShouldBe(aliases);
        decision.Dependencies.ShouldNotBeNull().ShouldHaveSingleItem().ShouldBe(new("igdb", "1092", igdb.Revision));
        _accepted.ShouldBeNull();
    }

    [Fact]
    public async Task DiscoverAsync_TurtlesInTimeDatBetaEntries_ReuseRetailSearchQueries()
    {
        const string title = "Teenage Mutant Ninja Turtles IV - Turtles in Time";
        var game = new ProviderGame("33864", "Teenage Mutant Ninja Turtles IV: Turtles in Time", "https://provider.test/game", Year: 1992);
        var igdb = new ProviderIdentityLink("igdb", "14697", true, false, true, Guid.NewGuid(), null, null, null)
        {
            TrustedGame = game with { Id = "14697", Releases = [new(1992, "SNES")] }
        };
        string[] releases = [$"{title} (Australia)", $"{title} (USA)", $"{title} (USA) (Beta 1)",
            $"{title} (USA) (Beta 2)", $"{title} (USA) (The Cowabunga Collection)"];
        var service = Create(title, releases, _ => ErrorOrFactory.From<IReadOnlyList<ProviderGame>>([game]), [igdb],
            completeness: ProviderGameSearchCompleteness.ProviderLimited, datFamily: new([$"{title} (USA)"], false),
            providerId: "steamgriddb", years: [],
            datLookupNames: releases[..^1].ToDictionary(name => name, _ => title, StringComparer.Ordinal));
        _adapter.IdentityScope.Returns(ProviderIdentityScope.CrossPlatformArtwork);

        await service.DiscoverAsync(1, null, default);

        var baseQuery = "TEENAGE MUTANT NINJA TURTLES IV TURTLES IN TIME";
        var taggedQuery = " (THE COWABUNGA COLLECTION)";
        _queries.ShouldBe([title.ToUpperInvariant(), baseQuery, game.Name.ToUpperInvariant(),
            title.ToUpperInvariant() + taggedQuery, baseQuery + taggedQuery]);
        var decision = _decision.ShouldNotBeNull();
        decision.State.ShouldBe("NeedsReview");
        decision.ProvisionalGameId.ShouldBe(game.Id);
        decision.LocalEvidence.ShouldNotBeNull().Names.ShouldBe(releases);
        _accepted.ShouldBeNull();
    }

    [Theory]
    [InlineData(ProviderGameSearchCompleteness.ProviderLimited, false)]
    [InlineData(ProviderGameSearchCompleteness.Complete, false)]
    [InlineData(ProviderGameSearchCompleteness.Complete, true)]
    public async Task DiscoverAsync_ZeldaTrustedTitleAndPlatformAlias_RequiresProvisionalEvidence(
        ProviderGameSearchCompleteness completeness, bool undatedContender)
    {
        const string title = "Legend of Zelda, The - A Link to the Past";
        const string providerTitle = "The Legend of Zelda: A Link to the Past";
        var game = new ProviderGame("38124", providerTitle, "https://provider.test/game", Year: 1991);
        var igdb = new ProviderIdentityLink("igdb", "1026", true, false, true, Guid.NewGuid(), null, null, null)
        {
            TrustedGame = game with { Id = "1026", Releases = [new(1991, "Super Famicom"), new(1992, "SNES")] }
        };
        ArtworkPlatformAlias[] aliases = [new(59, "Super Famicom")];
        var service = Create(title, [$"{title} (France) (Beta 1)"], query => ErrorOrFactory.From<IReadOnlyList<ProviderGame>>(
                query == providerTitle.ToUpperInvariant() ? [game] : undatedContender ? [game with { Id = "undated", Name = title, Year = null }] : []),
            [igdb], completeness: completeness, datFamily: new([$"{title} (USA)"], false),
            providerId: "steamgriddb", years: [], artworkPlatformAliases: aliases,
            datLookupNames: new Dictionary<string, string> { [$"{title} (France) (Beta 1)"] = title });
        _adapter.IdentityScope.Returns(ProviderIdentityScope.CrossPlatformArtwork);

        await service.DiscoverAsync(1, null, default);

        _queries.ShouldContain(providerTitle.ToUpperInvariant());
        var decision = _decision.ShouldNotBeNull();
        decision.State.ShouldBe("NeedsReview");
        decision.ProvisionalGameId.ShouldBe(undatedContender ? null : game.Id);
        decision.Candidates[0].Game.ShouldBe(game);
        decision.Candidates[0].Evidence.ShouldContain("Name matches the trusted provider title on the local system.");
        decision.LocalEvidence.ShouldNotBeNull().Names.ShouldNotContain(providerTitle);
        decision.Dependencies.ShouldNotBeNull().ShouldHaveSingleItem().ShouldBe(new("igdb", "1026", igdb.Revision));
        if (undatedContender)
            decision.ArtworkPlatformAliases.ShouldBeNull();
        else
            decision.ArtworkPlatformAliases.ShouldBe(aliases);
        _accepted.ShouldBeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DiscoverAsync_PhantasyStarIVTrustedTitle_FindsUndatedGameForReview(bool trusted)
    {
        const string title = "Phantasy Star IV";
        const string providerTitle = "Phantasy Star IV: The End of the Millennium";
        var game = new ProviderGame("39080", providerTitle, "https://provider.test/game");
        var igdb = new ProviderIdentityLink("igdb", "1230", false, false, trusted, Guid.NewGuid(), null, null, null)
        {
            TrustedGame = game with { Id = "1230", Year = 1993, Releases = [new(1993, "Genesis")] }
        };
        var service = Create(title, [$"{title} (USA) (Sample)"], query => ErrorOrFactory.From<IReadOnlyList<ProviderGame>>(
                query == providerTitle.ToUpperInvariant() ? [game] : []),
            [igdb], completeness: ProviderGameSearchCompleteness.ProviderLimited, datFamily: new([$"{title} (USA)"], false),
            providerId: "steamgriddb", years: [], platforms: ["genesis"],
            datLookupNames: new Dictionary<string, string> { [$"{title} (USA) (Sample)"] = title });
        _adapter.IdentityScope.Returns(ProviderIdentityScope.CrossPlatformArtwork);

        await service.DiscoverAsync(1, null, default);

        _queries.Contains(providerTitle.ToUpperInvariant()).ShouldBe(trusted);
        var decision = _decision.ShouldNotBeNull();
        decision.State.ShouldBe("NeedsReview");
        decision.ProvisionalGameId.ShouldBeNull();
        if (trusted)
        {
            var candidate = decision.Candidates.ShouldHaveSingleItem();
            candidate.Game.ShouldBe(game);
            candidate.Conflicts.ShouldBeEmpty();
            decision.Explanation.ShouldContain("no release year");
        }
        else
            decision.Candidates.ShouldBeEmpty();
        _accepted.ShouldBeNull();
    }

    [Theory]
    [InlineData(ProviderGameSearchCompleteness.Complete)]
    [InlineData(ProviderGameSearchCompleteness.ProviderLimited)]
    public async Task DiscoverAsync_FinalFantasyIIITrustedIgdbAlias_Selects1994Provisionally(
        ProviderGameSearchCompleteness completeness)
    {
        const string title = "Final Fantasy III";
        var wrong = new ProviderGame("5288929", title, "https://provider.test/game", Year: 1990);
        var expected = new ProviderGame("8236", "Final Fantasy VI", "https://provider.test/game", Year: 1994);
        var igdb = new ProviderIdentityLink("igdb", "426", false, false, true, Guid.NewGuid(), null, null, null)
        {
            TrustedGame = new("426", title, "https://provider.test/igdb", Year: 1994, Releases: [new(1994, "SNES")],
                Aliases: ["FF6", "Final Fantasy 6", "Final Fantasy VI", "FINAL FANTASY III", "Final Fantasy 3", "FFVI"])
        };
        var service = Create(title, [], query => query == "FINAL FANTASY VI" ? new[] { expected } : new[] { wrong },
            [igdb], completeness: completeness, datFamily: new([title], false), providerId: "steamgriddb", years: []);
        _adapter.IdentityScope.Returns(ProviderIdentityScope.CrossPlatformArtwork);

        await service.DiscoverAsync(1, null, default);

        _queries.ShouldBe(["FINAL FANTASY III", "FINAL FANTASY 3", "FINAL FANTASY 6", "FINAL FANTASY VI"]);
        var decision = _decision.ShouldNotBeNull();
        decision.State.ShouldBe("NeedsReview");
        decision.ProvisionalGameId.ShouldBe(expected.Id);
        decision.Candidates.Select(candidate => candidate.Game.Id).ShouldBe([expected.Id, wrong.Id]);
        decision.Explanation.ShouldContain("trusted IGDB alias");
        decision.LocalEvidence.ShouldNotBeNull().Names.ShouldBeEmpty();
        decision.Dependencies.ShouldNotBeNull().ShouldHaveSingleItem().ShouldBe(new("igdb", "426", igdb.Revision));
        _accepted.ShouldBeNull();
    }

    [Theory]
    [InlineData("untrusted", false)]
    [InlineData("wrong system", false)]
    [InlineData("ambiguous DAT", false)]
    [InlineData("budget exceeded", false)]
    [InlineData("undated primary", false)]
    [InlineData("competing alias", true)]
    [InlineData("undated alias", true)]
    [InlineData("wrong year", true)]
    [InlineData("edition conflict", true)]
    [InlineData("provider cap", true)]
    public async Task DiscoverAsync_FinalFantasyIIIAliasWithoutSufficientEvidence_StaysInReview(string scenario,
        bool searchesAlias)
    {
        const string title = "Final Fantasy III";
        const string alternate = "Final Fantasy VI";
        var alias = new ProviderGame("8236", scenario == "edition conflict" ? alternate + " Remastered" : alternate,
            "https://provider.test/game", Year: scenario == "undated alias" ? null : scenario == "wrong year" ? 1995 : 1994);
        var original = new ProviderGame("5288929", title, "https://provider.test/game",
            Year: scenario == "undated primary" ? null : 1990);
        var igdb = new ProviderIdentityLink("igdb", "426", false, false, scenario != "untrusted", Guid.NewGuid(), null, null, null)
        {
            TrustedGame = new("426", title, "https://provider.test/igdb",
                Releases: [new(1994, scenario == "wrong system" ? "PS5" : "SNES")],
                Aliases: scenario == "budget exceeded"
                    ? [alternate, "Alias One", "Alias Two", "Alias Three", "Alias Four", "Alias Five"]
                    : [alternate])
        };
        ProviderGame[] aliasResults = scenario == "competing alias" ? [alias, alias with { Id = "9999" }] : [alias];
        var service = Create(title, [], query => query == "FINAL FANTASY VI" ? aliasResults : [original],
            [igdb], completeness: ProviderGameSearchCompleteness.ProviderLimited,
            datFamily: new([title], scenario == "ambiguous DAT"), providerId: "steamgriddb", years: []);
        _adapter.IdentityScope.Returns(ProviderIdentityScope.CrossPlatformArtwork);
        if (scenario == "provider cap")
            _adapter.SearchAsync("FINAL FANTASY VI", Arg.Any<CancellationToken>())
                .Returns(_ =>
                {
                    _queries.Add("FINAL FANTASY VI");
                    return (ErrorOr<ProviderGameSearchResult>)new ProviderGameSearchResult([alias], ProviderGameSearchCompleteness.BudgetLimited, 1);
                });

        await service.DiscoverAsync(1, null, default);

        _queries.ShouldBe(searchesAlias ? ["FINAL FANTASY III", "FINAL FANTASY VI"] : ["FINAL FANTASY III"]);
        var decision = _decision.ShouldNotBeNull();
        decision.State.ShouldBe("NeedsReview");
        decision.ProvisionalGameId.ShouldBeNull();
        _accepted.ShouldBeNull();
    }

    [Theory]
    [InlineData("ToeJam & Earl", "ToeJam & Earl", "34242", 1991)]
    [InlineData("Shinobi III - Return of the Ninja Master", "Shinobi III : Return of the Ninja Master", "36280", 1993)]
    public async Task DiscoverAsync_SteamGridDbUndatedExactTitle_ExplainsWhyTheGameNeedsReview(
        string title, string providerTitle, string id, int year)
    {
        var game = new ProviderGame(id, providerTitle, "https://provider.test/game");
        var igdb = new ProviderIdentityLink("igdb", "1230", false, false, true, Guid.NewGuid(), null, null, null)
        {
            TrustedGame = new("1230", title, "https://provider.test/igdb", Releases: [new(year, "Genesis")])
        };
        var service = Create(title, [], _ => new[] { game }, [igdb],
            completeness: ProviderGameSearchCompleteness.ProviderLimited, datFamily: new([title], false),
            providerId: "steamgriddb", years: [], platforms: ["genesis"]);
        _adapter.IdentityScope.Returns(ProviderIdentityScope.CrossPlatformArtwork);

        await service.DiscoverAsync(1, null, default);

        var decision = _decision.ShouldNotBeNull();
        decision.State.ShouldBe("NeedsReview");
        decision.Candidates.ShouldHaveSingleItem().Game.ShouldBe(game);
        decision.Explanation.ShouldContain("no release year");
        decision.ProvisionalGameId.ShouldBeNull();
        _accepted.ShouldBeNull();
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
            : ProviderGameSearchCompleteness.Complete;
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
            completeness: ProviderGameSearchCompleteness.Complete, providerId: "steamgriddb", years: [year]);
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

    [Theory]
    [InlineData(ProviderGameSearchCompleteness.Complete)]
    [InlineData(ProviderGameSearchCompleteness.ProviderLimited)]
    public async Task DiscoverAsync_SteamGridDbExactTitleAndYearFromCappedSearch_StaysInReview(
        ProviderGameSearchCompleteness completeness)
    {
        var game = SonicGame("35571", 1991);
        var service = CreateSonicSteamGridDb([game], [SonicIgdb()]);
        _adapter.SearchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((ErrorOr<ProviderGameSearchResult>)new ProviderGameSearchResult([game], completeness, 1));

        await service.DiscoverAsync(1, null, default);

        var decision = _decision.ShouldNotBeNull();
        decision.State.ShouldBe("NeedsReview");
        decision.ProvisionalGameId.ShouldBeNull();
        _accepted.ShouldBeNull();
    }

    [Fact]
    public async Task DiscoverAsync_SteamGridDbIncompleteAliasSearch_OnlySelectsProvisionally()
    {
        var game = SonicGame("35571", 1991);
        var service = CreateSonicSteamGridDb([game], [SonicIgdb()],
            datFamily: new(["Sonic the Hedgehog", "Sonic Regional Title"], false));
        _adapter.SearchAsync("SONIC REGIONAL TITLE", Arg.Any<CancellationToken>())
            .Returns((ErrorOr<ProviderGameSearchResult>)new ProviderGameSearchResult([], ProviderGameSearchCompleteness.ProviderLimited, 100));

        await service.DiscoverAsync(1, null, default);

        var decision = _decision.ShouldNotBeNull();
        decision.State.ShouldBe("NeedsReview");
        decision.ProvisionalGameId.ShouldBe(game.Id);
        _accepted.ShouldBeNull();
    }

    [Theory]
    [InlineData(1991)]
    [InlineData(null)]
    public async Task DiscoverAsync_SteamGridDbVerifiedBridge_PreservesAutomaticAcceptance(int? year)
    {
        var original = SonicGame("2", year);
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
            ArtworkReleasePolicyVersion = scenario == "previous artwork policy" ? "steamgriddb-title-year-5" : "steamgriddb-title-year-6"
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
