using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Romd.Admin.Application.Catalog;
using NSubstitute;
using Romd.Admin.Application.Libraries;
using Romd.Admin.Application.Source.Dat;
using Romd.Domain.Catalog;
using Romd.Domain.Libraries;
using Romd.Persistence;
using Romd.Infrastructure.Tests.Helpers;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.Infrastructure.Source;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Source;

public sealed class CatalogProjectionRecoveryDispatcherTests
{
    [Fact]
    public async Task RecoverPendingAsync_DirtyAndFailedPlatforms_RebuildsToCleanAndFlagsAffectedLibraries()
    {
        await using var database = await TestDatabase.CreateAsync();
        SeedPlatform(database.Context, platformId: 10, CatalogRebuildState.Dirty);
        SeedPlatform(database.Context, platformId: 11, CatalogRebuildState.Failed);
        SeedPlatform(database.Context, platformId: 12, CatalogRebuildState.Clean);
        SeedLibrary(database.Context, libraryId: 3);
        await database.Context.SaveChangesAsync();
        database.Context.ChangeTracker.Clear();

        await RecoverPendingAsync(database.RealProjectionService(), database.RealLibraryRepository());

        var platforms = await database.Context.Platforms.AsNoTracking()
            .OrderBy(p => p.Id)
            .ToListAsync();
        platforms.Select(p => p.CatalogRebuildState).ShouldAllBe(state => state == CatalogRebuildState.Clean);
        platforms.Single(p => p.Id == 10).CatalogRebuiltAt.ShouldNotBeNull();
        platforms.Single(p => p.Id == 11).CatalogRebuiltAt.ShouldNotBeNull();
        // The already-Clean platform was not rebuilt.
        platforms.Single(p => p.Id == 12).CatalogRebuiltAt.ShouldBeNull();
        (await database.Context.Libraries.AsNoTracking().SingleAsync(l => l.Id == 3))
            .NeedsMaterialization.ShouldBeTrue();
    }

    [Fact]
    public async Task RecoverPendingAsync_SecondPass_IsDuplicateSafeAndDoesNotTouchCleanPlatforms()
    {
        await using var database = await TestDatabase.CreateAsync();
        SeedPlatform(database.Context, platformId: 10, CatalogRebuildState.Dirty);
        SeedLibrary(database.Context, libraryId: 3);
        await database.Context.SaveChangesAsync();
        database.Context.ChangeTracker.Clear();

        await RecoverPendingAsync(database.RealProjectionService(), database.RealLibraryRepository());
        var recoveredAt = (await database.Context.Platforms.AsNoTracking().SingleAsync(p => p.Id == 10))
            .CatalogRebuiltAt.ShouldNotBeNull();
        await database.Context.Libraries
            .Where(l => l.Id == 3)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.NeedsMaterialization, false));

        await RecoverPendingAsync(database.RealProjectionService(), database.RealLibraryRepository());

        var platform = await database.Context.Platforms.AsNoTracking().SingleAsync(p => p.Id == 10);
        platform.CatalogRebuildState.ShouldBe(CatalogRebuildState.Clean);
        platform.CatalogRebuiltAt.ShouldBe(recoveredAt);
        (await database.Context.Libraries.AsNoTracking().SingleAsync(l => l.Id == 3))
            .NeedsMaterialization.ShouldBeFalse();
    }

    [Fact]
    public async Task RecoverPendingAsync_RebuildFails_DoesNotFlagLibrariesAndContinuesWithNextPlatform()
    {
        var catalogProjection = Substitute.For<ICatalogProjectionService>();
        catalogProjection.GetPlatformIdsNeedingRebuildAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { 10, 11 });
        catalogProjection.RebuildPlatformAsync(10, Arg.Any<CancellationToken>()).Returns(false);
        catalogProjection.RebuildPlatformAsync(11, Arg.Any<CancellationToken>()).Returns(true);
        var libraries = Substitute.For<ILibraryRepository>();

        await RecoverPendingAsync(catalogProjection, libraries);

        await libraries.DidNotReceive()
            .FlagForRematerializationByPlatformAsync(10, Arg.Any<CancellationToken>());
        await libraries.Received(1)
            .FlagForRematerializationByPlatformAsync(11, Arg.Any<CancellationToken>());
    }

    private static async Task RecoverPendingAsync(
        ICatalogProjectionService catalogProjection,
        ILibraryRepository libraryRepository)
    {
        using var services = new ServiceCollection()
            .AddSingleton(catalogProjection)
            .AddSingleton(libraryRepository)
            .BuildServiceProvider();
        var dispatcher = new CatalogProjectionRecoveryDispatcher(
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<CatalogProjectionRecoveryDispatcher>.Instance);
        var recoverPending = typeof(CatalogProjectionRecoveryDispatcher).GetMethod(
            "RecoverPendingAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        await (Task)recoverPending.Invoke(dispatcher, [CancellationToken.None])!;
    }

    private static void SeedPlatform(RomdDbContext context, int platformId, CatalogRebuildState state) =>
        context.Platforms.Add(new PlatformEntity
        {
            Id = platformId,
            Name = $"Platform {platformId}",
            ShortName = $"p{platformId}",
            CatalogRebuildState = state,
            CreatedAt = DateTimeOffset.UtcNow
        });

    private static void SeedLibrary(RomdDbContext context, int libraryId) =>
        context.Libraries.Add(new LibraryEntity
        {
            Id = libraryId,
            Name = $"Library {libraryId}",
            ConfigurationJson = "{}",
            ConfigurationState = LibraryConfigurationState.Valid.ToString(),
            NeedsMaterialization = false,
            CreatedAt = DateTimeOffset.UtcNow
        });

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly PostgreSqlTestDatabase _connection;

        private TestDatabase(PostgreSqlTestDatabase connection, RomdDbContext context)
        {
            _connection = connection;
            Context = context;
        }

        public RomdDbContext Context { get; }

        public ICatalogProjectionService RealProjectionService() =>
            CatalogProjectionTestFactory.Create(Context);

        public ILibraryRepository RealLibraryRepository() => new LibraryRepository(Context);

        public static async Task<TestDatabase> CreateAsync()
        {
            var connection = PostgreSqlTestDatabase.Create();
            var options = new DbContextOptionsBuilder<RomdDbContext>()
                .UseNpgsql(connection.ConnectionString)
                .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
                .Options;
            var context = new RomdDbContext(options);
            return new TestDatabase(connection, context);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
