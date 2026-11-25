using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Romd.Admin.Application.MetadataProviders;
using Romd.Admin.Application.Search;
using Romd.Application.Common.Concurrency;
using Romd.Domain.Catalog;
using Romd.Persistence;
using Romd.Persistence.Artwork;
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
    public async Task MissingArtwork_UsesCurrentProvisionalAssetsInCatalogAndCompletionPreview()
    {
        await using var db = _database.CreateContext();
        var game = await SeedProvisionalMatchAsync(db);
        var roles = Enum.GetValues<ArtworkRole>();
        db.ArtworkAssets.AddRange(roles.Select((role, index) => ProvisionalAsset(game.Id, role, index + 1)));
        await db.SaveChangesAsync();

        var catalog = new SearchRepository(db);
        var completion = new ArtworkCompletionStore(db, TimeProvider.System);
        await AssertMissingAsync(0);

        await db.CatalogSources.ExecuteUpdateAsync(update =>
            update.SetProperty(source => source.Status, nameof(CatalogSourceStatus.Disabled)));
        await AssertMissingAsync(1);

        async Task AssertMissingAsync(int expected)
        {
            var preview = (await completion.PreviewAsync([1], default)).Value;
            preview.Roles.Count.ShouldBe(roles.Length);
            foreach (var role in roles)
            {
                preview.Roles.Single(result => result.Role == role.ToString()).Count.ShouldBe(expected);
                (await catalog.ScopeTitles(null, new TitleSearchFilters(ArtworkStatus: role.ToString()))
                    .CountAsync(title => title.Id == 1)).ShouldBe(expected);
            }
        }
    }

    [Theory]
    [InlineData("confirm")]
    [InlineData("reject")]
    [InlineData("replace")]
    [InlineData("IGDB revision")]
    [InlineData("DAT deactivation")]
    public async Task ProvisionalArtwork_EachRoleUsesTheCurrentProviderMatch(string scenario)
    {
        await using var db = _database.CreateContext();
        var game = await SeedProvisionalMatchAsync(db);
        var roles = Enum.GetValues<ArtworkRole>();
        db.ArtworkAssets.AddRange(roles.Select((role, index) => ProvisionalAsset(game.Id, role, index + 1)));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var store = new TitleProviderMatchStore(db, TimeProvider.System);
        var reader = new ArtworkReader(db);
        var match = (await store.ReadAuthorizationAsync(1, "steamgriddb", default))
            .ShouldBeOfType<ProviderMatchAuthorizationResult.Authorized>().Match;
        match.IsProvisional.ShouldBeTrue();
        match.GameId.ShouldBe(game.Id);
        (await CreateFamilyStore(db).ReadTrustedAsync(1, "steamgriddb", default)).ShouldBeNull();
        (await store.ReadFactsAsync(1, ["steamgriddb"], default)).States.ShouldHaveSingleItem()
            .Decision.ShouldNotBeNull().ProvisionalGameId.ShouldBe(game.Id);
        (await reader.ResolveAsync([1]))[1].ShouldAllBe(x => x.Asset != null);
        for (int id = 1; id <= roles.Length; id++)
            (await reader.FindVariantAsync(id, "large", "v1")).ShouldNotBeNull().IsProvisional.ShouldBeTrue();

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
        var assets = await db.ArtworkAssets.OrderBy(asset => asset.Id).ToArrayAsync();
        var artwork = (await reader.ResolveAsync([1]))[1];
        (await store.ReadFactsAsync(1, ["steamgriddb"], default)).States.ShouldHaveSingleItem()
            .Decision.ShouldNotBeNull().ProvisionalGameId.ShouldBeNull();
        if (scenario == "confirm")
        {
            assets.Length.ShouldBe(roles.Length);
            assets.ShouldAllBe(asset => asset.IsEligible && !asset.IsProvisional);
            artwork.Select(value => value.Asset.ShouldNotBeNull().Id).ShouldBe(assets.Select(asset => asset.Id));
            foreach (var asset in assets)
                (await reader.FindVariantAsync(asset.Id, "large", "v1")).ShouldNotBeNull().IsProvisional.ShouldBeFalse();
        }
        else
        {
            assets.Length.ShouldBe(scenario is "replace" or "reject" ? 0 : roles.Length);
            assets.ShouldAllBe(asset => asset.IsProvisional && !asset.IsEligible);
            artwork.ShouldAllBe(value => value.Asset == null);
            for (int id = 1; id <= roles.Length; id++)
                (await reader.FindVariantAsync(id, "large", "v1")).ShouldBeNull();
        }
    }

    [Fact]
    public async Task Upgrade_FromPosterOnlyMigration_KeepsTheEarlierDecisionAndPosterVisible()
    {
        await using var database = PostgreSqlTestDatabase.CreateEmpty();
        await using (var db = database.CreateContext())
        {
            await db.GetService<IMigrator>().MigrateAsync("20261123030000_ProvisionalSteamGridDbPosters");
            var game = await SeedProvisionalMatchAsync(db);
            db.ArtworkAssets.Add(ProvisionalAsset(game.Id, ArtworkRole.Poster, 1));
            await db.SaveChangesAsync();
        }

        await database.ProvisionAsync();

        await using var observer = database.CreateContext();
        var reader = new ArtworkReader(observer);
        (await reader.ResolveAsync([1]))[1].Single(x => x.Role == ArtworkRole.Poster).Asset.ShouldNotBeNull();
        (await reader.FindVariantAsync(1, "large", "v1")).ShouldNotBeNull().IsProvisional.ShouldBeTrue();
    }

    private static async Task<ProviderGame> SeedProvisionalMatchAsync(RomdDbContext db)
    {
        await Seed(db);
        await AddFamilyRom(db);
        db.DatRoms.Add(MatchedRom(1));
        await db.SaveChangesAsync();
        var igdbRevision = await AddAutomatic(db, "igdb", "42");
        var game = new ProviderGame("poster-game", "Game", "https://provider.test/game", Year: 1990);
        var fingerprint = await Fingerprint(db);
        var decision = new ProviderIdentityDecision("NeedsReview", "Review the suggested game.", "DAT", fingerprint,
            [new(game, 100, true, [], [])], [new("igdb", "42", igdbRevision)])
        { ProvisionalPolicyVersion = ProvisionalProviderMatchPolicy.Version, ProvisionalGameId = game.Id };
        db.Add(new TitleProviderMatchStateEntity
        {
            TitleId = 1,
            ProviderId = "steamgriddb",
            Revision = Guid.NewGuid(),
            InputFingerprint = fingerprint,
            MatcherVersion = ProviderIdentityPolicy.Version,
            DecisionJson = JsonSerializer.Serialize(decision)
        });
        await db.SaveChangesAsync();
        return game;
    }

    private static ArtworkAssetEntity ProvisionalAsset(string gameId, ArtworkRole role, int id) =>
        new()
        {
            Id = id,
            TitleId = 1,
            Role = role,
            SourceId = "steamgriddb",
            ProviderGameId = gameId,
            ProviderAssetId = role.ToString(),
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
        };
}
