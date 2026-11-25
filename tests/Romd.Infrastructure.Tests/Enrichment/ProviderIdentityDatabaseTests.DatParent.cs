using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.MetadataProviders;
using Romd.Domain.Catalog;
using Romd.Domain.Hashing;
using Romd.Persistence;
using Romd.Persistence.Catalog;
using Romd.Persistence.Entities;
using Romd.Persistence.MetadataProviders;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Enrichment;

public sealed partial class ProviderIdentityDatabaseTests
{
    [Fact]
    public async Task DatTitleGrouping_ReviewedFamily_VersionBoundAndIndependentOfProviderLinks()
    {
        await using var db = _database.CreateContext();
        await SeedParentFamilyAsync(db, "Streets of Rage 2", "Streets of Rage II (Japan, Europe)");
        var reader = new DatTitleGroupingReader(db);
        var review = (await reader.ReadAsync(1, default)).ShouldNotBeNull().Suggestion.ShouldNotBeNull();
        review.ParentTitleId.ShouldBe(2);
        review.ReleaseNames.ShouldBe(["Streets of Rage II (Japan, Europe)", "Streets of Rage 2 (USA)"]);
        var repository = new TitleRepository(db);

        await using var transaction = await db.Database.BeginTransactionAsync();
        (await repository.ValidateDatGroupingAsync(2, 1, review.Fingerprint, default)).ShouldBeTrue();
        (await repository.ValidateDatGroupingAsync(1, 2, review.Fingerprint, default)).ShouldBeTrue();
        await db.CatalogSources.ExecuteUpdateAsync(set => set.SetProperty(source => source.Status, "Disabled"));
        (await repository.ValidateDatGroupingAsync(2, 1, review.Fingerprint, default)).ShouldBeFalse();
    }

    [Theory]
    [InlineData("Streets of Rage 2", "Streets of Rage II (Japan, Europe) (En,Ja)", "1346", null)]
    [InlineData("Super Mario World", "Super Mario World (Europe) (Rev 1)", "36886", 1990)]
    public async Task DiscoverAsync_TrustedDatParent_AcceptsCloneAndFencesRetainedArtwork(
        string title, string parentRelease, string gameId, int? providerYear)
    {
        await using var db = _database.CreateContext();
        await SeedParentFamilyAsync(db, title, parentRelease);
        var game = new ProviderGame(gameId, title, "https://provider.test/game", Year: providerYear);
        var adapter = SteamGridDbParentAdapter(game);
        var store = CreateFamilyStore(db);
        var service = new ProviderIdentityService(store, [adapter], [], TimeProvider.System, new EfUnitOfWork(db),
            NullLogger<ProviderIdentityService>.Instance);
        await service.DiscoverAsync(1, null, default);
        (await store.ReadTrustedAsync(1, "steamgriddb", default)).ShouldBeNull();

        await ConfirmParentAsync(db, game);
        await service.DiscoverAsync(1, null, default);

        var snapshot = (await store.ReadAsync(1, default)).ShouldNotBeNull();
        var decision = snapshot.Links.ShouldHaveSingleItem().Decision.ShouldNotBeNull();
        decision.State.ShouldBe("AutoAccepted");
        decision.Explanation.ShouldContain(parentRelease);
        decision.DatParentMatch.ShouldBe(snapshot.DatParent.ShouldNotBeNull().Match);
        (await store.ReadTrustedAsync(1, "steamgriddb", default)).ShouldBe(gameId);
        await adapter.Received(1).ResolveAsync(gameId, Arg.Any<CancellationToken>());

        var poster = ProvisionalAsset(gameId, ArtworkRole.Poster, 1);
        poster.IsProvisional = false;
        poster.IsEligible = true;
        db.ArtworkAssets.Add(poster);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var artwork = new ArtworkReader(db);
        var gallery = new ArtworkCurationRepository(db, TimeProvider.System);
        (await artwork.FindVariantAsync(1, "large", "v1")).ShouldNotBeNull();
        (await gallery.GetGalleryAsync(1, default)).Value.ShouldHaveSingleItem();

        if (providerYear is null)
        {
            var newRevision = Guid.NewGuid();
            await db.Set<TitleProviderMatchStateEntity>().Where(state => state.TitleId == 2)
                .ExecuteUpdateAsync(set => set.SetProperty(state => state.Revision, newRevision)
                    .SetProperty(state => state.SuppressAutomaticMatch, true));
        }
        else
            await db.CatalogSources.ExecuteUpdateAsync(set => set.SetProperty(source => source.Status, "Paused"));

        (await store.ReadTrustedAsync(1, "steamgriddb", default)).ShouldBeNull();
        (await artwork.FindVariantAsync(1, "large", "v1")).ShouldBeNull();
        (await gallery.GetGalleryAsync(1, default)).Value.ShouldBeEmpty();
        await service.DiscoverAsync(1, null, default);
        (await artwork.FindVariantAsync(1, "large", "v1")).ShouldBeNull();
    }

