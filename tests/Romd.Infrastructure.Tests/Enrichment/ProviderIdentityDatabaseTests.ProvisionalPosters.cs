using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Artwork.Providers;
using Romd.Admin.Application.MetadataProviders;
using Romd.Application.Common.Concurrency;
using Romd.Domain.Catalog;
using Romd.Persistence.Entities;
using Romd.Persistence.MetadataProviders;
using Romd.Persistence.Repositories;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Enrichment;

public sealed partial class ProviderIdentityDatabaseTests
{
    [Theory]
    [InlineData("confirm")]
    [InlineData("reject")]
    [InlineData("replace")]
    [InlineData("IGDB revision")]
    [InlineData("DAT deactivation")]
    public async Task ProvisionalPoster_PresentsOnlyWhileItsEvidenceIsCurrent(string scenario)
    {
        await using var db = _database.CreateContext();
        await Seed(db);
        await AddFamilyRom(db);
        db.DatRoms.Add(MatchedRom(1));
        await db.SaveChangesAsync();
        var igdbRevision = await AddAutomatic(db, "igdb", "42");
        var game = new ProviderGame("poster-game", "Game", "https://provider.test/game", Year: 1990);
        var fingerprint = await Fingerprint(db);
        var decision = new ProviderIdentityDecision("NeedsReview", "Review the suggested game.", "DAT", fingerprint,
            [new(game, 100, true, [], [])], [new("igdb", "42", igdbRevision)])
        { PosterPolicyVersion = ProvisionalPosterPolicy.Version, ProvisionalGameId = game.Id };
        db.Add(new TitleProviderMatchStateEntity
        {
            TitleId = 1,
            ProviderId = "steamgriddb",
            Revision = Guid.NewGuid(),
            InputFingerprint = fingerprint,
            MatcherVersion = ProviderIdentityPolicy.Version,
            DecisionJson = JsonSerializer.Serialize(decision)
        });
        db.ArtworkAssets.Add(new ArtworkAssetEntity
        {
            Id = 1,
            TitleId = 1,
            Role = ArtworkRole.Poster,
            SourceId = "steamgriddb",
            ProviderGameId = game.Id,
            ProviderAssetId = "poster",
            OriginalFileId = 1,
            ContentVersion = "original",
            ContentType = "image/webp",
            Width = 600,
            Height = 900,
            IsProvisional = true,
            Variants = [new()
            {
                Name = "large", FileId = 1, ContentVersion = "v1", ContentType = "image/webp", Width = 600, Height = 900
            }]
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var store = new TitleProviderMatchStore(db, TimeProvider.System);
        var reader = new ArtworkReader(db);
        var match = (await store.ReadAsync(1, "steamgriddb", ArtworkRole.Poster, default))
            .ShouldBeOfType<ArtworkMatchResolution.Authorized>().Match;
        match.IsProvisional.ShouldBeTrue();
        match.GameId.ShouldBe(game.Id);
        (await store.ReadAsync(1, "steamgriddb", ArtworkRole.Logo, default))
            .ShouldBeOfType<ArtworkMatchResolution.Blocked>();
        (await CreateFamilyStore(db).ReadTrustedAsync(1, "steamgriddb", default)).ShouldBeNull();
        (await store.ReadFactsAsync(1, ["steamgriddb"], default)).States.ShouldHaveSingleItem()
            .Decision.ShouldNotBeNull().ProvisionalGameId.ShouldBe(game.Id);
        (await reader.ResolveAsync([1]))[1].Single(x => x.Role == ArtworkRole.Poster).Asset.ShouldNotBeNull();
        (await reader.FindVariantAsync(1, "large", "v1")).ShouldNotBeNull().IsProvisional.ShouldBeTrue();

        var initialRevision = await store.ReadRevisionAsync(1, "steamgriddb", default);
        if (scenario == "DAT deactivation")
            await db.CatalogSources.ExecuteUpdateAsync(update => update.SetProperty(source => source.Status, "Paused"));
        else if (scenario == "IGDB revision")
            await db.Set<TitleProviderMatchStateEntity>().Where(state => state.ProviderId == "igdb")
                .ExecuteUpdateAsync(update => update.SetProperty(state => state.Revision, Guid.NewGuid()));
        else
        {
            var ifMatch = RevisionPrecondition.For(initialRevision.ToString());
            await using var transaction = await db.Database.BeginTransactionAsync();
            var selected = scenario == "replace" ? game with { Id = "replacement" } : game;
            bool reject = scenario == "reject";
            (await store.StageChangeAsync(1, "steamgriddb", ifMatch, reject ? null : selected, reject, default))
                .IsError.ShouldBeFalse();
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        (await store.ReadRevisionAsync(1, "steamgriddb", default)).ShouldNotBe(initialRevision);
        var asset = await db.ArtworkAssets.SingleAsync();
        var poster = (await reader.ResolveAsync([1]))[1].Single(x => x.Role == ArtworkRole.Poster);
        var variant = await reader.FindVariantAsync(1, "large", "v1");
        (await store.ReadFactsAsync(1, ["steamgriddb"], default)).States.ShouldHaveSingleItem()
            .Decision.ShouldNotBeNull().ProvisionalGameId.ShouldBeNull();
        asset.IsEligible.ShouldBe(scenario == "confirm");
        if (scenario == "confirm")
        {
            asset.IsProvisional.ShouldBeFalse();
            poster.Asset.ShouldNotBeNull().Id.ShouldBe(asset.Id);
            variant.ShouldNotBeNull().IsProvisional.ShouldBeFalse();
        }
        else
        {
            poster.Asset.ShouldBeNull();
            variant.ShouldBeNull();
        }
    }
}
