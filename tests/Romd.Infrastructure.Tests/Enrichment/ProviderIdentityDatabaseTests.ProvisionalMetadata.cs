using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.MetadataProviders;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Application.Common.Concurrency;
using Romd.Domain.Catalog;
using Romd.Domain.Catalog.Ratings;
using Romd.Infrastructure.Tests.Helpers;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.MetadataProviders;
using Romd.Persistence.Queries;
using Romd.Persistence.Repositories;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Enrichment;

public sealed partial class ProviderIdentityDatabaseTests
{
    [Theory]
    [InlineData("confirm")]
    [InlineData("replace")]
    [InlineData("reject")]
    [InlineData("stale DAT")]
    [InlineData("new revision")]
    public async Task ProvisionalMetadata_PresentsCurrentGapsAndPromotesOnlyTheConfirmedGame(string action)
    {
        await using var db = _database.CreateContext();
        var (game, match) = await SeedProvisionalMetadataMatchAsync(db);
        var poster = ProvisionalAsset(game.Id, ArtworkRole.Poster, 1);
        poster.SourceId = "igdb";
        var otherGame = ProvisionalAsset("different-game", ArtworkRole.Hero, 2);
        otherGame.SourceId = "igdb";
        var rejected = ProvisionalAsset(game.Id, ArtworkRole.Logo, 3);
        rejected.SourceId = "igdb";
        rejected.IsRejected = true;
        db.ArtworkAssets.AddRange(poster, otherGame, rejected);
        await db.SaveChangesAsync();
        var adapter = Substitute.For<IProviderIdentityAdapter>();
        adapter.Id.Returns("igdb");
        adapter.Name.Returns("IGDB");
        adapter.Capabilities.Returns(["metadata"]);
        adapter.GetAvailabilityAsync(default).Returns(new ProviderAvailability(true, true));
        var selected = action == "replace" ? game with { Id = "43" } : game;
        adapter.ResolveAsync(selected.Id, default).Returns(selected);
        var matches = ProviderMatchTestServices.Create(db, [adapter]);
        var titles = new TitleRepository(db);
        var title = (await titles.GetAggregateAsync(1)).ShouldNotBeNull();
        var stored = new ProvisionalMetadataResult(match, Payload(game.Id));

        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await titles.ApplyProvisionalMetadataStagedAsync(title, [stored]);
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        db.ChangeTracker.Clear();

        (await db.Titles.SingleAsync()).Genre.ShouldBeNull();
        (await db.TitleContentRatings.CountAsync()).ShouldBe(0);
        (await db.Libraries.SingleAsync()).NeedsMaterialization.ShouldBeFalse();
        var detailReader = new TitleDetailReader(db);
        var detail = (await detailReader.GetDetailAsync(1)).ShouldNotBeNull();
        detail.Description.ShouldBe("User description");
        detail.Publisher.ShouldBe("Provisional publisher");
        detail.Developer.ShouldBe("Provisional developer");
        detail.Genre.ShouldBe("Provisional genre");
        detail.ReleaseDate.ShouldBe(new DateOnly(1994, 10, 1));
        detail.Players.ShouldBe(2);
        detail.Rating.ShouldBe(86.5);
        detail.ContentRatings.ShouldHaveSingleItem().Code.ShouldBe("PEGI 12");
        detail.FieldProvenance.ShouldNotBeNull()["Genre"].ShouldBe("igdb");
        (await db.PresentedTitles().Where(value => value.Genre == "Provisional genre").CountAsync()).ShouldBe(1);
        var current = (await matches.GetAsync(1, "igdb", default)).Value;
        current.State.ShouldBe("NeedsReview");
        current.ExternalId.ShouldBeNull();
        var review = current.ProvisionalReview.ShouldNotBeNull();
        review.GameId.ShouldBe(game.Id);
        review.EvidenceCurrent.ShouldBeTrue();
        var fields = review.Fields.ShouldNotBeNull();
        fields.Description.ShouldBeNull();
        fields.Genre.ShouldBe("Provisional genre");
        fields.ReleaseDate.ShouldBe(new DateOnly(1994, 10, 1));
        review.Ratings.ShouldHaveSingleItem().ShouldBe(new(RatingBoard.Pegi, "PEGI 12"));
        review.Artwork.ShouldHaveSingleItem().ShouldBe(new(ArtworkRole.Poster, 1, "large", "v1"));
        var ifMatch = RevisionPrecondition.For(current.Revision.ToString());

        if (action == "stale DAT")
        {
            await db.CatalogSources.ExecuteUpdateAsync(update => update.SetProperty(source => source.Status, "Paused"));
            var refreshed = (await matches.GetAsync(1, "igdb", default)).Value;
            refreshed.Revision.ShouldNotBe(current.Revision);
            refreshed.Decision!.ProvisionalGameId.ShouldBeNull();
            var expired = refreshed.ProvisionalReview.ShouldNotBeNull();
            expired.EvidenceCurrent.ShouldBeFalse();
            expired.Fields.ShouldBeNull();
            expired.Ratings.ShouldBeEmpty();
            expired.Artwork.ShouldBeEmpty();
            (await matches.SetAsync(1, "igdb", selected.Id, ifMatch, default)).FirstError.ShouldBe(ProviderMatchErrors.PreconditionFailed);
            (await matches.UnlinkAsync(1, "igdb", ifMatch, default)).FirstError.ShouldBe(ProviderMatchErrors.PreconditionFailed);
        }
        else if (action == "new revision")
        {
            await db.Set<TitleProviderMatchStateEntity>().ExecuteUpdateAsync(update => update.SetProperty(state => state.Revision, Guid.NewGuid()));
            var refreshed = (await matches.GetAsync(1, "igdb", default)).Value;
            var updated = refreshed.ProvisionalReview.ShouldNotBeNull();
            updated.EvidenceCurrent.ShouldBeTrue();
            updated.Fields.ShouldBeNull();
            updated.Ratings.ShouldBeEmpty();
        }
        else
        {
            var changed = action == "reject"
                ? await matches.UnlinkAsync(1, "igdb", ifMatch, default)
                : await matches.SetAsync(1, "igdb", selected.Id, ifMatch, default);
            changed.IsError.ShouldBeFalse();
            changed.Value.ProvisionalReview.ShouldBeNull();
        }

        db.ChangeTracker.Clear();
        bool confirmed = action == "confirm";
        detail = (await detailReader.GetDetailAsync(1)).ShouldNotBeNull();
        detail.Description.ShouldBe("User description");
        detail.Genre.ShouldBe(confirmed ? "Provisional genre" : null);
        detail.ContentRatings.Count.ShouldBe(confirmed ? 1 : 0);
        (await db.PresentedTitles().Where(value => value.Genre == "Provisional genre").CountAsync())
            .ShouldBe(confirmed ? 1 : 0);
        (await db.Titles.SingleAsync()).Genre.ShouldBe(confirmed ? "Provisional genre" : null);
        (await db.Libraries.SingleAsync()).NeedsMaterialization.ShouldBe(confirmed);
        (await db.TitleMetadataLayers.AnyAsync(value => value.SourceId == "igdb")).ShouldBe(confirmed);
    }

