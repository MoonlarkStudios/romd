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
            Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "Super Nintendo", BaseCompactLabel = "Super Nintendo", CanonicalKey = "snes", ShortName = "snes",
            Manufacturer = "Nintendo",
            CreatedAt = DateTimeOffset.UtcNow
        });
        db.Platforms.Add(new PlatformEntity
        {
            Id = 2,
            Name = "Genesis",
            Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "Genesis", BaseCompactLabel = "Genesis", CanonicalKey = "genesis", ShortName = "genesis",
            Manufacturer = "Sega",
            CreatedAt = DateTimeOffset.UtcNow
        });
        db.SaveChanges();
    }

    private RomdDbContext CreateDb() => new(_contextOptions);

    [Fact]
    public async Task SetAsync_CreatesNewEntry()
    {
        using (var db = CreateDb())
        {
            var repo = new PlatformFieldDefaultRepository(db);
            await repo.SetAsync(1, "Description", "igdb");
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
    public async Task SetAsync_UpdatesExistingEntry()
    {
        using (var db = CreateDb())
        {
            var repo = new PlatformFieldDefaultRepository(db);
            await repo.SetAsync(1, "Description", "igdb");
        }

        using (var db = CreateDb())
        {
            var repo = new PlatformFieldDefaultRepository(db);
            await repo.SetAsync(1, "Description", "screenscraper");
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
    public async Task ClearAsync_RemovesEntry()
    {
        using (var db = CreateDb())
        {
            var repo = new PlatformFieldDefaultRepository(db);
            await repo.SetAsync(1, "Description", "igdb");
            await repo.SetAsync(1, "Genre", "screenscraper");
        }

        using (var db = CreateDb())
        {
            var repo = new PlatformFieldDefaultRepository(db);
            await repo.ClearAsync(1, "Description");
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
            await repo.SetAsync(1, "Description", "igdb");
            await repo.SetAsync(1, "Genre", "screenscraper");
            await repo.SetAsync(1, "Publisher", "igdb");
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
            await repo.SetAsync(1, "Description", "igdb");
        }

        using (var db = CreateDb())
        {
            var repo = new PlatformFieldDefaultRepository(db);
            var defaults = await repo.GetByPlatformIdAsync(2);
            defaults.ShouldBeEmpty();
        }
    }

    public void Dispose()
    {
        _connection.Dispose();
    }
}
