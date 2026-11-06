using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;
using Romd.Consumer.Application.Account;
using Romd.Consumer.Application.Browse;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Identity;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Identity;

public sealed class ConsumerUserSettingsStoreTests
{
    [Fact]
    public async Task SaveSettingsAsync_ConcurrentFirstSaves_BothSucceedWithOneCompleteSettingsRow()
    {
        await using var database = PostgreSqlTestDatabase.Create();
        await using var seed = database.CreateContext();
        var user = await SeedUserAsync(seed);
        var gate = new SettingsWriteGate();
        await using var first = new RomdDbContext(database.CreateOptions(options => options.AddInterceptors(gate)));
        await using var second = new RomdDbContext(database.CreateOptions(options => options.AddInterceptors(gate)));
        var firstSettings = new ConsumerAccountSettings("dark", new([1, 2], [3], ConsumerRevisionPreference.NewestFirst));
        var secondSettings = new ConsumerAccountSettings("light", new([4], [5, 6], ConsumerRevisionPreference.OldestFirst));

        var results = await Task.WhenAll(
            CreateStore(first).SaveSettingsAsync(user.Id, firstSettings),
            CreateStore(second).SaveSettingsAsync(user.Id, secondSettings)).WaitAsync(TimeSpan.FromSeconds(30));

        results.ShouldBe([firstSettings, secondSettings]);
        var persisted = await seed.Set<ConsumerUserSettingsEntity>().AsNoTracking().SingleAsync();
        persisted.UserId.ShouldBe(user.Id);
        persisted.Theme.ShouldBeOneOf("dark", "light");
        var expected = persisted.Theme == "dark" ? firstSettings.ReleasePreference : secondSettings.ReleasePreference;
        JsonSerializer.Deserialize<int[]>(persisted.PreferredRegionIdsJson).ShouldBe(expected.PreferredRegionIds);
        JsonSerializer.Deserialize<int[]>(persisted.PreferredLanguageIdsJson).ShouldBe(expected.PreferredLanguageIds);
        persisted.RevisionPreference.ShouldBe(expected.RevisionStrategy.ToString());
        persisted.CreatedAt.ShouldNotBe(default);
        persisted.UpdatedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task SaveSettingsAsync_InsertThenUpdate_PreservesCreationTimeAndMillisecondPrecision()
    {
        await using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();
        var user = await SeedUserAsync(db);
        var createdAt = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMilliseconds(123);
        var updatedAt = createdAt.AddDays(1).AddMilliseconds(456);
        var clock = Substitute.For<TimeProvider>();
        clock.GetUtcNow().Returns(createdAt);
        var store = new ConsumerUserSettingsStore(db, clock, Substitute.For<IConsumerTaxonomyKeys>());

        await store.SaveSettingsAsync(user.Id, new ConsumerAccountSettings("dark"));

        var inserted = await db.Set<ConsumerUserSettingsEntity>().AsNoTracking().SingleAsync();
        inserted.Theme.ShouldBe("dark");
        inserted.CreatedAt.ShouldBe(createdAt);
        inserted.UpdatedAt.ShouldBeNull();

        clock.GetUtcNow().Returns(updatedAt);
        await store.SaveSettingsAsync(user.Id, new ConsumerAccountSettings("light"));

        var persisted = await db.Set<ConsumerUserSettingsEntity>().AsNoTracking().SingleAsync();
        persisted.Theme.ShouldBe("light");
        persisted.CreatedAt.ShouldBe(createdAt);
        persisted.UpdatedAt.ShouldBe(updatedAt);
    }

    private static async Task<RomdUser> SeedUserAsync(RomdDbContext db)
    {
        var user = new RomdUser
        {
            UserName = "consumer-account",
            PasswordHash = "original-hash",
            SecurityStamp = "original-stamp",
            ConcurrencyStamp = "original-concurrency"
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private static ConsumerUserSettingsStore CreateStore(RomdDbContext db) =>
        new(db, TimeProvider.System, Substitute.For<IConsumerTaxonomyKeys>());

    private sealed class SettingsWriteGate : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _bothWritten = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _writes;

        public override async ValueTask<int> NonQueryExecutedAsync(DbCommand command,
            CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("\"ConsumerUserSettings\"", StringComparison.Ordinal))
            {
                // The old UPDATE-then-INSERT sequence must observe two missing rows before either
                // insert runs. A single-statement upsert can complete both writes independently.
                if (Interlocked.Increment(ref _writes) == 2)
                    _bothWritten.TrySetResult();
                await _bothWritten.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            }
            return result;
        }
    }
}
