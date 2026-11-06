namespace Romd.Admin.Application.Libraries;

/// <summary>
///     Accelerates dispatch of an already committed materialization job.
/// </summary>
public interface IMaterializationJobEnqueuer
{
    Task EnqueueAsync(Guid jobId, CancellationToken ct = default);
}
