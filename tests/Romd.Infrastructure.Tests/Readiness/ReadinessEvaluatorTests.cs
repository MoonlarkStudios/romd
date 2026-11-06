using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Romd.Application.Common.Readiness;
using Romd.Infrastructure.Readiness;
using Romd.Infrastructure.Tests.Helpers;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Readiness;

public sealed class ReadinessEvaluatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task EvaluateAsync_AllChecksHealthy_ReturnsReady()
    {
        var evaluator = CreateEvaluator(
            [Healthy("database", isCritical: true), Healthy("disk")],
            new ManualTimeProvider(Now));

        var report = await evaluator.EvaluateAsync(CancellationToken.None);

        report.Status.ShouldBe(ReadinessStatus.Ready);
        report.Checks.ShouldBe([
            new ReadinessCheckResult("database", ReadinessCheckStatus.Healthy),
            new ReadinessCheckResult("disk", ReadinessCheckStatus.Healthy)
        ]);
    }

    [Fact]
    public async Task EvaluateAsync_NonCriticalCheckThrows_ReturnsDegradedWithPerCheckStatuses()
    {
        var evaluator = CreateEvaluator(
            [Healthy("database", isCritical: true), Throwing("disk", isCritical: false)],
            new ManualTimeProvider(Now));

        var report = await evaluator.EvaluateAsync(CancellationToken.None);

        report.Status.ShouldBe(ReadinessStatus.Degraded);
        report.Checks.ShouldBe([
            new ReadinessCheckResult("database", ReadinessCheckStatus.Healthy),
            new ReadinessCheckResult("disk", ReadinessCheckStatus.Degraded)
        ]);
    }

    [Fact]
    public async Task EvaluateAsync_CriticalCheckThrows_ReturnsUnready()
    {
        var evaluator = CreateEvaluator(
            [Throwing("database", isCritical: true), Healthy("disk")],
            new ManualTimeProvider(Now));

        var report = await evaluator.EvaluateAsync(CancellationToken.None);

        report.Status.ShouldBe(ReadinessStatus.Unready);
        report.Checks.ShouldContain(new ReadinessCheckResult("database", ReadinessCheckStatus.Unready));
    }

    [Fact]
    public async Task EvaluateAsync_NonCriticalCheckReportsUnready_ClampsToDegraded()
    {
        var evaluator = CreateEvaluator(
            [new FakeCheck("disk", isCritical: false, _ => Task.FromResult(ReadinessCheckStatus.Unready))],
            new ManualTimeProvider(Now));

        var report = await evaluator.EvaluateAsync(CancellationToken.None);

        report.Status.ShouldBe(ReadinessStatus.Degraded);
        report.Checks.ShouldBe([new ReadinessCheckResult("disk", ReadinessCheckStatus.Degraded)]);
    }

    [Fact]
    public async Task EvaluateAsync_CriticalCheckExceedsBudget_ReturnsUnreadyWithoutHanging()
    {
        var evaluator = CreateEvaluator(
            [Hanging("database", isCritical: true), Healthy("disk")],
            new ManualTimeProvider(Now),
            new ReadinessOptions { CheckBudget = TimeSpan.FromMilliseconds(100) });

        var report = await evaluator.EvaluateAsync(CancellationToken.None).WaitAsync(TestTimeout);

        report.Status.ShouldBe(ReadinessStatus.Unready);
        report.Checks.ShouldBe([
            new ReadinessCheckResult("database", ReadinessCheckStatus.Unready),
            new ReadinessCheckResult("disk", ReadinessCheckStatus.Healthy)
        ]);
    }

    [Fact]
    public async Task EvaluateAsync_NonCriticalCheckExceedsBudget_ReturnsDegraded()
    {
        var evaluator = CreateEvaluator(
            [Healthy("database", isCritical: true), Hanging("worker", isCritical: false)],
            new ManualTimeProvider(Now),
            new ReadinessOptions { CheckBudget = TimeSpan.FromMilliseconds(100) });

        var report = await evaluator.EvaluateAsync(CancellationToken.None).WaitAsync(TestTimeout);

        report.Status.ShouldBe(ReadinessStatus.Degraded);
        report.Checks.ShouldContain(new ReadinessCheckResult("worker", ReadinessCheckStatus.Degraded));
    }

    [Fact]
    public async Task EvaluateAsync_OverallBudgetExhausted_ReportsUninvokedChecksAsFailedWithinBound()
    {
        var first = Hanging("outbox", isCritical: false);
        var second = Hanging("worker", isCritical: false);
        var third = Hanging("disk", isCritical: false);
        var evaluator = CreateEvaluator(
            [first, second, third],
            new ManualTimeProvider(Now),
            new ReadinessOptions
            {
                // Per-check budget larger than the overall budget so the overall bound is what fires.
                CheckBudget = TimeSpan.FromSeconds(5),
                EvaluationBudget = TimeSpan.FromMilliseconds(250)
            });

        var report = await evaluator.EvaluateAsync(CancellationToken.None).WaitAsync(TestTimeout);

        report.Status.ShouldBe(ReadinessStatus.Degraded);
        report.Checks.ShouldBe([
            new ReadinessCheckResult("outbox", ReadinessCheckStatus.Degraded),
            new ReadinessCheckResult("worker", ReadinessCheckStatus.Degraded),
            new ReadinessCheckResult("disk", ReadinessCheckStatus.Degraded)
        ]);
        first.Evaluations.ShouldBe(1);
        second.Evaluations.ShouldBe(0);
        third.Evaluations.ShouldBe(0);
    }

    [Fact]
    public async Task EvaluateAsync_SecondCallWithinCacheTtl_RunsChecksOnce()
    {
        var timeProvider = new ManualTimeProvider(Now);
        var check = Healthy("database", isCritical: true);
        var evaluator = CreateEvaluator([check], timeProvider);

        await evaluator.EvaluateAsync(CancellationToken.None);
        await evaluator.EvaluateAsync(CancellationToken.None);

        check.Evaluations.ShouldBe(1);
    }

    [Fact]
    public async Task EvaluateAsync_AfterCacheTtlElapses_RunsChecksAgain()
    {
        var timeProvider = new ManualTimeProvider(Now);
        var check = Healthy("database", isCritical: true);
        var evaluator = CreateEvaluator([check], timeProvider);

        await evaluator.EvaluateAsync(CancellationToken.None);
        timeProvider.Advance(TimeSpan.FromSeconds(6));
        await evaluator.EvaluateAsync(CancellationToken.None);

        check.Evaluations.ShouldBe(2);
    }

    [Fact]
    public async Task EvaluateAsync_ConcurrentCallers_ShareOneEvaluation()
    {
        var gate = new TaskCompletionSource<ReadinessCheckStatus>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var check = new FakeCheck("database", isCritical: true, _ => gate.Task);
        var evaluator = CreateEvaluator(
            [check],
            new ManualTimeProvider(Now),
            new ReadinessOptions { CheckBudget = TimeSpan.FromSeconds(5) });

        var first = evaluator.EvaluateAsync(CancellationToken.None);
        var second = evaluator.EvaluateAsync(CancellationToken.None);
        gate.SetResult(ReadinessCheckStatus.Healthy);
        var reports = await Task.WhenAll(first, second).WaitAsync(TestTimeout);

        check.Evaluations.ShouldBe(1);
        reports.ShouldAllBe(report => report.Status == ReadinessStatus.Ready);
    }

    [Fact]
    public async Task EvaluateAsync_OverallStatusTransitions_LogsOnChangeOnly()
    {
        var timeProvider = new ManualTimeProvider(Now);
        var logger = new CapturingLogger();
        var currentStatus = ReadinessCheckStatus.Healthy;
        var check = new FakeCheck("database", isCritical: true, _ => Task.FromResult(currentStatus));
        var evaluator = CreateEvaluator([check], timeProvider, logger: logger);

        await evaluator.EvaluateAsync(CancellationToken.None);
        timeProvider.Advance(TimeSpan.FromSeconds(6));
        await evaluator.EvaluateAsync(CancellationToken.None);
        logger.Messages.Count.ShouldBe(1);

        currentStatus = ReadinessCheckStatus.Unready;
        timeProvider.Advance(TimeSpan.FromSeconds(6));
        await evaluator.EvaluateAsync(CancellationToken.None);

        logger.Messages.Count.ShouldBe(2);
        logger.Messages[^1].ShouldContain("ready -> unready");
    }

    private static ReadinessEvaluator CreateEvaluator(
        IReadOnlyList<IReadinessCheck> checks,
        TimeProvider timeProvider,
        ReadinessOptions? options = null,
        ILogger<ReadinessEvaluator>? logger = null)
    {
        var services = new ServiceCollection();
        foreach (var check in checks)
        {
            services.AddScoped(_ => check);
        }

        var provider = services.BuildServiceProvider();
        return new ReadinessEvaluator(
            provider.GetRequiredService<IServiceScopeFactory>(),
            timeProvider,
            options ?? new ReadinessOptions(),
            logger ?? NullLogger<ReadinessEvaluator>.Instance);
    }

    private static FakeCheck Healthy(string name, bool isCritical = false) =>
        new(name, isCritical, _ => Task.FromResult(ReadinessCheckStatus.Healthy));

    private static FakeCheck Throwing(string name, bool isCritical) =>
        new(name, isCritical, _ => Task.FromException<ReadinessCheckStatus>(
            new InvalidOperationException("readiness check failure")));

    private static FakeCheck Hanging(string name, bool isCritical) =>
        new(name, isCritical, _ => new TaskCompletionSource<ReadinessCheckStatus>().Task);

    private sealed class FakeCheck(
        string name,
        bool isCritical,
        Func<CancellationToken, Task<ReadinessCheckStatus>> evaluate) : IReadinessCheck
    {
        private int _evaluations;

        public int Evaluations => _evaluations;

        public string Name => name;

        public bool IsCritical => isCritical;

        public Task<ReadinessCheckStatus> EvaluateAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _evaluations);
            return evaluate(cancellationToken);
        }
    }

    private sealed class CapturingLogger : ILogger<ReadinessEvaluator>
    {
        public List<string> Messages { get; } = [];

        IDisposable? ILogger.BeginScope<TState>(TState state) => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
