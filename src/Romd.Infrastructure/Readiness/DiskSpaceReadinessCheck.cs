using Romd.Application.Common.Configuration;
using Romd.Application.Common.Readiness;

namespace Romd.Infrastructure.Readiness;

/// <summary>
///     Degraded: free space on the volume containing <c>Romd:DataDirectory</c> below
///     <see cref="ReadinessOptions.MinimumFreeDiskBytes" /> degrades the host.
/// </summary>
public sealed class DiskSpaceReadinessCheck(IRomdOptions romdOptions, ReadinessOptions options) : IReadinessCheck
{
    public string Name => ReadinessCheckNames.Disk;

    public bool IsCritical => false;

    public async Task<ReadinessCheckStatus> EvaluateAsync(CancellationToken cancellationToken)
    {
        // The filesystem probe is synchronous; yield first so the caller gets an incomplete task
        // and the evaluator's budget can bound a blocked volume query.
        await Task.Yield();

        cancellationToken.ThrowIfCancellationRequested();
        // On Unix the full path identifies its backing filesystem, including bind mounts.
        // Truncating it to '/' probes the host/container root instead of the data volume.
        var drive = new DriveInfo(Path.GetFullPath(romdOptions.DataDirectory));

        return drive.AvailableFreeSpace < options.MinimumFreeDiskBytes
            ? ReadinessCheckStatus.Degraded
            : ReadinessCheckStatus.Healthy;
    }
}
