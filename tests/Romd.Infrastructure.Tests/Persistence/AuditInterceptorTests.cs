using Microsoft.EntityFrameworkCore;
using Romd.Application.Common.Security;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Infrastructure.Tests.Helpers;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed class AuditInterceptorTests : IDisposable
{
    private static readonly Guid TestUserId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private static readonly DateTimeOffset FixedNow = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    private readonly PostgreSqlTestDatabase _connection;
    private readonly DbContextOptions<RomdDbContext> _contextOptions;
    private readonly ManualTimeProvider _timeProvider = new(FixedNow);

    public AuditInterceptorTests()
    {
        _connection = PostgreSqlTestDatabase.Create();

        var interceptor = new AuditInterceptor(new FakeAuditContext(TestUserId), _timeProvider);

        _contextOptions = new DbContextOptionsBuilder<RomdDbContext>()
            .UseNpgsql(_connection.ConnectionString)
            .AddInterceptors(interceptor)
            .Options;

        using var db = new RomdDbContext(_contextOptions);
    }

    private RomdDbContext CreateDb() => new(_contextOptions);

    [Fact]
    public async Task Added_ICreatableEntity_StampsCreatedAtAndCreatedByUserId()
    {
        int platformId;
        using (var db = CreateDb())
        {
            var platform = new PlatformEntity
            {
                Name = "Super Nintendo",
                Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "Super Nintendo", BaseCompactLabel = "Super Nintendo", CanonicalKey = "snes", ShortName = "snes",
                Manufacturer = "Nintendo"
            };
            db.Platforms.Add(platform);
            await db.SaveChangesAsync();
            platformId = platform.Id;
        }

        using (var db = CreateDb())
        {
            var entity = await db.Platforms.FirstAsync(p => p.Id == platformId);
            entity.CreatedAt.ShouldBe(FixedNow);
            entity.CreatedByUserId.ShouldBe(TestUserId);
        }
    }

    [Fact]
    public async Task Added_IEditableEntity_StampsAllAuditFields()
    {
        // Seed platform for FK
        using (var db = CreateDb())
        {
            db.Platforms.Add(new PlatformEntity
            {
                Id = 1, Name = "Test", Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "Test", BaseCompactLabel = "Test", CanonicalKey = "test", ShortName = "test", Manufacturer = "Test"
            });
            await db.SaveChangesAsync();
        }

        int titleId;
        using (var db = CreateDb())
        {
            var title = new TitleEntity
            {
                PlatformId = 1,
                Name = "Test Title",
                NormalizedName = "test title",
                EnrichmentStatus = "None"
            };
            db.Titles.Add(title);
            await db.SaveChangesAsync();
            titleId = title.Id;
        }

        using (var db = CreateDb())
        {
            var entity = await db.Titles.FirstAsync(t => t.Id == titleId);
            entity.CreatedAt.ShouldBe(FixedNow);
            entity.CreatedByUserId.ShouldBe(TestUserId);
            entity.UpdatedAt.ShouldBe(FixedNow);
            entity.UpdatedByUserId.ShouldBe(TestUserId);
        }
    }

    [Fact]
    public async Task Modified_IEditableEntity_UpdatesOnlyEditFields()
    {
        // Seed platform for FK
        using (var db = CreateDb())
        {
            db.Platforms.Add(new PlatformEntity
            {
                Id = 1, Name = "Test", Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "Test", BaseCompactLabel = "Test", CanonicalKey = "test", ShortName = "test", Manufacturer = "Test"
            });
            await db.SaveChangesAsync();
        }

        int titleId;

        using (var db = CreateDb())
        {
            var title = new TitleEntity
            {
                PlatformId = 1,
                Name = "Test Title",
                NormalizedName = "test title",
                EnrichmentStatus = "None"
            };
            db.Titles.Add(title);
            await db.SaveChangesAsync();
            titleId = title.Id;
        }

        // Read back from DB to get SQLite-rounded value
        DateTimeOffset originalCreatedAt;
        using (var db = CreateDb())
        {
            var entity = await db.Titles.FirstAsync(t => t.Id == titleId);
            originalCreatedAt = entity.CreatedAt;
        }

        var updatedAt = FixedNow.AddHours(1);
        _timeProvider.SetUtcNow(updatedAt);

        using (var db = CreateDb())
        {
            var title = await db.Titles.AsTracking().FirstAsync(t => t.Id == titleId);
            title.Name = "Updated Title";
            await db.SaveChangesAsync();
        }

        using (var db = CreateDb())
        {
            var entity = await db.Titles.FirstAsync(t => t.Id == titleId);

            // Creation fields protected from mutation
            entity.CreatedAt.UtcTicks.ShouldBe(originalCreatedAt.UtcTicks);
            entity.CreatedByUserId.ShouldBe(TestUserId);

            // Edit fields updated
            entity.UpdatedAt.ShouldNotBeNull();
            entity.UpdatedAt!.Value.UtcTicks.ShouldBe(updatedAt.UtcTicks);
            entity.UpdatedByUserId.ShouldBe(TestUserId);
            entity.Name.ShouldBe("Updated Title");
        }
    }

    [Fact]
    public async Task Modified_ICreatableEntity_ProtectsCreationFields()
    {
        int platformId;

        using (var db = CreateDb())
        {
            var platform = new PlatformEntity
            {
                Name = "Original",
                Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "Original", BaseCompactLabel = "Original", CanonicalKey = "orig", ShortName = "orig",
                Manufacturer = "Test"
            };
            db.Platforms.Add(platform);
            await db.SaveChangesAsync();
            platformId = platform.Id;
        }

        // Read back from DB to get SQLite-rounded value
        DateTimeOffset originalCreatedAt;
        using (var db = CreateDb())
        {
            var entity = await db.Platforms.FirstAsync(p => p.Id == platformId);
            originalCreatedAt = entity.CreatedAt;
        }

        using (var db = CreateDb())
        {
            var platform = await db.Platforms.AsTracking().FirstAsync(p => p.Id == platformId);
            // Attempt to tamper with creation fields
            platform.CreatedByUserId = Guid.Empty;
            platform.CreatedAt = DateTimeOffset.MinValue;
            platform.Name = "Renamed"; // Legitimate change
            await db.SaveChangesAsync();
        }

        using (var db = CreateDb())
        {
            var entity = await db.Platforms.FirstAsync(p => p.Id == platformId);
            entity.CreatedAt.UtcTicks.ShouldBe(originalCreatedAt.UtcTicks);
            entity.CreatedByUserId.ShouldBe(TestUserId);
            entity.Name.ShouldBe("Renamed");
        }
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    private sealed class FakeAuditContext(Guid actorId) : IAuditContext
    {
        public Guid ActorId => actorId;
        public bool IsSystem => false;
    }
}
