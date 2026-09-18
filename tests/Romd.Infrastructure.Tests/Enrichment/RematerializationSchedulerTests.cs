using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Infrastructure.Enrichment;
using Romd.Infrastructure.Tests.Helpers;
using Romd.Persistence;
using Romd.Persistence.Enrichment;
using Romd.Persistence.Entities;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Enrichment;

public sealed class RematerializationSchedulerTests : IDisposable
{
    private readonly PostgreSqlTestDatabase _database = PostgreSqlTestDatabase.Create();
    private readonly ManualTimeProvider _clock = new(new DateTimeOffset(2026, 9, 9, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task EnqueueAsync_CallerCommits_PersistsAllRequestScopes()
    {
        await using var context = _database.CreateContext();
        await using var transaction = await new EfUnitOfWork(context).BeginTransactionAsync();
        var scheduler = new RematerializationScheduler(context, _clock);

        await scheduler.EnqueueTitleAsync(12);
        await scheduler.EnqueuePlatformAsync(34);
        await scheduler.EnqueueAllAsync();
        await using (var observer = _database.CreateContext())
        {
            (await observer.MetadataRematerializationRequests.CountAsync()).ShouldBe(0);
        }
        await transaction.CommitAsync();

        await using var assertion = _database.CreateContext();
        var requests = await assertion.MetadataRematerializationRequests.ToListAsync();
        requests.Count.ShouldBe(3);
        requests.ShouldContain(row => row.TitleId == 12 && row.PlatformId == null);
        requests.ShouldContain(row => row.TitleId == null && row.PlatformId == 34);
        requests.ShouldContain(row => row.TitleId == null && row.PlatformId == null);
        requests.ShouldAllBe(row => row.CreatedAtUtc == _clock.GetUtcNow()
            && row.AvailableAtUtc == _clock.GetUtcNow() && row.Attempts == 0);
    }

    [Fact]
    public async Task EnqueueAsync_CallerRollsBackFlushedIntent_LeavesNoWork()
    {
        await using var context = _database.CreateContext();
        var unitOfWork = new EfUnitOfWork(context);
        await using var transaction = await unitOfWork.BeginTransactionAsync();
        await new RematerializationScheduler(context, _clock).EnqueueTitleAsync(12);
        await unitOfWork.FlushAsync();

        await transaction.RollbackAsync();
        await unitOfWork.FlushAsync();

        await using var assertion = _database.CreateContext();
        (await assertion.MetadataRematerializationRequests.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task EnqueueAsync_WithoutTransaction_RejectsUndurableScheduling()
    {
        await using var context = _database.CreateContext();
        var scheduler = new RematerializationScheduler(context, _clock);

        await Should.ThrowAsync<InvalidOperationException>(() => scheduler.EnqueueTitleAsync(12));
        await Should.ThrowAsync<InvalidOperationException>(() => scheduler.EnqueuePlatformAsync(34));
        await Should.ThrowAsync<InvalidOperationException>(() => scheduler.EnqueueAllAsync());

        await context.SaveChangesAsync();
        (await context.MetadataRematerializationRequests.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task ProcessPendingAsync_NewWorker_ProcessesCommittedScopesInSeparateServiceScopes()
    {
        await EnqueueAsync(async scheduler =>
        {
            await scheduler.EnqueueTitleAsync(12);
            await scheduler.EnqueueTitleAsync(13);
            await scheduler.EnqueueTitleAsync(14);
        });
        var calls = new List<(Guid Scope, string Kind, int? Id)>();
        using var provider = CreateProvider((scope, kind, id, _) =>
        {
            calls.Add((scope, kind, id));
            return Task.CompletedTask;
        });
        using var worker = CreateWorker(provider);

        await worker.ProcessPendingAsync();

        calls.Count.ShouldBe(3);
        calls.Select(call => call.Scope).Distinct().Count().ShouldBe(3);
        calls.ShouldContain(call => call.Kind == "title" && call.Id == 12);
        calls.ShouldContain(call => call.Kind == "title" && call.Id == 13);
        calls.ShouldContain(call => call.Kind == "title" && call.Id == 14);
        await using var assertion = _database.CreateContext();
        (await assertion.MetadataRematerializationRequests.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task ProcessPendingAsync_WorkFails_NewWorkerRetriesOnlyWhenDue()
    {
        await EnqueueAsync(scheduler => scheduler.EnqueueTitleAsync(12));
        using (var failingProvider = CreateProvider((_, _, _, _) =>
                   Task.FromException(new InvalidOperationException("Injected rematerialization failure"))))
        using (var failingWorker = CreateWorker(failingProvider))
        {
            await failingWorker.ProcessPendingAsync();
        }
        await using (var assertion = _database.CreateContext())
        {
            var request = await assertion.MetadataRematerializationRequests.SingleAsync();
            request.Attempts.ShouldBe(1);
            request.AvailableAtUtc.ShouldBe(_clock.GetUtcNow().AddSeconds(30));
        }
        int attempts = 0;
        using var restartedProvider = CreateProvider((_, _, _, _) =>
        {
            attempts++;
            return Task.CompletedTask;
        });
        using var restartedWorker = CreateWorker(restartedProvider);
        await restartedWorker.ProcessPendingAsync();
        attempts.ShouldBe(0);

        _clock.Advance(TimeSpan.FromSeconds(30));
        await restartedWorker.ProcessPendingAsync();

        attempts.ShouldBe(1);
        await using var finalAssertion = _database.CreateContext();
        (await finalAssertion.MetadataRematerializationRequests.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task ProcessPendingAsync_CancelledAfterWorkBeforeAcknowledgement_NewWorkerRepeatsRequest()
    {
        await EnqueueAsync(scheduler => scheduler.EnqueueTitleAsync(12));
        using var cancellation = new CancellationTokenSource();
        int completedWork = 0;
        using (var provider = CreateProvider((_, _, _, _) =>
               {
                   completedWork++;
                   cancellation.Cancel();
                   return Task.CompletedTask;
               }))
        using (var worker = CreateWorker(provider))
        {
            await Should.ThrowAsync<OperationCanceledException>(() => worker.ProcessPendingAsync(cancellation.Token));
        }
        await using (var assertion = _database.CreateContext())
        {
            var request = await assertion.MetadataRematerializationRequests.SingleAsync();
            request.Attempts.ShouldBe(0);
        }
        using var restartedProvider = CreateProvider((_, _, _, _) =>
        {
            completedWork++;
            return Task.CompletedTask;
        });
        using var restartedWorker = CreateWorker(restartedProvider);

        await restartedWorker.ProcessPendingAsync();

        completedWork.ShouldBe(2);
        await using var finalAssertion = _database.CreateContext();
        (await finalAssertion.MetadataRematerializationRequests.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task ProcessPendingAsync_SameTitleEnqueuedDuringWork_PreservesNewRequest()
    {
        await EnqueueAsync(scheduler => scheduler.EnqueueTitleAsync(12));
        int calls = 0;
        using var provider = CreateProvider(async (_, _, _, _) =>
        {
            calls++;
            if (calls == 1)
                await EnqueueAsync(scheduler => scheduler.EnqueueTitleAsync(12));
        });
        using var worker = CreateWorker(provider);

        await worker.ProcessPendingAsync();

        calls.ShouldBe(1);
        await using (var assertion = _database.CreateContext())
        {
            var pending = await assertion.MetadataRematerializationRequests.SingleAsync();
            pending.TitleId.ShouldBe(12);
        }
        await worker.ProcessPendingAsync();
        calls.ShouldBe(2);
        await using var finalAssertion = _database.CreateContext();
        (await finalAssertion.MetadataRematerializationRequests.CountAsync()).ShouldBe(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProcessPendingAsync_BulkRequest_ExpandsDurableChildrenConsumedAfterRestart(bool all)
    {
        await SeedTitlesAsync();
        await EnqueueAsync(scheduler => all ? scheduler.EnqueueAllAsync() : scheduler.EnqueuePlatformAsync(34));
        var calls = new List<int?>();
        using (var provider = CreateProvider((_, _, id, _) =>
               {
                   calls.Add(id);
                   return Task.CompletedTask;
               }))
        using (var worker = CreateWorker(provider))
        {
            await worker.ProcessPendingAsync();
        }

        calls.ShouldBeEmpty();
        int[] expected = all ? [12, 13, 14] : [12, 13];
        await using (var assertion = _database.CreateContext())
        {
            var children = await assertion.MetadataRematerializationRequests.ToListAsync();
            children.Select(row => row.TitleId!.Value).Order().ShouldBe(expected);
            children.ShouldAllBe(row => row.PlatformId == null && row.Attempts == 0);
        }
        using var restartedProvider = CreateProvider((_, kind, id, _) =>
        {
            kind.ShouldBe("title");
            calls.Add(id);
            return Task.CompletedTask;
        });
        using var restartedWorker = CreateWorker(restartedProvider);

        await restartedWorker.ProcessPendingAsync();

        calls.Select(id => id!.Value).Order().ShouldBe(expected);
        await using var finalAssertion = _database.CreateContext();
        (await finalAssertion.MetadataRematerializationRequests.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task ProcessPendingAsync_OneExpandedTitleFails_OtherTitlesCompleteAndFailedChildRetries()
    {
        await SeedTitlesAsync();
        await EnqueueAsync(scheduler => scheduler.EnqueueAllAsync());
        var completed = new List<int?>();
        using (var provider = CreateProvider((_, kind, id, _) =>
               {
                   kind.ShouldBe("title");
                   if (id == 12) throw new InvalidOperationException("Injected title failure");
                   completed.Add(id);
                   return Task.CompletedTask;
               }))
        using (var worker = CreateWorker(provider))
        {
            await worker.ProcessPendingAsync();
            await worker.ProcessPendingAsync();
        }

        completed.Select(id => id!.Value).Order().ShouldBe([13, 14]);
        await using (var assertion = _database.CreateContext())
        {
            var remaining = await assertion.MetadataRematerializationRequests.SingleAsync();
            remaining.TitleId.ShouldBe(12);
            remaining.Attempts.ShouldBe(1);
        }
        _clock.Advance(TimeSpan.FromSeconds(30));
        using var restartedProvider = CreateProvider((_, _, id, _) =>
        {
            completed.Add(id);
            return Task.CompletedTask;
        });
        using var restartedWorker = CreateWorker(restartedProvider);
        await restartedWorker.ProcessPendingAsync();
        completed.Select(id => id!.Value).Order().ShouldBe([12, 13, 14]);
        await using var finalAssertion = _database.CreateContext();
        (await finalAssertion.MetadataRematerializationRequests.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task ProcessPendingAsync_FanoutFailsAfterChildrenFlush_RollsBackChildrenAndRetainsParent()
    {
        await SeedTitlesAsync();
        await EnqueueAsync(scheduler => scheduler.EnqueueAllAsync());
        var interceptor = new FailFanoutAfterFlushInterceptor();
        using (var provider = CreateProvider((_, _, _, _) => Task.CompletedTask, interceptor))
        using (var worker = CreateWorker(provider))
        {
            await worker.ProcessPendingAsync();
        }

        interceptor.Triggered.ShouldBeTrue();
        await using (var assertion = _database.CreateContext())
        {
            var parent = await assertion.MetadataRematerializationRequests.SingleAsync();
            parent.TitleId.ShouldBeNull();
            parent.PlatformId.ShouldBeNull();
            parent.Attempts.ShouldBe(1);
        }
        _clock.Advance(TimeSpan.FromSeconds(30));
        using var restartedProvider = CreateProvider((_, _, _, _) => Task.CompletedTask);
        using var restartedWorker = CreateWorker(restartedProvider);
        await restartedWorker.ProcessPendingAsync();
        await using var finalAssertion = _database.CreateContext();
        var children = await finalAssertion.MetadataRematerializationRequests.ToListAsync();
        children.Select(row => row.TitleId!.Value).Order().ShouldBe([12, 13, 14]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(501)]
    public async Task ProcessPendingAsync_GlobalExpansion_HandlesEmptyAndMultipleBatches(int titleCount)
    {
        await SeedTitlesAsync(titleCount);
        await EnqueueAsync(scheduler => scheduler.EnqueueAllAsync());
        using var provider = CreateProvider((_, _, _, _) => Task.CompletedTask);
        using var worker = CreateWorker(provider);

        await worker.ProcessPendingAsync();

        await using var assertion = _database.CreateContext();
        var children = await assertion.MetadataRematerializationRequests.ToListAsync();
        children.Select(row => row.TitleId!.Value).Order().ShouldBe(Enumerable.Range(12, titleCount));
        children.ShouldAllBe(row => row.PlatformId == null);
    }

    private async Task SeedTitlesAsync(int titleCount = 3)
    {
        await using var context = _database.CreateContext();
        context.Platforms.AddRange(
            new PlatformEntity { Id = 34, Name = "NES", Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "NES", BaseCompactLabel = "NES", CanonicalKey = "nes", ShortName = "nes", CreatedAt = _clock.GetUtcNow() },
            new PlatformEntity { Id = 35, Name = "SNES", Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "SNES", BaseCompactLabel = "SNES", CanonicalKey = "snes", ShortName = "snes", CreatedAt = _clock.GetUtcNow() });
        context.Titles.AddRange(Enumerable.Range(12, titleCount).Select(id => new TitleEntity
        {
            Id = id, PlatformId = id == 14 ? 35 : 34, Name = $"Title {id}",
            NormalizedName = $"title{id}", EnrichmentStatus = "NotEnriched", CreatedAt = _clock.GetUtcNow()
        }));
        await context.SaveChangesAsync();
    }

    private async Task EnqueueAsync(Func<RematerializationScheduler, Task> enqueue)
    {
        await using var context = _database.CreateContext();
        await using var transaction = await new EfUnitOfWork(context).BeginTransactionAsync();
        await enqueue(new RematerializationScheduler(context, _clock));
        await transaction.CommitAsync();
    }

    private ServiceProvider CreateProvider(
        Func<Guid, string, int?, CancellationToken, Task> execute,
        IInterceptor? interceptor = null) =>
        new ServiceCollection()
            .AddScoped(_ => interceptor is null
                ? _database.CreateContext()
                : new RomdDbContext(_database.CreateOptions(options => options.AddInterceptors(interceptor))))
            .AddScoped<IMetadataRematerializationQueue, MetadataRematerializationQueue>()
            .AddScoped<IRematerializationService>(_ => new RecordingRematerializationService(execute))
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

    private MetadataRematerializationWorker CreateWorker(ServiceProvider provider) => new(
        provider.GetRequiredService<IServiceScopeFactory>(), _clock,
        NullLogger<MetadataRematerializationWorker>.Instance);

    private sealed class RecordingRematerializationService(
        Func<Guid, string, int?, CancellationToken, Task> execute) : IRematerializationService
    {
        private readonly Guid _scope = Guid.NewGuid();
        public Task RematerializeTitleAsync(int titleId, CancellationToken ct = default) =>
            execute(_scope, "title", titleId, ct);
        public Task RematerializePlatformAsync(int platformId, CancellationToken ct = default) =>
            execute(_scope, "platform", platformId, ct);
        public Task RematerializeAllAsync(CancellationToken ct = default) => execute(_scope, "all", null, ct);
    }

    private sealed class FailFanoutAfterFlushInterceptor : SaveChangesInterceptor
    {
        public bool Triggered { get; private set; }

        public override ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData,
            int result,
            CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<MetadataRematerializationRequestEntity>()
                .Any(entry => entry.Entity.TitleId.HasValue))
            {
                Triggered = true;
                throw new InvalidOperationException("Injected failure after child requests were flushed");
            }
            return base.SavedChangesAsync(eventData, result, cancellationToken);
        }
    }

    public void Dispose() => _database.Dispose();
}
