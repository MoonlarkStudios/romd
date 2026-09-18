using System.Collections.Immutable;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Romd.Admin.Application.Catalog;
using Romd.Domain.Catalog;
using Romd.Domain.Identity;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Infrastructure.Source;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Source;

public sealed class TitlePayloadAvailabilityProjectionTests : IAsyncLifetime
{
    private readonly PostgreSqlTestDatabase _connection = PostgreSqlTestDatabase.Create();
    private DbContextOptions<RomdDbContext> _options = null!;

    public async Task InitializeAsync()
    {
        _options = new DbContextOptionsBuilder<RomdDbContext>()
            .UseNpgsql(_connection.ConnectionString)
            .Options;
        await using var db = CreateDb();
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    [Fact]
    public async Task RefreshPayloadAssertionsAsync_WithoutCallerTransaction_Throws()
    {
        await using var db = CreateDb();
        var projection = CreateProjection(db, AssertionProvider((1, 10)));

        var exception = await Should.ThrowAsync<InvalidOperationException>(() =>
            projection.RefreshPayloadAssertionsAsync([10]));

        exception.Message.ShouldContain("caller-owned transaction");
    }

    [Fact]
    public async Task RefreshPayloadAssertionsAsync_ImportProviderAssertion_DoesNotDependOnDatRows()
    {
        await using var db = CreateDb();
        await SeedAsync(db);
        var projection = CreateProjection(db, AssertionProvider((1, 10)));
        await using var transaction = await db.Database.BeginTransactionAsync();

        await projection.RefreshPayloadAssertionsAsync([10]);
        await transaction.CommitAsync();

        (await db.SourceEntries.AsNoTracking().SingleAsync()).HasLocalPayload.ShouldBeTrue();
        (await db.Titles.AsNoTracking().SingleAsync()).HasLocalPayload.ShouldBeTrue();
        (await db.DatGames.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task RollupTitlesAsync_DisableThenReactivate_IsReversibleWithoutRelinking()
    {
        await using var db = CreateDb();
        await SeedAsync(db);
        var projection = CreateProjection(db, AssertionProvider((1, 10)));
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await projection.RefreshPayloadAssertionsAsync([10]);
            await transaction.CommitAsync();
        }

        int linkCount = await db.TitleSourceLinks.CountAsync();
        await db.CatalogSources.ExecuteUpdateAsync(update =>
            update.SetProperty(source => source.Status, nameof(CatalogSourceStatus.Disabled)));
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await projection.RollupTitlesAsync([10]);
            await transaction.CommitAsync();
        }

        (await db.Titles.AsNoTracking().SingleAsync()).HasLocalPayload.ShouldBeFalse();
        (await db.SourceEntries.AsNoTracking().SingleAsync()).HasLocalPayload.ShouldBeTrue();
        (await db.TitleSourceLinks.CountAsync()).ShouldBe(linkCount);

        await db.CatalogSources.ExecuteUpdateAsync(update =>
            update.SetProperty(source => source.Status, nameof(CatalogSourceStatus.Active)));
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await projection.RollupTitlesAsync([10]);
            await transaction.CommitAsync();
        }

        (await db.Titles.AsNoTracking().SingleAsync()).HasLocalPayload.ShouldBeTrue();
        (await db.TitleSourceLinks.CountAsync()).ShouldBe(linkCount);
    }

    [Fact]
    public async Task RefreshPayloadAssertionsAsync_Rollback_RestoresSourceAndTitleFactsTogether()
    {
        await using var db = CreateDb();
        await SeedAsync(db);
        var projection = CreateProjection(db, AssertionProvider((1, 10)));
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await projection.RefreshPayloadAssertionsAsync([10]);
            (await db.Titles.AsNoTracking().SingleAsync()).HasLocalPayload.ShouldBeTrue();
            await transaction.RollbackAsync();
        }

