using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Common.Realtime;
using Romd.Admin.Application.Libraries;
using Romd.Admin.Application.Source.Dat;
using Romd.Application.Common.Ids;
using Romd.Contracts.Management.Realtime;
using Romd.Domain.Catalog.Ratings;
using Romd.Domain.Libraries;
using Romd.Infrastructure.Libraries;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.Infrastructure.Realtime;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Libraries;

public sealed class LibraryMaterializationServiceTests
{
    [Fact]
    public async Task MaterializeAsync_ValidConfiguration_TryMarksMaterializedWithStartToken()
    {
        await using var database = await CreateDatabaseAsync();
        var libraryRepo = Substitute.For<ILibraryRepository>();
        var dataProvider = Substitute.For<IMaterializationDataProvider>();
        var realtimeOutbox = Substitute.For<IAdminEventOutbox>();
        var unitOfWork = Substitute.For<IUnitOfWork>();
        var transaction = Substitute.For<ITransaction>();
        var library = NewLibrary(7, "Fresh", needsMaterialization: true);
        var updatedLibrary = NewLibrary(7, "Fresh", needsMaterialization: false, itemCount: 1);
        library.FlagForRematerialization();
        var materializationToken = library.MaterializationRevision;
        var candidates = new[]
        {
            new TitleCandidates(
                TitleId: 1,
                PlatformId: 1,
                Genre: null,
                ContentRatings:
                [
                    new ContentRating
                    {
                        Board = RatingBoard.Esrb,
                        Code = "E",
                        Designation = RatingDesignation.Rated,
                        MinimumAge = 0,
                        SourceId = "test"
                    }
                ],
                Candidates:
                [
                    new GameCandidate(
                        DatGameId: 1,
                        CatalogReleaseId: 1,
                        DatFileId: 1,
                        Revision: null,
                        HasOwnedRoms: true,
                        IsComplete: true,
                        SourceDatFileIds: [1],
                        RegionIds: [],
                        LanguageIds: [])
                ])
        };
        var service = new LibraryMaterializationService(
            libraryRepo,
            dataProvider,
            CleanCatalogProjection(),
            realtimeOutbox,
            unitOfWork,
            Substitute.For<ILogger<LibraryMaterializationService>>());

        unitOfWork.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(transaction);
        libraryRepo.GetByIdAsync(7, Arg.Any<CancellationToken>())
            .Returns(library, updatedLibrary);
        libraryRepo.TryReplaceMaterializedProjectionsAndActivateAsync(
                library.Id,
                Arg.Any<MaterializedLibraryProjection>(),
                1,
                materializationToken,
                Arg.Any<CancellationToken>())
            .Returns(true);
        dataProvider.GetCandidatesAsync(library.Configuration, Arg.Any<CancellationToken>())
            .Returns(candidates);

        var result = await service.MaterializeAsync(7);

        result.ShouldBe(new MaterializationResult(
            IncludedReleaseCount: 1,
            ExcludedTitleCount: 0,
            TotalTitleCount: 1));
        await libraryRepo.Received(1).TryReplaceMaterializedProjectionsAndActivateAsync(
            library.Id,
            Arg.Any<MaterializedLibraryProjection>(),
            1,
            materializationToken,
            Arg.Any<CancellationToken>());
        await realtimeOutbox.Received(1).EnqueueAsync(
            AdminRealtimeEventTypes.LibraryUpdated,
            Arg.Is<AdminRealtimeLibraryUpdatedPayload>(payload =>
                payload.LibraryId == IdCoder.Encode(7) &&
                payload.Name == "Fresh" &&
                !payload.NeedsMaterialization &&
                payload.ItemCount == 1 &&
                payload.ConfigurationState == LibraryConfigurationState.Valid.ToString()),
            Arg.Any<CancellationToken>());
        await transaction.Received(1).CommitAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MaterializeAsync_InvalidPersistedConfiguration_FailsClosedAtomically()
    {
        await using var database = await CreateDatabaseAsync();
        var libraryRepo = Substitute.For<ILibraryRepository>();
        var dataProvider = Substitute.For<IMaterializationDataProvider>();
        var realtimeOutbox = Substitute.For<IAdminEventOutbox>();
        var unitOfWork = Substitute.For<IUnitOfWork>();
        var transaction = Substitute.For<ITransaction>();
        var library = NewLibrary(7, "Broken", needsMaterialization: true);
        library.MarkConfigurationInvalid("bad reference");
        var service = new LibraryMaterializationService(
            libraryRepo,
            dataProvider,
            CleanCatalogProjection(),
            realtimeOutbox,
            unitOfWork,
            Substitute.For<ILogger<LibraryMaterializationService>>());

        unitOfWork.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(transaction);
        libraryRepo.GetByIdAsync(7, Arg.Any<CancellationToken>())
            .Returns(library);

        var result = await service.MaterializeAsync(7);

        result.ShouldBe(new MaterializationResult(
            IncludedReleaseCount: 0,
            ExcludedTitleCount: 0,
            TotalTitleCount: 0));
        await libraryRepo.Received(1)
            .TryReplaceMaterializedProjectionsAndMarkConfigurationInvalidAsync(
                library.Id,
                "bad reference",
                library.MaterializationRevision,
                Arg.Any<CancellationToken>());
        await realtimeOutbox.Received(1).EnqueueAsync(
            AdminRealtimeEventTypes.LibraryUpdated,
            Arg.Is<AdminRealtimeLibraryUpdatedPayload>(payload =>
                payload.LibraryId == IdCoder.Encode(7) &&
                payload.Name == "Broken" &&
                !payload.NeedsMaterialization &&
                payload.ItemCount == 0 &&
                payload.ConfigurationState == LibraryConfigurationState.Invalid.ToString()),
            Arg.Any<CancellationToken>());
        await dataProvider.DidNotReceiveWithAnyArgs().GetCandidatesAsync(default!, default);
        await transaction.Received(1).CommitAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MaterializeAsync_OutboxSaveFails_RollsBackLibraryActivationAndEventRow()
    {
        await using var connection = PostgreSqlTestDatabase.Create();
        var interceptor = new FailOutboxSaveInterceptor();
        var options = new DbContextOptionsBuilder<RomdDbContext>()
            .UseNpgsql(connection.ConnectionString)
            .AddInterceptors(interceptor)
            .Options;
        await using var context = new RomdDbContext(options);
        var token = DateTimeOffset.UtcNow.AddMinutes(-5);
        context.Libraries.Add(new LibraryEntity
        {
            Id = 7,
            Name = "Atomic",
            ConfigurationJson = "{}",
            ConfigurationState = LibraryConfigurationState.Valid.ToString(),
            NeedsMaterialization = true,
            ItemCount = 3,
            UpdatedAt = token,
            CreatedAt = token
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var repository = new LibraryRepository(context);
        var dataProvider = Substitute.For<IMaterializationDataProvider>();
        dataProvider.GetCandidatesAsync(Arg.Any<LibraryConfiguration>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<TitleCandidates>());
        var service = new LibraryMaterializationService(
            repository,
            dataProvider,
            CleanCatalogProjection(),
            new AdminRealtimeOutbox(context, TimeProvider.System),
            new EfUnitOfWork(context),
            Substitute.For<ILogger<LibraryMaterializationService>>());

        await Should.ThrowAsync<DbUpdateException>(() => service.MaterializeAsync(7));

        interceptor.Enabled = false;
        await context.SaveChangesAsync();

        var assertOptions = new DbContextOptionsBuilder<RomdDbContext>()
            .UseNpgsql(connection.ConnectionString)
            .Options;
        await using var assertContext = new RomdDbContext(assertOptions);
        var library = await assertContext.Libraries.SingleAsync(entity => entity.Id == 7);
        library.NeedsMaterialization.ShouldBeTrue();
        library.ItemCount.ShouldBe(3);
        library.LastMaterializedAt.ShouldBeNull();
        library.MaterializationGeneration.ShouldBe(0);
        library.UpdatedAt.ShouldNotBeNull();
        library.UpdatedAt.Value.ToUnixTimeMilliseconds().ShouldBe(token.ToUnixTimeMilliseconds());
        (await assertContext.MaterializedLibraryTitles.CountAsync()).ShouldBe(0);
        (await assertContext.MaterializedLibraryReleases.CountAsync()).ShouldBe(0);
        (await assertContext.AdminRealtimeOutboxEvents.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task MaterializeAsync_InvalidConfiguration_PreservesPreEnlistedEventAndCommitsLibraryEvent()
    {
        await using var database = await CreateDatabaseAsync();
        var token = DateTimeOffset.UtcNow.AddMinutes(-5);
        database.Context.Libraries.Add(new LibraryEntity
        {
            Id = 7,
            Name = "Invalid",
            ConfigurationJson = "{}",
            ConfigurationState = LibraryConfigurationState.Invalid.ToString(),
            ConfigurationError = "bad reference",
            NeedsMaterialization = true,
            ItemCount = 3,
            UpdatedAt = token,
            CreatedAt = token
        });
        await database.Context.SaveChangesAsync();
        database.Context.ChangeTracker.Clear();

        var outbox = new AdminRealtimeOutbox(database.Context, TimeProvider.System);
        await outbox.EnqueueAsync(AdminRealtimeEventTypes.StorageStatsChanged);
        var service = new LibraryMaterializationService(
            new LibraryRepository(database.Context),
            Substitute.For<IMaterializationDataProvider>(),
            CleanCatalogProjection(),
            outbox,
            new EfUnitOfWork(database.Context),
            Substitute.For<ILogger<LibraryMaterializationService>>());

        await service.MaterializeAsync(7);

        database.Context.ChangeTracker.Clear();
        var library = await database.Context.Libraries.SingleAsync(entity => entity.Id == 7);
        library.ConfigurationState.ShouldBe(LibraryConfigurationState.Invalid.ToString());
        library.NeedsMaterialization.ShouldBeFalse();
        library.ItemCount.ShouldBe(0);
        var events = await database.Context.AdminRealtimeOutboxEvents
            .OrderBy(entity => entity.Id)
            .Select(entity => entity.EventType)
            .ToListAsync();
        events.ShouldBe([
            AdminRealtimeEventTypes.StorageStatsChanged,
            AdminRealtimeEventTypes.LibraryUpdated
        ]);
    }

    [Fact]
    public async Task MaterializeAsync_ValidConfiguration_PreservesPreEnlistedEventAndCommitsLibraryEvent()
    {
        await using var database = await CreateDatabaseAsync();
        var token = DateTimeOffset.UtcNow.AddMinutes(-5);
        database.Context.Libraries.Add(new LibraryEntity
        {
            Id = 7,
            Name = "Valid",
            ConfigurationJson = "{}",
            ConfigurationState = LibraryConfigurationState.Valid.ToString(),
            NeedsMaterialization = true,
            ItemCount = 3,
            UpdatedAt = token,
            CreatedAt = token
        });
        await database.Context.SaveChangesAsync();
        database.Context.ChangeTracker.Clear();

        var dataProvider = Substitute.For<IMaterializationDataProvider>();
        dataProvider.GetCandidatesAsync(Arg.Any<LibraryConfiguration>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<TitleCandidates>());
        var outbox = new AdminRealtimeOutbox(database.Context, TimeProvider.System);
        await outbox.EnqueueAsync(AdminRealtimeEventTypes.StorageStatsChanged);
        var service = new LibraryMaterializationService(
            new LibraryRepository(database.Context),
            dataProvider,
            CleanCatalogProjection(),
            outbox,
            new EfUnitOfWork(database.Context),
            Substitute.For<ILogger<LibraryMaterializationService>>());

        await service.MaterializeAsync(7);

        database.Context.ChangeTracker.Clear();
        var library = await database.Context.Libraries.SingleAsync(entity => entity.Id == 7);
        library.NeedsMaterialization.ShouldBeFalse();
        library.ItemCount.ShouldBe(0);
        library.LastMaterializedAt.ShouldNotBeNull();
        var events = await database.Context.AdminRealtimeOutboxEvents
            .OrderBy(entity => entity.Id)
            .Select(entity => entity.EventType)
            .ToListAsync();
        events.ShouldBe([
            AdminRealtimeEventTypes.StorageStatsChanged,
            AdminRealtimeEventTypes.LibraryUpdated
        ]);
    }

    [Fact]
    public async Task MaterializeAsync_CommitFails_RollsBackActivationAndBothPreEnlistedAndLibraryEvents()
    {
        await using var database = await CreateDatabaseAsync();
        var token = DateTimeOffset.UtcNow.AddMinutes(-5);
        database.Context.Libraries.Add(new LibraryEntity
        {
            Id = 7,
            Name = "Commit failure",
            ConfigurationJson = "{}",
            ConfigurationState = LibraryConfigurationState.Valid.ToString(),
            NeedsMaterialization = true,
            ItemCount = 3,
            UpdatedAt = token,
            CreatedAt = token
        });
        await database.Context.SaveChangesAsync();
        database.Context.ChangeTracker.Clear();

        var dataProvider = Substitute.For<IMaterializationDataProvider>();
        dataProvider.GetCandidatesAsync(Arg.Any<LibraryConfiguration>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<TitleCandidates>());
        var outbox = new AdminRealtimeOutbox(database.Context, TimeProvider.System);
        await outbox.EnqueueAsync(AdminRealtimeEventTypes.StorageStatsChanged);
        var service = new LibraryMaterializationService(
            new LibraryRepository(database.Context),
            dataProvider,
            CleanCatalogProjection(),
            outbox,
            new RollbackThenThrowUnitOfWork(database.Context),
            Substitute.For<ILogger<LibraryMaterializationService>>());

        await Should.ThrowAsync<InvalidOperationException>(() => service.MaterializeAsync(7));
        database.Context.ChangeTracker.Entries().ShouldBeEmpty();
        await database.Context.SaveChangesAsync();

        database.Context.ChangeTracker.Clear();
        var library = await database.Context.Libraries.SingleAsync(entity => entity.Id == 7);
        library.NeedsMaterialization.ShouldBeTrue();
        library.ItemCount.ShouldBe(3);
        library.LastMaterializedAt.ShouldBeNull();
        library.UpdatedAt.ShouldNotBeNull();
        library.UpdatedAt.Value.ToUnixTimeMilliseconds().ShouldBe(token.ToUnixTimeMilliseconds());
        (await database.Context.AdminRealtimeOutboxEvents.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task MaterializeAsync_DependentCatalogProjectionNonClean_DefersAndLeavesNeedsMaterializationDurable()
    {
        await using var database = await CreateDatabaseAsync();
        var token = DateTimeOffset.UtcNow.AddMinutes(-5);
        database.Context.Libraries.Add(new LibraryEntity
        {
            Id = 7,
            Name = "Gated",
            ConfigurationJson = "{}",
            ConfigurationState = LibraryConfigurationState.Valid.ToString(),
            NeedsMaterialization = true,
            ItemCount = 3,
            UpdatedAt = token,
            CreatedAt = token
        });
        await database.Context.SaveChangesAsync();
        database.Context.ChangeTracker.Clear();

        var dataProvider = Substitute.For<IMaterializationDataProvider>();
        var catalogProjection = Substitute.For<ICatalogProjectionService>();
        catalogProjection.GetPlatformIdsNeedingRebuildAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { 10 });
        var service = new LibraryMaterializationService(
            new LibraryRepository(database.Context),
            dataProvider,
            catalogProjection,
            new AdminRealtimeOutbox(database.Context, TimeProvider.System),
            new EfUnitOfWork(database.Context),
            Substitute.For<ILogger<LibraryMaterializationService>>());

        var result = await service.MaterializeAsync(7);

        result.ShouldBe(MaterializationResult.Deferred());
        result.Outcome.ShouldBe(MaterializationOutcome.Deferred);
        await dataProvider.DidNotReceiveWithAnyArgs().GetCandidatesAsync(default!, default);
        database.Context.ChangeTracker.Clear();
        var library = await database.Context.Libraries.SingleAsync(entity => entity.Id == 7);
        library.NeedsMaterialization.ShouldBeTrue();
        library.ItemCount.ShouldBe(3);
        library.LastMaterializedAt.ShouldBeNull();
        (await database.Context.AdminRealtimeOutboxEvents.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task MaterializeAsync_GatedThenCatalogBecomesClean_ProceedsAndClearsFlag()
    {
        await using var database = await CreateDatabaseAsync();
        var token = DateTimeOffset.UtcNow.AddMinutes(-5);
        database.Context.Libraries.Add(new LibraryEntity
        {
            Id = 7,
            Name = "Recovering",
            ConfigurationJson = "{}",
            ConfigurationState = LibraryConfigurationState.Valid.ToString(),
            NeedsMaterialization = true,
            ItemCount = 0,
            UpdatedAt = token,
            CreatedAt = token
        });
        await database.Context.SaveChangesAsync();
        database.Context.ChangeTracker.Clear();

        var dataProvider = Substitute.For<IMaterializationDataProvider>();
        dataProvider.GetCandidatesAsync(Arg.Any<LibraryConfiguration>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<TitleCandidates>());
        var catalogProjection = Substitute.For<ICatalogProjectionService>();
        catalogProjection.GetPlatformIdsNeedingRebuildAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { 10 }, Array.Empty<int>());
        var service = new LibraryMaterializationService(
            new LibraryRepository(database.Context),
            dataProvider,
            catalogProjection,
            new AdminRealtimeOutbox(database.Context, TimeProvider.System),
            new EfUnitOfWork(database.Context),
            Substitute.For<ILogger<LibraryMaterializationService>>());

        (await service.MaterializeAsync(7)).ShouldBe(MaterializationResult.Deferred());
        database.Context.ChangeTracker.Clear();
        (await database.Context.Libraries.SingleAsync(entity => entity.Id == 7))
            .NeedsMaterialization.ShouldBeTrue();

        var recoveredResult = await service.MaterializeAsync(7);
        recoveredResult.Outcome.ShouldBe(MaterializationOutcome.Materialized);

        database.Context.ChangeTracker.Clear();
        var library = await database.Context.Libraries.SingleAsync(entity => entity.Id == 7);
        library.NeedsMaterialization.ShouldBeFalse();
        library.LastMaterializedAt.ShouldNotBeNull();
    }

    private static ICatalogProjectionService CleanCatalogProjection()
    {
        var catalogProjection = Substitute.For<ICatalogProjectionService>();
        catalogProjection.GetPlatformIdsNeedingRebuildAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<int>());
        return catalogProjection;
    }

    private static Library NewLibrary(
        int id,
        string name,
        bool needsMaterialization,
        int itemCount = 0) =>
        new LibraryEntity
        {
            Id = id,
            Name = name,
            ConfigurationJson = "{}",
            ConfigurationState = LibraryConfigurationState.Valid.ToString(),
            NeedsMaterialization = needsMaterialization,
            ItemCount = itemCount,
            CreatedAt = DateTimeOffset.UtcNow
        }.ToDomain();

    private static async Task<TestDatabase> CreateDatabaseAsync()
    {
        var connection = PostgreSqlTestDatabase.Create();
        var options = new DbContextOptionsBuilder<RomdDbContext>()
            .UseNpgsql(connection.ConnectionString)
            .Options;
        var context = new RomdDbContext(options);
        return new TestDatabase(connection, context);
    }

    private sealed class FailOutboxSaveInterceptor : SaveChangesInterceptor
    {
        public bool Enabled { get; set; } = true;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Enabled && eventData.Context?.ChangeTracker.Entries<AdminRealtimeOutboxEventEntity>()
                .Any(entry => entry.State == EntityState.Added) == true)
            {
                throw new DbUpdateException("Injected outbox save failure.");
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class RollbackThenThrowUnitOfWork(RomdDbContext context) : IUnitOfWork
    {
        private readonly IUnitOfWork _inner = new EfUnitOfWork(context);

        public Task FlushAsync(CancellationToken cancellationToken = default) =>
            _inner.FlushAsync(cancellationToken);

        public async Task<ITransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
            new RollbackThenThrowTransaction(
                await _inner.BeginTransactionAsync(cancellationToken));
    }

    private sealed class RollbackThenThrowTransaction(ITransaction transaction) : ITransaction
    {
        private bool _rolledBack;

        public async Task CommitAsync(CancellationToken cancellationToken = default)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            _rolledBack = true;
            throw new InvalidOperationException("Injected commit failure after rollback.");
        }

        public async Task RollbackAsync(CancellationToken cancellationToken = default)
        {
            if (!_rolledBack)
            {
                await transaction.RollbackAsync(cancellationToken);
                _rolledBack = true;
            }
        }

        public ValueTask DisposeAsync() => transaction.DisposeAsync();
    }

    private sealed class TestDatabase(PostgreSqlTestDatabase connection, RomdDbContext context) : IAsyncDisposable
    {
        public RomdDbContext Context { get; } = context;

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
