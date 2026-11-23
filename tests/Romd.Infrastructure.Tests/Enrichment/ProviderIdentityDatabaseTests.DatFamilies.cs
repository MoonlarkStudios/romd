using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.MetadataProviders;
using Romd.Contracts.Management.MetadataProviders;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Dat;
using Romd.Infrastructure.Enrichment;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.MetadataProviders;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Enrichment;

public sealed partial class ProviderIdentityDatabaseTests
{
    private static readonly Sha1 FamilyRomSha1 = Sha1.FromBytes(HashBytes(Sha1.ByteLength, 50));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DiscoverAsync_RenamedClone_CanonicalProviderGameRequiresHashBackedFamilySearch(bool linked)
    {
        await using var db = _database.CreateContext();
        await Seed(db);
        await AddFamilyRom(db);
        AddFamilyRelease(db, 1, 3, "Original Release (USA)", declaredId: "root");
        await db.DatGames.Where(game => game.Id == 1)
            .ExecuteUpdateAsync(update => update.SetProperty(game => game.CloneOfId, "root"));
        db.DatRoms.Add(MatchedRom(1, linked: false));
        await db.SaveChangesAsync();
        if (linked)
            await new DatRepository(db, TimeProvider.System).LinkDatRomsToRomFileAsync(FamilyRomSha1, 50);

        var provider = Substitute.For<IProviderIdentityAdapter>();
        provider.Id.Returns("igdb");
        provider.Capabilities.Returns(["metadata"]);
        provider.GetAvailabilityAsync(Arg.Any<CancellationToken>()).Returns(new ProviderAvailability(true, true));
        var game = new ProviderGame("42", "Original Release", "https://provider.test/game", Year: 1990, Platforms: ["Test"], Aliases: ["Game"]);
        var queries = new List<string>();
        provider.SearchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            string query = call.ArgAt<string>(0);
            queries.Add(query);
            return new ProviderGameSearchResult(query == "ORIGINAL RELEASE" ? [game] : [], ProviderGameSearchCompleteness.Complete, 500);
        });
        var store = CreateFamilyStore(db);
        var identity = new ProviderIdentityService(store, [provider], [], TimeProvider.System, new EfUnitOfWork(db),
            NullLogger<ProviderIdentityService>.Instance);

        await identity.DiscoverAsync(1, null, default);

        queries.ShouldBe(linked ? ["GAME", "ORIGINAL RELEASE"] : ["GAME"]);
        (await store.ReadTrustedAsync(1, "igdb", default)).ShouldBe(linked ? "42" : null);
        var snapshot = (await store.ReadAsync(1, default)).ShouldNotBeNull();
        snapshot.Links.ShouldHaveSingleItem().Decision.ShouldNotBeNull().State.ShouldBe(linked ? "AutoAccepted" : "NoMatch");
    }

    [Fact]
    public async Task ReadAsync_StoredSha1MatchesConvergeOnRoot_UsesMatchedReleaseAndRootNames()
    {
        await using var db = _database.CreateContext();
        await Seed(db);
        await AddFamilyRom(db);
        AddFamilyRelease(db, 1, 3, "Root (USA)", declaredId: "root");
        AddFamilyRelease(db, 1, 4, "Intermediate (Europe)", cloneOfId: "root");
        AddFamilyRelease(db, 1, 5, "Other Release (Japan)", cloneOf: "Root (USA)", cloneOfId: "root");
        await db.DatGames.Where(g => g.Id == 1).ExecuteUpdateAsync(update => update
            .SetProperty(g => g.CloneOf, "Intermediate (Europe)")
            .SetProperty(g => g.RomOf, "BIOS"));
        db.DatRoms.AddRange(MatchedRom(1, linked: false), MatchedRom(2, linked: false), MatchedRom(5, linked: false));
        await db.SaveChangesAsync();
        var store = CreateFamilyStore(db);
        var unmatched = (await store.ReadAsync(1, default)).ShouldNotBeNull();
        unmatched.DatFamily.ShouldBeNull();
        await AddAutomatic(db, "a", "42");

        await new DatRepository(db, TimeProvider.System).LinkDatRomsToRomFileAsync(FamilyRomSha1, 50);

        var matched = (await store.ReadAsync(1, default)).ShouldNotBeNull();
        matched.DatFamily.ShouldNotBeNull().RequiresReview.ShouldBeFalse();
        matched.DatFamily!.Names.ShouldBe(["Root (USA)", "Game", "Other Release (Japan)"]);
        matched.Facts.Names.ShouldBe(["Game"]);
        matched.Fingerprint.ShouldNotBe(unmatched.Fingerprint);
        (await store.ReadTrustedAsync(1, "a", default)).ShouldBeNull();
    }

    [Fact]
    public async Task ReadAsync_LinkWithoutMatchingSha1_UsesOrdinaryEvidence()
    {
        await using var db = _database.CreateContext();
        await Seed(db);
        await AddFamilyRom(db);
        var original = await Fingerprint(db);
        var claimed = MatchedRom(1);
        claimed.Sha1 = Sha1.FromBytes(HashBytes(Sha1.ByteLength, 51));
        var withoutHash = MatchedRom(1);
        withoutHash.Name = "no-hash.rom";
        withoutHash.Sha1 = null;
        db.DatRoms.AddRange(claimed, withoutHash, MatchedRom(2));
        await db.SaveChangesAsync();

        var snapshot = (await CreateFamilyStore(db).ReadAsync(1, default)).ShouldNotBeNull();

        snapshot.DatFamily.ShouldBeNull();
        snapshot.Fingerprint.ShouldBe(original);
    }

    [Theory]
    [InlineData("missing ancestor")]
    [InlineData("cycle")]
    [InlineData("duplicate ID")]
    [InlineData("conflicting references")]
    [InlineData("BIOS parent")]
    [InlineData("competing release")]
    public async Task ReadAsync_AmbiguousHashBackedReleases_RequiresReview(string scenario)
    {
        await using var db = _database.CreateContext();
        await Seed(db);
        await AddFamilyRom(db);
        var root = AddFamilyRelease(db, 1, 3, "Root", declaredId: "root");
        var other = AddFamilyRelease(db, 1, 4, "Other title");
        var game = await db.DatGames.AsTracking().SingleAsync(game => game.Id == 1);
        game.CloneOf = "Root";
        db.DatRoms.Add(MatchedRom(1));
        switch (scenario)
        {
            case "missing ancestor":
                root.CloneOf = "Missing";
                break;
            case "cycle":
                root.CloneOf = "Game";
                break;
            case "duplicate ID":
                game.CloneOfId = "root";
                other.DeclaredId = "root";
                break;
            case "conflicting references":
                game.CloneOfId = "other";
                other.DeclaredId = "other";
                break;
            case "BIOS parent":
                game.CloneOf = "BIOS";
                break;
            case "competing release":
                // A matching ROM under a different source entry may be assigned to
                // another title. All linked releases on the system must agree.
                db.DatRoms.Add(MatchedRom(4));
                break;
        }
        await db.SaveChangesAsync();

        var snapshot = (await CreateFamilyStore(db).ReadAsync(1, default)).ShouldNotBeNull();

        snapshot.DatFamily.ShouldNotBeNull().RequiresReview.ShouldBeTrue();
        snapshot.DatFamily!.Names.ShouldBeEmpty();
    }

    [Fact]
    public async Task ReadAsync_SharedHashInTwoDatSources_RequiresReviewUntilCompetingSourceIsPaused()
    {
        await using var db = _database.CreateContext();
        await Seed(db);
        await AddFamilyRom(db);
        db.DatRoms.Add(MatchedRom(1));
        await db.SaveChangesAsync();
        var store = CreateFamilyStore(db);
        var original = (await store.ReadAsync(1, default)).ShouldNotBeNull();
        original.DatFamily.ShouldNotBeNull().Names.ShouldBe(["Game"]);
        await AddAutomatic(db, "a", "42");

        db.DatSources.Add(new DatSourceEntity
        {
            Id = 2,
            CatalogSource = new CatalogSourceEntity { Id = 2, Kind = "Dat", Status = "Active" }
        });
        db.SourceEntries.Add(new SourceEntryEntity { Id = 3, CatalogSourceId = 2, EntryKey = "game", Name = "Game", PlatformId = 1 });
        await AddDatVersion(db, 2, DatFileLifecycle.Active, 1990, datSourceId: 2, sourceEntryId: 3);
        db.DatRoms.Add(MatchedRom(102));
        await db.SaveChangesAsync();

        var competing = (await store.ReadAsync(1, default)).ShouldNotBeNull();
        competing.DatFamily.ShouldNotBeNull().RequiresReview.ShouldBeTrue();
        competing.Fingerprint.ShouldNotBe(original.Fingerprint);
        (await store.ReadTrustedAsync(1, "a", default)).ShouldBeNull();

        await db.CatalogSources.Where(source => source.Id == 2)
            .ExecuteUpdateAsync(update => update.SetProperty(source => source.Status, "Paused"));

        var paused = (await store.ReadAsync(1, default)).ShouldNotBeNull();
        paused.DatFamily.ShouldNotBeNull().Names.ShouldBe(["Game"]);
        paused.Fingerprint.ShouldBe(original.Fingerprint);
        (await store.ReadTrustedAsync(1, "a", default)).ShouldBe("42");
    }

    [Fact]
    public async Task ReadAsync_ReplacementChangesOnlyCloneRoot_OnlyActivationInvalidatesHashBackedTrust()
    {
        await using var db = _database.CreateContext();
        await Seed(db);
        await AddFamilyRom(db);
        AddFamilyRelease(db, 1, 4, "Published root", declaredId: "family-root");
        await db.DatGames.Where(g => g.Id == 1).ExecuteUpdateAsync(update => update.SetProperty(g => g.CloneOfId, "family-root"));
        db.DatRoms.Add(MatchedRom(1));
        await db.SaveChangesAsync();
        var store = CreateFamilyStore(db);
        var original = (await store.ReadAsync(1, default)).ShouldNotBeNull();
        original.DatFamily.ShouldNotBeNull().Names.ShouldBe(["Published root", "Game"]);
        await AddAutomatic(db, "a", "42");

        await AddDatVersion(db, 2, DatFileLifecycle.PendingActivation, 1990);
        AddFamilyRelease(db, 2, 5, "Pending root", declaredId: "family-root");
        await db.DatGames.Where(g => g.Id == 102).ExecuteUpdateAsync(update => update.SetProperty(g => g.CloneOfId, "family-root"));
        db.DatRoms.Add(MatchedRom(102));
        await db.SaveChangesAsync();

        var staged = (await store.ReadAsync(1, default)).ShouldNotBeNull();
        staged.DatFamily.ShouldNotBeNull().Names.ShouldBe(["Published root", "Game"]);
        staged.Fingerprint.ShouldBe(original.Fingerprint);
        (await store.ReadTrustedAsync(1, "a", default)).ShouldBe("42");

        await ActivateVersion(2);

        var active = (await store.ReadAsync(1, default)).ShouldNotBeNull();
        active.DatFamily.ShouldNotBeNull().Names.ShouldBe(["Pending root", "Game"]);
        active.Facts.Names.ShouldBe(original.Facts.Names);
        active.Facts.Years.ShouldBe(original.Facts.Years);
        active.Fingerprint.ShouldNotBe(original.Fingerprint);
        (await store.ReadTrustedAsync(1, "a", default)).ShouldBeNull();
    }

    [Fact]
    public async Task Provision_WithExistingHashMatchAndLongParentId_ResolvesFamilyAndInvalidatesPreviousDecision()
    {
        await using var database = PostgreSqlTestDatabase.CreateEmpty();
        await using var db = database.CreateContext();
        var migrator = db.Database.GetService<IMigrator>();
        const string previousMigration = "20261123010000_ActiveDatProviderIdentity";
        await migrator.MigrateAsync(previousMigration);
        await Seed(db);
        await AddFamilyRom(db);
        // Existing custom DAT identifiers can be longer than a B-tree index value.
        string declaredId = string.Concat(Enumerable.Range(1, 128)
            .Select(index => Convert.ToHexString(SHA256.HashData(BitConverter.GetBytes(index)))));
        AddFamilyRelease(db, 1, 3, "Root", declaredId: declaredId);
        await db.DatGames.Where(g => g.Id == 1).ExecuteUpdateAsync(update => update.SetProperty(g => g.CloneOfId, declaredId));
        db.DatRoms.Add(MatchedRom(1));
        await db.SaveChangesAsync();
        await AddAutomatic(db, "a", "42");
        string previous = await Fingerprint(db);

        await database.ProvisionAsync();

        var upgraded = (await CreateFamilyStore(db).ReadAsync(1, default)).ShouldNotBeNull();
        upgraded.DatFamily.ShouldNotBeNull().Names.ShouldBe(["Root", "Game"]);
        upgraded.Fingerprint.ShouldNotBe(previous);
        (await Trusted(db, "a")).ShouldBeFalse();

        await migrator.MigrateAsync(previousMigration);
        (await Fingerprint(db)).ShouldBe(previous);
    }

    private static ProviderIdentityStore CreateFamilyStore(RomdDbContext db) =>
        new(db, new TitleEnrichmentEvidenceReader(db), TimeProvider.System);

    private static async Task AddFamilyRom(RomdDbContext db)
    {
        db.Files.Add(new FileEntityPersistence
        {
            Id = 50,
            Sha256 = Sha256.FromBytes(HashBytes(Sha256.ByteLength, 50)),
            Size = 1,
            SizeOnDisk = 1
        });
        db.StageOriginalRom(new OriginalRomFixture
        {
            Id = 50,
            FileId = 50,
            OriginalFilename = "family.rom",
            Sha1 = FamilyRomSha1,
            Md5 = Md5.FromBytes(HashBytes(Md5.ByteLength, 50)),
            Crc32 = Crc32.FromBytes(HashBytes(Crc32.ByteLength, 50))
        });
        await db.PublishOriginalRomsAsync();
    }

    private static DatGameEntity AddFamilyRelease(RomdDbContext db, int versionId, int gameId, string name,
        string? declaredId = null, string? cloneOf = null, string? cloneOfId = null)
    {
        db.SourceEntries.Add(new SourceEntryEntity { Id = gameId, CatalogSourceId = 1, EntryKey = name, Name = name, PlatformId = 1 });
        return db.DatGames.Add(new DatGameEntity
        {
            Id = gameId,
            DatFileId = versionId,
            SourceEntryId = gameId,
            Name = name,
            DeclaredId = declaredId,
            CloneOf = cloneOf,
            CloneOfId = cloneOfId
        }).Entity;
    }

    private static DatRomEntity MatchedRom(int gameId, bool linked = true) => new()
    {
        DatGameId = gameId,
        Name = "family.rom",
        Sha1 = FamilyRomSha1,
        Size = 1,
        RomFileId = linked ? 50 : null
    };

    private static byte[] HashBytes(int length, byte marker)
    {
        var bytes = new byte[length];
        bytes[0] = marker;
        return bytes;
    }
}
