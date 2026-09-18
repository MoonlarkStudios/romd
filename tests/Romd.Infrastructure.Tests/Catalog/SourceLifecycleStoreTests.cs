using Microsoft.EntityFrameworkCore;
using Romd.Domain.Catalog;
using Romd.Infrastructure.Catalog;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Catalog;

/// <summary>
///     Real-SQLite contract proofs for <see cref="SourceLifecycleStore" />: status reads
///     round-trip the stored name, status writes are idempotent, entry-platform reads are
///     distinct and skip unrouted entries, and the link reads are truth-level — source
///     status never filters them.
/// </summary>
public sealed class SourceLifecycleStoreTests : IDisposable
{
    private const int PlatformId = 3;
    private const int OtherPlatformId = 4;
    private const int ActiveSourceId = 7;
    private const int DisabledSourceId = 8;

    private readonly PostgreSqlTestDatabase _connection;
    private readonly DbContextOptions<RomdDbContext> _contextOptions;

    public SourceLifecycleStoreTests()
    {
        _connection = PostgreSqlTestDatabase.Create();

        _contextOptions = new DbContextOptionsBuilder<RomdDbContext>()
            .UseNpgsql(_connection.ConnectionString)
            .Options;

        using var db = CreateDb();
        SeedCatalog(db);
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task GetStatus_MissingSource_ReturnsNull()
    {
        await using var db = CreateDb();

        (await new SourceLifecycleStore(db).GetStatusAsync(999)).ShouldBeNull();
    }

    [Fact]
    public async Task GetStatus_ParsesStoredStatusName()
    {
        await using var db = CreateDb();
        db.CatalogSources.Add(NewSource(9, nameof(CatalogSourceStatus.Discontinued)));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var store = new SourceLifecycleStore(db);

        (await store.GetStatusAsync(ActiveSourceId)).ShouldBe(CatalogSourceStatus.Active);
        (await store.GetStatusAsync(DisabledSourceId)).ShouldBe(CatalogSourceStatus.Disabled);
        (await store.GetStatusAsync(9)).ShouldBe(CatalogSourceStatus.Discontinued);
    }

    [Fact]
    public async Task SetStatus_UpdatesStoredStatusAndRepeatingIsIdempotent()
    {
        await using var db = CreateDb();
        var store = new SourceLifecycleStore(db);

        await store.SetStatusAsync(ActiveSourceId, CatalogSourceStatus.Disabled);
        await store.SetStatusAsync(ActiveSourceId, CatalogSourceStatus.Disabled);

        (await store.GetStatusAsync(ActiveSourceId)).ShouldBe(CatalogSourceStatus.Disabled);
        (await db.CatalogSources.AsNoTracking().SingleAsync(s => s.Id == ActiveSourceId))
            .Status.ShouldBe(nameof(CatalogSourceStatus.Disabled));
        // Other sources are untouched.
        (await store.GetStatusAsync(DisabledSourceId)).ShouldBe(CatalogSourceStatus.Disabled);
    }

    [Fact]
    public async Task GetEntryPlatformIds_ReturnsDistinctPlatformsSkippingUnroutedEntries()
    {
        await using var db = CreateDb();
        db.SourceEntries.AddRange(
            NewEntry(101, ActiveSourceId, "A", PlatformId),
            NewEntry(102, ActiveSourceId, "B", PlatformId),
            NewEntry(103, ActiveSourceId, "C", OtherPlatformId),
            NewEntry(104, ActiveSourceId, "Unrouted", platformId: null),
            NewEntry(105, DisabledSourceId, "Other Source", OtherPlatformId));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var platformIds = await new SourceLifecycleStore(db).GetEntryPlatformIdsAsync(ActiveSourceId);

        platformIds.OrderBy(id => id).ShouldBe([PlatformId, OtherPlatformId]);
    }

    [Fact]
    public async Task GetLinkedTitleIds_ReturnsDistinctTruthLevelLinksIgnoringSourceStatus()
    {
        await using var db = CreateDb();
        db.Titles.AddRange(NewTitle(201), NewTitle(202), NewTitle(203));
        db.SourceEntries.AddRange(
            // Two entries of the DISABLED source backing the same title: status never
            // filters truth-level links, and the duplicate collapses.
            NewEntry(101, DisabledSourceId, "A", PlatformId),
            NewEntry(102, DisabledSourceId, "B", PlatformId),
            NewEntry(103, DisabledSourceId, "C", PlatformId),
            NewEntry(104, ActiveSourceId, "Elsewhere", PlatformId));
        db.TitleSourceLinks.AddRange(
            new TitleSourceLinkEntity { SourceEntryId = 101, TitleId = 201 },
            new TitleSourceLinkEntity { SourceEntryId = 102, TitleId = 201 },
            new TitleSourceLinkEntity { SourceEntryId = 103, TitleId = 202 },
            new TitleSourceLinkEntity { SourceEntryId = 104, TitleId = 203 });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var titleIds = await new SourceLifecycleStore(db).GetLinkedTitleIdsAsync(DisabledSourceId);

        titleIds.OrderBy(id => id).ShouldBe([201, 202]);
    }

    [Fact]
    public async Task GetOrphanedTitleIds_ReturnsOnlyCandidatesWithZeroLinks()
    {
        await using var db = CreateDb();
        db.Titles.AddRange(NewTitle(201), NewTitle(202), NewTitle(203));
        db.SourceEntries.Add(NewEntry(101, ActiveSourceId, "A", PlatformId));
        db.TitleSourceLinks.Add(new TitleSourceLinkEntity { SourceEntryId = 101, TitleId = 201 });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var store = new SourceLifecycleStore(db);

        // 201 is still linked; 202 has zero links; 203 is orphaned but not a candidate.
        (await store.GetOrphanedTitleIdsAsync([201, 202])).ShouldBe([202]);
        (await store.GetOrphanedTitleIdsAsync([])).ShouldBeEmpty();
    }

    private RomdDbContext CreateDb() => new(_contextOptions);

    private static CatalogSourceEntity NewSource(int id, string status) =>
        new() { Id = id, Kind = "Dat", Status = status, CreatedAt = DateTimeOffset.UtcNow };

    private static SourceEntryEntity NewEntry(int id, int catalogSourceId, string entryKey, int? platformId) =>
        new()
        {
            Id = id,
            CatalogSourceId = catalogSourceId,
            EntryKey = entryKey,
            Name = entryKey,
            PlatformId = platformId,
            CreatedAt = DateTimeOffset.UtcNow
        };

    private static TitleEntity NewTitle(int id) =>
        new()
        {
            Id = id,
            PlatformId = PlatformId,
            Name = $"Title {id}",
            NormalizedName = $"title{id}",
            EnrichmentStatus = "None",
            CreatedAt = DateTimeOffset.UtcNow
        };

    private static void SeedCatalog(RomdDbContext db)
    {
        db.Platforms.AddRange(
            new PlatformEntity
            {
                Id = PlatformId,
                Name = "Super Nintendo",
                Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "Super Nintendo", BaseCompactLabel = "Super Nintendo", CanonicalKey = "snes", ShortName = "snes",
                CreatedAt = DateTimeOffset.UtcNow
            },
            new PlatformEntity
            {
                Id = OtherPlatformId,
                Name = "Nintendo 64",
                Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "Nintendo 64", BaseCompactLabel = "Nintendo 64", CanonicalKey = "n64", ShortName = "n64",
                CreatedAt = DateTimeOffset.UtcNow
            });
        db.CatalogSources.AddRange(
            NewSource(ActiveSourceId, nameof(CatalogSourceStatus.Active)),
            NewSource(DisabledSourceId, nameof(CatalogSourceStatus.Disabled)));
        db.SaveChanges();
        db.ChangeTracker.Clear();
    }
}
