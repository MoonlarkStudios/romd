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
    public async Task EnqueueAsync_CallerCommits_PersistsTitleIntent()
    {
        await using var context = _database.CreateContext();
        await using var transaction = await new EfUnitOfWork(context).BeginTransactionAsync();
        var scheduler = new RematerializationScheduler(context, _clock);

        await scheduler.EnqueueTitleAsync(12);
        await using (var observer = _database.CreateContext())
        {
            (await observer.MetadataRematerializationRequests.CountAsync()).ShouldBe(0);
        }
        await transaction.CommitAsync();

        await using var assertion = _database.CreateContext();
        var requests = await assertion.MetadataRematerializationRequests.ToListAsync();
        requests.Count.ShouldBe(1);
        requests.ShouldContain(row => row.TitleId == 12);
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
    }

    public void Dispose() => _database.Dispose();
}
