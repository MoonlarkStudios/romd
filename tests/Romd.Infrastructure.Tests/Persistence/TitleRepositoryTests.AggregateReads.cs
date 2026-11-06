using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Romd.Admin.Application.Common.Persistence;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed partial class TitleRepositoryTests
{
    [Fact]
    public async Task GetAggregateAsync_LargeGallery_ReadsCollectionsWithoutMultiplyingSiblingRows()
    {
        await SeedCuratedTitleAsync();
        using (var seed = CreateDb())
        {
            seed.TitleMedia.AddRange(Enumerable.Range(4, 500).Select(id => new TitleMediaEntity
            {
                TitleId = 1,
                FileId = 1,
                Type = "Screenshot",
                SourceId = $"gallery:{id}",
                CreatedAt = DateTimeOffset.UtcNow
            }));
            await seed.SaveChangesAsync();
        }
        var commands = new AggregateReadInterceptor();
        await using var db = AggregateReadContext(commands);

        var title = (await new TitleRepository(db).GetAggregateAsync(1))!;

        title.Media.Count.ShouldBe(503);
        title.ExternalIds.Count.ShouldBe(2);
        title.MetadataLayers.Count.ShouldBe(2);
        title.ContentRatings.Count.ShouldBe(1);
        commands.Commands.Count.ShouldBeLessThanOrEqualTo(6);
        commands.Commands.ShouldAllBe(command => AggregateReadInterceptor.ChildTables.Count(command.Contains) <= 1,
            "a read must never join sibling collections together");
        db.ChangeTracker.Entries().ShouldBeEmpty();
        db.Database.CurrentTransaction.ShouldBeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetAggregateAsync_RevisionChangesBetweenCollections_ReloadsTheCompleteSnapshot(bool callerTransaction)
    {
        await SeedCuratedTitleAsync();
        var commands = new AggregateReadInterceptor(async () => await ChangeAggregateAsync("new snapshot"));
        await using var db = AggregateReadContext(commands);
        await using var transaction = callerTransaction
            ? await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted)
            : null;

        var title = (await new TitleRepository(db).GetAggregateAsync(1))!;

        title.Description.ShouldBe("new snapshot");
        title.Media.Single(media => media.Id == 1).SourceUrl.ShouldBe("https://example.test/new-snapshot.png");
        commands.Commands.Count.ShouldBeGreaterThan(6);
        commands.Commands.Count.ShouldBeLessThanOrEqualTo(12);
        db.Database.CurrentTransaction.ShouldBe(transaction);
        db.ChangeTracker.Entries().ShouldBeEmpty();
    }

    [Fact]
    public async Task GetAggregateAsync_CallerSnapshotSurvivesConcurrentMutation_PreservesItsIsolationAndOwnership()
    {
        await SeedCuratedTitleAsync();
        var commands = new AggregateReadInterceptor(async () => await ChangeAggregateAsync("new snapshot"));
        await using var db = AggregateReadContext(commands);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead);

        var title = (await new TitleRepository(db).GetAggregateAsync(1))!;

        title.Description.ShouldBe("Curated description");
        title.Media.Single(media => media.Id == 1).SourceUrl.ShouldBeNull();
        db.Database.CurrentTransaction.ShouldBe(transaction);
        commands.Commands.Count.ShouldBeLessThanOrEqualTo(6);
        await transaction.CommitAsync();
    }

    [Fact]
    public async Task GetAggregateAsync_RepeatedConcurrentChanges_ReportsConflictAfterBoundedReads()
    {
        await SeedCuratedTitleAsync();
        var commands = new AggregateReadInterceptor(async () => await ChangeAggregateAsync("changing"), repeat: true);
        await using var db = AggregateReadContext(commands);

        await Should.ThrowAsync<PersistenceConflictException>(() => new TitleRepository(db).GetAggregateAsync(1));

        commands.Commands.Count.ShouldBeLessThanOrEqualTo(18);
        db.ChangeTracker.Entries().ShouldBeEmpty();
        db.Database.CurrentTransaction.ShouldBeNull();
    }

    [Fact]
    public async Task GetAggregateAsync_TitleDeletedBetweenCollections_ReturnsMissingInsteadOfPartialAggregate()
    {
        await SeedCuratedTitleAsync();
        var commands = new AggregateReadInterceptor(async () =>
        {
            await using var writer = CreateDb();
            await writer.Titles.Where(title => title.Id == 1).ExecuteDeleteAsync();
        });
        await using var db = AggregateReadContext(commands);

        (await new TitleRepository(db).GetAggregateAsync(1)).ShouldBeNull();

        commands.Commands.Count.ShouldBeLessThanOrEqualTo(7);
        db.ChangeTracker.Entries().ShouldBeEmpty();
    }

    [Fact]
    public async Task GetAggregateAsync_AutomaticIdentityInsertedBetweenCollections_PreservesOtherAggregateState()
    {
        await SeedCuratedTitleAsync();
        using var baseline = CreateDb();
        var revision = await baseline.Titles.Select(title => title.Revision).SingleAsync();
        var commands = new AggregateReadInterceptor(async () =>
        {
            await using var writer = CreateDb();
            writer.TitleExternalIds.Add(new TitleExternalIdEntity
            {
                TitleId = 1,
                Provider = "automatic-provider",
                ExternalId = "new-identity",
                CreatedAt = DateTimeOffset.UtcNow
            });
            await writer.SaveChangesAsync();
        });
        await using var db = AggregateReadContext(commands);

        var title = (await new TitleRepository(db).GetAggregateAsync(1))!;

        title.Revision.ShouldBe(revision);
        title.ExternalIds.Single(link => link.Provider == "automatic-provider").ExternalId.ShouldBe("new-identity");
        title.Description.ShouldBe("Curated description");
        title.Media.Count.ShouldBe(3);
        title.MetadataLayers.Count.ShouldBe(2);
        title.ContentRatings.Count.ShouldBe(1);
        commands.Commands.Count.ShouldBeLessThanOrEqualTo(6);
        db.ChangeTracker.Entries().ShouldBeEmpty();
    }

    private RomdDbContext AggregateReadContext(AggregateReadInterceptor interceptor) => new(
        new DbContextOptionsBuilder<RomdDbContext>(_contextOptions).AddInterceptors(interceptor).Options);

    private async Task ChangeAggregateAsync(string description)
    {
        await using var writer = CreateDb();
        await using var transaction = await writer.Database.BeginTransactionAsync();
        await writer.Titles.Where(title => title.Id == 1).ExecuteUpdateAsync(setters => setters
            .SetProperty(title => title.Revision, Guid.NewGuid())
            .SetProperty(title => title.Description, description));
        await writer.TitleMedia.Where(media => media.Id == 1).ExecuteUpdateAsync(setters => setters
            .SetProperty(media => media.SourceUrl, "https://example.test/new-snapshot.png"));
        await transaction.CommitAsync();
    }

    private sealed class AggregateReadInterceptor(Func<Task>? betweenCollections = null, bool repeat = false) : DbCommandInterceptor
    {
        internal static readonly string[] ChildTables = ["\"TitleExternalIds\"", "\"TitleMedia\"", "\"TitleMetadataLayers\"", "\"TitleContentRatings\""];
        public List<string> Commands { get; } = [];
        private bool _mutated;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            if (betweenCollections is not null && command.CommandText.Contains("\"TitleExternalIds\"") && (!_mutated || repeat))
            {
                _mutated = true;
                await betweenCollections();
            }
            return result;
        }
    }
}
