using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Admin.Application.Dashboard.Queries.GetCoverageStats;
using Romd.Admin.Application.Dashboard.Queries.GetStorageStats;
using Romd.Admin.Application.Dashboard.Queries.GetSystemHealth;
using Romd.Contracts.Management.Models;

namespace Romd.Hosting.Dashboard;

/// <summary>
/// Shares global dashboard calculations across authenticated callers in this admin process.
/// Each calculation owns its scope; disconnecting a caller cancels only that caller's wait.
/// </summary>
public sealed class DashboardStatsCache
{
    private readonly Entry<GetStorageStatsQuery, StorageStatsDto> _storage;
    private readonly Entry<GetCoverageStatsQuery, CoverageStatsDto> _coverage;
    private readonly Entry<GetSystemHealthQuery, SystemHealthDto> _health;

    public DashboardStatsCache(IServiceScopeFactory scopes, TimeProvider clock, IHostApplicationLifetime lifetime)
    {
        _storage = new(scopes, clock, lifetime.ApplicationStopping);
        _coverage = new(scopes, clock, lifetime.ApplicationStopping);
        _health = new(scopes, clock, lifetime.ApplicationStopping);
    }

    public Task<ErrorOr<StorageStatsDto>> GetStorageAsync(CancellationToken ct) => _storage.GetAsync(ct);
    public Task<ErrorOr<CoverageStatsDto>> GetCoverageAsync(CancellationToken ct) => _coverage.GetAsync(ct);
    public Task<ErrorOr<SystemHealthDto>> GetHealthAsync(CancellationToken ct) => _health.GetAsync(ct);
    public void InvalidateStorage() => _storage.Invalidate();
    public void InvalidateCoverage() => _coverage.Invalidate();
    public void InvalidateHealth() => _health.Invalidate();

    private sealed class Entry<TQuery, TResult>(IServiceScopeFactory scopes, TimeProvider clock, CancellationToken stopping)
        where TQuery : IQuery<TResult>, new()
    {
        private static readonly TimeSpan Freshness = TimeSpan.FromSeconds(2);
        private readonly object _gate = new();
        private Task<ErrorOr<TResult>>? _flight;
        private long _flightVersion;
        private long _version;
        private TResult? _value;
        private bool _hasValue;
        private DateTimeOffset _expires;

        public void Invalidate()
        {
            lock (_gate)
            {
                _version++;
                _hasValue = false;
            }
        }

        public async Task<ErrorOr<TResult>> GetAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            long requiredVersion;
            lock (_gate) requiredVersion = _version;

            while (true)
            {
                Task<ErrorOr<TResult>> flight;
                long flightVersion;
                lock (_gate)
                {
                    if (_hasValue && clock.GetUtcNow() < _expires)
                        return _value!;

                    if (_flight is null)
                    {
                        var completion = new TaskCompletionSource<ErrorOr<TResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
                        _flight = completion.Task;
                        _flightVersion = _version;
                        // Publish the task before executing: handlers may complete synchronously.
                        _ = CalculateAsync(completion, _flightVersion);
                    }
                    flight = _flight;
                    flightVersion = _flightVersion;
                }

                var result = await flight.WaitAsync(ct);
                // A request after an invalidation must not receive an older in-flight snapshot.
                if (flightVersion >= requiredVersion)
                    return result;
            }
        }

        private async Task<ErrorOr<TResult>> ComputeInScopeAsync(CancellationToken ct)
        {
            await using var scope = scopes.CreateAsyncScope();
            var handler = scope.ServiceProvider.GetRequiredService<IQueryHandler<TQuery, TResult>>();
            return await handler.HandleAsync(new TQuery(), ct);
        }

        private async Task CalculateAsync(TaskCompletionSource<ErrorOr<TResult>> completion, long version)
        {
            // Never execute or complete under the caller's lock, even for synchronous handlers.
            await Task.Yield();
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping);
                timeout.CancelAfter(TimeSpan.FromSeconds(60));
                var result = await ComputeInScopeAsync(timeout.Token);
                lock (_gate)
                {
                    if (!result.IsError && version == _version)
                    {
                        _value = result.Value;
                        _hasValue = true;
                        _expires = clock.GetUtcNow() + Freshness;
                    }
                    _flight = null;
                }
                completion.TrySetResult(result);
            }
            catch (Exception exception)
            {
                lock (_gate) _flight = null;
                completion.TrySetException(exception);
                // A disconnected last waiter must not leave an unobserved exception.
                _ = completion.Task.Exception;
            }
        }
    }
}
