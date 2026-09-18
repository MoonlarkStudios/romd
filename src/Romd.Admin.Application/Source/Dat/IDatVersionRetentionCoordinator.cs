namespace Romd.Admin.Application.Source.Dat;

/// <summary>
///     Enforces DAT version retention together with catalog/library invalidation in one
///     mutation transaction for the recurring convergence sweep. Normal activation owns its
///     retention DELETE inside the activation transaction; this boundary is the crash/race backstop.
/// </summary>
public interface IDatVersionRetentionCoordinator
{
    Task<int> EnforceAsync(int datSourceId, CancellationToken cancellationToken = default);
}
