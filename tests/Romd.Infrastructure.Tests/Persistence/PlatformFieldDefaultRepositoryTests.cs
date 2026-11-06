using Microsoft.EntityFrameworkCore;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public class PlatformFieldDefaultRepositoryTests : IDisposable
{
    private readonly PostgreSqlTestDatabase _connection;
    private readonly DbContextOptions<RomdDbContext> _contextOptions;

    public PlatformFieldDefaultRepositoryTests()
    {
        _connection = PostgreSqlTestDatabase.Create();

        _contextOptions = new DbContextOptionsBuilder<RomdDbContext>()
            .UseNpgsql(_connection.ConnectionString)
            .Options;

        using var db = new RomdDbContext(_contextOptions);

        // Seed platforms for FK constraints
        db.Platforms.Add(new PlatformEntity
        {
            Id = 1,
            Name = "Super Nintendo",
            Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd,
            BuiltInVersion = 1,
            BaseName = "Super Nintendo",
            BaseCompactLabel = "Super Nintendo",
            CanonicalKey = "snes",
            ShortName = "snes",
            Manufacturer = "Nintendo",
            CreatedAt = DateTimeOffset.UtcNow
        });
        db.Platforms.Add(new PlatformEntity
        {
            Id = 2,
            Name = "Genesis",
            Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd,
            BuiltInVersion = 1,
            BaseName = "Genesis",
            BaseCompactLabel = "Genesis",
            CanonicalKey = "genesis",
            ShortName = "genesis",
            Manufacturer = "Sega",
            CreatedAt = DateTimeOffset.UtcNow
        });
        db.SaveChanges();
    }

    private RomdDbContext CreateDb() => new(_contextOptions);

    [Fact]
    public async Task SetStagedAsync_CreatesNewEntry()
    {
        using (var db = CreateDb())
        {
            var repo = new PlatformFieldDefaultRepository(db);
            await ReplaceAsync(db, repo, 1, "Description", "igdb");
        }

        using (var db = CreateDb())
        {
            var repo = new PlatformFieldDefaultRepository(db);
            var defaults = await repo.GetByPlatformIdAsync(1);
            defaults.Count.ShouldBe(1);
            defaults["Description"].ShouldBe("igdb");
        }
    }

    [Fact]
    public async Task SetStagedAsync_UpdatesExistingEntry()
    {
        using (var db = CreateDb())
        {
            var repo = new PlatformFieldDefaultRepository(db);
            await ReplaceAsync(db, repo, 1, "Description", "igdb");
        }

        using (var db = CreateDb())
        {
            var repo = new PlatformFieldDefaultRepository(db);
            await ReplaceAsync(db, repo, 1, "Description", "screenscraper");
        }

        using (var db = CreateDb())
        {
            var repo = new PlatformFieldDefaultRepository(db);
            var defaults = await repo.GetByPlatformIdAsync(1);
            defaults.Count.ShouldBe(1);
            defaults["Description"].ShouldBe("screenscraper");
        }
    }

    [Fact]
    public async Task ClearStagedAsync_RemovesEntry()
    {
        using (var db = CreateDb())
        {
            var repo = new PlatformFieldDefaultRepository(db);
            await ReplaceAsync(db, repo, 1, "Description", "igdb");
            await ReplaceAsync(db, repo, 1, "Genre", "screenscraper");
        }

        using (var db = CreateDb())
        {
            var repo = new PlatformFieldDefaultRepository(db);
            var work = new EfUnitOfWork(db);
            await using var transaction = await work.BeginTransactionAsync();
            await repo.ClearStagedAsync(1, "Description");
            await transaction.CommitAsync();
        }

        using (var db = CreateDb())
        {
            var repo = new PlatformFieldDefaultRepository(db);
            var defaults = await repo.GetByPlatformIdAsync(1);
            defaults.Count.ShouldBe(1);
            defaults.ShouldNotContainKey("Description");
            defaults["Genre"].ShouldBe("screenscraper");
        }
    }

    [Fact]
    public async Task GetByPlatformIdAsync_ReturnsAllDefaults()
    {
        using (var db = CreateDb())
        {
            var repo = new PlatformFieldDefaultRepository(db);
            await ReplaceAsync(db, repo, 1, "Description", "igdb");
            await ReplaceAsync(db, repo, 1, "Genre", "screenscraper");
            await ReplaceAsync(db, repo, 1, "Publisher", "igdb");
        }

        using (var db = CreateDb())
        {
            var repo = new PlatformFieldDefaultRepository(db);
            var defaults = await repo.GetByPlatformIdAsync(1);

            defaults.Count.ShouldBe(3);
            defaults["Description"].ShouldBe("igdb");
            defaults["Genre"].ShouldBe("screenscraper");
            defaults["Publisher"].ShouldBe("igdb");
        }
    }

    [Fact]
    public async Task GetByPlatformIdAsync_EmptyForNewPlatform()
    {
        using (var db = CreateDb())
        {
            var repo = new PlatformFieldDefaultRepository(db);
            await ReplaceAsync(db, repo, 1, "Description", "igdb");
        }

        using (var db = CreateDb())
        {
            var repo = new PlatformFieldDefaultRepository(db);
            var defaults = await repo.GetByPlatformIdAsync(2);
            defaults.ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task StagedWrites_WithoutTransaction_RejectBeforeTracking()
    {
        await using var db = CreateDb();
        var repo = new PlatformFieldDefaultRepository(db);

        await Should.ThrowAsync<InvalidOperationException>(() => repo.SetStagedAsync(1, "Description", "igdb"));
        await Should.ThrowAsync<InvalidOperationException>(() => repo.ClearStagedAsync(1, "Description"));

        db.ChangeTracker.Entries().ShouldBeEmpty();
        (await db.PlatformFieldDefaults.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task SetStagedAsync_PersistedMatch_RequiresCallerDeletionFlush()
    {
        await using var db = CreateDb();
        var repo = new PlatformFieldDefaultRepository(db);
        await ReplaceAsync(db, repo, 1, "Description", "igdb");
        int originalId = (await db.PlatformFieldDefaults.SingleAsync()).Id;
        var work = new EfUnitOfWork(db);
        await using var transaction = await work.BeginTransactionAsync();
        await repo.ClearStagedAsync(1, "description");

        await Should.ThrowAsync<InvalidOperationException>(() => repo.SetStagedAsync(1, "Description", "user"));
        (await db.PlatformFieldDefaults.SingleAsync()).Id.ShouldBe(originalId);
        await work.FlushAsync();
        (await db.PlatformFieldDefaults.CountAsync()).ShouldBe(0);
        await repo.SetStagedAsync(1, "Description", "user");
        (await db.PlatformFieldDefaults.CountAsync()).ShouldBe(0);
        await transaction.CommitAsync();

        await using var verification = CreateDb();
        var replacement = await verification.PlatformFieldDefaults.SingleAsync();
        replacement.Id.ShouldNotBe(originalId);
        replacement.SourceId.ShouldBe("user");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StagedWrites_PendingSetClearSet_ComposeAndPreserveUnrelatedChanges(bool setAfterClear)
    {
        await using var db = CreateDb();
        var repo = new PlatformFieldDefaultRepository(db);
        var work = new EfUnitOfWork(db);
        await using var transaction = await work.BeginTransactionAsync();
        var platform = await db.Platforms.AsTracking().SingleAsync(row => row.Id == 1);
        platform.Name = "Before clear";
        await repo.SetStagedAsync(1, "Description", "igdb");
        await repo.SetStagedAsync(2, "Description", "user");
        await repo.SetStagedAsync(1, "Genre", "dat");

        await repo.ClearStagedAsync(1, "description");
        platform.Name = "After clear";
        if (setAfterClear)
        {
            await repo.SetStagedAsync(1, "description", "user");
            await repo.SetStagedAsync(1, "Description", "dat");
        }
        (await db.PlatformFieldDefaults.CountAsync()).ShouldBe(0);
        await transaction.CommitAsync();

        await using var verification = CreateDb();
        var persisted = new PlatformFieldDefaultRepository(verification);
        var first = await persisted.GetByPlatformIdAsync(1);
        if (setAfterClear)
            first["Description"].ShouldBe("dat");
        else
            first.ShouldNotContainKey("Description");
        first["Genre"].ShouldBe("dat");
        (await persisted.GetByPlatformIdAsync(2))["Description"].ShouldBe("user");
        (await verification.Platforms.SingleAsync(row => row.Id == 1)).Name.ShouldBe("After clear");
        (await verification.PlatformFieldDefaults.CountAsync()).ShouldBe(setAfterClear ? 3 : 2);
    }

    private static async Task ReplaceAsync(RomdDbContext db, PlatformFieldDefaultRepository repo,
        int platformId, string fieldName, string sourceId)
    {
        var work = new EfUnitOfWork(db);
        await using var transaction = await work.BeginTransactionAsync();
        await repo.ClearStagedAsync(platformId, fieldName);
        await work.FlushAsync();
        await repo.SetStagedAsync(platformId, fieldName, sourceId);
        await transaction.CommitAsync();
    }

    public void Dispose()
    {
        _connection.Dispose();
    }
}
