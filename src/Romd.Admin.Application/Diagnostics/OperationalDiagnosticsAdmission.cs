using System.Collections.Concurrent;

namespace Romd.Admin.Application.Diagnostics;

/// <summary>
///     Prevents a diagnostics request from starting another copy of a section whose
///     previous provider call outlived its response budget.
/// </summary>
public sealed class OperationalDiagnosticsAdmission
{
    private readonly ConcurrentDictionary<OperationalDiagnosticsSection, byte> _active = [];

    internal Lease? TryAcquire(OperationalDiagnosticsSection section) =>
        _active.TryAdd(section, 0) ? new Lease(_active, section) : null;

    internal sealed class Lease(
        ConcurrentDictionary<OperationalDiagnosticsSection, byte> active,
        OperationalDiagnosticsSection section) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                active.TryRemove(section, out _);
            }
        }
    }
}

internal enum OperationalDiagnosticsSection
{
    CatalogProjections,
    Jobs,
    Outbox,
    Hangfire,
    Storage
}
