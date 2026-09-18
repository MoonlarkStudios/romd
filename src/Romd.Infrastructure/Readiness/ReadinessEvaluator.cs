using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Romd.Application.Common.Readiness;

namespace Romd.Infrastructure.Readiness;

/// <summary>
///     Bounded, non-amplifying readiness evaluation: one full evaluation gets
///     <see cref="ReadinessOptions.EvaluationBudget" /> and every check gets
///     <see cref="ReadinessOptions.CheckBudget" /> within it. A timed-out check counts as failed per
///     its criticality; once the overall budget is exhausted, remaining checks are reported as
///     failed without being invoked. Each check runs in its own DI scope, concurrent probes share
///     one in-flight evaluation, and the result is cached for
///     <see cref="ReadinessOptions.ResultCacheTtl" />. Overall-state transitions are logged once at
///     Information; individual evaluations are not.
/// </summary>
public sealed class ReadinessEvaluator(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ReadinessOptions options,
    ILogger<ReadinessEvaluator> logger) : IReadinessEvaluator
{
    private readonly object _gate = new();
    private Task<ReadinessReport>? _inFlight;
    private ReadinessReport? _cachedReport;
    private DateTimeOffset _cacheExpiresAtUtc;
    private ReadinessStatus? _lastLoggedStatus;

    public Task<ReadinessReport> EvaluateAsync(CancellationToken cancellationToken)
    {
        Task<ReadinessReport> evaluation;
        lock (_gate)
        {
            if (_cachedReport is { } cached && timeProvider.GetUtcNow() < _cacheExpiresAtUtc)
            {
                return Task.FromResult(cached);
            }

            _inFlight ??= EvaluateAndCacheAsync();
            evaluation = _inFlight;
        }

        // The shared evaluation is not linked to any single caller: one probe disconnecting must
        // not cancel the evaluation other probes are awaiting. The evaluation bounds itself.
        return evaluation.WaitAsync(cancellationToken);
    }

    private async Task<ReadinessReport> EvaluateAndCacheAsync()
    {
        // Leave the caller's lock before doing any real work.
        await Task.Yield();

        try
        {
            var report = await RunChecksAsync();
            lock (_gate)
            {
                _cachedReport = report;
                _cacheExpiresAtUtc = timeProvider.GetUtcNow() + options.ResultCacheTtl;
            }

            LogTransition(report);
            return report;
        }
        finally
        {
            lock (_gate)
            {
                _inFlight = null;
            }
        }
    }

    private async Task<ReadinessReport> RunChecksAsync()
    {
        using var overallBudget = new CancellationTokenSource(options.EvaluationBudget);

        // Capture the registered inventory (name + criticality) without evaluating anything, so
        // checks the overall budget prevents from running still appear in the body by name.
        (string Name, bool IsCritical)[] inventory;
        await using (var inventoryScope = scopeFactory.CreateAsyncScope())
        {
            inventory = inventoryScope.ServiceProvider
                .GetServices<IReadinessCheck>()
                .Select(check => (check.Name, check.IsCritical))
                .ToArray();
        }

        var results = new List<ReadinessCheckResult>(inventory.Length);

        // Sequential on purpose: one readiness probe must not fan out into parallel dependency hits.
        for (int index = 0; index < inventory.Length; index++)
        {
            (string name, bool isCritical) = inventory[index];
            results.Add(overallBudget.IsCancellationRequested
                ? FailedResult(name, isCritical)
                : await EvaluateCheckAsync(index, name, isCritical, overallBudget.Token));
        }

        var status = results.Any(result => result.Status == ReadinessCheckStatus.Unready)
            ? ReadinessStatus.Unready
            : results.Any(result => result.Status == ReadinessCheckStatus.Degraded)
                ? ReadinessStatus.Degraded
                : ReadinessStatus.Ready;

        return new ReadinessReport(status, results);
    }

    /// <summary>
    ///     Runs one check in its own DI scope so an abandoned over-budget check can neither collide
    ///     with the next check's scoped services nor hit a disposed scope: the scope (and the
    ///     check's linked budget) is released only when the check's task actually completes.
    /// </summary>
    private async Task<ReadinessCheckResult> EvaluateCheckAsync(
        int index,
        string name,
        bool isCritical,
        CancellationToken overallToken)
    {
        var scope = scopeFactory.CreateScope();
        var budget = CancellationTokenSource.CreateLinkedTokenSource(overallToken);
        Task<ReadinessCheckStatus>? evaluation = null;
        try
        {
            budget.CancelAfter(options.CheckBudget);

            var check = scope.ServiceProvider
                .GetServices<IReadinessCheck>()
                .ElementAt(index);

            // WaitAsync bounds even a check that ignores its cancellation token.
            evaluation = check.EvaluateAsync(budget.Token);
            var status = await evaluation.WaitAsync(budget.Token);
            if (status == ReadinessCheckStatus.Unready && !isCritical)
            {
                status = ReadinessCheckStatus.Degraded;
            }

            return new ReadinessCheckResult(name, status);
        }
        catch (Exception)
        {
            return FailedResult(name, isCritical);
        }
        finally
        {
            ReleaseWhenCheckCompletes(scope, budget, evaluation, name);
        }
    }

    private static ReadinessCheckResult FailedResult(string name, bool isCritical) =>
        new(name, isCritical ? ReadinessCheckStatus.Unready : ReadinessCheckStatus.Degraded);

    private void ReleaseWhenCheckCompletes(
        IServiceScope scope,
        CancellationTokenSource budget,
        Task<ReadinessCheckStatus>? evaluation,
        string checkName)
    {
        if (evaluation is null)
        {
            ObserveAndRelease(completed: null);
            return;
        }

        evaluation.ContinueWith(
            ObserveAndRelease,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        void ObserveAndRelease(Task? completed)
        {
            // Observe the abandoned task's exception so it can never surface as an unobserved
            // task exception; the check already counted as failed.
            if (completed?.Exception is { } exception)
            {
                logger.LogDebug(
                    exception,
                    "Readiness check {CheckName} completed with an error after being abandoned by its budget",
                    checkName);
            }

            budget.Dispose();
            scope.Dispose();
        }
    }

    private void LogTransition(ReadinessReport report)
    {
        ReadinessStatus? previous;
        lock (_gate)
        {
            previous = _lastLoggedStatus;
            if (previous == report.Status)
            {
                return;
            }

            _lastLoggedStatus = report.Status;
        }

        logger.LogInformation(
            "Readiness changed {PreviousStatus} -> {CurrentStatus}: {CheckStatuses}",
            previous is { } previousStatus ? ReadinessStatusNames.Of(previousStatus) : "unknown",
            ReadinessStatusNames.Of(report.Status),
            string.Join(
                ", ",
                report.Checks.Select(check => $"{check.Name}={ReadinessStatusNames.Of(check.Status)}")));
    }
}
