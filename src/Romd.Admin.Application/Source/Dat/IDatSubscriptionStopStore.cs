using ErrorOr;

namespace Romd.Admin.Application.Source.Dat;

/// <summary>Stages subscription removal in a required caller-owned transaction.</summary>
public interface IDatSubscriptionStopStore
{
    /// <summary>
    /// Acquires the existing catalog and source locks, rejects a running import, and stages
    /// removal of update tracking only. Installed sources, files and jobs remain unchanged.
    /// The caller owns the flush and commit.
    /// </summary>
    Task<ErrorOr<Success>> StopStagedAsync(int id, CancellationToken ct = default);
}
