namespace Romd.Admin.Application.Source.Dat;

public sealed record DatSubscriptionApplyPlatform(bool Published, bool Retired);

/// <summary>Database facts and acceptance fields inside the subscription owner's transaction.</summary>
public interface IDatSubscriptionApplyStore
{
    Task<DatSubscriptionApplyPlatform?> ReadPlatformAsync(int platformId, CancellationToken ct = default);

    /// <summary>Stages only source binding, job, Applying state and message; never flushes or commits.</summary>
    Task StageAcceptedAsync(int id, int? sourceId, Guid jobId, CancellationToken ct = default);
}
