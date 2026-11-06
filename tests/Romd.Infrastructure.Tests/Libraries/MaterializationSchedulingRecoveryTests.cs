using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Romd.Domain.Jobs;
using Romd.Domain.Libraries;
using Romd.Infrastructure.Tests.Helpers;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Libraries;

public sealed class MaterializationSchedulingRecoveryTests
{
    [Fact]
    public async Task SetHangfireJobIdAsync_MissingJob_RejectsUnexpectedRowCount()
    {
        await using var database = await TestDatabase.CreateAsync();

        var exception = await Should.ThrowAsync<InvalidOperationException>(() =>
            database.JobRepository.SetHangfireJobIdAsync(Guid.NewGuid(), "hangfire-missing"));

        exception.Message.ShouldContain("updated 0");
    }

    [Fact]
    public async Task AddStagedAsync_DeferredJobPresent_OwnerCanCommitANewJob()
    {
        await using var database = await TestDatabase.CreateAsync();
        var deferred = MaterializationJob.Create(7, "Arcade", createdAt: DateTimeOffset.UtcNow);
        deferred.Start("hangfire-deferred", startedAt: DateTimeOffset.UtcNow);
        deferred.MarkDeferred();
        deferred.Complete(completedAt: DateTimeOffset.UtcNow);
        await database.JobRepository.SeedCommittedAsync(database.Context, deferred);

        (await database.JobRepository.GetActiveForLibraryAsync(7)).ShouldBeNull();
        (await database.JobRepository.GetActiveAsync()).ShouldBeEmpty();

        var next = MaterializationJob.Create(7, "Arcade", createdAt: DateTimeOffset.UtcNow);
        await database.JobRepository.SeedCommittedAsync(database.Context, next);
        (await database.Context.Jobs.OfType<MaterializationJobEntity>().CountAsync()).ShouldBe(2);
        (await database.Context.JobDispatches.SingleAsync()).JobId.ShouldBe(next.Id);
    }

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly PostgreSqlTestDatabase _connection;

        private TestDatabase(PostgreSqlTestDatabase connection, RomdDbContext context)
        {
            _connection = connection;
            Context = context;
            LibraryRepository = new LibraryRepository(context);
            JobRepository = new MaterializationJobRepository(context, TimeProvider.System);
        }

        public RomdDbContext Context { get; }
        public LibraryRepository LibraryRepository { get; }
        public MaterializationJobRepository JobRepository { get; }

        public static async Task<TestDatabase> CreateAsync()
        {
            var connection = PostgreSqlTestDatabase.Create();
            var options = new DbContextOptionsBuilder<RomdDbContext>()
                .UseNpgsql(connection.ConnectionString)
                .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
                .Options;
            var context = new RomdDbContext(options);
            context.Libraries.Add(new LibraryEntity
            {
                Id = 7,
                Name = "Arcade",
                ConfigurationJson = JsonSerializer.Serialize(new LibraryConfiguration()),
                ConfigurationState = LibraryConfigurationState.Valid.ToString(),
                NeedsMaterialization = true,
                CreatedAt = DateTimeOffset.UtcNow,
                CreatedByUserId = Guid.Empty
            });
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();
            return new TestDatabase(connection, context);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