    [Fact]
    public async Task ProvisionalMetadata_PreservesPublishedRatingsForTheSameBoard()
    {
        await using var db = _database.CreateContext();
        var (game, match) = await SeedProvisionalMetadataMatchAsync(db);
        db.TitleContentRatings.Add(TitleContentRatingEntity.FromDomain(1, new ContentRating
        {
            Board = RatingBoard.Esrb,
            Code = "T",
            Designation = RatingDesignation.Rated,
            MinimumAge = 13,
            SourceId = "user"
        }));
        await db.SaveChangesAsync();
        var titles = new TitleRepository(db);
        var title = (await titles.GetAggregateAsync(1)).ShouldNotBeNull();
        var payload = Payload(game.Id) with
        {
            ContentRatings = [new() { Board = RatingBoard.Esrb, RawCode = "E" }, new() { Board = RatingBoard.Pegi, RawCode = "12" }]
        };

        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await titles.ApplyProvisionalMetadataStagedAsync(title, [new(match, payload)]);
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        var ratings = await db.PresentedRatings().Where(value => value.TitleId == 1).ToListAsync();
        ratings.Count.ShouldBe(2);
        ratings.Single(value => value.Board == (int)RatingBoard.Esrb).Code.ShouldBe("T");
        ratings.Single(value => value.Board == (int)RatingBoard.Pegi).Code.ShouldBe("PEGI 12");
    }

