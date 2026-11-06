using Microsoft.Extensions.Options;
using Romd.Application.Common.Readiness;
using Romd.Storage;

namespace Romd.Infrastructure.Readiness;

/// <summary>
///     Degraded: the content-addressable storage root must be accessible. Uses the same source of
///     truth as the store itself (<see cref="ContentStoreOptions.RootPath" />, configured from
///     <c>Romd:DataDirectory</c> by AddRomdContentAddressableStorage) and mirrors the store's lazy,
///     idempotent root creation so a fresh install is healthy before its first content access.
/// </summary>
public sealed class ContentStorageReadinessCheck(IOptions<ContentStoreOptions> contentStoreOptions) : IReadinessCheck
{
    public string Name => ReadinessCheckNames.Storage;

    public bool IsCritical => false;

    public async Task<ReadinessCheckStatus> EvaluateAsync(CancellationToken cancellationToken)
    {
        // The filesystem probe is synchronous; yield first so the caller gets an incomplete task
        // and the evaluator's budget can bound a blocked volume access.
        await Task.Yield();

        string rootPath = contentStoreOptions.Value.RootPath;
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return ReadinessCheckStatus.Degraded;
        }

        try
        {
            Directory.CreateDirectory(rootPath);
            return ReadinessCheckStatus.Healthy;
        }
        catch (Exception)
        {
            return ReadinessCheckStatus.Degraded;
        }
    }
}