        db.ChangeTracker.Clear();
        (await db.SourceEntries.AsNoTracking().SingleAsync()).HasLocalPayload.ShouldBeFalse();
        (await db.Titles.AsNoTracking().SingleAsync()).HasLocalPayload.ShouldBeFalse();
    }

    [Fact]
    public async Task RefreshPayloadAssertionsAsync_FirstAndLastPayloadAcrossSources_ChangesOnlyAtTruthBoundaries()
    {
        await using var db = CreateDb();
        await SeedAsync(db);
        var now = DateTimeOffset.UtcNow;
        db.CatalogSources.Add(new CatalogSourceEntity
        {
            Id = 8,
            Kind = nameof(CatalogSourceKind.Import),
            Status = nameof(CatalogSourceStatus.Active),
            CreatedAt = now,
            CreatedByUserId = SystemActor.UserId
        });
        db.SourceEntries.Add(new SourceEntryEntity
        {
            Id = 2,
            CatalogSourceId = 8,
            EntryKey = "manual:payload",
            Name = "Manual payload",
            PlatformId = 3,
            CreatedAt = now,
            CreatedByUserId = SystemActor.UserId
        });
        db.TitleSourceLinks.Add(new TitleSourceLinkEntity
        {
            SourceEntryId = 2,
            TitleId = 10,
            CreatedAt = now,
            CreatedByUserId = SystemActor.UserId
        });
        await db.SaveChangesAsync();

        await RefreshAsync(db, AssertionProvider((1, 10), (2, 10)));
        (await db.Titles.AsNoTracking().SingleAsync()).HasLocalPayload.ShouldBeTrue();

        await RefreshAsync(db, AssertionProvider((2, 10)));
        (await db.SourceEntries.AsNoTracking().SingleAsync(entry => entry.Id == 1))
            .HasLocalPayload.ShouldBeFalse();
        (await db.Titles.AsNoTracking().SingleAsync()).HasLocalPayload.ShouldBeTrue();

        await RefreshAsync(db, AssertionProvider());
        (await db.Titles.AsNoTracking().SingleAsync()).HasLocalPayload.ShouldBeFalse();
    }

    [Fact]
    public async Task AuditBatchAsync_CorruptCleanFact_RepairsBoundedKeysetPage()
    {
        await using var db = CreateDb();
        await SeedAsync(db);
        var projection = CreateProjection(db, AssertionProvider((1, 10)));
        await using var transaction = await db.Database.BeginTransactionAsync();

        var result = await projection.AuditBatchAsync(null, 1);
        await transaction.CommitAsync();

        result.ProcessedCount.ShouldBe(1);
        result.NextTitleId.ShouldBe(10);
        (await db.Titles.AsNoTracking().SingleAsync()).HasLocalPayload.ShouldBeTrue();
    }

    [Fact]
    public async Task AssertionReader_ScopesProvidersByKind_AndUnionsOwnedFacts()
    {
        await using var db = CreateDb();
        await SeedAsync(db);
        await AddSourceEntryAsync(db, 8, CatalogSourceKind.Dat, 2);
        var import = AssertionProvider(CatalogSourceKind.Import, (1, 10));
        var dat = AssertionProvider(CatalogSourceKind.Dat, (2, 10));
        var reader = new CatalogPayloadAssertionReader(db, [import, dat]);

        var result = await reader.ReadAsync([1, 2]);

        result.Select(assertion => assertion.SourceEntryId).ShouldBe([1, 2]);
        await import.Received(1).ReadAsync(
            Arg.Is<IReadOnlyCollection<int>>(ids => ids.SequenceEqual(new[] { 1 })),
            Arg.Any<CancellationToken>());
        await dat.Received(1).ReadAsync(
            Arg.Is<IReadOnlyCollection<int>>(ids => ids.SequenceEqual(new[] { 2 })),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AssertionReader_DuplicateProviderKind_Throws()
    {
        await using var db = CreateDb();
        await SeedAsync(db);
        var reader = new CatalogPayloadAssertionReader(
            db,
            [AssertionProvider((1, 10)), AssertionProvider((1, 10))]);

        var exception = await Should.ThrowAsync<InvalidOperationException>(() => reader.ReadAsync([1]));

        exception.Message.ShouldContain("Multiple payload assertion providers");
    }

    [Fact]
    public async Task AssertionReader_MissingProvider_ThrowsBeforeProjectionCanClearFacts()
    {
        await using var db = CreateDb();
        await SeedAsync(db);
        await db.SourceEntries.ExecuteUpdateAsync(update =>
            update.SetProperty(entry => entry.HasLocalPayload, true));
        await db.Titles.ExecuteUpdateAsync(update =>
            update.SetProperty(title => title.HasLocalPayload, true));
        var projection = new TitlePayloadAvailabilityProjection(
            db,
            new CatalogPayloadAssertionReader(db, []));
        await using var transaction = await db.Database.BeginTransactionAsync();

        var exception = await Should.ThrowAsync<InvalidOperationException>(() =>
            projection.RefreshPayloadAssertionsAsync([10]));
        await transaction.RollbackAsync();

        exception.Message.ShouldContain("No catalog payload assertion provider");
        db.ChangeTracker.Clear();
        (await db.SourceEntries.AsNoTracking().SingleAsync()).HasLocalPayload.ShouldBeTrue();
        (await db.Titles.AsNoTracking().SingleAsync()).HasLocalPayload.ShouldBeTrue();
    }

    [Fact]
    public async Task AssertionSynchronizer_WithoutCallerTransaction_ThrowsBeforeProviderMutation()
    {
        await using var db = CreateDb();
        await SeedAsync(db);
        var provider = AssertionProvider();
        var synchronizer = new CatalogPayloadAssertionSynchronizer(db, [provider]);

        var exception = await Should.ThrowAsync<InvalidOperationException>(() =>
            synchronizer.SynchronizeCatalogSourceAsync(7));

        exception.Message.ShouldContain("caller-owned transaction");
        await provider.DidNotReceiveWithAnyArgs().SynchronizeCatalogSourceAsync(default);
    }

    [Fact]
    public async Task AssertionSynchronizer_MissingProvider_ThrowsBeforeAnyProviderMutation()
    {
        await using var db = CreateDb();
        await SeedAsync(db);
        await AddSourceEntryAsync(db, 8, CatalogSourceKind.Dat, 2);
        var import = AssertionProvider();
        var synchronizer = new CatalogPayloadAssertionSynchronizer(db, [import]);
        await using var transaction = await db.Database.BeginTransactionAsync();

        var exception = await Should.ThrowAsync<InvalidOperationException>(() =>
            synchronizer.SynchronizePlatformAsync(3));

        exception.Message.ShouldContain("source kind 'Dat'");
        await import.DidNotReceiveWithAnyArgs().SynchronizePlatformAsync(default);
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task AssertionSynchronizer_DuplicateProviderKind_ThrowsAtComposition()
    {
        await using var db = CreateDb();

        var exception = Should.Throw<InvalidOperationException>(() =>
            new CatalogPayloadAssertionSynchronizer(
                db,
                [AssertionProvider(), AssertionProvider()]));

        exception.Message.ShouldContain("multiple providers");
    }

    [Fact]
    public async Task AssertionSynchronizer_MixedKinds_RoutesEachKindExactlyOnce()
    {
        await using var db = CreateDb();
        await SeedAsync(db);
        await AddSourceEntryAsync(db, 8, CatalogSourceKind.Dat, 2);
        var import = AssertionProvider();
        var dat = AssertionProvider(CatalogSourceKind.Dat);
        var synchronizer = new CatalogPayloadAssertionSynchronizer(db, [import, dat]);
        await using var transaction = await db.Database.BeginTransactionAsync();

        await synchronizer.SynchronizePlatformAsync(3);

        await import.Received(1).SynchronizePlatformAsync(3, Arg.Any<CancellationToken>());
        await dat.Received(1).SynchronizePlatformAsync(3, Arg.Any<CancellationToken>());
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task AssertionSynchronizer_ProviderMutation_RollsBackWithCallerTransaction()
    {
        await using var db = CreateDb();
        await SeedAsync(db);
        var provider = AssertionProvider();
        provider.SynchronizeCatalogSourceAsync(7, Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                await db.SourceEntries
                    .Where(entry => entry.CatalogSourceId == (int)call[0])
                    .ExecuteUpdateAsync(update =>
                        update.SetProperty(entry => entry.HasLocalPayload, true));
            });
        var synchronizer = new CatalogPayloadAssertionSynchronizer(db, [provider]);
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await synchronizer.SynchronizeCatalogSourceAsync(7);
            (await db.SourceEntries.AsNoTracking().SingleAsync()).HasLocalPayload.ShouldBeTrue();
            await transaction.RollbackAsync();
        }

        db.ChangeTracker.Clear();
        (await db.SourceEntries.AsNoTracking().SingleAsync()).HasLocalPayload.ShouldBeFalse();
    }

    [Fact]
    public async Task AssertionReader_ProviderReturningAnotherKindsEntry_Throws()
    {
        await using var db = CreateDb();
        await SeedAsync(db);
        await AddSourceEntryAsync(db, 8, CatalogSourceKind.Dat, 2);
        var dat = Substitute.For<ICatalogPayloadAssertionProvider>();
        dat.SourceKind.Returns(CatalogSourceKind.Dat);
        dat.ReadAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns([new CatalogPayloadAssertion(1, 10, true)]);
        var reader = new CatalogPayloadAssertionReader(db, [AssertionProvider(), dat]);

        var exception = await Should.ThrowAsync<InvalidOperationException>(() => reader.ReadAsync([1, 2]));

        exception.Message.ShouldContain("does not own");
    }

    [Fact]
    public async Task DatProvider_DoesNotAssertImportEntryEvenWhenDatPayloadReferencesItsId()
    {
        await using var db = CreateDb();
        await SeedAsync(db);
        await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlRawAsync("SET session_replication_role = replica;");
        var now = DateTimeOffset.UtcNow;
        db.DatGames.Add(new DatGameEntity
        {
            Id = 90,
            DatFileId = 90,
            SourceEntryId = 1,
            Name = "foreign-kind collision",
            CreatedAt = now,
            CreatedByUserId = SystemActor.UserId
        });
        db.DatRoms.Add(new DatRomEntity
        {
            Id = 91,
            DatGameId = 90,
            Name = "collision.rom",
            Size = 1,
            RomFileId = 90,
            CreatedAt = now,
            CreatedByUserId = SystemActor.UserId
        });
        await db.SaveChangesAsync();

        var result = await new DatCatalogPayloadAssertionProvider(db).ReadAsync([1]);

        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task DatProvider_LocalBiosEntryWithoutTitle_ReportsNullAssertedTitleId()
    {
        await using var db = CreateDb();
        await SeedAsync(db);
        await db.Database.OpenConnectionAsync();
        await db.Database.ExecuteSqlRawAsync("SET session_replication_role = replica;");
        var now = DateTimeOffset.UtcNow;
        db.CatalogSources.Add(new CatalogSourceEntity
        {
            Id = 8,
            Kind = nameof(CatalogSourceKind.Dat),
            Status = nameof(CatalogSourceStatus.Active),
            CreatedAt = now,
            CreatedByUserId = SystemActor.UserId
        });
        db.SourceEntries.Add(new SourceEntryEntity
        {
            Id = 2,
            CatalogSourceId = 8,
            EntryKey = "bios:unlinked",
            Name = "Unlinked BIOS",
            PlatformId = 3,
            CreatedAt = now,
            CreatedByUserId = SystemActor.UserId
        });
        db.DatGames.Add(new DatGameEntity
        {
            Id = 92,
            DatFileId = 92,
            SourceEntryId = 2,
            Name = "Unlinked BIOS",
            IsBios = true,
            CreatedAt = now,
            CreatedByUserId = SystemActor.UserId
        });
        db.DatRoms.Add(new DatRomEntity
        {
            Id = 93,
            DatGameId = 92,
            Name = "bios.bin",
            Size = 1,
            RomFileId = 93,
            CreatedAt = now,
            CreatedByUserId = SystemActor.UserId
        });
        await db.SaveChangesAsync();

        var result = await new DatCatalogPayloadAssertionProvider(db).ReadAsync([2]);

        result.ShouldHaveSingleItem();
        result[0].SourceEntryId.ShouldBe(2);
        result[0].HasLocalPayload.ShouldBeTrue();
        result[0].AssertedTitleId.ShouldBeNull();
    }

    [Fact]
    public async Task RollupTitlesAsync_SparseExactIds_RemainsCorrect()
    {
        await using var db = CreateDb();
        await SeedAsync(db);
        var now = DateTimeOffset.UtcNow;
        var titleIds = Enumerable.Range(1, 130).Select(index => index * 10_000).ToArray();
        foreach (int titleId in titleIds)
        {
            int entryId = titleId + 1;
            db.Titles.Add(new TitleEntity
            {
                Id = titleId,
                PlatformId = 3,
                Name = $"Sparse {titleId}",
                NormalizedName = $"sparse{titleId}",
                EnrichmentStatus = nameof(EnrichmentStatus.None),
                CreatedAt = now,
                CreatedByUserId = SystemActor.UserId
            });
            db.SourceEntries.Add(new SourceEntryEntity
            {
                Id = entryId,
                CatalogSourceId = 7,
                EntryKey = $"import:{titleId}",
                Name = $"Sparse {titleId}",
                PlatformId = 3,
                HasLocalPayload = true,
                CreatedAt = now,
                CreatedByUserId = SystemActor.UserId
            });
            db.TitleSourceLinks.Add(new TitleSourceLinkEntity
            {
                SourceEntryId = entryId,
                TitleId = titleId,
                CreatedAt = now,
                CreatedByUserId = SystemActor.UserId
            });
        }
        await db.SaveChangesAsync();
        var projection = CreateProjection(db, AssertionProvider());
        await using var transaction = await db.Database.BeginTransactionAsync();

        await projection.RollupTitlesAsync(titleIds);
        await transaction.CommitAsync();

        (await db.Titles.CountAsync(title => titleIds.Contains(title.Id) && title.HasLocalPayload))
            .ShouldBe(titleIds.Length);
    }

    [Fact]
    public async Task RollupTitlesAsync_MixedActiveAndDormantSources_UsesPersistedFactsOnly()
    {
        await using var db = CreateDb();
        await SeedAsync(db);
        await AddSourceEntryAsync(db, 8, CatalogSourceKind.Import, 2);
        db.TitleSourceLinks.Add(new TitleSourceLinkEntity
        {
            SourceEntryId = 2,
            TitleId = 10,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = SystemActor.UserId
        });
        await db.SaveChangesAsync();
        await db.SourceEntries.ExecuteUpdateAsync(update =>
            update.SetProperty(entry => entry.HasLocalPayload, true));
        await db.CatalogSources.Where(source => source.Id == 8).ExecuteUpdateAsync(update =>
            update.SetProperty(source => source.Status, nameof(CatalogSourceStatus.Disabled)));
        var provider = AssertionProvider((1, 10), (2, 10));
        var projection = CreateProjection(db, provider);

        await RollupAsync(db, projection);
        (await db.Titles.AsNoTracking().SingleAsync()).HasLocalPayload.ShouldBeTrue();

        await db.CatalogSources.Where(source => source.Id == 7).ExecuteUpdateAsync(update =>
            update.SetProperty(source => source.Status, nameof(CatalogSourceStatus.Disabled)));
        await RollupAsync(db, projection);
        (await db.Titles.AsNoTracking().SingleAsync()).HasLocalPayload.ShouldBeFalse();

        await db.CatalogSources.Where(source => source.Id == 8).ExecuteUpdateAsync(update =>
            update.SetProperty(source => source.Status, nameof(CatalogSourceStatus.Active)));
        await RollupAsync(db, projection);
        (await db.Titles.AsNoTracking().SingleAsync()).HasLocalPayload.ShouldBeTrue();
        await provider.DidNotReceiveWithAnyArgs().ReadAsync(default!);
    }

    [Fact]
    public async Task CleanAudit_MismatchOnlyUpdates_DoNotRewriteStoredFacts()
    {
        await using var db = CreateDb();
        await SeedAsync(db);
        var projection = CreateProjection(db, AssertionProvider());
        await using var transaction = await db.Database.BeginTransactionAsync();
        long before = await TotalChangesAsync(db);

        var result = await projection.AuditBatchAsync(null, 1);

        (await TotalChangesAsync(db)).ShouldBe(before);
        result.ProcessedCount.ShouldBe(1);
        await transaction.RollbackAsync();
    }

    private RomdDbContext CreateDb() => new(_options);

    // Rows written by the current transaction on this connection, mirroring SQLite's total_changes().
    private static Task<long> TotalChangesAsync(RomdDbContext db) =>
        db.Database
            .SqlQueryRaw<long>(
                $"""
                 SELECT coalesce(sum(n_tup_ins + n_tup_upd + n_tup_del), 0)::bigint AS "Value"
                 FROM pg_stat_xact_all_tables
                 WHERE schemaname = '{PostgreSqlConfiguration.SchemaName}'
                 """)
            .SingleAsync();

    private static TitlePayloadAvailabilityProjection CreateProjection(
        RomdDbContext db,
        ICatalogPayloadAssertionProvider provider) =>
        new(db, new CatalogPayloadAssertionReader(db, [provider]));

    private static ICatalogPayloadAssertionProvider AssertionProvider(
        params (int SourceEntryId, int AssertedTitleId)[] assertions) =>
        AssertionProvider(CatalogSourceKind.Import, assertions);

    private static ICatalogPayloadAssertionProvider AssertionProvider(
        CatalogSourceKind kind,
        params (int SourceEntryId, int AssertedTitleId)[] assertions)
    {
        var provider = Substitute.For<ICatalogPayloadAssertionProvider>();
        provider.SourceKind.Returns(kind);
        provider.ReadAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns(call => ((IReadOnlyCollection<int>)call[0])
                .Join(assertions, id => id, assertion => assertion.SourceEntryId,
                    (_, assertion) => new CatalogPayloadAssertion(
                        assertion.SourceEntryId,
                        assertion.AssertedTitleId,
                        true))
                .ToImmutableArray());
        return provider;
    }

    private static async Task AddSourceEntryAsync(
        RomdDbContext db,
        int sourceId,
        CatalogSourceKind kind,
        int entryId)
    {
        var now = DateTimeOffset.UtcNow;
        db.CatalogSources.Add(new CatalogSourceEntity
        {
            Id = sourceId,
            Kind = kind.ToString(),
            Status = nameof(CatalogSourceStatus.Active),
            CreatedAt = now,
            CreatedByUserId = SystemActor.UserId
        });
        db.SourceEntries.Add(new SourceEntryEntity
        {
            Id = entryId,
            CatalogSourceId = sourceId,
            EntryKey = $"{kind}:{entryId}",
            Name = $"{kind} entry",
            PlatformId = 3,
            CreatedAt = now,
            CreatedByUserId = SystemActor.UserId
        });
        await db.SaveChangesAsync();
    }

    private static async Task RefreshAsync(
        RomdDbContext db,
        ICatalogPayloadAssertionProvider provider)
    {
        await using var transaction = await db.Database.BeginTransactionAsync();
        await CreateProjection(db, provider).RefreshPayloadAssertionsAsync([10]);
        await transaction.CommitAsync();
    }

    private static async Task RollupAsync(
        RomdDbContext db,
        ITitlePayloadAvailabilityProjection projection)
    {
        await using var transaction = await db.Database.BeginTransactionAsync();
        await projection.RollupTitlesAsync([10]);
        await transaction.CommitAsync();
    }

    private static async Task SeedAsync(RomdDbContext db)
    {
        var now = DateTimeOffset.UtcNow;
        db.Platforms.Add(new PlatformEntity
        {
            Id = 3,
            Name = "Import Platform",
            Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "Import Platform", BaseCompactLabel = "Import Platform", CanonicalKey = "import-platform", ShortName = "import-platform",
            CreatedAt = now,
            CreatedByUserId = SystemActor.UserId
        });
        db.CatalogSources.Add(new CatalogSourceEntity
        {
            Id = 7,
            Kind = nameof(CatalogSourceKind.Import),
            Status = nameof(CatalogSourceStatus.Active),
            CreatedAt = now,
            CreatedByUserId = SystemActor.UserId
        });
        db.SourceEntries.Add(new SourceEntryEntity
        {
            Id = 1,
            CatalogSourceId = 7,
            EntryKey = "import:payload",
            Name = "Imported payload",
            PlatformId = 3,
            CreatedAt = now,
            CreatedByUserId = SystemActor.UserId
        });
        db.Titles.Add(new TitleEntity
        {
            Id = 10,
            PlatformId = 3,
            Name = "Imported title",
            NormalizedName = "importedtitle",
            EnrichmentStatus = nameof(EnrichmentStatus.None),
            CreatedAt = now,
            CreatedByUserId = SystemActor.UserId
        });
        db.TitleSourceLinks.Add(new TitleSourceLinkEntity
        {
            SourceEntryId = 1,
            TitleId = 10,
            CreatedAt = now,
            CreatedByUserId = SystemActor.UserId
        });
        await db.SaveChangesAsync();
    }
}