    [Theory]
    [InlineData("equivalent match", true)]
    [InlineData("different game", false)]
    [InlineData("changed DAT evidence", false)]
    [InlineData("outdated payload revision", false)]
    public async Task ProvisionalMetadata_ArtworkIdentityRefreshPreservesOnlyReverifiedPayload(string change, bool preservesPayload)
    {
        await using var db = _database.CreateContext();
        await Seed(db);
        await AddFamilyRom(db);
        db.DatRoms.Add(MatchedRom(1));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var game = new ProviderGame("42", "Game", "https://provider.test/game", Platforms: ["Test"], Releases: [new(1990, "Test")]);
        var adapter = Substitute.For<IProviderIdentityAdapter>();
        adapter.Id.Returns("igdb");
        adapter.Capabilities.Returns(["metadata"]);
        adapter.GetAvailabilityAsync(default).Returns(new ProviderAvailability(true, true));
        adapter.SearchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ =>
            new ProviderGameSearchResult([game], ProviderGameSearchCompleteness.ProviderLimited, 50));
        var store = CreateFamilyStore(db);
        var identity = new ProviderIdentityService(store, [adapter], [], TimeProvider.System, new EfUnitOfWork(db),
            NullLogger<ProviderIdentityService>.Instance);
        await identity.DiscoverAsync(1, null, default);
        var before = (await store.ReadAsync(1, default)).ShouldNotBeNull().Links.ShouldHaveSingleItem();
        before.Decision.ShouldNotBeNull().ProvisionalGameId.ShouldBe(game.Id);
        var authorization = new ProviderMatchAuthorization("igdb", game.Id, before.Revision, IsProvisional: true);
        var titles = new TitleRepository(db);
        var title = (await titles.GetAggregateAsync(1)).ShouldNotBeNull();
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await titles.ApplyProvisionalMetadataStagedAsync(title, [new(authorization, Payload(game.Id))]);
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        var detailReader = new TitleDetailReader(db);
        (await detailReader.GetDetailAsync(1)).ShouldNotBeNull().Genre.ShouldBe("Provisional genre");

        await db.Set<TitleProviderMatchStateEntity>().ExecuteUpdateAsync(update =>
            update.SetProperty(state => state.LastAttemptAt, DateTimeOffset.UnixEpoch));
        if (change == "different game")
            game = game with { Id = "43" };
        else if (change == "changed DAT evidence")
            await db.DatGames.Where(release => release.Id == 1).ExecuteUpdateAsync(update =>
                update.SetProperty(release => release.Name, "Game (World)"));
        else if (change == "outdated payload revision")
            await db.Set<TitleProviderMatchStateEntity>().ExecuteUpdateAsync(update =>
                update.SetProperty(state => state.Revision, Guid.NewGuid()));
        db.ChangeTracker.Clear();

        // Artwork refresh rediscovers identity without fetching text again.
        await identity.DiscoverAsync(1, null, default);