    [Theory]
    [InlineData("no declaration")]
    [InlineData("parent hash removed")]
    [InlineData("preview")]
    [InlineData("numbered beta")]
    [InlineData("prototype with revision")]
    [InlineData("non-game category")]
    public async Task DiscoverAsync_UnverifiedParentFamily_StaysInReview(string scenario)
    {
        await using var db = _database.CreateContext();
        await SeedParentFamilyAsync(db, "Super Mario World", "Super Mario World (Europe) (Rev 1)");
        var game = new ProviderGame("36886", "Super Mario World", "https://provider.test/game", Year: 1990);
        await ConfirmParentAsync(db, game);
        switch (scenario)
        {
            case "no declaration":
                await db.DatGames.Where(entry => entry.Id == 1)
                    .ExecuteUpdateAsync(set => set.SetProperty(entry => entry.CloneOfId, (string?)null));
                break;
            case "parent hash removed":
                await db.DatRoms.Where(rom => rom.DatGameId == 3)
                    .ExecuteUpdateAsync(set => set.SetProperty(rom => rom.RomFileId, (int?)null));
                break;
            case "preview":
                await db.DatGames.Where(entry => entry.Id == 1)
                    .ExecuteUpdateAsync(set => set.SetProperty(entry => entry.Name, "Super Mario World (USA) (beta)"));
                break;
            case "numbered beta":
                await db.DatGames.Where(entry => entry.Id == 1)
                    .ExecuteUpdateAsync(set => set.SetProperty(entry => entry.Name, "Super Mario World (USA) (Beta 1)"));
                break;
            case "prototype with revision":
                await db.DatGames.Where(entry => entry.Id == 3)
                    .ExecuteUpdateAsync(set => set.SetProperty(entry => entry.Name, "Super Mario World (Europe) (Proto) (Rev 1)"));
                break;
            case "non-game category":
                await db.DatGames.Where(entry => entry.Id == 3)
                    .ExecuteUpdateAsync(set => set.SetProperty(entry => entry.Category, "Applications"));
                break;
        }
        var adapter = SteamGridDbParentAdapter(game);
        var store = CreateFamilyStore(db);
        var service = new ProviderIdentityService(store, [adapter], [], TimeProvider.System, new EfUnitOfWork(db),
            NullLogger<ProviderIdentityService>.Instance);

        await service.DiscoverAsync(1, null, default);

        var snapshot = (await store.ReadAsync(1, default)).ShouldNotBeNull();
        snapshot.DatParent.ShouldBeNull();
        snapshot.Links.ShouldHaveSingleItem().Decision.ShouldNotBeNull().State.ShouldBe("NeedsReview");
        (await store.ReadTrustedAsync(1, "steamgriddb", default)).ShouldBeNull();
        await adapter.DidNotReceive().ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DiscoverAsync_ParentAssociationChangesDuringProviderLookup_DoesNotPublishStaleMatch()
    {
        await using var db = _database.CreateContext();
        await SeedParentFamilyAsync(db, "Super Mario World", "Super Mario World (Europe) (Rev 1)");
        var game = new ProviderGame("36886", "Super Mario World", "https://provider.test/game", Year: 1990);
        await ConfirmParentAsync(db, game);
        var adapter = SteamGridDbParentAdapter(game);
        adapter.ResolveAsync(game.Id, Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            await using var writer = _database.CreateContext();
            var revision = Guid.NewGuid();
            await writer.Set<TitleProviderMatchStateEntity>().Where(state => state.TitleId == 2)
                .ExecuteUpdateAsync(set => set.SetProperty(state => state.Revision, revision));
            return ErrorOr.ErrorOrFactory.From(game);
        });
        var store = CreateFamilyStore(db);
        var service = new ProviderIdentityService(store, [adapter], [], TimeProvider.System, new EfUnitOfWork(db),
            NullLogger<ProviderIdentityService>.Instance);

        await service.DiscoverAsync(1, null, default);

        (await store.ReadAsync(1, default)).ShouldNotBeNull().Links.ShouldBeEmpty();
    }

