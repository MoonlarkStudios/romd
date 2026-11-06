using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Diagnostics;
using Romd.Admin.Application.Diagnostics.Queries.GetOperationalDiagnostics;
using Romd.Application.Common.Ids;
using Romd.Contracts.Management.Diagnostics;
using Romd.Domain.Catalog;
using Romd.Domain.Jobs;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Diagnostics;

public sealed class GetOperationalDiagnosticsQueryHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task HandleAsync_DiagnosticsAvailable_MapsOpaqueIdsNamedStatesAndAges()
    {
        var relational = Substitute.For<IOperationalDiagnosticsReader>();
        var hangfire = Substitute.For<IHangfireDiagnosticsReader>();
        var storage = Substitute.For<IStorageDiagnosticsReader>();
        relational.ReadCatalogProjectionsAsync(
                OperationalDiagnosticsPolicy.CatalogProjectionLimit,
                Arg.Any<CancellationToken>())
            .Returns(new BoundedDiagnosticsData<CatalogProjectionDiagnosticsData>(
                [new("n64", "Nintendo 64", "n64", CatalogRebuildState.Failed, null, "boom", Now.AddMinutes(-4))],
                true));
        relational.ReadJobsAsync(OperationalDiagnosticsPolicy.JobLimit, Arg.Any<CancellationToken>())
            .Returns(new OperationalJobDiagnosticsData(
                new BoundedDiagnosticsData<ReplaceDatJobDiagnosticsData>(
                [new(
                    Guid.Parse("11111111-1111-1111-1111-111111111111"),
                    9,
                    "n64",
                    ReplaceDatPhase.Replacing,
                    Now.AddHours(-2),
                    Now.AddHours(-1),
                    Now.AddMinutes(-40),
                    "activation failed",
                    true)],
                    false),
                new BoundedDiagnosticsData<BulkEnrichmentJobDiagnosticsData>(
                    [new(
                        Guid.Parse("22222222-2222-2222-2222-222222222222"),
                        "n64",
                        "n64",
                        BulkEnrichmentPhase.Pending,
                        Now.AddMinutes(-15))],
                    false)));
        relational.ReadOutboxAsync(Arg.Any<CancellationToken>())
            .Returns(new OutboxDiagnosticsData(3, Now.AddMinutes(-6), 1, Now.AddSeconds(-20)));
        hangfire.ReadAsync(
                OperationalDiagnosticsPolicy.HangfireServerLimit,
                OperationalDiagnosticsPolicy.RecurringJobLimit,
                Arg.Any<CancellationToken>())
            .Returns(new HangfireDiagnosticsData(
                true,
                null,
                new BoundedDiagnosticsData<HangfireServerDiagnosticsData>(
                    [new("worker-a", 2, ["default"], Now.AddHours(-1), Now.AddSeconds(-30))],
                    false),
                new BoundedDiagnosticsData<RecurringJobDiagnosticsData>(
                    [
                        new("sweep", "default", "*/30 * * * *", Now.AddMinutes(-20), Now.AddMinutes(10), "Succeeded", null),
                        new("future-state", "default", "0 * * * *", null, Now.AddHours(1), "Paused", null),
                        new("never-run", "default", "0 0 * * *", null, Now.AddDays(1), null, null)
                    ],
                    false),
                [new("default", 4, 1)]));
        storage.ReadAsync(Arg.Any<CancellationToken>())
            .Returns(new StorageDiagnosticsData(true, 1234, true, null));

        await using var services = Services(relational, hangfire, storage);
        var result = await Handler(services)
            .HandleAsync(new GetOperationalDiagnosticsQuery());

        result.IsError.ShouldBeFalse();
        var dto = result.Value;
        dto.GeneratedAt.ShouldBe(Now);
        dto.CatalogProjections.IsTruncated.ShouldBeTrue();
        var projection = dto.CatalogProjections.Items.ShouldHaveSingleItem();
        projection.SystemKey.ShouldBe(TestSystemCatalog.Keys.Required(7));
        projection.State.ShouldBe(CatalogProjectionState.Failed);
        var replace = dto.Jobs.WedgedReplaceDatJobs.ShouldHaveSingleItem();
        replace.ExistingDatId.ShouldBe(IdCoder.Encode(9));
        replace.SystemKey.ShouldBe(TestSystemCatalog.Keys.Required(7));
        replace.Phase.ShouldBe(ReplaceDatDiagnosticPhase.Replacing);
        replace.StalledForSeconds.ShouldBe(2400);
        replace.AttemptError.ShouldBe("activation failed");
        replace.AttemptErrorTruncated.ShouldBeTrue();
        dto.Jobs.StrandedBulkEnrichmentJobs.ShouldHaveSingleItem().StrandedForSeconds.ShouldBe(900);
        dto.Outbox.OldestPendingAgeSeconds.ShouldBe(360);
        dto.Hangfire.Availability.ShouldBe(DiagnosticAvailability.Available);
        dto.Hangfire.Servers.ShouldHaveSingleItem().HeartbeatAgeSeconds.ShouldBe(30);
        dto.Hangfire.RecurringJobs.Select(job => job.LastJobState).ShouldBe([
            RecurringJobDiagnosticState.Succeeded,
            RecurringJobDiagnosticState.Unknown,
            null
        ]);
        dto.Storage.CasAvailability.ShouldBe(DiagnosticAvailability.Available);
    }

    [Fact]
    public async Task HandleAsync_HangfireUnavailable_ReturnsSuccessfulUnavailableSection()
    {
        var relational = Substitute.For<IOperationalDiagnosticsReader>();
        var hangfire = Substitute.For<IHangfireDiagnosticsReader>();
        var storage = Substitute.For<IStorageDiagnosticsReader>();
        relational.ReadCatalogProjectionsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new BoundedDiagnosticsData<CatalogProjectionDiagnosticsData>([], false));
        relational.ReadJobsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new OperationalJobDiagnosticsData(
                new BoundedDiagnosticsData<ReplaceDatJobDiagnosticsData>([], false),
                new BoundedDiagnosticsData<BulkEnrichmentJobDiagnosticsData>([], false)));
        relational.ReadOutboxAsync(Arg.Any<CancellationToken>())
            .Returns(new OutboxDiagnosticsData(0, null, 0, null));
        hangfire.ReadAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new HangfireDiagnosticsData(
                false,
                "not registered",
                new BoundedDiagnosticsData<HangfireServerDiagnosticsData>([], false),
                new BoundedDiagnosticsData<RecurringJobDiagnosticsData>([], false),
                []));
        storage.ReadAsync(Arg.Any<CancellationToken>())
            .Returns(new StorageDiagnosticsData(false, null, false, "unavailable"));

        await using var services = Services(relational, hangfire, storage);
        var result = await Handler(services)
            .HandleAsync(new GetOperationalDiagnosticsQuery());

        result.IsError.ShouldBeFalse();
        result.Value.Hangfire.Availability.ShouldBe(DiagnosticAvailability.Unavailable);
        result.Value.Hangfire.Error.ShouldBe("not registered");
        result.Value.Storage.DataVolumeAvailability.ShouldBe(DiagnosticAvailability.Unavailable);
    }

    [Fact]
    public async Task HandleAsync_RelationalSectionFails_OtherSectionsRemainAvailable()
    {
        var relational = Substitute.For<IOperationalDiagnosticsReader>();
        var hangfire = Substitute.For<IHangfireDiagnosticsReader>();
        var storage = Substitute.For<IStorageDiagnosticsReader>();
        relational.ReadCatalogProjectionsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<Task<BoundedDiagnosticsData<CatalogProjectionDiagnosticsData>>>(_ =>
                throw new InvalidOperationException("database unavailable"));
        relational.ReadJobsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new OperationalJobDiagnosticsData(
                new BoundedDiagnosticsData<ReplaceDatJobDiagnosticsData>([], false),
                new BoundedDiagnosticsData<BulkEnrichmentJobDiagnosticsData>([], false)));
        relational.ReadOutboxAsync(Arg.Any<CancellationToken>())
            .Returns(new OutboxDiagnosticsData(0, null, 0, null));
        hangfire.ReadAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(AvailableHangfire());
        storage.ReadAsync(Arg.Any<CancellationToken>())
            .Returns(new StorageDiagnosticsData(true, 1234, true, null));

        await using var services = Services(relational, hangfire, storage);
        var result = await Handler(services).HandleAsync(new GetOperationalDiagnosticsQuery());

        result.IsError.ShouldBeFalse();
        result.Value.CatalogProjections.Availability.ShouldBe(DiagnosticAvailability.Unavailable);
        result.Value.CatalogProjections.Error.ShouldNotBeNull();
        result.Value.CatalogProjections.Error.ShouldContain("database unavailable");
        result.Value.Jobs.Availability.ShouldBe(DiagnosticAvailability.Available);
        result.Value.Outbox.Availability.ShouldBe(DiagnosticAvailability.Available);
        result.Value.Hangfire.Availability.ShouldBe(DiagnosticAvailability.Available);
        result.Value.Storage.CasAvailability.ShouldBe(DiagnosticAvailability.Available);
    }

    [Fact]
    public async Task HandleAsync_SectionIgnoresCancellation_ReturnsOnBudgetAndRetainsScopeUntilCompletion()
    {
        var state = new HangingReaderState();
        // The Hangfire sibling takes longer in wall-clock time than every section's budget; only the handler's
        // TimeProvider may spend a budget.
        var completed = new CompletedReaders(hangfireDelay: TimeSpan.FromMilliseconds(250));
        var services = new ServiceCollection()
            .AddSingleton<OperationalDiagnosticsAdmission>()
            .AddScoped<IOperationalDiagnosticsReader>(_ => new HangingOperationalReader(state))
            .AddScoped<IHangfireDiagnosticsReader>(_ => completed.Hangfire())
            .AddScoped<IStorageDiagnosticsReader>(_ => completed.Storage())
            .BuildServiceProvider();
        var time = new ManualTimeProvider(Now);
        var handler = Handler(
            services,
            new OperationalDiagnosticsOptions
            {
                SectionBudget = TimeSpan.FromMilliseconds(75),
                OverallBudget = TimeSpan.FromSeconds(2)
            },
            time);

        var pending = handler.HandleAsync(new GetOperationalDiagnosticsQuery());
        // The blocked projection section's budget expires only after every sibling has finished, so the
        // siblings' availability never depends on how quickly the scheduler ran them.
        await WaitUntilAsync(() => state.ProjectionReader is not null
            && state.DisposedReaders == 2 && completed.AllDisposed(hangfire: 1, storage: 1));
        time.Advance(TimeSpan.FromMilliseconds(75));
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(10));

        result.Value.CatalogProjections.Availability.ShouldBe(DiagnosticAvailability.Unavailable);
        result.Value.CatalogProjections.Error.ShouldNotBeNull();
        result.Value.CatalogProjections.Error.ShouldContain("budget was exceeded");
        result.Value.Jobs.Availability.ShouldBe(DiagnosticAvailability.Available);
        result.Value.Hangfire.Availability.ShouldBe(DiagnosticAvailability.Available);
        state.ProjectionReader.ShouldNotBeNull();
        state.ProjectionReader.IsDisposed.ShouldBeFalse();

        state.Completion.SetResult(new BoundedDiagnosticsData<CatalogProjectionDiagnosticsData>([], false));
        await WaitUntilAsync(() => state.ProjectionReader.IsDisposed);
        await services.DisposeAsync();
    }

    [Fact]
    public async Task HandleAsync_OverallBudgetExpires_PreservesCompletedSectionsAndRetainsAbandonedScope()
    {
        var state = new HangingStorageState();
        // The Hangfire sibling takes longer in wall-clock time than the whole overall budget. Only the handler's
        // TimeProvider may spend the budget, so a slow scheduler cannot turn a completed section unavailable.
        var completed = new CompletedReaders(hangfireDelay: TimeSpan.FromMilliseconds(250));
        var services = new ServiceCollection()
            .AddSingleton<OperationalDiagnosticsAdmission>()
            .AddScoped<IOperationalDiagnosticsReader>(_ => completed.Operational())
            .AddScoped<IHangfireDiagnosticsReader>(_ => completed.Hangfire())
            .AddScoped<IStorageDiagnosticsReader>(_ => new HangingStorageReader(state))
            .BuildServiceProvider();
        var time = new ManualTimeProvider(Now);
        var handler = Handler(
            services,
            new OperationalDiagnosticsOptions
            {
                SectionBudget = TimeSpan.FromSeconds(2),
                OverallBudget = TimeSpan.FromMilliseconds(75)
            },
            time);

        var pending = handler.HandleAsync(new GetOperationalDiagnosticsQuery());
        await WaitUntilAsync(() => state.Reader is not null && completed.AllDisposed(operational: 3, hangfire: 1));
        time.Advance(TimeSpan.FromMilliseconds(75));
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(10));

        result.Value.CatalogProjections.Availability.ShouldBe(DiagnosticAvailability.Available);
        result.Value.Jobs.Availability.ShouldBe(DiagnosticAvailability.Available);
        result.Value.Outbox.Availability.ShouldBe(DiagnosticAvailability.Available);
        result.Value.Hangfire.Availability.ShouldBe(DiagnosticAvailability.Available);
        result.Value.Storage.CasAvailability.ShouldBe(DiagnosticAvailability.Unavailable);
        result.Value.Storage.Error.ShouldNotBeNull();
        result.Value.Storage.Error.ShouldContain("budget was exceeded");
        state.Reader.ShouldNotBeNull();
        state.Reader.IsDisposed.ShouldBeFalse();

        state.Completion.SetResult(new StorageDiagnosticsData(true, 1234, true, null));
        await WaitUntilAsync(() => state.Reader.IsDisposed);
        await services.DisposeAsync();
    }

    [Fact]
    public async Task HandleAsync_ReaderBlocksBeforeReturningTask_BudgetsSection()
    {
        var state = new SynchronouslyBlockingReaderState();
        var hangfire = Substitute.For<IHangfireDiagnosticsReader>();
        var storage = Substitute.For<IStorageDiagnosticsReader>();
        var services = new ServiceCollection()
            .AddSingleton<OperationalDiagnosticsAdmission>()
            .AddScoped<IOperationalDiagnosticsReader>(_ => new SynchronouslyBlockingOperationalReader(state))
            .AddScoped(_ => hangfire)
            .AddScoped(_ => storage)
            .BuildServiceProvider();
        hangfire.ReadAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(AvailableHangfire());
        storage.ReadAsync(Arg.Any<CancellationToken>())
            .Returns(new StorageDiagnosticsData(true, 1234, true, null));
        var handler = Handler(
            services,
            new OperationalDiagnosticsOptions
            {
                SectionBudget = TimeSpan.FromMilliseconds(75),
                OverallBudget = TimeSpan.FromSeconds(2)
            });

        try
        {
            var result = await handler.HandleAsync(new GetOperationalDiagnosticsQuery())
                .WaitAsync(TimeSpan.FromSeconds(2));

            result.Value.CatalogProjections.Availability.ShouldBe(DiagnosticAvailability.Unavailable);
            result.Value.CatalogProjections.Error.ShouldNotBeNull().ShouldContain("budget was exceeded");
            state.Entered.Task.IsCompleted.ShouldBeTrue();
        }
        finally
        {
            state.Release.TrySetResult();
        }

        await WaitUntilAsync(() => state.Reader is { IsDisposed: true });
        await services.DisposeAsync();
    }

    [Fact]
    public async Task HandleAsync_ReaderBlocksBeforeReturningTask_StartsSiblingSectionsIndependently()
    {
        var state = new SynchronouslyBlockingReaderState();
        var hangfireEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var storageEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hangfire = Substitute.For<IHangfireDiagnosticsReader>();
        var storage = Substitute.For<IStorageDiagnosticsReader>();
        var services = new ServiceCollection()
            .AddSingleton<OperationalDiagnosticsAdmission>()
            .AddScoped<IOperationalDiagnosticsReader>(_ => new SynchronouslyBlockingOperationalReader(state))
            .AddScoped(_ => hangfire)
            .AddScoped(_ => storage)
            .BuildServiceProvider();
        hangfire.ReadAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                hangfireEntered.SetResult();
                return Task.FromResult(AvailableHangfire());
            });
        storage.ReadAsync(Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                storageEntered.SetResult();
                return Task.FromResult(new StorageDiagnosticsData(true, 1234, true, null));
            });
        var handler = Handler(
            services,
            new OperationalDiagnosticsOptions
            {
                SectionBudget = TimeSpan.FromSeconds(30),
                OverallBudget = TimeSpan.FromSeconds(30)
            });

        var handleTask = handler.HandleAsync(new GetOperationalDiagnosticsQuery());
        try
        {
            await Task.WhenAll(
                    state.Entered.Task,
                    state.JobsEntered.Task,
                    state.OutboxEntered.Task,
                    hangfireEntered.Task,
                    storageEntered.Task)
                .WaitAsync(TimeSpan.FromSeconds(10));

            handleTask.IsCompleted.ShouldBeFalse();
        }
        finally
        {
            state.Release.TrySetResult();
        }

        var result = await handleTask.WaitAsync(TimeSpan.FromSeconds(10));
        result.Value.CatalogProjections.Availability.ShouldBe(DiagnosticAvailability.Available);
        result.Value.Jobs.Availability.ShouldBe(DiagnosticAvailability.Available);
        result.Value.Outbox.Availability.ShouldBe(DiagnosticAvailability.Available);
        result.Value.Hangfire.Availability.ShouldBe(DiagnosticAvailability.Available);
        result.Value.Storage.CasAvailability.ShouldBe(DiagnosticAvailability.Available);

        await WaitUntilAsync(() => state.Reader is { IsDisposed: true });
        await services.DisposeAsync();
    }

    [Fact]
    public async Task HandleAsync_PreviousTimedOutProbeStillRunning_DoesNotStartAnotherProbeForSection()
    {
        var state = new HangingReaderState();
        var hangfire = Substitute.For<IHangfireDiagnosticsReader>();
        var storage = Substitute.For<IStorageDiagnosticsReader>();
        var services = new ServiceCollection()
            .AddSingleton<OperationalDiagnosticsAdmission>()
            .AddScoped<IOperationalDiagnosticsReader>(_ => new HangingOperationalReader(state))
            .AddScoped(_ => hangfire)
            .AddScoped(_ => storage)
            .BuildServiceProvider();
        hangfire.ReadAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(AvailableHangfire());
        storage.ReadAsync(Arg.Any<CancellationToken>())
            .Returns(new StorageDiagnosticsData(true, 1234, true, null));
        var options = new OperationalDiagnosticsOptions
        {
            SectionBudget = TimeSpan.FromMilliseconds(75),
            OverallBudget = TimeSpan.FromSeconds(2)
        };
        var handler = Handler(services, options);

        var first = await handler.HandleAsync(new GetOperationalDiagnosticsQuery())
            .WaitAsync(TimeSpan.FromSeconds(2));
        var second = await handler.HandleAsync(new GetOperationalDiagnosticsQuery())
            .WaitAsync(TimeSpan.FromSeconds(2));

        first.Value.CatalogProjections.Error.ShouldNotBeNull().ShouldContain("budget was exceeded");
        second.Value.CatalogProjections.Availability.ShouldBe(DiagnosticAvailability.Unavailable);
        second.Value.CatalogProjections.Error.ShouldNotBeNull().ShouldContain("still running");
        state.ProjectionReadCount.ShouldBe(1);

        state.Completion.SetResult(new BoundedDiagnosticsData<CatalogProjectionDiagnosticsData>([], false));
        await WaitUntilAsync(() => state.ProjectionReader is { IsDisposed: true });
        await services.DisposeAsync();
    }

    private static ServiceProvider Services(
        IOperationalDiagnosticsReader relational,
        IHangfireDiagnosticsReader hangfire,
        IStorageDiagnosticsReader storage) =>
        new ServiceCollection()
            .AddSingleton<OperationalDiagnosticsAdmission>()
            .AddScoped(_ => relational)
            .AddScoped(_ => hangfire)
            .AddScoped(_ => storage)
            .BuildServiceProvider();

    private static GetOperationalDiagnosticsQueryHandler Handler(
        ServiceProvider services,
        OperationalDiagnosticsOptions? options = null,
        TimeProvider? timeProvider = null) =>
        new(
            services.GetRequiredService<IServiceScopeFactory>(),
            services.GetRequiredService<OperationalDiagnosticsAdmission>(),
            timeProvider ?? new FixedTimeProvider(Now),
            options ?? new OperationalDiagnosticsOptions(),
            NullLogger<GetOperationalDiagnosticsQueryHandler>.Instance);

    private static HangfireDiagnosticsData AvailableHangfire() => new(
        true,
        null,
        new BoundedDiagnosticsData<HangfireServerDiagnosticsData>([], false),
        new BoundedDiagnosticsData<RecurringJobDiagnosticsData>([], false),
        []);

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var timeout = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while (!predicate())
        {
            if (DateTimeOffset.UtcNow >= timeout)
                throw new TimeoutException("Timed out waiting for diagnostics scopes to release.");
            await Task.Delay(10);
        }
    }

    private sealed class HangingReaderState
    {
        public TaskCompletionSource<BoundedDiagnosticsData<CatalogProjectionDiagnosticsData>> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public HangingOperationalReader? ProjectionReader { get; set; }
        public int ProjectionReadCount { get; set; }
        private int _disposedReaders;
        public int DisposedReaders => Volatile.Read(ref _disposedReaders);
        public void ReaderDisposed() => Interlocked.Increment(ref _disposedReaders);
    }

    private sealed class HangingStorageState
    {
        public TaskCompletionSource<StorageDiagnosticsData> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public HangingStorageReader? Reader { get; set; }
    }

    private sealed class HangingOperationalReader(HangingReaderState state)
        : IOperationalDiagnosticsReader, IDisposable
    {
        public bool IsDisposed { get; private set; }

        public Task<BoundedDiagnosticsData<CatalogProjectionDiagnosticsData>> ReadCatalogProjectionsAsync(
            int limit,
            CancellationToken ct = default)
        {
            state.ProjectionReader = this;
            state.ProjectionReadCount++;
            return state.Completion.Task;
        }

        public Task<OperationalJobDiagnosticsData> ReadJobsAsync(int limit, CancellationToken ct = default) =>
            Task.FromResult(new OperationalJobDiagnosticsData(
                new BoundedDiagnosticsData<ReplaceDatJobDiagnosticsData>([], false),
                new BoundedDiagnosticsData<BulkEnrichmentJobDiagnosticsData>([], false)));

        public Task<OutboxDiagnosticsData> ReadOutboxAsync(CancellationToken ct = default) =>
            Task.FromResult(new OutboxDiagnosticsData(0, null, 0, null));

        public void Dispose()
        {
            IsDisposed = true;
            if (state.ProjectionReader != this)
                state.ReaderDisposed();
        }
    }

    private sealed class SynchronouslyBlockingReaderState
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource JobsEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource OutboxEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public SynchronouslyBlockingOperationalReader? Reader { get; set; }
    }

    private sealed class SynchronouslyBlockingOperationalReader(SynchronouslyBlockingReaderState state)
        : IOperationalDiagnosticsReader, IDisposable
    {
        public bool IsDisposed { get; private set; }

        public Task<BoundedDiagnosticsData<CatalogProjectionDiagnosticsData>> ReadCatalogProjectionsAsync(
            int limit,
            CancellationToken ct = default)
        {
            state.Reader = this;
            state.Entered.SetResult();
            state.Release.Task.GetAwaiter().GetResult();
            return Task.FromResult(new BoundedDiagnosticsData<CatalogProjectionDiagnosticsData>([], false));
        }

        public Task<OperationalJobDiagnosticsData> ReadJobsAsync(int limit, CancellationToken ct = default)
        {
            state.JobsEntered.SetResult();
            return Task.FromResult(new OperationalJobDiagnosticsData(
                new BoundedDiagnosticsData<ReplaceDatJobDiagnosticsData>([], false),
                new BoundedDiagnosticsData<BulkEnrichmentJobDiagnosticsData>([], false)));
        }

        public Task<OutboxDiagnosticsData> ReadOutboxAsync(CancellationToken ct = default)
        {
            state.OutboxEntered.SetResult();
            return Task.FromResult(new OutboxDiagnosticsData(0, null, 0, null));
        }

        public void Dispose() => IsDisposed = true;
    }

    private sealed class HangingStorageReader(HangingStorageState state)
        : IStorageDiagnosticsReader, IDisposable
    {
        public bool IsDisposed { get; private set; }

        public Task<StorageDiagnosticsData> ReadAsync(CancellationToken ct = default)
        {
            state.Reader = this;
            return state.Completion.Task;
        }

        public void Dispose() => IsDisposed = true;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>Readers that complete successfully and record their scope's disposal, which the handler performs only
    /// after the section's result is fixed.</summary>
    private sealed class CompletedReaders(TimeSpan hangfireDelay = default)
    {
        private int _operational;
        private int _hangfire;
        private int _storage;

        public IOperationalDiagnosticsReader Operational() => new OperationalReader(() => Interlocked.Increment(ref _operational));
        public IHangfireDiagnosticsReader Hangfire() => new HangfireReader(hangfireDelay, () => Interlocked.Increment(ref _hangfire));
        public IStorageDiagnosticsReader Storage() => new StorageReader(() => Interlocked.Increment(ref _storage));

        public bool AllDisposed(int operational = 0, int hangfire = 0, int storage = 0) =>
            Volatile.Read(ref _operational) == operational && Volatile.Read(ref _hangfire) == hangfire
            && Volatile.Read(ref _storage) == storage;

        private sealed class OperationalReader(Action disposed) : IOperationalDiagnosticsReader, IDisposable
        {
            public Task<BoundedDiagnosticsData<CatalogProjectionDiagnosticsData>> ReadCatalogProjectionsAsync(
                int limit, CancellationToken ct = default) =>
                Task.FromResult(new BoundedDiagnosticsData<CatalogProjectionDiagnosticsData>([], false));

            public Task<OperationalJobDiagnosticsData> ReadJobsAsync(int limit, CancellationToken ct = default) =>
                Task.FromResult(new OperationalJobDiagnosticsData(
                    new BoundedDiagnosticsData<ReplaceDatJobDiagnosticsData>([], false),
                    new BoundedDiagnosticsData<BulkEnrichmentJobDiagnosticsData>([], false)));

            public Task<OutboxDiagnosticsData> ReadOutboxAsync(CancellationToken ct = default) =>
                Task.FromResult(new OutboxDiagnosticsData(0, null, 0, null));

            public void Dispose() => disposed();
        }

        private sealed class HangfireReader(TimeSpan delay, Action disposed) : IHangfireDiagnosticsReader, IDisposable
        {
            public async Task<HangfireDiagnosticsData> ReadAsync(
                int serverLimit, int recurringJobLimit, CancellationToken ct = default)
            {
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, CancellationToken.None);
                return AvailableHangfire();
            }

            public void Dispose() => disposed();
        }

        private sealed class StorageReader(Action disposed) : IStorageDiagnosticsReader, IDisposable
        {
            public Task<StorageDiagnosticsData> ReadAsync(CancellationToken ct = default) =>
                Task.FromResult(new StorageDiagnosticsData(true, 1234, true, null));

            public void Dispose() => disposed();
        }
    }

    /// <summary>A clock whose one-shot timers fire only when the test advances it.</summary>
    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private readonly Lock _gate = new();
        private readonly List<ManualTimer> _armed = [];
        private TimeSpan _elapsed;

        public override DateTimeOffset GetUtcNow() => now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            return timer;
        }

        public void Advance(TimeSpan delta)
        {
            ManualTimer[] due;
            lock (_gate)
            {
                _elapsed += delta;
                due = [.. _armed.Where(timer => timer.DueAt <= _elapsed)];
                foreach (var timer in due)
                    _armed.Remove(timer);
            }
            foreach (var timer in due)
                timer.Fire();
        }

        private sealed class ManualTimer(ManualTimeProvider clock, TimerCallback callback, object? state) : ITimer
        {
            public TimeSpan DueAt { get; private set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (clock._gate)
                {
                    clock._armed.Remove(this);
                    if (dueTime == Timeout.InfiniteTimeSpan)
                        return true;
                    DueAt = clock._elapsed + dueTime;
                    clock._armed.Add(this);
                }
                return true;
            }

            public void Fire() => callback(state);

            public void Dispose() => Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
