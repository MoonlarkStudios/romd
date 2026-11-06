using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NSubstitute;
using Romd.Admin.Application.MetadataProviders;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Contracts.Management.MetadataProviders;
using Romd.Domain.Catalog;
using Romd.Domain.Hashing;
using Romd.Domain.ReferenceData;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.MetadataProviders;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

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
        var oldDecision = JsonSerializer.Serialize(new ProviderIdentityDecisionDto("AutoAccepted", "Old truncated search.",
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
        adapter.SearchAsync("GAME", Arg.Any<CancellationToken>()).Returns(new ProviderGameSearchResultDto(
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
        JsonSerializer.Deserialize<ProviderIdentityDecisionDto>(state.DecisionJson!)!.State.ShouldBe("NeedsReview");
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
        var cycle = JsonSerializer.Serialize(new ProviderIdentityDecisionDto("AutoMatched", "test", "test",
            await Fingerprint(db), [], [new("b", "84", secondRevision)]));
        await db.Set<TitleProviderMatchStateEntity>().Where(x => x.ProviderId == "a")
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.DecisionJson, cycle));
        (await Trusted(db, "a")).ShouldBeFalse();
        (await Trusted(db, "b")).ShouldBeFalse();
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
        IReadOnlyList<ProviderIdentityDependencyDto>? dependencies = null)
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
            GameJson = JsonSerializer.Serialize(new ProviderGameDto(gameId, "Game", "https://example.com/game")),
            DecisionJson = JsonSerializer.Serialize(new ProviderIdentityDecisionDto("AutoMatched", "test", "test", fingerprint, [], dependencies))
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return revision;
    }

    private static async Task Seed(RomdDbContext db)
    {
        db.Platforms.Add(new PlatformEntity
        {
            Id = 1,
            Name = "Test",
            ShortName = "test",
            Ownership = ReferenceOwnership.Romd,
            BuiltInVersion = 1,
            BaseName = "Test",
            BaseCompactLabel = "Test",
            CanonicalKey = "test"
        });
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