    private static IProviderIdentityAdapter SteamGridDbParentAdapter(ProviderGame game)
    {
        var adapter = Substitute.For<IProviderIdentityAdapter>();
        adapter.Id.Returns("steamgriddb");
        adapter.IdentityScope.Returns(ProviderIdentityScope.CrossPlatformArtwork);
        adapter.Capabilities.Returns(["artwork"]);
        adapter.GetAvailabilityAsync(Arg.Any<CancellationToken>()).Returns(new ProviderAvailability(true, true));
        adapter.SearchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(
            new ProviderGameSearchResult([game], ProviderGameSearchCompleteness.ProviderLimited, 100));
        adapter.ResolveAsync(game.Id, Arg.Any<CancellationToken>()).Returns(ErrorOr.ErrorOrFactory.From(game));
        return adapter;
    }

    private static async Task SeedParentFamilyAsync(RomdDbContext db, string title, string parentRelease)
    {
        await Seed(db);
        await AddFamilyRom(db);
        await db.Titles.Where(entry => entry.Id == 1).ExecuteUpdateAsync(set => set.SetProperty(entry => entry.Name, title));
        await db.DatGames.Where(entry => entry.Id == 1).ExecuteUpdateAsync(set => set
            .SetProperty(entry => entry.CloneOfId, "parent")
            .SetProperty(entry => entry.Year, (string?)null)
            .SetProperty(entry => entry.Category, "Games")
            .SetProperty(entry => entry.DevelopmentStatus, "Stable")
            .SetProperty(entry => entry.Name, title + " (USA)"));
        AddFamilyRelease(db, 1, 3, parentRelease, declaredId: "parent").Category = "Games";
        db.Titles.Add(new TitleEntity { Id = 2, PlatformId = 1, Name = parentRelease, NormalizedName = "parent", EnrichmentStatus = "None" });
        db.TitleSourceLinks.Add(new TitleSourceLinkEntity { TitleId = 2, SourceEntryId = 3 });
        db.DatRoms.Add(MatchedRom(1));
        var parentHash = Sha1.FromBytes(HashBytes(Sha1.ByteLength, 51));
        db.Files.Add(new FileEntityPersistence { Id = 51, Sha256 = Sha256.FromBytes(HashBytes(Sha256.ByteLength, 51)), Size = 1, SizeOnDisk = 1 });
        db.StageOriginalRom(new OriginalRomFixture
        {
            Id = 51,
            FileId = 51,
            OriginalFilename = "parent.rom",
            Sha1 = parentHash,
            Md5 = Md5.FromBytes(HashBytes(Md5.ByteLength, 51)),
            Crc32 = Crc32.FromBytes(HashBytes(Crc32.ByteLength, 51))
        });
        await db.PublishOriginalRomsAsync();
        db.DatRoms.Add(new DatRomEntity { DatGameId = 3, Name = "parent.rom", Sha1 = parentHash, Size = 1, RomFileId = 51 });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static async Task ConfirmParentAsync(RomdDbContext db, ProviderGame game)
    {
        var store = new TitleProviderMatchStore(db, TimeProvider.System);
        var version = await store.ReadRevisionAsync(2, "steamgriddb", default);
        await using var transaction = await db.Database.BeginTransactionAsync();
        var condition = Romd.Application.Common.Concurrency.RevisionPrecondition.For(version.ToString());
        (await store.StageChangeAsync(2, "steamgriddb", condition, game, false, default)).IsError.ShouldBeFalse();
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        db.ChangeTracker.Clear();
    }
}
