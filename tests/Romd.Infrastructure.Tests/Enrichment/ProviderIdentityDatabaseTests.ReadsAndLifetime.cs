using Romd.Domain.Catalog;
using Microsoft.Extensions.Logging.Abstractions;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Artwork.Providers;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.MetadataProviders;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Contracts.Management.MetadataProviders;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.MetadataProviders;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Enrichment;

public sealed partial class ProviderIdentityDatabaseTests
{
    [Fact]
    public async Task ReadTrustedAsync_CurrentEvidenceAndSuppression_AreReadInOneStatement()
    {
        await using var seed = _database.CreateContext();
        await Seed(seed);
        await AddAutomatic(seed, "a", "42");
        var commands = new IdentityReadCounter();
        var options = new DbContextOptionsBuilder<RomdDbContext>(_database.CreateOptions()).AddInterceptors(commands).Options;
        await using var db = new RomdDbContext(options);
        var store = new ProviderIdentityStore(db, Substitute.For<ITitleEnrichmentEvidenceReader>(), TimeProvider.System);

        (await store.ReadTrustedAsync(1, "a", default)).ShouldBe("42");
        commands.Count.ShouldBe(1);
        await seed.CatalogSources.ExecuteUpdateAsync(update => update.SetProperty(source => source.Status, "Paused"));
        (await store.ReadTrustedAsync(1, "a", default)).ShouldBeNull();
        commands.Count.ShouldBe(2);
        await seed.TitleExternalIds.ExecuteUpdateAsync(update => update.SetProperty(link => link.IsConfirmed, true));
        (await store.ReadTrustedAsync(1, "a", default)).ShouldBe("42");
        await seed.Set<TitleProviderMatchStateEntity>().ExecuteUpdateAsync(update => update.SetProperty(state => state.SuppressAutomaticMatch, true));
        (await store.ReadTrustedAsync(1, "a", default)).ShouldBeNull();
        commands.Count.ShouldBe(4);
        db.ChangeTracker.Entries().ShouldBeEmpty();
        db.Database.CurrentTransaction.ShouldBeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiscoverAsync_ManySuccessfulDiscoveries_ReleasesOnlyItsOwnTracking(bool owned)
    {
        await using var db = _database.CreateContext();
        await Seed(db);
        var unrelated = await db.Platforms.AsTracking().SingleAsync();
        var evidence = Substitute.For<ITitleEnrichmentEvidenceReader>();
        evidence.ReadAsync(1, Arg.Any<CancellationToken>()).Returns(Array.Empty<TitleEnrichmentEvidence>());
        var store = new ProviderIdentityStore(db, evidence, TimeProvider.System);
        var owner = owned ? new JobContext(null, _ => Task.CompletedTask, default,
            ownedMutationDelegate: async (mutation, ct) =>
            {
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                await mutation(ct);
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
            }) : null;

        for (int index = 0; index < 100; index++)
        {
            var provider = $"provider-{index}";
            var service = new ProviderIdentityService(store, [IdentityAdapter(provider)], [], TimeProvider.System,
                new EfUnitOfWork(db), NullLogger<ProviderIdentityService>.Instance);
            await service.DiscoverAsync(1, owner, default);

            db.ChangeTracker.Entries().ShouldHaveSingleItem().Entity.ShouldBeSameAs(unrelated);
        }
        (await db.TitleExternalIds.CountAsync()).ShouldBe(100);
    }

    [Fact]
    public async Task DiscoverAsync_OwnedMutationRollsBack_ReleasesItsRowsAndPreservesExistingTracking()
    {
        await using var db = _database.CreateContext();
        await Seed(db);
        var unrelated = await db.Platforms.AsTracking().SingleAsync();
        var evidence = Substitute.For<ITitleEnrichmentEvidenceReader>();
        evidence.ReadAsync(1, Arg.Any<CancellationToken>()).Returns(Array.Empty<TitleEnrichmentEvidence>());
        var store = new ProviderIdentityStore(db, evidence, TimeProvider.System);
        var service = new ProviderIdentityService(store, [IdentityAdapter("a")], [], TimeProvider.System,
            new EfUnitOfWork(db), NullLogger<ProviderIdentityService>.Instance);
        var owner = new JobContext(null, _ => Task.CompletedTask, default,
            ownedMutationDelegate: async (mutation, ct) =>
            {
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                await mutation(ct);
                throw new IOException("mutation rejected before commit");
            });

        await Should.ThrowAsync<IOException>(() => service.DiscoverAsync(1, owner, default));

        db.ChangeTracker.Entries().ShouldHaveSingleItem().Entity.ShouldBeSameAs(unrelated);
        (await db.TitleExternalIds.CountAsync()).ShouldBe(0);
        (await db.Set<TitleProviderMatchStateEntity>().CountAsync()).ShouldBe(0);
        await service.DiscoverAsync(1, null, default);
        (await store.ReadTrustedAsync(1, "a", default)).ShouldBe("42");
    }

    private static IProviderIdentityAdapter IdentityAdapter(string provider)
    {
        var adapter = Substitute.For<IProviderIdentityAdapter>();
        adapter.Id.Returns(provider);
        adapter.Capabilities.Returns(["metadata"]);
        adapter.IdentityScope.Returns(ProviderIdentityScope.CrossPlatformArtwork);
        adapter.GetAvailabilityAsync(Arg.Any<CancellationToken>()).Returns(new ProviderAvailability(true, true));
        adapter.SearchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(
            new ProviderGameSearchResult([new("42", "Game", "https://example.test/game")], ProviderGameSearchCompleteness.Complete, 500));
        return adapter;
    }

    private sealed class IdentityReadCounter : DbCommandInterceptor
    {
        public int Count { get; private set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Count++;
            return ValueTask.FromResult(result);
        }
    }
}
