using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NSubstitute;
using Romd.Admin.Application.Artwork.Providers;
using Romd.Admin.Application.MetadataProviders;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Domain.Catalog;
using Romd.Domain.Hashing;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.MetadataProviders;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;
using Legacy = Romd.Contracts.Management.MetadataProviders;

namespace Romd.Infrastructure.Tests.Enrichment;

public sealed partial class ProviderIdentityDatabaseTests : IDisposable
{
    private readonly PostgreSqlTestDatabase _database = PostgreSqlTestDatabase.Create();

    [Fact]
    public async Task Fingerprint_TracksActiveLocalEvidenceButExcludesBiosAndEnrichedDates()
    {
        await using var db = _database.CreateContext();
        await Seed(db);
        var original = await Fingerprint(db);
        await db.DatGames.Where(x => x.IsBios).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.Name, "Changed BIOS").SetProperty(x => x.Year, "1999"));
        await db.Titles.ExecuteUpdateAsync(s => s.SetProperty(x => x.ReleaseDate, new DateOnly(2020, 1, 1)));
        (await Fingerprint(db)).ShouldBe(original);
        await db.CatalogSources.ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "Paused"));
        (await Fingerprint(db)).ShouldNotBe(original);
        await db.CatalogSources.ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "Active"));
        (await Fingerprint(db)).ShouldBe(original);
        await db.DatGames.Where(x => !x.IsBios).ExecuteUpdateAsync(s => s.SetProperty(x => x.Year, "1992"));
        (await Fingerprint(db)).ShouldNotBe(original);
    }

    [Fact]
    public async Task Trust_RequiresCurrentEvidenceWhileExplicitConfirmationRemainsAuthoritative()
    {
        await using var db = _database.CreateContext();
        await Seed(db);
        await AddAutomatic(db, "a", "42");
        (await Trusted(db, "a")).ShouldBeTrue();
        await db.CatalogSources.ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "Paused"));
        (await Trusted(db, "a")).ShouldBeFalse();
        await db.TitleExternalIds.ExecuteUpdateAsync(s => s.SetProperty(x => x.IsConfirmed, true));
        (await Trusted(db, "a")).ShouldBeTrue();
        await db.Set<TitleProviderMatchStateEntity>().ExecuteDeleteAsync();
        (await Trusted(db, "a")).ShouldBeTrue();
    }

    [Fact]
    public async Task ArtworkAuthorization_UsesCurrentTrustedAssociationAndRechecksItsRevision()
    {
        await using var db = _database.CreateContext();
        await Seed(db);
        await AddAutomatic(db, "igdb", "42");
        var provider = Substitute.For<IProviderIdentityAdapter>();
        provider.Id.Returns("igdb");
        provider.GetAvailabilityAsync(default).Returns(new ProviderAvailability(true, true));
        var store = new TitleProviderMatchStore(db, TimeProvider.System);
        var service = new ArtworkMatchAuthorizationService(Substitute.For<IProviderIdentityService>(), [provider], store);
        var resolved = await service.ReadAsync(1, "igdb", ArtworkRole.Poster, default);
        var match = resolved.ShouldBeOfType<ArtworkMatchResolution.Authorized>().Match;
        match.GameId.ShouldBe("42");
        match.Role.ShouldBe(ArtworkRole.Poster);
        (await service.IsCurrentAsync(1, match, default)).ShouldBeTrue();

        await db.Set<TitleProviderMatchStateEntity>()
            .ExecuteUpdateAsync(set => set.SetProperty(state => state.Revision, Guid.NewGuid()));
        (await service.IsCurrentAsync(1, match, default)).ShouldBeFalse();
        await db.CatalogSources.ExecuteUpdateAsync(set => set.SetProperty(source => source.Status, "Paused"));
        var blocked = await service.ReadAsync(1, "igdb", ArtworkRole.Poster, default);
        blocked.ShouldBe(new ArtworkMatchResolution.Blocked(ProviderMatchBlock.NeedsMatch));
    }

    [Fact]
    public async Task ReadAsync_ReadsPreviouslyStoredIgdbGameAndDecisionIntoApplicationModels()
    {
        await using var db = _database.CreateContext();
        await Seed(db);
        await AddAutomatic(db, "igdb", "3192");
        var game = new Legacy.ProviderGame("3192", "Sonic the Hedgehog", "https://provider.test/game",
            Releases: [new(1991, "Genesis")]);
        var decision = new Legacy.ProviderIdentityDecision("AutoAccepted", "Verified", "DAT",
            await Fingerprint(db), [new(game, 120, true, ["Platform matches"], [])], [],
            new("Sonic the Hedgehog", ["Sonic"], [1991], ["Genesis"]));
        string gameJson = JsonSerializer.Serialize(game);
        string decisionJson = JsonSerializer.Serialize(decision);
        var states = db.Set<TitleProviderMatchStateEntity>().Where(state => state.ProviderId == "igdb");
        await states.ExecuteUpdateAsync(update => update.SetProperty(state => state.GameJson, gameJson)
            .SetProperty(state => state.DecisionJson, decisionJson));
        var store = CreateFamilyStore(db);

        var snapshot = (await store.ReadAsync(1, default)).ShouldNotBeNull();
        var link = snapshot.Links.ShouldHaveSingleItem();
        link.TrustedGame.ShouldNotBeNull().Releases.ShouldBe([new(1991, "Genesis")]);
        JsonSerializer.Serialize(link.TrustedGame).ShouldBe(gameJson);
        JsonSerializer.Serialize(link.Decision).ShouldBe(decisionJson);
        var storedMatch = (await new TitleProviderMatchStore(db, TimeProvider.System).ReadFactsAsync(1, ["igdb"], default))
            .States.ShouldHaveSingleItem();
        JsonSerializer.Serialize(storedMatch.Game).ShouldBe(gameJson);
        JsonSerializer.Serialize(storedMatch.Decision).ShouldBe(decisionJson);

        await states.ExecuteUpdateAsync(update => update.SetProperty(state => state.SuppressAutomaticMatch, true));
        var suppressed = (await store.ReadAsync(1, default)).ShouldNotBeNull();
        suppressed.Links.ShouldHaveSingleItem().TrustedGame.ShouldBeNull();

        await states.ExecuteUpdateAsync(update => update.SetProperty(state => state.SuppressAutomaticMatch, false));
        await db.TitleExternalIds.ExecuteUpdateAsync(update => update.SetProperty(link => link.IsConfirmed, true)
            .SetProperty(link => link.ExternalId, "42"));
        var mismatched = (await store.ReadAsync(1, default)).ShouldNotBeNull().Links.ShouldHaveSingleItem();
        mismatched.Trusted.ShouldBeTrue();
        mismatched.TrustedGame.ShouldBeNull();
    }

    [Fact]
    public async Task Trust_ChangedPolicyVersion_InvalidatesAutomaticTrustButPreservesConfirmation()
    {
        await using var db = _database.CreateContext();
        await Seed(db);
        await AddAutomatic(db, "a", "42");
        var nextVersion = ProviderIdentityPolicy.Version + "-next";

        var nextFingerprint = await db.Titles.Where(x => x.Id == 1)
            .Select(x => ProviderIdentityDatabaseFunctions.Fingerprint(x.Id, nextVersion)).SingleAsync();
        nextFingerprint.ShouldNotBe(await Fingerprint(db));
        (await db.Titles.Where(x => x.Id == 1)
            .Select(x => ProviderIdentityDatabaseFunctions.IsTrusted(x.Id, "a", nextVersion)).SingleAsync()).ShouldBeFalse();

        await db.TitleExternalIds.ExecuteUpdateAsync(s => s.SetProperty(x => x.IsConfirmed, true));
        (await db.Titles.Where(x => x.Id == 1)
            .Select(x => ProviderIdentityDatabaseFunctions.IsTrusted(x.Id, "a", nextVersion)).SingleAsync()).ShouldBeTrue();
    }

    [Fact]
    public async Task Trust_PreClosureAutomaticDecision_IsInvalidatedButManualConfirmationSurvives()
    {
        await using var db = _database.CreateContext();
        await Seed(db);
        await AddAutomatic(db, "a", "42");
        var oldFingerprint = await db.Titles.Where(x => x.Id == 1)
            .Select(x => ProviderIdentityDatabaseFunctions.Fingerprint(x.Id, "provider-identity-4")).SingleAsync();
        await db.Set<TitleProviderMatchStateEntity>().ExecuteUpdateAsync(update => update
            .SetProperty(x => x.MatcherVersion, "provider-identity-4")
            .SetProperty(x => x.InputFingerprint, oldFingerprint));

        (await Trusted(db, "a")).ShouldBeFalse();
        (await Fingerprint(db)).ShouldNotBe(oldFingerprint);

        await db.TitleExternalIds.ExecuteUpdateAsync(update => update.SetProperty(x => x.IsConfirmed, true));
        (await Trusted(db, "a")).ShouldBeTrue();
    }

    [Fact]
    public async Task Discovery_PreClosureCachedAcceptance_IsReevaluatedAndLimitedEvidenceStaysUntrusted()
    {
        await using var db = _database.CreateContext();
        await Seed(db);
        await AddAutomatic(db, "a", "42");
        var oldFingerprint = await db.Titles.Where(x => x.Id == 1)
            .Select(x => ProviderIdentityDatabaseFunctions.Fingerprint(x.Id, "provider-identity-4")).SingleAsync();
        var oldDecision = JsonSerializer.Serialize(new ProviderIdentityDecision("AutoAccepted", "Old truncated search.",
            "test", oldFingerprint, []));
        await db.Set<TitleProviderMatchStateEntity>().ExecuteUpdateAsync(update => update
            .SetProperty(x => x.MatcherVersion, "provider-identity-4")
            .SetProperty(x => x.InputFingerprint, oldFingerprint)
            .SetProperty(x => x.LastAttemptAt, DateTimeOffset.UtcNow)
            .SetProperty(x => x.DecisionJson, oldDecision));
        var evidence = Substitute.For<ITitleEnrichmentEvidenceReader>();
        evidence.ReadAsync(1, Arg.Any<CancellationToken>()).Returns(Array.Empty<TitleEnrichmentEvidence>());
        var adapter = Substitute.For<IProviderIdentityAdapter>();
        adapter.Id.Returns("a");
        adapter.IdentityScope.Returns(ProviderIdentityScope.CrossPlatformArtwork);
        adapter.Capabilities.Returns(["artwork"]);
        adapter.GetAvailabilityAsync(Arg.Any<CancellationToken>()).Returns(new ProviderAvailability(true, true));
        adapter.SearchAsync("GAME", Arg.Any<CancellationToken>()).Returns(new ProviderGameSearchResult(
            [new("42", "Game", "https://example.com/game", Platforms: ["Test"])], ProviderGameSearchCompleteness.ProviderLimited, 100));
        var store = new ProviderIdentityStore(db, evidence, TimeProvider.System);
        var service = new ProviderIdentityService(store, [adapter], [], TimeProvider.System, new EfUnitOfWork(db), NullLogger<ProviderIdentityService>.Instance);

        (await service.GetTrustedAsync(1, "a", default)).ShouldBeNull();
        await service.DiscoverAsync(1, null, default);

        await adapter.Received(1).SearchAsync("GAME", Arg.Any<CancellationToken>());
        var state = await db.Set<TitleProviderMatchStateEntity>().SingleAsync();
        state.MatcherVersion.ShouldBe(ProviderIdentityPolicy.Version);
        state.InputFingerprint.ShouldNotBe(oldFingerprint);
        state.AutomaticallyTrusted.ShouldBeFalse();
        JsonSerializer.Deserialize<ProviderIdentityDecision>(state.DecisionJson!)!.State.ShouldBe("NeedsReview");
        (await service.GetTrustedAsync(1, "a", default)).ShouldBeNull();
        (await db.TitleExternalIds.SingleAsync()).ExternalId.ShouldBe("42");
    }

    [Fact]
    public async Task Trust_RejectsChangedDependencyRevisionAndCycles()
    {
        await using var db = _database.CreateContext();
        await Seed(db);
        var firstRevision = await AddAutomatic(db, "a", "42");
        var secondRevision = await AddAutomatic(db, "b", "84", [new("a", "42", firstRevision)]);
        (await Trusted(db, "b")).ShouldBeTrue();
        var changedRevision = Guid.NewGuid();
        await db.Set<TitleProviderMatchStateEntity>().Where(x => x.ProviderId == "a")
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Revision, changedRevision));
        (await Trusted(db, "b")).ShouldBeFalse();
        await db.Set<TitleProviderMatchStateEntity>().Where(x => x.ProviderId == "a")
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Revision, firstRevision));
        (await Trusted(db, "b")).ShouldBeTrue();
        var cycle = JsonSerializer.Serialize(new ProviderIdentityDecision("AutoMatched", "test", "test",
            await Fingerprint(db), [], [new("b", "84", secondRevision)]));
        await db.Set<TitleProviderMatchStateEntity>().Where(x => x.ProviderId == "a")
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.DecisionJson, cycle));
        (await Trusted(db, "a")).ShouldBeFalse();
        (await Trusted(db, "b")).ShouldBeFalse();
    }

    [Theory]
    [InlineData(ProviderGameSearchCompleteness.Complete, "35571", null)]
    [InlineData(ProviderGameSearchCompleteness.ProviderLimited, null, "35571")]
    public async Task DiscoverAsync_SteamGridDbTitleYearMatch_RequiresCurrentIgdbAssociation(
        ProviderGameSearchCompleteness completeness, string? trustedId, string? provisionalId)
    {
        await using var db = _database.CreateContext();
        await Seed(db);
        await AddFamilyRom(db);
        db.DatRoms.Add(MatchedRom(1));
        const string betaRelease = "Game (USA) (Beta 1)";
        AddFamilyRelease(db, 1, 3, betaRelease);
        db.TitleSourceLinks.Add(new TitleSourceLinkEntity { TitleId = 1, SourceEntryId = 3 });
        await db.SaveChangesAsync();
        await db.DatGames.Where(game => !game.IsBios).ExecuteUpdateAsync(update => update.SetProperty(game => game.Year, (string?)null));
        var revision = await AddAutomatic(db, "igdb", "3192");
        var igdb = new ProviderGame("3192", "Game", "https://provider.test/game", Releases: [new(1991, "Test")]);
        var igdbJson = JsonSerializer.Serialize(igdb);
        var states = db.Set<TitleProviderMatchStateEntity>();
        await states.Where(state => state.ProviderId == "igdb")
            .ExecuteUpdateAsync(update => update.SetProperty(state => state.GameJson, igdbJson));
        var provider = Substitute.For<IProviderIdentityAdapter>();
        provider.Id.Returns("steamgriddb");
        provider.IdentityScope.Returns(ProviderIdentityScope.CrossPlatformArtwork);
        provider.Capabilities.Returns(["artwork"]);
        provider.GetAvailabilityAsync(Arg.Any<CancellationToken>()).Returns(new ProviderAvailability(true, true));
        provider.SearchAsync("GAME", Arg.Any<CancellationToken>()).Returns(new ProviderGameSearchResult(
            [new("1", "Game", "https://provider.test/game", Year: 2006), new("35571", "Game", "https://provider.test/game", Year: 1991)],
            completeness, 100));
        var store = CreateFamilyStore(db);
        var artworkMatches = new TitleProviderMatchStore(db, TimeProvider.System);
        var service = new ProviderIdentityService(store, [provider], [], TimeProvider.System, new EfUnitOfWork(db),
            NullLogger<ProviderIdentityService>.Instance);

        await service.DiscoverAsync(1, null, default);

        await provider.Received(1).SearchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        (await store.ReadTrustedAsync(1, "steamgriddb", default)).ShouldBe(trustedId);
        var authorization = (await artworkMatches.ReadAuthorizationAsync(1, "steamgriddb", default))
            .ShouldBeOfType<ProviderMatchAuthorizationResult.Authorized>().Match;
        authorization.GameId.ShouldBe(trustedId ?? provisionalId);
        authorization.IsProvisional.ShouldBe(provisionalId is not null);
        var snapshot = (await store.ReadAsync(1, default)).ShouldNotBeNull();
        snapshot.Facts.Names.ShouldContain(betaRelease);
        snapshot.DatLookupNames[betaRelease].ShouldBe("Game");
        var link = snapshot.Links.Single(link => link.ProviderId == "steamgriddb");
        link.GameId.ShouldBe(trustedId);
        var decision = link.Decision.ShouldNotBeNull();
        decision.ProvisionalGameId.ShouldBe(provisionalId);
        decision.Dependencies.ShouldNotBeNull().ShouldHaveSingleItem().ShouldBe(new("igdb", "3192", revision));
        var updatedRevision = Guid.NewGuid();
        await states.Where(state => state.ProviderId == "igdb")
            .ExecuteUpdateAsync(update => update.SetProperty(state => state.Revision, updatedRevision));
        (await store.ReadTrustedAsync(1, "steamgriddb", default)).ShouldBeNull();
        (await artworkMatches.ReadAuthorizationAsync(1, "steamgriddb", default))
            .ShouldBeOfType<ProviderMatchAuthorizationResult.Blocked>().Reason.ShouldBe(ProviderMatchBlock.NeedsMatch);
    }

    [Fact]
    public async Task Snapshot_BindsFactsAndFingerprintToSameDatabaseSnapshot()
    {
        await using var db = _database.CreateContext();
        await Seed(db);
        var before = await Fingerprint(db);
        var evidence = new MutatingEvidence(_database);
        var store = new ProviderIdentityStore(db, evidence, TimeProvider.System);
        var snapshot = await store.ReadAsync(1, default);
        snapshot.ShouldNotBeNull();
        snapshot.Facts.Name.ShouldBe("Game");
        snapshot.Fingerprint.ShouldBe(before);
        (await Fingerprint(db)).ShouldNotBe(before);
    }

    private sealed class MutatingEvidence(PostgreSqlTestDatabase database)
        : Romd.Admin.Application.Titles.Enrichment.ITitleEnrichmentEvidenceReader
    {
        public async Task<IReadOnlyList<Romd.Admin.Application.Titles.Enrichment.TitleEnrichmentEvidence>> ReadAsync(
            int titleId, CancellationToken cancellationToken = default)
        {
            await using var writer = database.CreateContext();
            await writer.Titles.Where(x => x.Id == titleId).ExecuteUpdateAsync(s => s.SetProperty(x => x.Name, "New Name"), cancellationToken);
            return [];
        }
    }

    private static Task<string> Fingerprint(RomdDbContext db) => db.Titles.Where(x => x.Id == 1)
        .Select(x => ProviderIdentityDatabaseFunctions.Fingerprint(x.Id, ProviderIdentityPolicy.Version)).SingleAsync();

    private static async Task<bool> Trusted(RomdDbContext db, string provider)
    {
        var trusted = await db.Titles.Where(x => x.Id == 1)
            .Select(x => ProviderIdentityDatabaseFunctions.IsTrusted(x.Id, provider, ProviderIdentityPolicy.Version)).SingleAsync();
        var withFingerprint = await db.Database.SqlQuery<bool>($"""
            SELECT romd.provider_identity_trusted_walk(1, {provider}, {ProviderIdentityPolicy.Version}, ARRAY[]::text[],
                romd.provider_identity_fingerprint(1, {ProviderIdentityPolicy.Version})) AS "Value"
            """).SingleAsync();
        withFingerprint.ShouldBe(trusted);
        return trusted;
    }

    private static async Task<Guid> AddAutomatic(RomdDbContext db, string provider, string gameId,
        IReadOnlyList<ProviderIdentityDependency>? dependencies = null)
    {
        var fingerprint = await Fingerprint(db);
        var revision = Guid.NewGuid();
        db.TitleExternalIds.Add(TitleExternalIdEntity.FromDomain(TitleExternalId.CreateNew(1, provider, gameId, 1, createdAt: DateTimeOffset.UnixEpoch)));
        db.Add(new TitleProviderMatchStateEntity
        {
            TitleId = 1,
            ProviderId = provider,
            Revision = revision,
            AutomaticallyTrusted = true,
            InputFingerprint = fingerprint,
            MatcherVersion = ProviderIdentityPolicy.Version,
            GameJson = JsonSerializer.Serialize(new ProviderGame(gameId, "Game", "https://example.com/game")),
            DecisionJson = JsonSerializer.Serialize(new ProviderIdentityDecision("AutoMatched", "test", "test", fingerprint, [], dependencies))
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return revision;
    }

    private static async Task Seed(RomdDbContext db)
    {
        // This fixture also seeds historical schemas before upgrading them.
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO romd."Platforms" ("Id", "Name", "ShortName", "CreatedAt", "CreatedByUserId",
                "Ownership", "BuiltInVersion", "BaseName", "BaseCompactLabel", "CanonicalKey",
                "BaseMonochrome", "Retired", "HasDescriptionOverride", "HasArtworkOverride")
            VALUES (1, 'Test', 'test', 0, '00000000-0000-0000-0000-000000000000',
                'Romd', 1, 'Test', 'Test', 'test', FALSE, FALSE, FALSE, FALSE);
            """);
        db.Titles.Add(new TitleEntity { Id = 1, PlatformId = 1, Name = "Game", NormalizedName = "game", EnrichmentStatus = "None" });
        db.Files.Add(new FileEntityPersistence { Id = 1, Sha256 = Sha256.FromBytes(new byte[Sha256.ByteLength]), Size = 1, SizeOnDisk = 1 });
        db.DatSources.Add(new DatSourceEntity { Id = 1, CatalogSource = new CatalogSourceEntity { Id = 1, Kind = "Dat", Status = "Active" } });
        db.DatFiles.Add(new DatFileEntity
        {
            Id = 1,
            DatSourceId = 1,
            Name = "Test DAT",
            Description = "Test",
            Type = "NoIntro",
            PlatformId = 1,
            OriginalFilename = "test.dat",
            FileId = 1
        });
        db.SourceEntries.AddRange(new SourceEntryEntity { Id = 1, CatalogSourceId = 1, EntryKey = "game", Name = "Game", PlatformId = 1 },
            new SourceEntryEntity { Id = 2, CatalogSourceId = 1, EntryKey = "bios", Name = "BIOS", PlatformId = 1 });
        db.DatGames.AddRange(new DatGameEntity { Id = 1, DatFileId = 1, SourceEntryId = 1, Name = "Game", Year = "1990" },
            new DatGameEntity { Id = 2, DatFileId = 1, SourceEntryId = 2, Name = "BIOS", Year = "1989", IsBios = true });
        db.TitleSourceLinks.AddRange(new TitleSourceLinkEntity { SourceEntryId = 1, TitleId = 1 },
            new TitleSourceLinkEntity { SourceEntryId = 2, TitleId = 1 });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    public void Dispose() => _database.Dispose();
}
