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

        string volumeRoot = Path.GetPathRoot(Path.GetFullPath(romdOptions.DataDirectory))
            ?? throw new InvalidOperationException(
                $"Could not determine the volume root of '{romdOptions.DataDirectory}'.");
        var drive = new DriveInfo(volumeRoot);

        return drive.AvailableFreeSpace < options.MinimumFreeDiskBytes
            ? ReadinessCheckStatus.Degraded
            : ReadinessCheckStatus.Healthy;
    }
}
