using System.Text.Json;
using Microsoft.EntityFrameworkCore;
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
            await titles.StageProvisionalMetadataAsync(title, [stored]);
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
        var ifMatch = RevisionPrecondition.For(current.Revision.ToString());

        if (action == "stale DAT")
            await db.CatalogSources.ExecuteUpdateAsync(update => update.SetProperty(source => source.Status, "Paused"));
        else if (action == "new revision")
            await db.Set<TitleProviderMatchStateEntity>().ExecuteUpdateAsync(update => update.SetProperty(state => state.Revision, Guid.NewGuid()));
        else
        {
            var changed = action == "reject"
                ? await matches.UnlinkAsync(1, "igdb", ifMatch, default)
                : await matches.SetAsync(1, "igdb", selected.Id, ifMatch, default);
            changed.IsError.ShouldBeFalse();
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
            Board = RatingBoard.Esrb, Code = "T", Designation = RatingDesignation.Rated,
            MinimumAge = 13, SourceId = "user"
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
            await titles.StageProvisionalMetadataAsync(title, [new(match, payload)]);
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        var ratings = await db.PresentedRatings().Where(value => value.TitleId == 1).ToListAsync();
        ratings.Count.ShouldBe(2);
        ratings.Single(value => value.Board == (int)RatingBoard.Esrb).Code.ShouldBe("T");
        ratings.Single(value => value.Board == (int)RatingBoard.Pegi).Code.ShouldBe("PEGI 12");
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
            await titles.StageProvisionalMetadataAsync(title, [new(match, Payload(game.Id))]);
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
