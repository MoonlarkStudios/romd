using System.Reflection;
using System.Text.Json;
using Hangfire;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Romd.Admin.Application.Catalog;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Libraries;
using Romd.Admin.Application.Source.Dat;
using Romd.Domain.Catalog;
using Romd.Domain.Jobs;
using Romd.Domain.Libraries;
using Romd.Infrastructure.Libraries;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.Infrastructure.Source;
using Romd.Infrastructure.Tests.Helpers;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Libraries;

public sealed class MaterializationSchedulingRecoveryTests
{
    [Fact]
    public async Task SetHangfireJobIdAsync_MissingJob_RejectsUnexpectedRowCount()
    {
        await using var database = await TestDatabase.CreateAsync();

        var exception = await Should.ThrowAsync<InvalidOperationException>(() =>
            database.JobRepository.SetHangfireJobIdAsync(Guid.NewGuid(), "hangfire-missing"));

        exception.Message.ShouldContain("updated 0");
    }

    [Fact]
    public async Task Dispatcher_CatalogProjectionNonClean_DefersJobCreationUntilClean()
    {
        await using var database = await TestDatabase.CreateAsync();
        database.Context.Platforms.Add(new PlatformEntity
        {
            Id = 10,
            Name = "Platform 10",
            Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "Platform 10", BaseCompactLabel = "Platform 10", CanonicalKey = "p10", ShortName = "p10",
            CatalogRebuildState = CatalogRebuildState.Dirty,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await database.Context.SaveChangesAsync();
        database.Context.ChangeTracker.Clear();
        var backgroundJobs = Substitute.For<IBackgroundJobClient>();

        await DispatchPendingAsync(database, database.JobRepository, backgroundJobs);

        (await database.JobRepository.GetActiveAsync()).ShouldBeEmpty();
        backgroundJobs.DidNotReceiveWithAnyArgs().Create(default!, default!);

        await database.Context.Platforms
            .Where(p => p.Id == 10)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.CatalogRebuildState, CatalogRebuildState.Clean));
        backgroundJobs.Create(Arg.Any<Hangfire.Common.Job>(), Arg.Any<IState>())
            .Returns("hangfire-after-clean");

        await DispatchPendingAsync(database, database.JobRepository, backgroundJobs);

        var job = (await database.JobRepository.GetActiveAsync()).ShouldHaveSingleItem();
        job.LibraryId.ShouldBe(7);
        job.HangfireJobId.ShouldBeNull();
        (await database.Context.JobDispatches.SingleAsync()).JobId.ShouldBe(job.Id);
    }

    [Fact]
    public async Task TryAddIfNoActiveForLibraryAsync_DeferredJobPresent_TreatsItAsTerminalAndAcceptsNewJob()
    {
        await using var database = await TestDatabase.CreateAsync();
        var deferred = MaterializationJob.Create(7, "Arcade");
        deferred.Start("hangfire-deferred");
        deferred.MarkDeferred();
        deferred.Complete();
        await database.JobRepository.AddAsync(deferred);

        (await database.JobRepository.GetActiveForLibraryAsync(7)).ShouldBeNull();
        (await database.JobRepository.GetActiveAsync()).ShouldBeEmpty();

        var next = MaterializationJob.Create(7, "Arcade");
        (await database.JobRepository.TryAddIfNoActiveForLibraryAsync(next)).ShouldBeTrue();
        (await database.Context.Jobs.OfType<MaterializationJobEntity>().CountAsync()).ShouldBe(2);
    }

    private static bool HasRomdJobId(Hangfire.Common.Job job, Guid expectedId) =>
        job.Args.Count > 0 && job.Args[0] is Guid jobId && jobId == expectedId;

    private static async Task DispatchPendingAsync(
        TestDatabase database,
        IMaterializationJobRepository jobRepository,
        IBackgroundJobClient backgroundJobs)
    {
        using var services = new ServiceCollection()
            .AddSingleton<ILibraryRepository>(database.LibraryRepository)
            .AddSingleton(jobRepository)
            .AddSingleton(backgroundJobs)
            .AddSingleton<ICatalogProjectionService>(CatalogProjectionTestFactory.Create(database.Context))
            .BuildServiceProvider();
        var dispatcher = new LibraryMaterializationReconciler(
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<LibraryMaterializationReconciler>.Instance);
        var dispatchPending = typeof(LibraryMaterializationReconciler).GetMethod(
            "DispatchPendingAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        await (Task)dispatchPending.Invoke(dispatcher, [CancellationToken.None])!;
    }

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly PostgreSqlTestDatabase _connection;

        private TestDatabase(PostgreSqlTestDatabase connection, RomdDbContext context)
        {
            _connection = connection;
            Context = context;
            LibraryRepository = new LibraryRepository(context);
            JobRepository = new MaterializationJobRepository(context, TimeProvider.System);
        }

        public RomdDbContext Context { get; }
        public LibraryRepository LibraryRepository { get; }
        public MaterializationJobRepository JobRepository { get; }

        public static async Task<TestDatabase> CreateAsync()
        {
            var connection = PostgreSqlTestDatabase.Create();
            var options = new DbContextOptionsBuilder<RomdDbContext>()
                .UseNpgsql(connection.ConnectionString)
                .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
                .Options;
            var context = new RomdDbContext(options);
            context.Libraries.Add(new LibraryEntity
            {
                Id = 7,
                Name = "Arcade",
                ConfigurationJson = JsonSerializer.Serialize(new LibraryConfiguration()),
                ConfigurationState = LibraryConfigurationState.Valid.ToString(),
                NeedsMaterialization = true,
                CreatedAt = DateTimeOffset.UtcNow,
                CreatedByUserId = Guid.Empty
            });
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();
            return new TestDatabase(connection, context);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
