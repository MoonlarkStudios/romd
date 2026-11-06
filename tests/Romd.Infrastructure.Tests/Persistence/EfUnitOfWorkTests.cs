using Microsoft.EntityFrameworkCore;
using Npgsql;
using Romd.Admin.Application.Common.Persistence;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Dat;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed class EfUnitOfWorkTests
{
    [Fact]
    public async Task CommitAsync_WithoutExplicitSave_PersistsTrackedChanges()
    {
        await using var database = await TestDatabase.CreateAsync();
        var unitOfWork = new EfUnitOfWork(database.Context);

        await using (var transaction = await unitOfWork.BeginTransactionAsync())
        {
            database.Context.Files.Add(NewFile(1));

            await transaction.CommitAsync();
        }

        await using var readContext = database.CreateReadContext();
        (await readContext.Files.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task RollbackAsync_FlushedChanges_ClearsTrackerAndPreventsResurrection()
    {
        await using var database = await TestDatabase.CreateAsync();
        var unitOfWork = new EfUnitOfWork(database.Context);

        await using (var transaction = await unitOfWork.BeginTransactionAsync())
        {
            database.Context.Files.Add(NewFile(2));
            await unitOfWork.FlushAsync();
            database.Context.ChangeTracker.Entries().ShouldNotBeEmpty();

            await transaction.RollbackAsync();
            database.Context.ChangeTracker.Entries().ShouldBeEmpty();
        }

        await database.Context.SaveChangesAsync();
        await using var readContext = database.CreateReadContext();
        (await readContext.Files.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task DisposeAsync_WithoutCommit_RollsBackClearsTrackerAndPreventsResurrection()
    {
        await using var database = await TestDatabase.CreateAsync();
        var unitOfWork = new EfUnitOfWork(database.Context);

        await using (var transaction = await unitOfWork.BeginTransactionAsync())
        {
            database.Context.Files.Add(NewFile(3));
            await unitOfWork.FlushAsync();
            database.Context.ChangeTracker.Entries().ShouldNotBeEmpty();
        }

        database.Context.ChangeTracker.Entries().ShouldBeEmpty();
        await database.Context.SaveChangesAsync();
        await using var readContext = database.CreateReadContext();
        (await readContext.Files.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task CommitAsync_Success_RetainsTrackedStateAfterDisposal()
    {
        await using var database = await TestDatabase.CreateAsync();
        var unitOfWork = new EfUnitOfWork(database.Context);

        await using (var transaction = await unitOfWork.BeginTransactionAsync())
        {
            database.Context.Files.Add(NewFile(4));
            await unitOfWork.FlushAsync();

            await transaction.CommitAsync();
        }

        var entry = database.Context.ChangeTracker.Entries<FileEntityPersistence>().ShouldHaveSingleItem();
        entry.State.ShouldBe(EntityState.Unchanged);
    }

    [Fact]
    public async Task CommitAsync_FinalFlushFails_RollsBackClearsTrackerAndPreventsResurrection()
    {
        await using var database = await TestDatabase.CreateAsync();
        database.Context.Files.Add(NewFile(6));
        await database.Context.SaveChangesAsync();
        database.Context.ChangeTracker.Clear();
        var unitOfWork = new EfUnitOfWork(database.Context);

        await using (var transaction = await unitOfWork.BeginTransactionAsync())
        {
            var duplicate = NewFile(7);
            duplicate.Sha256 = NewFile(6).Sha256;
            database.Context.Files.Add(duplicate);

            await Should.ThrowAsync<DbUpdateException>(() => transaction.CommitAsync());
            database.Context.ChangeTracker.Entries().ShouldBeEmpty();
        }

        database.Context.Files.Add(NewFile(8));
        await database.Context.SaveChangesAsync();
        await using var readContext = database.CreateReadContext();
        (await readContext.Files.OrderBy(row => row.Id).Select(row => row.Id).ToArrayAsync())
            .ShouldBe([6, 8]);
    }

    [Fact]
    public async Task CommitAsync_DeferredConstraintFailure_RollsBackAndClearsTracker()
    {
        await using var database = await TestDatabase.CreateAsync();
        var unitOfWork = new EfUnitOfWork(database.Context);

        await using (var transaction = await unitOfWork.BeginTransactionAsync())
        {
            // Defer the FK so the violation surfaces at commit, which is the path under test.
            await database.Context.Database.ExecuteSqlRawAsync(
                "ALTER TABLE romd.\"DatFiles\" ALTER CONSTRAINT \"FK_DatFiles_Files_FileId\" "
                + "DEFERRABLE INITIALLY DEFERRED;");
            database.Context.DatFiles.Add(new DatFileEntity
            {
                Source = new DatSourceEntity
                {
                    CatalogSource = new CatalogSourceEntity { Kind = "Dat", Status = "Active" }
                },
                Name = "Invalid DAT",
                Description = "Invalid DAT",
                Type = DatType.NoIntro.ToString(),
                OriginalFilename = "invalid.dat",
                FileId = 999,
                CreatedAt = DateTimeOffset.UtcNow
            });
            await unitOfWork.FlushAsync();
            database.Context.ChangeTracker.Entries().ShouldNotBeEmpty();

            await Should.ThrowAsync<PostgresException>(() => transaction.CommitAsync());
            database.Context.ChangeTracker.Entries().ShouldBeEmpty();
        }

        database.Context.Files.Add(NewFile(5));
        await database.Context.SaveChangesAsync();
        await using var readContext = database.CreateReadContext();
        (await readContext.DatFiles.CountAsync()).ShouldBe(0);
        (await readContext.Files.CountAsync()).ShouldBe(1);
    }

    private static FileEntityPersistence NewFile(int id) => new()
    {
        Id = id,
        Sha256 = Sha256.Parse(id.ToString("x64")),
        Size = 1,
        SizeOnDisk = 1,
        CreatedAt = DateTimeOffset.UtcNow
    };

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly PostgreSqlTestDatabase _connection;
        private readonly DbContextOptions<RomdDbContext> _options;

        private TestDatabase(
            PostgreSqlTestDatabase connection,
            DbContextOptions<RomdDbContext> options,
            RomdDbContext context)
        {
            _connection = connection;
            _options = options;
            Context = context;
        }

        public RomdDbContext Context { get; }

        public static async Task<TestDatabase> CreateAsync()
        {
            var connection = PostgreSqlTestDatabase.Create();
            var options = new DbContextOptionsBuilder<RomdDbContext>()
                .UseNpgsql(connection.ConnectionString)
                .Options;
            var context = new RomdDbContext(options);
            return new TestDatabase(connection, options, context);
        }

        public RomdDbContext CreateReadContext() => new(_options);

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
