using Microsoft.EntityFrameworkCore;
using Npgsql;
using Romd.Domain.Catalog;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed class TitleCreationStoreTests : IDisposable
{
    private static readonly DateTimeOffset CreatedAt = new DateTimeOffset(2026, 11, 4, 12, 34, 56, TimeSpan.Zero).AddTicks(1234560);
    private readonly PostgreSqlTestDatabase _database = PostgreSqlTestDatabase.Create();

    public TitleCreationStoreTests()
    {
        using var db = _database.CreateContext();
        db.Platforms.Add(new PlatformEntity
        {
            Id = 7,
            Name = "Test",
            ShortName = "test",
            CanonicalKey = "test",
            BaseName = "Test",
            BaseCompactLabel = "Test",
            Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd,
            BuiltInVersion = 1
        });
        db.SaveChanges();
    }

    public void Dispose() => _database.Dispose();

    [Fact]
    public async Task AddStagedAsync_ConcurrentCreations_ReturnTheirOwnGeneratedIdentityAndMetadata()
    {
        async Task<int> CreateAsync(string name)
        {
            await using var db = _database.CreateContext();
            var work = new EfUnitOfWork(db);
            await using var transaction = await work.BeginTransactionAsync();
            var title = Title.CreateNew(7, name, name.ToLowerInvariant(), CreatedAt);
            title.StoreUserLayer(new TitleMetadataPayload { Description = name }, CreatedAt);
            var creation = await new TitleRepository(db).AddStagedAsync(title);
            Should.Throw<InvalidOperationException>(() => creation.ReadIdAfterFlush());
            (await db.Titles.AnyAsync(row => row.NormalizedName == title.NormalizedName)).ShouldBeFalse();
            await work.FlushAsync();
            int id = creation.ReadIdAfterFlush();
            id.ShouldBeGreaterThan(0);
            await using (var outside = _database.CreateContext())
                (await outside.Titles.AnyAsync(row => row.Id == id)).ShouldBeFalse();
            await transaction.CommitAsync();
            return id;
        }

        var ids = await Task.WhenAll(CreateAsync("First"), CreateAsync("Second"));

        ids.Distinct().Count().ShouldBe(2);
        await using var verification = _database.CreateContext();
        for (int i = 0; i < ids.Length; i++)
        {
            string name = i == 0 ? "First" : "Second";
            var row = await verification.Titles.SingleAsync(row => row.Id == ids[i]);
            row.Name.ShouldBe(name);
            row.NormalizedName.ShouldBe(name.ToLowerInvariant());
            row.CreatedAt.ShouldBe(DateTimeOffset.FromUnixTimeMilliseconds(CreatedAt.ToUnixTimeMilliseconds()));
            (await verification.TitleMetadataLayers.SingleAsync(layer => layer.TitleId == row.Id)).MetadataJson.ShouldContain(name);
        }
    }

    [Fact]
    public async Task AddStagedAsync_ConcurrentSameNormalizedName_PreservesExistingUniqueConstraint()
    {
        async Task<(int? Id, string? Conflict)> CreateAsync()
        {
            await using var db = _database.CreateContext();
            var work = new EfUnitOfWork(db);
            await using var transaction = await work.BeginTransactionAsync();
            var creation = await new TitleRepository(db).AddStagedAsync(Title.CreateNew(7, "Same", "same", CreatedAt));
            try
            {
                await work.FlushAsync();
                await transaction.CommitAsync();
                return (creation.ReadIdAfterFlush(), null);
            }
            catch (DbUpdateException error) when (error.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                return (null, PostgresErrorCodes.UniqueViolation);
            }
        }

        var results = await Task.WhenAll(CreateAsync(), CreateAsync());

        results.Count(result => result.Id is > 0).ShouldBe(1);
        results.Count(result => result.Conflict == PostgresErrorCodes.UniqueViolation).ShouldBe(1);
        await using var verification = _database.CreateContext();
        (await verification.Titles.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task AddStagedAsync_WithoutTransactionOrAlreadyCancelled_RejectsBeforeTracking()
    {
        await using var db = _database.CreateContext();
        var repository = new TitleRepository(db);
        var title = Title.CreateNew(7, "New", "new", CreatedAt);
        await Should.ThrowAsync<InvalidOperationException>(() => repository.AddStagedAsync(title));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var error = await Should.ThrowAsync<OperationCanceledException>(() => repository.AddStagedAsync(title, cancellation.Token));
        error.CancellationToken.ShouldBe(cancellation.Token);
        db.ChangeTracker.Entries().ShouldBeEmpty();
    }

    [Fact]
    public async Task AddStagedAsync_ExistingIdentity_RejectsBeforeTracking()
    {
        await using var db = _database.CreateContext();
        db.Titles.Add(new TitleEntity { Id = 42, PlatformId = 7, Name = "Existing", NormalizedName = "existing", EnrichmentStatus = "None" });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var repository = new TitleRepository(db);
        var title = (await repository.GetAggregateAsync(42)).ShouldNotBeNull();
        var work = new EfUnitOfWork(db);
        await using var transaction = await work.BeginTransactionAsync();

        var error = await Should.ThrowAsync<ArgumentException>(() => repository.AddStagedAsync(title));

        error.ParamName.ShouldBe("title");
        db.ChangeTracker.Entries().ShouldBeEmpty();
    }

    [Fact]
    public async Task AddStagedAsync_RollbackAfterFlush_DoesNotLeakIntoLaterFlush()
    {
        await using var db = _database.CreateContext();
        var work = new EfUnitOfWork(db);
        await using var transaction = await work.BeginTransactionAsync();
        var creation = await new TitleRepository(db).AddStagedAsync(Title.CreateNew(7, "Rolled back", "rolledback", CreatedAt));
        await work.FlushAsync();
        creation.ReadIdAfterFlush().ShouldBeGreaterThan(0);

        await transaction.RollbackAsync();
        await work.FlushAsync();

        db.ChangeTracker.Entries().ShouldBeEmpty();
        await using var verification = _database.CreateContext();
        (await verification.Titles.CountAsync()).ShouldBe(0);
    }
}
