using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Catalog;
using Romd.Application.Common.Security;
using Romd.Domain.Hashing;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.Infrastructure.Tests.Helpers;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed class TitleSourceAssignmentStoreTests : IDisposable
{
    private static readonly Guid TestUserId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private static readonly DateTimeOffset InitialTime = new(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);

    private readonly PostgreSqlTestDatabase _connection;
    private readonly DbContextOptions<RomdDbContext> _contextOptions;
    private readonly ManualTimeProvider _timeProvider = new(InitialTime);

    public TitleSourceAssignmentStoreTests()
    {
        _connection = PostgreSqlTestDatabase.Create();

        var auditInterceptor = new AuditInterceptor(new FakeAuditContext(TestUserId), _timeProvider);
        _contextOptions = new DbContextOptionsBuilder<RomdDbContext>()
            .UseNpgsql(_connection.ConnectionString)
            .AddInterceptors(auditInterceptor)
            .Options;

        using var db = CreateDb();
        SeedCatalog(db);
    }

    [Fact]
    public async Task UpsertAssignmentsAsync_InsertUpdateAndEmptyInput_PreservesMappingAndAuditBehavior()
    {
        await using var db = CreateDb();
        ITitleSourceAssignmentStore store = new DatRepository(db, _timeProvider);

        await store.UpsertAssignmentsAsync([
            new TitleSourceAssignment(101, 201),
            new TitleSourceAssignment(102, 201)
        ]);
        db.ChangeTracker.Entries().ShouldBeEmpty();

        var inserted = await db.TitleSourceLinks.OrderBy(link => link.SourceEntryId).ToListAsync();
        inserted.Select(link => (link.SourceEntryId, link.TitleId)).ShouldBe([(101, 201), (102, 201)]);
        inserted.ShouldAllBe(link => link.CreatedByUserId == TestUserId);
        inserted.ShouldAllBe(link => link.CreatedAt == InitialTime);

        _timeProvider.SetUtcNow(InitialTime.AddMinutes(1));
        await store.UpsertAssignmentsAsync([new TitleSourceAssignment(101, 202)]);
        await store.UpsertAssignmentsAsync([]);

        var updated = await db.TitleSourceLinks.SingleAsync(link => link.SourceEntryId == 101);
        updated.TitleId.ShouldBe(202);
        updated.CreatedAt.ShouldBe(InitialTime);
        updated.CreatedByUserId.ShouldBe(TestUserId);
        updated.UpdatedAt.ShouldBe(InitialTime.AddMinutes(1));
        updated.UpdatedByUserId.ShouldBe(TestUserId);
        (await db.TitleSourceLinks.CountAsync()).ShouldBe(2);
    }

    [Fact]
    public async Task UpsertAssignmentsAsync_AmbientTransactionRollback_DoesNotPersistAssignments()
    {
        await using (var db = CreateDb())
        {
            ITitleSourceAssignmentStore store = new DatRepository(db, _timeProvider);
            await using var transaction = await db.Database.BeginTransactionAsync();

            await store.UpsertAssignmentsAsync([new TitleSourceAssignment(101, 201)]);

            db.ChangeTracker.Entries().ShouldBeEmpty();
            (await db.TitleSourceLinks.CountAsync()).ShouldBe(1);
            await transaction.RollbackAsync();
        }

        await using var verificationDb = CreateDb();
        (await verificationDb.TitleSourceLinks.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task AssignmentReads_ExistingAndMissingEntries_ReturnNeutralAssignmentFacts()
    {
        await using var db = CreateDb();
        ITitleSourceAssignmentStore store = new DatRepository(db, _timeProvider);
        await store.UpsertAssignmentsAsync([
            new TitleSourceAssignment(101, 201),
            new TitleSourceAssignment(102, 201)
        ]);

        var assigned = await store.GetAssignmentContextAsync(101);
        var unassigned = await store.GetAssignmentContextAsync(103);
        var missing = await store.GetAssignmentContextAsync(999);
        var sourceEntryIds = await store.GetSourceEntryIdsByTitleAsync(201);

        assigned.ShouldBe(new TitleSourceAssignmentContext(101, 7, 201));
        unassigned.ShouldBe(new TitleSourceAssignmentContext(103, 7, null));
        missing.ShouldBeNull();
        sourceEntryIds.OrderBy(id => id).ShouldBe([101, 102]);
    }

    [Fact]
    public async Task GetAssignmentContextAsync_UnroutedSourceEntry_ReturnsNullPlatformId()
    {
        await using var db = CreateDb();
        ITitleSourceAssignmentStore store = new DatRepository(db, _timeProvider);

        var unrouted = await store.GetAssignmentContextAsync(104);

        unrouted.ShouldBe(new TitleSourceAssignmentContext(104, null, null));
    }

    [Fact]
    public async Task ClearAssignmentsAsync_TargetedAndEmptyInput_PreservesUnrelatedMappings()
    {
        await using var db = CreateDb();
        ITitleSourceAssignmentStore store = new DatRepository(db, _timeProvider);
        await store.UpsertAssignmentsAsync([
            new TitleSourceAssignment(101, 201),
            new TitleSourceAssignment(102, 201)
        ]);

        await store.ClearAssignmentsAsync([]);
        await store.ClearAssignmentsAsync([101]);

        var links = await db.TitleSourceLinks.ToListAsync();
        links.Select(link => (link.SourceEntryId, link.TitleId)).ShouldBe([(102, 201)]);
    }

    [Fact]
    public async Task DatGameDelete_AssignmentSurvives()
    {
        await using var db = CreateDb();
        ITitleSourceAssignmentStore store = new DatRepository(db, _timeProvider);
        await store.UpsertAssignmentsAsync([new TitleSourceAssignment(101, 201)]);

        await db.DatGames.Where(game => game.Id == 101).ExecuteDeleteAsync();

        (await db.TitleSourceLinks.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task SourceEntryDelete_AssignmentCascades()
    {
        await using var db = CreateDb();
        ITitleSourceAssignmentStore store = new DatRepository(db, _timeProvider);
        await store.UpsertAssignmentsAsync([new TitleSourceAssignment(101, 201)]);

        // Games restrict entry deletion, so the version's game goes first.
        await db.DatGames.Where(game => game.Id == 101).ExecuteDeleteAsync();
        await db.SourceEntries.Where(entry => entry.Id == 101).ExecuteDeleteAsync();

        (await db.TitleSourceLinks.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task TitleDelete_AssignmentCascades()
    {
        await using var db = CreateDb();
        ITitleSourceAssignmentStore store = new DatRepository(db, _timeProvider);
        await store.UpsertAssignmentsAsync([new TitleSourceAssignment(101, 201)]);

        await db.Titles.Where(title => title.Id == 201).ExecuteDeleteAsync();

        (await db.TitleSourceLinks.CountAsync()).ShouldBe(0);
    }

    public void Dispose() => _connection.Dispose();

    private RomdDbContext CreateDb() => new(_contextOptions);

    private static void SeedCatalog(RomdDbContext db)
    {
        db.Platforms.Add(new PlatformEntity
        {
            Id = 7,
            Name = "Test Platform",
            Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "Test Platform", BaseCompactLabel = "Test Platform", CanonicalKey = "test", ShortName = "test"
        });
        db.Files.Add(new FileEntityPersistence
        {
            Id = 51,
            Sha256 = NewSha256(51),
            Size = 1,
            SizeOnDisk = 1
        });
        db.Files.Add(new FileEntityPersistence
        {
            Id = 52,
            Sha256 = NewSha256(52),
            Size = 1,
            SizeOnDisk = 1
        });
        db.DatFiles.AddRange(
            new DatFileEntity
            {
                Source = new DatSourceEntity
                {
                    Id = 71,
                    CatalogSource = new CatalogSourceEntity { Id = 71, Kind = "Dat", Status = "Active" }
                },
                Id = 71,
                Name = "Test DAT",
                Description = "Test DAT",
                Type = "NoIntro",
                PlatformId = 7,
                OriginalFilename = "test.dat",
                FileId = 51
            },
            new DatFileEntity
            {
                Source = new DatSourceEntity
                {
                    Id = 72,
                    CatalogSource = new CatalogSourceEntity { Id = 72, Kind = "Dat", Status = "Active" }
                },
                Id = 72,
                Name = "Unrouted DAT",
                Description = "Unrouted DAT",
                Type = "NoIntro",
                PlatformId = null,
                OriginalFilename = "unrouted.dat",
                FileId = 52
            });
        db.SourceEntries.AddRange(
            new SourceEntryEntity { Id = 101, CatalogSourceId = 71, EntryKey = "Game One", Name = "Game One", PlatformId = 7 },
            new SourceEntryEntity { Id = 102, CatalogSourceId = 71, EntryKey = "Game Two", Name = "Game Two", PlatformId = 7 },
            new SourceEntryEntity { Id = 103, CatalogSourceId = 71, EntryKey = "Game Three", Name = "Game Three", PlatformId = 7 },
            new SourceEntryEntity { Id = 104, CatalogSourceId = 72, EntryKey = "Unrouted Game", Name = "Unrouted Game" });
        db.DatGames.AddRange(
            new DatGameEntity { Id = 101, DatFileId = 71, SourceEntryId = 101, Name = "Game One" },
            new DatGameEntity { Id = 102, DatFileId = 71, SourceEntryId = 102, Name = "Game Two" },
            new DatGameEntity { Id = 103, DatFileId = 71, SourceEntryId = 103, Name = "Game Three" },
            new DatGameEntity { Id = 104, DatFileId = 72, SourceEntryId = 104, Name = "Unrouted Game" });
        db.Titles.AddRange(
            new TitleEntity
            {
                Id = 201,
                PlatformId = 7,
                Name = "Title One",
                NormalizedName = "titleone",
                EnrichmentStatus = "None"
            },
            new TitleEntity
            {
                Id = 202,
                PlatformId = 7,
                Name = "Title Two",
                NormalizedName = "titletwo",
                EnrichmentStatus = "None"
            });
        db.SaveChanges();
        db.ChangeTracker.Clear();
    }

    private static Sha256 NewSha256(byte firstByte)
    {
        var bytes = new byte[Sha256.ByteLength];
        bytes[0] = firstByte;
        return Sha256.FromBytes(bytes);
    }

    private sealed class FakeAuditContext(Guid actorId) : IAuditContext
    {
        public Guid ActorId => actorId;
        public bool IsSystem => false;
    }
}