        var after = (await store.ReadAsync(1, default)).ShouldNotBeNull().Links.ShouldHaveSingleItem();
        after.Revision.ShouldNotBe(before.Revision);
        (after.InputFingerprint == before.InputFingerprint).ShouldBe(change != "changed DAT evidence");
        after.Decision.ShouldNotBeNull().ProvisionalGameId.ShouldBe(game.Id);
        var detail = (await detailReader.GetDetailAsync(1)).ShouldNotBeNull();
        detail.Genre.ShouldBe(preservesPayload ? "Provisional genre" : null);
        detail.ContentRatings.Count.ShouldBe(preservesPayload ? 1 : 0);
        (await db.Set<TitleProvisionalMetadataEntity>().SingleAsync()).MatchRevision
            .ShouldBe(preservesPayload ? after.Revision : before.Revision);
        (await db.Titles.SingleAsync()).Genre.ShouldBeNull();
    }

    [Fact]
    public async Task ProvisionalMetadata_StaleAuthorizationCannotPublish()
    {
        await using var db = _database.CreateContext();
        var (game, match) = await SeedProvisionalMetadataMatchAsync(db);
        var titles = new TitleRepository(db);
        var title = (await titles.GetAggregateAsync(1)).ShouldNotBeNull();
        await db.Set<TitleProviderMatchStateEntity>().ExecuteUpdateAsync(update => update.SetProperty(state => state.Revision, Guid.NewGuid()));

        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await titles.ApplyProvisionalMetadataStagedAsync(title, [new(match, Payload(game.Id))]);
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        (await db.Set<TitleProvisionalMetadataEntity>().CountAsync()).ShouldBe(0);
    }

    private static TitleMetadataPayload Payload(string gameId) => new()
    {
        ProviderGameId = gameId,
        Description = "Provider description",
        Publisher = "Provisional publisher",
        Developer = "Provisional developer",
        Genre = "Provisional genre",
        ReleaseDate = new DateOnly(1994, 10, 1),
        Players = 2,
        Rating = 86.5,
        ContentRatings = [new() { Board = RatingBoard.Pegi, RawCode = "12" }]
    };

    private static async Task<(ProviderGame Game, ProviderMatchAuthorization Match)> SeedProvisionalMetadataMatchAsync(RomdDbContext db)
    {
        await Seed(db);
        await AddFamilyRom(db);
        db.DatRoms.Add(MatchedRom(1));
        db.Libraries.Add(new LibraryEntity { Name = "Published rules", ConfigurationJson = "{}", NeedsMaterialization = false });
        db.TitleMetadataLayers.Add(TitleMetadataLayerEntity.FromDomain(
            TitleMetadataLayer.CreateNew(1, "user", MetadataSourceType.User,
                new() { Description = "User description" }, DateTimeOffset.UnixEpoch)));
        await db.SaveChangesAsync();
        await db.Titles.ExecuteUpdateAsync(update => update.SetProperty(title => title.Description, "User description"));

        var game = new ProviderGame("42", "Game", "https://provider.test/game", Releases: [new(1994, "Test")]);
        var fingerprint = await Fingerprint(db);
        var decision = new ProviderIdentityDecision("NeedsReview", "Review.", "DAT", fingerprint,
            [new(game, 120, true, [], [])])
        { ProvisionalGameId = game.Id, ProvisionalPolicyVersion = ProvisionalProviderMatchPolicy.MetadataVersion };
        var revision = Guid.NewGuid();
        db.Add(new TitleProviderMatchStateEntity
        {
            TitleId = 1,
            ProviderId = "igdb",
            Revision = revision,
            InputFingerprint = fingerprint,
            MatcherVersion = ProviderIdentityPolicy.Version,
            GameJson = JsonSerializer.Serialize(game),
            DecisionJson = JsonSerializer.Serialize(decision)
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (game, new ProviderMatchAuthorization("igdb", game.Id, revision, IsProvisional: true));
    }
}
