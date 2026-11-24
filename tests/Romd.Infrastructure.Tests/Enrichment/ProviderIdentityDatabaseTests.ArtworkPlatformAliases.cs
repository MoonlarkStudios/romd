using System.Text.Json;
using Microsoft.EntityFrameworkCore;
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
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Enrichment;

public sealed partial class ProviderIdentityDatabaseTests
{
    private static readonly ProviderGame SuperMarioWorldArtwork = new("36886", "Super Mario World", "https://provider.test/game", Year: 1990);

    [Theory]
    [InlineData("alias renamed")]
    [InlineData("alias deleted")]
    [InlineData("IGDB revision changed")]
    public async Task DiscoverAsync_CanonicalTitleWithOwnedAlias_AcceptsAndRevokesArtworkWhenEvidenceChanges(string scenario)
    {
        await using var db = _database.CreateContext();
        var igdbRevision = await SeedArtworkPlatformAliasAsync(db, ReferenceOwnership.Romd, "Super Famicom");
        var store = CreateFamilyStore(db);
        var service = CreateAliasDiscovery(db, store, SteamGridDbParentAdapter(SuperMarioWorldArtwork));
        (await store.ReadAsync(1, default)).ShouldNotBeNull().DatParent.ShouldBeNull();

        await service.DiscoverAsync(1, null, default);

        var match = (await store.ReadAsync(1, default)).ShouldNotBeNull().Links.Single(link => link.ProviderId == "steamgriddb");
        var decision = match.Decision.ShouldNotBeNull();
        decision.State.ShouldBe("AutoAccepted");
        decision.Explanation.ShouldContain("Super Famicom");
        decision.ArtworkPlatformAliases.ShouldBe([new ArtworkPlatformAlias(1, "Super Famicom")]);
        decision.Dependencies.ShouldBe([new ProviderIdentityDependency("igdb", "1070", igdbRevision)]);
        (await store.ReadTrustedAsync(1, "steamgriddb", default)).ShouldBe(SuperMarioWorldArtwork.Id);
        var poster = ProvisionalAsset(SuperMarioWorldArtwork.Id, ArtworkRole.Poster, 1);
        poster.IsProvisional = false;
        poster.IsEligible = true;
        db.ArtworkAssets.Add(poster);
        await db.SaveChangesAsync();
        var artwork = new ArtworkReader(db);
        var gallery = new ArtworkCurationRepository(db, TimeProvider.System);
        (await artwork.FindVariantAsync(1, "large", "v1")).ShouldNotBeNull();
        (await gallery.GetGalleryAsync(1, default)).Value.ShouldHaveSingleItem();

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
        (await artwork.FindVariantAsync(1, "large", "v1")).ShouldBeNull();
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
        var adapter = SteamGridDbParentAdapter(SuperMarioWorldArtwork);
        var games = scenario switch
        {
            "same-year alternative" => new[] { SuperMarioWorldArtwork, SuperMarioWorldArtwork with { Id = "other" } },
            "undated alternative" => new[] { SuperMarioWorldArtwork, SuperMarioWorldArtwork with { Id = "other", Year = null } },
            _ => new[] { SuperMarioWorldArtwork }
        };
        var completeness = scenario == "budget limited" ? ProviderGameSearchCompleteness.BudgetLimited : ProviderGameSearchCompleteness.ProviderLimited;
        adapter.SearchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new ProviderGameSearchResult(games, completeness, 100));
        var store = CreateFamilyStore(db);

        await CreateAliasDiscovery(db, store, adapter).DiscoverAsync(1, null, default);

        (await store.ReadTrustedAsync(1, "steamgriddb", default)).ShouldBeNull();
        var match = (await store.ReadAsync(1, default)).ShouldNotBeNull().Links.Single(link => link.ProviderId == "steamgriddb");
        match.Decision.ShouldNotBeNull().State.ShouldBe("NeedsReview");
        match.Decision!.ArtworkPlatformAliases.ShouldBeNull();
    }

    [Fact]
    public async Task DiscoverAsync_ArtworkAliasChangesDuringSearch_DoesNotPublishStaleMatch()
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
                [SuperMarioWorldArtwork], ProviderGameSearchCompleteness.ProviderLimited, 100));
        });
        var store = CreateFamilyStore(db);

        await CreateAliasDiscovery(db, store, adapter).DiscoverAsync(1, null, default);

        (await store.ReadAsync(1, default)).ShouldNotBeNull().Links.ShouldNotContain(link => link.ProviderId == "steamgriddb");
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
