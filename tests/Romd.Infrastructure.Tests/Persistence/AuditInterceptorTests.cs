using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Taxonomy;
using Romd.Application.Common.Security;
using Romd.Domain.Taxonomy;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
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
                Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd,
                BuiltInVersion = 1,
                BaseName = "Super Nintendo",
                BaseCompactLabel = "Super Nintendo",
                CanonicalKey = "snes",
                ShortName = "snes",
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
                Id = 1,
                Name = "Test",
                Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd,
                BuiltInVersion = 1,
                BaseName = "Test",
                BaseCompactLabel = "Test",
                CanonicalKey = "test",
                ShortName = "test",
                Manufacturer = "Test"
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
                Id = 1,
                Name = "Test",
                Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd,
                BuiltInVersion = 1,
                BaseName = "Test",
                BaseCompactLabel = "Test",
                CanonicalKey = "test",
                ShortName = "test",
                Manufacturer = "Test"
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
                Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd,
                BuiltInVersion = 1,
                BaseName = "Original",
                BaseCompactLabel = "Original",
                CanonicalKey = "orig",
                ShortName = "orig",
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

    [Fact]
    public async Task TaxonomyEvents_NameRegionsLanguagesAndTheirAliasesByKey()
    {
        using (var db = CreateDb())
        {
            db.Regions.Add(new RegionEntity { Name = "Atlantis", CanonicalKey = "dat-atlantis", IsAutoCreated = true });
            db.GameLanguages.Add(new GameLanguageEntity { Name = "Klingon", Code = "tlh", CanonicalKey = "dat-klingon", IsAutoCreated = true });
            await db.SaveChangesAsync();
        }

        // Alias writes from a fresh context: the row is not tracked, so its key comes from the database.
        using (var db = CreateDb())
        {
            int regionId = await db.Regions.Select(row => row.Id).SingleAsync();
            int languageId = await db.GameLanguages.Select(row => row.Id).SingleAsync();
            var regions = new RegionRepository(db);
            var unitOfWork = new EfUnitOfWork(db);
            await using var transaction = await unitOfWork.BeginTransactionAsync();
            var staged = await regions.AddAliasStagedAsync(regionId, "atlantis");
            await unitOfWork.FlushAsync();
            var alias = staged.ReadAfterFlush();
            var language = await new LanguageRepository(db).AddAliasStagedAsync(languageId, "klingon");
            await unitOfWork.FlushAsync();
            language.ReadAfterFlush().ShouldNotBeNull();
            (await regions.RemoveAliasAsync(regionId, alias.Id)).ShouldBeTrue();
            await transaction.CommitAsync();
        }

        using (var db = CreateDb())
        {
            var events = await db.AdminAuditEvents.OrderBy(item => item.Id)
                .Select(item => new { item.TargetType, item.TargetId, item.Action, item.Changes }).ToListAsync();
            events.Select(item => (item.TargetType, item.TargetId, item.Action)).ShouldBe(
            [
                ("Region", "dat-atlantis", "Added"), ("Language", "dat-klingon", "Added"),
                ("Region", "dat-atlantis", "Added"), ("Language", "dat-klingon", "Added"),
                ("Region", "dat-atlantis", "Deleted")
            ], ignoreOrder: true);
            events.Where(item => item.Changes.Contains("NormalizedAlias")).Select(item => item.TargetId)
                .ShouldBe(["dat-atlantis", "dat-klingon", "dat-atlantis"], ignoreOrder: true);
        }
    }

    [Fact]
    public async Task Save_WithOnlyUnchangedAliasesTracked_LooksUpNoRegionKey()
    {
        using (var db = CreateDb())
        {
            var region = new RegionEntity { Name = "Atlantis", CanonicalKey = "dat-atlantis", IsAutoCreated = true };
            db.Regions.Add(region);
            await db.SaveChangesAsync();
            db.RegionAliases.Add(new RegionAliasEntity { RegionId = region.Id, NormalizedAlias = "atlantis" });
            await db.SaveChangesAsync();
        }

        var log = new CommandLog();
        var options = new DbContextOptionsBuilder<RomdDbContext>()
            .UseNpgsql(_connection.ConnectionString)
            .AddInterceptors(new AuditInterceptor(new FakeAuditContext(TestUserId), _timeProvider), log)
            .Options;
        using (var db = new RomdDbContext(options))
        {
            // A tracked alias that is not being written needs no key: it produces no audit event.
            _ = await db.RegionAliases.AsTracking().SingleAsync();
            db.GameLanguages.Add(new GameLanguageEntity { Name = "Klingon", Code = "tlh", CanonicalKey = "dat-klingon", IsAutoCreated = true });
            int before = log.Commands.Count;

            await db.SaveChangesAsync();

            log.Commands.Skip(before).ShouldNotContain(command => command.Contains("FROM romd.\"Regions\""));
        }
    }

    [Fact]
    public async Task Merge_AuditNamesTheSourceAndTargetByKey()
    {
        using (var db = CreateDb())
        {
            db.Regions.AddRange(new RegionEntity { Name = "Source", CanonicalKey = "dat-source" },
                new RegionEntity { Name = "Target", CanonicalKey = "dat-target" });
            await db.SaveChangesAsync();
        }

        using (var db = CreateDb())
        {
            var service = new TaxonomyService<Region>(new RegionRepository(db), new EfUnitOfWork(db), Microsoft.Extensions.Logging.Abstractions.NullLogger<TaxonomyService<Region>>.Instance);
            (await service.MergeAsync("dat-source", "dat-target", default)).IsError.ShouldBeFalse();
        }

        using (var db = CreateDb())
        {
            var merged = await db.AdminAuditEvents.SingleAsync(item => item.Action == "Merged");
            (merged.TargetType, merged.TargetId, merged.Changes).ShouldBe(("Region", "dat-source", "{\"TargetKey\":\"dat-target\"}"));
            merged.ActorId.ShouldBe(TestUserId);
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
