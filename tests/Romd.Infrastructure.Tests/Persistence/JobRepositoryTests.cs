using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Domain.Jobs;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.Infrastructure.Tests.Helpers;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed class JobRepositoryTests
{
    [Fact]
    public async Task ArchiveCompletedAsync_DeferredMaterializationJob_ArchivesItAndSkipsActiveJob()
    {
        await using var database = await CreateDatabaseAsync();
        var archivedAt = new DateTimeOffset(2026, 8, 20, 14, 0, 0, TimeSpan.Zero);
        var repository = new JobRepository(database.Context, new ManualTimeProvider(archivedAt));
        var deferred = MaterializationJob.Create(7, "Arcade");
        deferred.Start("hangfire-deferred");
        deferred.MarkDeferred();
        deferred.Complete();
        var pending = MaterializationJob.Create(7, "Arcade");
        database.Context.Set<MaterializationJobEntity>().AddRange(
            MaterializationJobEntity.FromDomain(deferred),
            MaterializationJobEntity.FromDomain(pending));
        await database.Context.SaveChangesAsync();
        database.Context.ChangeTracker.Clear();

        int archivedCount = await repository.ArchiveCompletedAsync();

        archivedCount.ShouldBe(1);
        var deferredEntity = await database.Context.Jobs
            .AsNoTracking()
            .SingleAsync(job => job.Id == deferred.Id);
        deferredEntity.IsArchived.ShouldBeTrue();
        deferredEntity.ArchivedAt.ShouldBe(archivedAt);
        var pendingEntity = await database.Context.Jobs
            .AsNoTracking()
            .SingleAsync(job => job.Id == pending.Id);
        pendingEntity.IsArchived.ShouldBeFalse();
        pendingEntity.ArchivedAt.ShouldBeNull();
    }

    [Fact]
    public async Task GetHistoryAsync_TiedTimestamps_PagesWithoutDuplicatesAndHonorsFilters()
    {
        await using var database = await CreateDatabaseAsync();
        var owner = Guid.NewGuid();
        var other = Guid.NewGuid();
        database.Context.Users.AddRange(new Romd.Persistence.Identity.RomdUser { Id = owner, UserName = "owner" }, new Romd.Persistence.Identity.RomdUser { Id = other, UserName = "other" });
        var created = new DateTimeOffset(2026, 9, 10, 0, 0, 0, TimeSpan.Zero);
        var entities = Enumerable.Range(0, 6).Select(index =>
        {
            var job = UploadJob.Create($"pack-{index}.zip", createdByUserId: index == 5 ? other : owner);
            if (index != 0) job.Fail("test failure");
            if (index == 4) job.Archive();
            var entity = UploadJobEntity.FromDomain(job);
            entity.CreatedAt = created;
            return entity;
        }).ToArray();
        database.Context.AddRange(entities);
        await database.Context.SaveChangesAsync();
        database.Context.ChangeTracker.Clear();
        var repository = new JobRepository(database.Context, TimeProvider.System);
        var filter = new JobHistoryFilter(owner, "pack", "failed", "upload", "active", created, created.AddDays(1), null, null, 2);
        var first = await repository.GetHistoryAsync(filter);
        first.Count.ShouldBe(3); // two rows plus one lookahead
        var second = await repository.GetHistoryAsync(filter with { CursorCreatedAt = first[1].CreatedAt, CursorId = first[1].Id });
        second.Count.ShouldBe(1);
        first.Take(2).Select(job => job.Id).Intersect(second.Select(job => job.Id)).ShouldBeEmpty();
        first.All(job => job.CreatedByUserId == owner && job.Phase == "Failed" && !job.IsArchived).ShouldBeTrue();
        var archived = await repository.GetHistoryAsync(filter with { Archive = "archived" });
        archived.Single().Id.ShouldBe(entities[4].Id);
        var queued = await repository.GetHistoryAsync(filter with { Outcome = "queued" });
        queued.Single().Id.ShouldBe(entities[0].Id);
    }

    private static async Task<TestDatabase> CreateDatabaseAsync()
    {
        var connection = PostgreSqlTestDatabase.Create();
        var options = new DbContextOptionsBuilder<RomdDbContext>()
            .UseNpgsql(connection.ConnectionString)
            .Options;
        var context = new RomdDbContext(options);
        return new TestDatabase(connection, context);
    }

    private sealed class TestDatabase(PostgreSqlTestDatabase connection, RomdDbContext context) : IAsyncDisposable
    {
        public RomdDbContext Context { get; } = context;

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
