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
        var handler = Handler(
            services,
            new OperationalDiagnosticsOptions
            {
                SectionBudget = TimeSpan.FromMilliseconds(75),
                OverallBudget = TimeSpan.FromSeconds(2)
            });

        var result = await handler.HandleAsync(new GetOperationalDiagnosticsQuery())
            .WaitAsync(TimeSpan.FromSeconds(2));

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
        var relational = Substitute.For<IOperationalDiagnosticsReader>();
        var hangfire = Substitute.For<IHangfireDiagnosticsReader>();
        var state = new HangingStorageState();
        relational.ReadCatalogProjectionsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new BoundedDiagnosticsData<CatalogProjectionDiagnosticsData>([], false));
        relational.ReadJobsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new OperationalJobDiagnosticsData(
                new BoundedDiagnosticsData<ReplaceDatJobDiagnosticsData>([], false),
                new BoundedDiagnosticsData<BulkEnrichmentJobDiagnosticsData>([], false)));
        relational.ReadOutboxAsync(Arg.Any<CancellationToken>())
            .Returns(new OutboxDiagnosticsData(0, null, 0, null));
        hangfire.ReadAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(AvailableHangfire());
        var services = new ServiceCollection()
            .AddSingleton<OperationalDiagnosticsAdmission>()
            .AddScoped(_ => relational)
            .AddScoped(_ => hangfire)
            .AddScoped<IStorageDiagnosticsReader>(_ => new HangingStorageReader(state))
            .BuildServiceProvider();
        var handler = Handler(
            services,
            new OperationalDiagnosticsOptions
            {
                SectionBudget = TimeSpan.FromSeconds(2),
                OverallBudget = TimeSpan.FromMilliseconds(75)
            });

        var result = await handler.HandleAsync(new GetOperationalDiagnosticsQuery())
            .WaitAsync(TimeSpan.FromSeconds(2));

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
        OperationalDiagnosticsOptions? options = null) =>
        new(
            services.GetRequiredService<IServiceScopeFactory>(),
            services.GetRequiredService<OperationalDiagnosticsAdmission>(),
            new FixedTimeProvider(Now),
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
        var timeout = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2);
        while (!predicate())
        {
            if (DateTimeOffset.UtcNow >= timeout)
                throw new TimeoutException("Timed out waiting for the abandoned diagnostics scope to release.");
            await Task.Delay(10);
        }
    }

    private sealed class HangingReaderState
    {
        public TaskCompletionSource<BoundedDiagnosticsData<CatalogProjectionDiagnosticsData>> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public HangingOperationalReader? ProjectionReader { get; set; }
        public int ProjectionReadCount { get; set; }
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

        public void Dispose() => IsDisposed = true;
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
}
