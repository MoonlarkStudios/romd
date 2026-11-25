using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.MetadataProviders;
using Romd.Domain.Catalog;
using Romd.Domain.ReferenceData;
using Romd.Domain.Source.Platform;
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
    private static readonly ProviderGame SuperMarioWorldArtwork = new("36886", "Super Mario World", "https://provider.test/game", Year: 1990);

    [Theory]
    [InlineData(ProviderGameSearchCompleteness.Complete, "alias renamed")]
    [InlineData(ProviderGameSearchCompleteness.Complete, "alias deleted")]
    [InlineData(ProviderGameSearchCompleteness.Complete, "IGDB revision changed")]
    [InlineData(ProviderGameSearchCompleteness.ProviderLimited, "alias renamed")]
    [InlineData(ProviderGameSearchCompleteness.ProviderLimited, "alias deleted")]
    [InlineData(ProviderGameSearchCompleteness.ProviderLimited, "IGDB revision changed")]
    public async Task DiscoverAsync_CanonicalTitleWithOwnedAlias_SelectsAndRevokesArtworkWhenEvidenceChanges(
        ProviderGameSearchCompleteness completeness, string scenario)
    {
        await using var db = _database.CreateContext();
        var igdbRevision = await SeedArtworkPlatformAliasAsync(db, ReferenceOwnership.Romd, "Super Famicom");
        var store = CreateFamilyStore(db);
        var service = CreateAliasDiscovery(db, store, SuperMarioWorldSearch(completeness,
            SuperMarioWorldArtwork with { Id = "1", Year = 2006 }, SuperMarioWorldArtwork));
        (await store.ReadAsync(1, default)).ShouldNotBeNull().DatParent.ShouldBeNull();

        await service.DiscoverAsync(1, null, default);

        bool isProvisional = completeness == ProviderGameSearchCompleteness.ProviderLimited;
        var authorization = new TitleProviderMatchStore(db, TimeProvider.System);
        var match = (await store.ReadAsync(1, default)).ShouldNotBeNull().Links.Single(link => link.ProviderId == "steamgriddb");
        var decision = match.Decision.ShouldNotBeNull();
        decision.State.ShouldBe(isProvisional ? "NeedsReview" : "AutoAccepted");
        decision.Candidates[0].Game.Id.ShouldBe(SuperMarioWorldArtwork.Id);
        decision.ProvisionalGameId.ShouldBe(isProvisional ? SuperMarioWorldArtwork.Id : null);
        decision.Explanation.ShouldContain("Super Famicom");
        decision.ArtworkPlatformAliases.ShouldBe([new ArtworkPlatformAlias(1, "Super Famicom")]);
        decision.Dependencies.ShouldBe([new ProviderIdentityDependency("igdb", "1070", igdbRevision)]);
        var selected = (await authorization.ReadAuthorizationAsync(1, "steamgriddb", default))
            .ShouldBeOfType<ProviderMatchAuthorizationResult.Authorized>().Match;
        selected.IsProvisional.ShouldBe(isProvisional);
        selected.GameId.ShouldBe(SuperMarioWorldArtwork.Id);
        match.GameId.ShouldBe(isProvisional ? null : SuperMarioWorldArtwork.Id);
        (await store.ReadTrustedAsync(1, "steamgriddb", default)).ShouldBe(match.GameId);
        var poster = ProvisionalAsset(SuperMarioWorldArtwork.Id, ArtworkRole.Poster, 1);
        poster.IsProvisional = isProvisional;
        poster.IsEligible = !isProvisional;
        db.ArtworkAssets.Add(poster);
        await db.SaveChangesAsync();
        var artwork = new ArtworkReader(db);
        var gallery = new ArtworkCurationRepository(db, TimeProvider.System);
        (await artwork.FindVariantAsync(1, "large", "v1")).ShouldNotBeNull().IsProvisional.ShouldBe(isProvisional);
        (await artwork.ResolveAsync([1]))[1].Single(value => value.Role == ArtworkRole.Poster).Asset.ShouldNotBeNull().Id.ShouldBe(1);
        (await gallery.GetGalleryAsync(1, default)).Value.Count.ShouldBe(isProvisional ? 0 : 1);

        switch (scenario)
        {
            case "alias renamed":
                await db.PlatformAliases.ExecuteUpdateAsync(update => update
                    .SetProperty(alias => alias.Value, "Super Famicom Mini")
                    .SetProperty(alias => alias.NormalizedValue, "super famicom mini"));
                break;
            case "alias deleted":
                await db.PlatformAliases.ExecuteDeleteAsync();
                break;
            case "IGDB revision changed":
                var revision = Guid.NewGuid();
                await db.Set<TitleProviderMatchStateEntity>().Where(state => state.ProviderId == "igdb")
                    .ExecuteUpdateAsync(update => update.SetProperty(state => state.Revision, revision));
                break;
        }

        (await store.ReadTrustedAsync(1, "steamgriddb", default)).ShouldBeNull();
        (await authorization.ReadAuthorizationAsync(1, "steamgriddb", default))
            .ShouldBeOfType<ProviderMatchAuthorizationResult.Blocked>().Reason.ShouldBe(ProviderMatchBlock.NeedsMatch);
        (await artwork.FindVariantAsync(1, "large", "v1")).ShouldBeNull();
        (await artwork.ResolveAsync([1]))[1].Single(value => value.Role == ArtworkRole.Poster).Asset.ShouldBeNull();
        (await gallery.GetGalleryAsync(1, default)).Value.ShouldBeEmpty();
        if (scenario != "IGDB revision changed")
        {
            await service.DiscoverAsync(1, null, default);
            (await store.ReadTrustedAsync(1, "steamgriddb", default)).ShouldBeNull();
            (await artwork.FindVariantAsync(1, "large", "v1")).ShouldBeNull();
        }
    }

    [Theory]
    [InlineData("installation alias")]
    [InlineData("inexact release label")]
    [InlineData("same-year alternative")]
    [InlineData("other platform-supported year")]
    [InlineData("undated alternative")]
    [InlineData("budget limited")]
    [InlineData("unresolved clone")]
    public async Task DiscoverAsync_ArtworkAliasEvidenceDoesNotBypassReviewGates(string scenario)
    {
        await using var db = _database.CreateContext();
        await SeedArtworkPlatformAliasAsync(db,
            scenario == "installation alias" ? ReferenceOwnership.Installation : ReferenceOwnership.Romd,
            scenario == "inexact release label" ? "Super Famicom Mini" : "Super Famicom");
        if (scenario == "unresolved clone")
        {
            await db.DatGames.Where(game => game.Id == 1)
                .ExecuteUpdateAsync(update => update.SetProperty(game => game.CloneOfId, "missing"));
        }
        var games = scenario switch
        {
            "same-year alternative" => new[] { SuperMarioWorldArtwork, SuperMarioWorldArtwork with { Id = "other" } },
            "undated alternative" => new[] { SuperMarioWorldArtwork, SuperMarioWorldArtwork with { Id = "other", Year = null } },
            "other platform-supported year" => new[] { SuperMarioWorldArtwork, SuperMarioWorldArtwork with { Id = "other", Year = 1991 } },
            _ => new[] { SuperMarioWorldArtwork }
        };
        var completeness = scenario == "budget limited" ? ProviderGameSearchCompleteness.BudgetLimited : ProviderGameSearchCompleteness.ProviderLimited;
        var adapter = SuperMarioWorldSearch(completeness, games);
        var store = CreateFamilyStore(db);

        await CreateAliasDiscovery(db, store, adapter).DiscoverAsync(1, null, default);

        (await store.ReadTrustedAsync(1, "steamgriddb", default)).ShouldBeNull();
        var match = (await store.ReadAsync(1, default)).ShouldNotBeNull().Links.Single(link => link.ProviderId == "steamgriddb");
        match.Decision.ShouldNotBeNull().State.ShouldBe("NeedsReview");
        match.Decision!.ProvisionalGameId.ShouldBeNull();
        match.Decision!.ArtworkPlatformAliases.ShouldBeNull();
    }

    [Theory]
    [InlineData(ProviderGameSearchCompleteness.Complete)]
    [InlineData(ProviderGameSearchCompleteness.ProviderLimited)]
    public async Task DiscoverAsync_ArtworkAliasChangesDuringSearch_DoesNotPublishStaleMatch(ProviderGameSearchCompleteness completeness)
    {
        await using var db = _database.CreateContext();
        await SeedArtworkPlatformAliasAsync(db, ReferenceOwnership.Romd, "Super Famicom");
        var adapter = SteamGridDbParentAdapter(SuperMarioWorldArtwork);
        adapter.SearchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            await using var writer = _database.CreateContext();
            await writer.PlatformAliases.ExecuteUpdateAsync(update =>
                update.SetProperty(alias => alias.Ownership, ReferenceOwnership.Installation));
            return ErrorOr.ErrorOrFactory.From(new ProviderGameSearchResult(
                [SuperMarioWorldArtwork], completeness, 100));
        });
        var store = CreateFamilyStore(db);

        await CreateAliasDiscovery(db, store, adapter).DiscoverAsync(1, null, default);

        (await store.ReadAsync(1, default)).ShouldNotBeNull().Links.ShouldNotContain(link => link.ProviderId == "steamgriddb");
    }

    [Fact]
    public async Task Downgrade_ProvisionalArtworkAliasMigration_RevokesDependentArtwork()
    {
        const string versionWithoutProvisionalAliasProof = "20261124010000_ArtworkPlatformAliasEvidence";
        await using var database = PostgreSqlTestDatabase.CreateEmpty();
        await using var db = database.CreateContext();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(versionWithoutProvisionalAliasProof);
        await SeedArtworkPlatformAliasAsync(db, ReferenceOwnership.Romd, "Super Famicom");
        await database.ProvisionAsync();
        var store = CreateFamilyStore(db);
        var service = CreateAliasDiscovery(db, store,
            SuperMarioWorldSearch(ProviderGameSearchCompleteness.ProviderLimited, SuperMarioWorldArtwork));
        await service.DiscoverAsync(1, null, default);
        db.ArtworkAssets.Add(ProvisionalAsset(SuperMarioWorldArtwork.Id, ArtworkRole.Poster, 1));
        await db.SaveChangesAsync();
        var artwork = new ArtworkReader(db);
        var authorization = new TitleProviderMatchStore(db, TimeProvider.System);
        (await artwork.FindVariantAsync(1, "large", "v1")).ShouldNotBeNull();

        await migrator.MigrateAsync(versionWithoutProvisionalAliasProof);

        (await store.ReadTrustedAsync(1, "igdb", default)).ShouldBe("1070");
        (await authorization.ReadAuthorizationAsync(1, "steamgriddb", default))
            .ShouldBeOfType<ProviderMatchAuthorizationResult.Blocked>().Reason.ShouldBe(ProviderMatchBlock.NeedsMatch);
        (await artwork.FindVariantAsync(1, "large", "v1")).ShouldBeNull();
    }

    private static IProviderIdentityAdapter SuperMarioWorldSearch(ProviderGameSearchCompleteness completeness, params ProviderGame[] games)
    {
        var adapter = SteamGridDbParentAdapter(SuperMarioWorldArtwork);
        adapter.SearchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new ProviderGameSearchResult(games, completeness, 100));
        return adapter;
    }

    private static ProviderIdentityService CreateAliasDiscovery(RomdDbContext db, ProviderIdentityStore store, IProviderIdentityAdapter adapter) =>
        new(store, [adapter], [], TimeProvider.System, new EfUnitOfWork(db), NullLogger<ProviderIdentityService>.Instance);

    private static async Task<Guid> SeedArtworkPlatformAliasAsync(RomdDbContext db, ReferenceOwnership ownership, string releasePlatform)
    {
        await SeedParentFamilyAsync(db, "Super Mario World", "Super Mario World (Europe) (Rev 1)");
        await db.TitleSourceLinks.Where(link => link.TitleId == 2).ExecuteDeleteAsync();
        db.TitleSourceLinks.Add(new TitleSourceLinkEntity { TitleId = 1, SourceEntryId = 3 });
        db.PlatformAliases.Add(new PlatformAliasEntity
        {
            Id = 1,
            PlatformId = 1,
            Type = PlatformAliasType.Name,
            Value = "Super Famicom",
            NormalizedValue = "super famicom",
            Ownership = ownership
        });
        await db.SaveChangesAsync();
        var revision = await AddAutomatic(db, "igdb", "1070");
        var igdb = new ProviderGame("1070", "Super Mario World", "https://provider.test/igdb", Year: 1990,
            Releases: [new(1991, "Test"), new(1990, releasePlatform), new(1990, "Wii")]);
        await db.Set<TitleProviderMatchStateEntity>().Where(state => state.ProviderId == "igdb")
            .ExecuteUpdateAsync(update => update.SetProperty(state => state.GameJson, JsonSerializer.Serialize(igdb, (JsonSerializerOptions?)null)));
        return revision;
    }
}
